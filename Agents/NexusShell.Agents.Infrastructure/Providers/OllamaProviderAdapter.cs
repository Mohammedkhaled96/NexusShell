using Microsoft.Extensions.AI;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Domain.Providers;
using OllamaSharp;

namespace NexusShell.Agents.Infrastructure.Providers;

/// <summary>Local models through Ollama's native API (OllamaSharp implements IChatClient, including tool calling).</summary>
internal sealed class OllamaProviderAdapter(IHttpClientFactory httpClientFactory) : IModelProviderAdapter
{
    public static Uri DefaultEndpoint { get; } = new("http://localhost:11434");

    public bool CanHandle(ProviderKind kind) => kind == ProviderKind.Ollama;

    public IChatClient CreateChatClient(ProviderProfile provider, string modelId, string? apiKey, Uri? endpoint)
    {
        var http = httpClientFactory.CreateClient(HttpClientNames.Llm);
        http.BaseAddress = endpoint ?? DefaultEndpoint;
        if (!string.IsNullOrWhiteSpace(apiKey))
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        return new OllamaApiClient(http, modelId);
    }
}
