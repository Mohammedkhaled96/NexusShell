using System.ClientModel;
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
internal sealed class ProviderErrorChatClient(IChatClient inner, ProviderProfile provider, string modelId) : DelegatingChatClient(inner)
{
    private const int MaxDetailChars = 300;

    private TimeSpan _timeout => provider.EffectiveRequestTimeout;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
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
        catch (Exception ex) when (Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
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

        var kind = status switch
        {
            404 => ModelFailureKind.ModelNotFound,
            400 or 403 or 422 when modelMentioned => ModelFailureKind.ModelNotFound,
            401 or 403 => ModelFailureKind.Unauthorized,
            429 => ModelFailureKind.RateLimited,
            >= 500 => ModelFailureKind.Unavailable,
            _ when overloaded => ModelFailureKind.Unavailable,
            null when exception is HttpRequestException => ModelFailureKind.Unavailable,
            null when modelMentioned => ModelFailureKind.ModelNotFound,
            _ => ModelFailureKind.Other,
        };

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
                $"{provider.Id} model '{modelId}' is overloaded, unreachable or failing{(status is null ? string.Empty : $" (HTTP {status})")}. NexusAgent tries another configured model; otherwise try again shortly.{suffix}",
            _ => $"{provider.Id} returned HTTP {status} for model '{modelId}'.{suffix}",
        };

        return new ModelProviderException(provider.Id, modelId, kind, status, message, exception);
    }

    private static int? StatusOf(Exception exception)
    {
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
        return text.Length <= MaxDetailChars ? text : text[..MaxDetailChars] + " ...";
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
