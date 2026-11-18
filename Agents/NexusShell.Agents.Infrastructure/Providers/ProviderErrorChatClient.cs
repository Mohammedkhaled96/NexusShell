using System.ClientModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Domain.Providers;

namespace NexusShell.Agents.Infrastructure.Providers;

/// <summary>
/// Decorator that turns SDK-specific failures (OpenAI ClientResultException,
/// Anthropic API exceptions, OllamaSharp / HttpRequestException ...) into a
/// <see cref="ModelProviderException"/> with a clear, actionable message.
/// This lets the Application layer fall back to another model when a model
/// id is retired, without knowing any provider SDK.
/// </summary>
internal sealed partial class ProviderErrorChatClient(IChatClient inner, ProviderProfile provider, string modelId) : DelegatingChatClient(inner)
{
    private const int MaxDetailChars = 300;
    private const int MaxSpokenDetailChars = 160;

    private TimeSpan _timeout => provider.EffectiveRequestTimeout;

    /// <summary>Set once the model said it cannot call tools; later calls are sent without tools.</summary>
    private volatile bool _toolsUnsupported;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (_toolsUnsupported) options = WithoutTools(options);

        // Bound every model call: an overloaded provider can hang for minutes before answering 503.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            return await base.GetResponseAsync(messages, options, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw TimedOut(ex);
        }
        catch (Exception ex) when (options?.Tools is { Count: > 0 } && IsToolsUnsupported(ex))
        {
            // Small local models (e.g. gemma, phi) refuse requests that carry tools: answer without them.
            _toolsUnsupported = true;
        }
        catch (Exception ex) when (Translate(ex) is { } translated)
        {
            throw translated;
        }

        return await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsToolsUnsupported(Exception exception)
    {
        string detail = DetailOf(exception);
        return detail.Contains("does not support tools", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("does not support tool", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("tool use is not supported", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("tools are not supported", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("does not support function calling", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("function calling is not supported", StringComparison.OrdinalIgnoreCase);
    }

    private static ChatOptions? WithoutTools(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 }) return options;
        var copy = options.Clone();
        copy.Tools = null;
        copy.ToolMode = null;
        copy.AllowMultipleToolCalls = null;
        return copy;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_toolsUnsupported) options = WithoutTools(options);

        // Idle timeout for streams: the clock restarts with every update received.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        await using var enumerator = base.GetStreamingResponseAsync(messages, options, timeout.Token).GetAsyncEnumerator(timeout.Token);
        while (true)
        {
            ChatResponseUpdate update;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) yield break;
                update = enumerator.Current;
                timeout.CancelAfter(_timeout);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw TimedOut(ex);
            }
            catch (Exception ex) when (Translate(ex) is { } translated)
            {
                throw translated;
            }
            yield return update;
        }
    }

    private ModelProviderException TimedOut(Exception inner) => new(
        provider.Id,
        modelId,
        ModelFailureKind.Unavailable,
        null,
        $"{provider.Id} model '{modelId}' did not answer within {_timeout.TotalSeconds:0} seconds (it is probably overloaded). " +
        $"NexusAgent tries another configured model; to allow slower answers, raise \"timeoutSeconds\" for \"{provider.Id}\" in agent.json.",
        inner);

    internal ModelProviderException? Translate(Exception exception)
    {
        if (exception is ModelProviderException or OperationCanceledException) return null;

        int? status = StatusOf(exception);
        string detail = DetailOf(exception);


        // Some providers answer 400 for decommissioned / unknown models.
        bool modelMentioned = detail.Contains("model", StringComparison.OrdinalIgnoreCase)
            && (detail.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("decommissioned", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("no longer", StringComparison.OrdinalIgnoreCase));
        // ("not supported" is deliberately excluded: an unsupported OPTION must not quarantine a working model.)

        bool overloaded = detail.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("UNAVAILABLE", StringComparison.Ordinal);

        // No HTTP answer at all: TLS refused, DNS failure, connection blocked (firewall / proxy / region).
        var networkFailure = status is null ? FindNetworkFailure(exception) : null;

        var kind = status switch
        {
            404 => ModelFailureKind.ModelNotFound,
            400 or 403 or 422 when modelMentioned => ModelFailureKind.ModelNotFound,
            401 or 403 => ModelFailureKind.Unauthorized,
            429 => ModelFailureKind.RateLimited,
            >= 500 => ModelFailureKind.Unavailable,
            _ when overloaded => ModelFailureKind.Unavailable,
            null when networkFailure is not null => ModelFailureKind.Unreachable,
            null when modelMentioned => ModelFailureKind.ModelNotFound,
            _ => ModelFailureKind.Other,
        };

        if (networkFailure is not null) detail = Clean(networkFailure.Message);

        if (kind == ModelFailureKind.Other && status is null) return null; // not a provider error: let it surface unchanged

        string suffix = detail.Length > 0 ? $" Provider said: {detail}" : string.Empty;
        string message = kind switch
        {
            ModelFailureKind.ModelNotFound =>
                $"{provider.Id} does not serve the model '{modelId}' for this API key (it may be retired, renamed or not enabled for your account). " +
                $"NexusAgent tries the next configured model automatically; to choose one, set \"defaultModel\" for \"{provider.Id}\" in agent.json.{suffix}",
            ModelFailureKind.Unauthorized =>
                $"{provider.Id} rejected the API key{(provider.ApiKey is null ? string.Empty : $" in {provider.ApiKey.Name}")} (HTTP {status}). Check the key in Agent keys.{suffix}",
            ModelFailureKind.RateLimited =>
                $"{provider.Id} rate limit or quota reached (HTTP 429). Wait a moment, or add a key for another provider.{suffix}",
            ModelFailureKind.Unavailable =>
                $"{provider.Id} model '{modelId}' is overloaded or failing{(status is null ? string.Empty : $" (HTTP {status})")}. NexusAgent tries another configured model; otherwise try again shortly.{suffix}",
            ModelFailureKind.Unreachable =>
                $"{provider.Id} cannot be reached from this computer: the connection was refused or blocked (often a firewall, proxy or regional restriction). " +
                $"NexusAgent switches to another provider. To stop using {provider.Id}, remove its key in Agent keys. Network said: {detail}",
            _ => $"{provider.Id} returned HTTP {status} for model '{modelId}'.{suffix}",
        };

        var retryAfter = kind is ModelFailureKind.RateLimited or ModelFailureKind.Unavailable ? RetryAfterOf(exception) : null;
        return new ModelProviderException(provider.Id, modelId, kind, status, message, exception, retryAfter);
    }

    /// <summary>Walks the exception tree (inner and aggregated exceptions).</summary>
    private static IEnumerable<Exception> Chain(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
    }

    /// <summary>The most specific network-level cause (TLS, socket, DNS) of a failed request, if any.</summary>
    private static Exception? FindNetworkFailure(Exception exception)
    {
        Exception? found = null;
        foreach (var current in Chain(exception))
        {
            switch (current)
            {
                case System.Security.Authentication.AuthenticationException:
                case System.Net.Sockets.SocketException:
                    return current;
                case HttpRequestException http when http.StatusCode is null:
                    found ??= current;
                    break;
            }
        }
        return found;
    }

    private static string Clean(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= MaxDetailChars ? text : text[..MaxDetailChars] + " ...";
    }

    private static int? StatusOf(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (StatusOf(inner) is int innerStatus) return innerStatus;
            }
            return null;
        }

        switch (exception)
        {
            case ClientResultException client when client.Status > 0:
                return client.Status;
            case HttpRequestException http when http.StatusCode is not null:
                return (int)http.StatusCode;
        }

        // Other SDKs (Anthropic, OllamaSharp) expose the status on their own exception types.
        foreach (string name in new[] { "StatusCode", "Status", "HttpStatusCode" })
        {
            var property = exception.GetType().GetProperty(name);
            object? value = property?.GetValue(exception);
            switch (value)
            {
                case int i when i >= 100:
                    return i;
                case HttpStatusCode code:
                    return (int)code;
            }
        }

        return exception.InnerException is null ? null : StatusOf(exception.InnerException);
    }

    private static string DetailOf(Exception exception)
    {
        string? body = null;
        if (exception is ClientResultException client)
        {
            try
            {
                body = client.GetRawResponse()?.Content?.ToString();
            }
            catch (Exception)
            {
                body = null;
            }
        }

        string text = ExtractErrorMessage(body) ?? exception.Message;
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        // The SDK's generic text adds nothing for the user.
        if (text.StartsWith("Service request failed.", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(ExtractErrorMessage(body)))
            text = string.Empty;
        // Screen readers read every character: keep the provider's first sentence(s), drop links and metric dumps.
        return FirstSentences(text, MaxSpokenDetailChars);
    }

    /// <summary>Keeps whole sentences up to <paramref name="max"/> characters.</summary>
    internal static string FirstSentences(string text, int max)
    {
        if (text.Length <= max) return text;
        int cut = -1;
        for (int i = text.IndexOf(". ", StringComparison.Ordinal); i >= 0 && i < max; i = text.IndexOf(". ", i + 2, StringComparison.Ordinal))
            cut = i + 1;
        return cut >= 20 ? text[..cut] : text[..max].TrimEnd() + " ...";
    }

    /// <summary>The provider's requested wait: Retry-After header, Gemini "retryDelay", or "retry in 23.4s" text.</summary>
    private static TimeSpan? RetryAfterOf(Exception exception)
    {
        foreach (var current in Chain(exception))
        {
            if (current is ClientResultException client)
            {
                try
                {
                    var raw = client.GetRawResponse();
                    if (raw is not null)
                    {
                        if (raw.Headers.TryGetValue("Retry-After", out string? header) && ParseRetryAfterHeader(header) is { } fromHeader)
                            return fromHeader;
                        if (ParseRetryDelay(raw.Content?.ToString()) is { } fromBody)
                            return fromBody;
                    }
                }
                catch (Exception)
                {
                    // Raw response not available: fall back to the message text.
                }
            }

            if (ParseRetryDelay(current.Message) is { } fromMessage) return fromMessage;
        }
        return null;
    }

    [GeneratedRegex(@"retry\s*(?:in|after)\s*([0-9]+(?:\.[0-9]+)?)\s*(?:s|sec|secs|seconds)\b|""retryDelay""\s*:\s*""([0-9]+(?:\.[0-9]+)?)s""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RetryDelayRegex();

    internal static TimeSpan? ParseRetryDelay(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = RetryDelayRegex().Match(text);
        if (!match.Success) return null;
        string number = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    internal static TimeSpan? ParseRetryAfterHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            return TimeSpan.FromSeconds(seconds);
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            var wait = at - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>Pulls error.message out of the common {"error":{"message":...}} / [{"error":...}] shapes.</summary>
    internal static string? ExtractErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return body;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString();
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)) return message.GetString();
            }
            return root.TryGetProperty("message", out var top) && top.ValueKind == JsonValueKind.String ? top.GetString() : body;
        }
        catch (JsonException)
        {
            return body;
        }
    }
}
