using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Domain.Providers;

namespace NexusShell.Agents.Infrastructure.Providers;

/// <summary>Anthropic Messages API (Claude) through the official Anthropic SDK's IChatClient.</summary>
internal sealed class AnthropicProviderAdapter(IHttpClientFactory httpClientFactory) : IModelProviderAdapter
{
    private const int DefaultMaxOutputTokens = 8192;

    public bool CanHandle(ProviderKind kind) => kind == ProviderKind.Anthropic;

    public IChatClient CreateChatClient(ProviderProfile provider, string modelId, string? apiKey, Uri? endpoint)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"Provider '{provider.Id}' requires an API key.");

        var options = new ClientOptions
        {
            ApiKey = apiKey,
            HttpClient = httpClientFactory.CreateClient(HttpClientNames.Llm),
            MaxRetries = 1, // fail over to the next model instead of a long backoff
        };
        if (endpoint is not null) options.BaseUrl = endpoint.ToString().TrimEnd('/');

        var client = new AnthropicClient(options);
        return client.AsIChatClient(modelId, DefaultMaxOutputTokens);
    }
}
