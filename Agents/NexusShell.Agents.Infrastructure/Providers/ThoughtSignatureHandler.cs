using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusShell.Agents.Infrastructure.Providers;

/// <summary>Bounded store of tool-call id -> thought signature, shared by all handler instances.</summary>
internal sealed class ThoughtSignatureCache
{
    private const int Capacity = 4096;
    private readonly ConcurrentDictionary<string, string> _signatures = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();

    public int Count => _signatures.Count;

    public void Add(string toolCallId, string signature)
    {
        if (_signatures.TryAdd(toolCallId, signature))
        {
            _order.Enqueue(toolCallId);
            while (_signatures.Count > Capacity && _order.TryDequeue(out var oldest))
                _signatures.TryRemove(oldest, out _);
        }
    }

    public bool TryGet(string toolCallId, out string signature) => _signatures.TryGetValue(toolCallId, out signature!);
}

/// <summary>
/// Thinking models behind OpenAI-compatible endpoints (Gemini 3.x) attach an
/// opaque "thought signature" to each tool call
/// (<c>tool_calls[].extra_content.google.thought_signature</c>) and REJECT the next
/// request (HTTP 400) unless it is echoed back. The OpenAI SDK drops unknown
/// fields, so this handler captures signatures from chat-completion responses
/// and re-attaches them to the matching tool calls in later requests.
/// It never changes anything for providers that do not send signatures.
/// </summary>
internal sealed class ThoughtSignatureHandler(ThoughtSignatureCache cache) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        bool chatCompletions = request.Method == HttpMethod.Post
            && request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) == true;

        if (chatCompletions && request.Content is not null && cache.Count > 0)
            request.Content = await InjectAsync(request.Content, cancellationToken).ConfigureAwait(false);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (chatCompletions && response.IsSuccessStatusCode
            && string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            await CaptureAsync(response, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private async Task<HttpContent> InjectAsync(HttpContent content, CancellationToken cancellationToken)
    {
        string body = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string updated = Inject(body, cache);
        if (ReferenceEquals(updated, body)) return Rebuffer(content, body);
        return Rebuffer(content, updated);
    }

    private async Task CaptureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Capture(body, cache);
        response.Content = Rebuffer(response.Content, body);
    }

    /// <summary>Adds known signatures to assistant tool calls that lack one. Returns the same instance when nothing changed.</summary>
    internal static string Inject(string requestJson, ThoughtSignatureCache cache)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(requestJson);
        }
        catch (JsonException)
        {
            return requestJson;
        }

        if (root?["messages"] is not JsonArray messages) return requestJson;

        bool changed = false;
        foreach (var message in messages)
        {
            if (message?["tool_calls"] is not JsonArray toolCalls) continue;
            foreach (var call in toolCalls)
            {
                if (call is not JsonObject callObject || callObject["extra_content"] is not null) continue;
                if (callObject["id"]?.GetValue<string>() is not { } id || !cache.TryGet(id, out var signature)) continue;

                callObject["extra_content"] = new JsonObject { ["google"] = new JsonObject { ["thought_signature"] = signature } };
                changed = true;
            }
        }

        return changed ? root.ToJsonString() : requestJson;
    }

    internal static void Capture(string responseJson, ThoughtSignatureCache cache)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) return;

            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("message", out var message) ||
                    !message.TryGetProperty("tool_calls", out var toolCalls) ||
                    toolCalls.ValueKind != JsonValueKind.Array) continue;

                foreach (var call in toolCalls.EnumerateArray())
                {
                    if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                        call.TryGetProperty("extra_content", out var extra) && extra.ValueKind == JsonValueKind.Object &&
                        extra.TryGetProperty("google", out var google) && google.ValueKind == JsonValueKind.Object &&
                        google.TryGetProperty("thought_signature", out var signature) && signature.ValueKind == JsonValueKind.String)
                    {
                        cache.Add(id.GetString()!, signature.GetString()!);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON we understand: nothing to capture.
        }
    }

    private static HttpContent Rebuffer(HttpContent original, string body)
    {
        var replacement = new StringContent(body, Encoding.UTF8);
        foreach (var header in original.Headers)
        {
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            replacement.Headers.Remove(header.Key);
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        replacement.Headers.ContentType ??= new MediaTypeHeaderValue("application/json");
        return replacement;
    }
}
