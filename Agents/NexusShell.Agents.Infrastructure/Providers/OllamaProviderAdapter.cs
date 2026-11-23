using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Domain.Models;
using NexusShell.Agents.Domain.Providers;
using OllamaSharp;
using OllamaSharp.Models;

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

        // Ollama reads only num_ctx tokens (a small default) and silently cuts the rest of a longer
        // prompt, which makes local models forget the conversation and invent answers. Ask for the
        // configured window (provider "contextWindow", default 16K, never above the model's maximum).
        return new ContextWindowChatClient(new OllamaApiClient(http, modelId), ModelContextWindows.Resolve(provider, modelId));
    }

    /// <summary>Adds Ollama's num_ctx to every request without changing the caller's options.</summary>
    internal sealed class ContextWindowChatClient(IChatClient inner, int contextWindow) : DelegatingChatClient(inner)
    {
        public int ContextWindow { get; } = contextWindow;

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            base.GetResponseAsync(messages, WithContextWindow(options), cancellationToken);

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, WithContextWindow(options), cancellationToken).ConfigureAwait(false))
                yield return update;
        }

        internal ChatOptions WithContextWindow(ChatOptions? options)
        {
            var copy = options?.Clone() ?? new ChatOptions();
            copy.AddOllamaOption(OllamaOption.NumCtx, ContextWindow);
            return copy;
        }
    }
}
