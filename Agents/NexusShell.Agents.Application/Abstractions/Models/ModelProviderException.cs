namespace NexusShell.Agents.Application.Abstractions.Models;

public enum ModelFailureKind
{
    /// <summary>The model id does not exist for this key (retired, renamed, or not enabled). Another model may work.</summary>
    ModelNotFound,

    /// <summary>The API key was rejected.</summary>
    Unauthorized,

    /// <summary>Rate limit or quota exceeded.</summary>
    RateLimited,

    /// <summary>The provider is down or unreachable.</summary>
    Unavailable,

    Other,
}

/// <summary>
/// Provider-neutral model failure with a user-facing message. Infrastructure
/// translates SDK-specific exceptions (OpenAI, Anthropic, Ollama, HTTP) into
/// this type so the Application layer can react (e.g. fall back to another
/// model) without depending on any provider SDK.
/// </summary>
public sealed class ModelProviderException(
    string providerId,
    string modelId,
    ModelFailureKind kind,
    int? statusCode,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string ProviderId { get; } = providerId;

    public string ModelId { get; } = modelId;

    public ModelFailureKind Kind { get; } = kind;

    public int? StatusCode { get; } = statusCode;

    /// <summary>The model itself is unusable for this key (retired / renamed / not enabled).</summary>
    public bool IsModelSpecific => Kind == ModelFailureKind.ModelNotFound;

    /// <summary>True when trying a different model may succeed (missing model, or an overloaded / failing one).</summary>
    public bool CanFallBack => Kind is ModelFailureKind.ModelNotFound or ModelFailureKind.Unavailable;

    /// <summary>How long to avoid this model: long for a missing model, shorter for an overload / timeout.</summary>
    public TimeSpan QuarantineDuration => Kind == ModelFailureKind.ModelNotFound ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(10);
}
