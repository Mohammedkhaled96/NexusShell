using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Domain.Providers;
using OpenAI;

namespace NexusShell.Agents.Infrastructure.Providers;

/// <summary>
/// OpenAI, Azure OpenAI (v1 endpoint) and every OpenAI-compatible server
/// (Groq, OpenRouter, Gemini, DeepSeek, Mistral, xAI, LM Studio, vLLM, llama.cpp ...).
/// </summary>
internal sealed class OpenAICompatibleProviderAdapter(IHttpClientFactory httpClientFactory) : IModelProviderAdapter
{
    // Keyless local servers (LM Studio, vLLM, llama.cpp) do not check credentials,
    // but the SDK requires a non-empty value. This is not a secret.
    private const string LocalServerCredential = "no-key-required";

    public bool CanHandle(ProviderKind kind) =>
        kind is ProviderKind.OpenAI or ProviderKind.OpenAICompatible or ProviderKind.AzureOpenAI;

    public IChatClient CreateChatClient(ProviderProfile provider, string modelId, string? apiKey, Uri? endpoint)
    {
        var options = new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(httpClientFactory.CreateClient(HttpClientNames.Llm)),
            NetworkTimeout = TimeSpan.FromMinutes(5),
            // One quick retry only: on an overloaded model NexusAgent fails over to the
            // next model instead of waiting through the SDK's long backoff.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 1),
        };

        if (endpoint is not null)
        {
            options.Endpoint = provider.Kind == ProviderKind.AzureOpenAI ? NormalizeAzureEndpoint(endpoint) : endpoint;
        }
        else if (provider.Kind != ProviderKind.OpenAI)
        {
            throw new InvalidOperationException($"Provider '{provider.Id}' needs an endpoint.");
        }

        var client = new OpenAIClient(new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? LocalServerCredential : apiKey), options);
        return client.GetChatClient(modelId).AsIChatClient();
    }

    /// <summary>Azure OpenAI's OpenAI-compatible surface lives under /openai/v1/.</summary>
    internal static Uri NormalizeAzureEndpoint(Uri endpoint)
    {
        string text = endpoint.ToString().TrimEnd('/');
        if (!text.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase))
            text += text.EndsWith("/openai", StringComparison.OrdinalIgnoreCase) ? "/v1" : "/openai/v1";
        return new Uri(text + "/");
    }
}
