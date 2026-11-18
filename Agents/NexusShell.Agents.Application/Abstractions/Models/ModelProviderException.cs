namespace NexusShell.Agents.Application.Abstractions.Models;

public enum ModelFailureKind
{
    /// <summary>The model id does not exist for this key (retired, renamed, or not enabled). Another model may work.</summary>
    ModelNotFound,

    /// <summary>The API key was rejected. Every model of this provider will fail the same way.</summary>
    Unauthorized,

    /// <summary>Rate limit or quota exceeded for the account.</summary>
    RateLimited,

    /// <summary>The model is overloaded, failing (5xx) or timed out. Another model of the same provider may work.</summary>
    Unavailable,

    /// <summary>The provider cannot be reached from this computer (TLS refused, DNS, connection blocked by a firewall or proxy).</summary>
    Unreachable,

    Other,
}

/// <summary>
/// Provider-neutral model failure with a user-facing message. Infrastructure
/// translates SDK-specific exceptions (OpenAI, Anthropic, Ollama, HTTP) into
/// this type so the Application layer can react (fall back to another model or
/// another provider) without depending on any provider SDK.
/// </summary>
public sealed class ModelProviderException(
    string providerId,
    string modelId,
    ModelFailureKind kind,
    int? statusCode,
    string message,
    Exception? innerException = null,
    TimeSpan? retryAfter = null) : Exception(message, innerException)
{
    public string ProviderId { get; } = providerId;

    public string ModelId { get; } = modelId;

    public ModelFailureKind Kind { get; } = kind;

    public int? StatusCode { get; } = statusCode;

    /// <summary>How long the provider asked us to wait (Retry-After header or "retry in 23s" in the error).</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>The model itself is unusable for this key (retired / renamed / not enabled).</summary>
    public bool IsModelSpecific => Kind == ModelFailureKind.ModelNotFound;

    /// <summary>
    /// The whole provider is unusable for now (unreachable, key rejected, quota used up):
    /// its other models would fail the same way, so skip the provider.
    /// </summary>
    public bool IsProviderWide => Kind is ModelFailureKind.Unreachable or ModelFailureKind.Unauthorized or ModelFailureKind.RateLimited;

    /// <summary>True when trying a different model or provider may succeed.</summary>
    public bool CanFallBack => Kind != ModelFailureKind.Other;

    /// <summary>How long to avoid the model (or the provider, when <see cref="IsProviderWide"/>).</summary>
    public TimeSpan QuarantineDuration => Kind switch
    {
        ModelFailureKind.ModelNotFound or ModelFailureKind.Unauthorized => TimeSpan.FromMinutes(30),
        // Follow the provider's own retry time when it gives one (per-minute quotas reset quickly).
        ModelFailureKind.RateLimited => Clamp(RetryAfter ?? TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10)),
        _ => TimeSpan.FromMinutes(10),
    };

    /// <summary>Short reason for status lines and summaries ("quota or rate limit reached").</summary>
    public string ShortReason => Kind switch
    {
        ModelFailureKind.RateLimited => "quota or rate limit reached",
        ModelFailureKind.Unreachable => "cannot be reached from this computer",
        ModelFailureKind.Unauthorized => "API key rejected",
        ModelFailureKind.ModelNotFound => $"model {ModelId} not available",
        ModelFailureKind.Unavailable => $"model {ModelId} overloaded or slow",
        _ => "failing",
    };

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}
