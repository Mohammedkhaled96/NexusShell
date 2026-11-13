using NexusShell.Agents.Domain.Secrets;

namespace NexusShell.Agents.Domain.Providers;

/// <summary>
/// A configured model provider. Holds only NON-secret data: the API key is a
/// <see cref="SecretReference"/> (the NAME of an environment variable), never a value.
/// </summary>
public sealed record ProviderProfile
{
    public required string Id { get; init; }

    public required ProviderKind Kind { get; init; }

    /// <summary>Base endpoint. Optional for providers with a well-known default.</summary>
    public Uri? Endpoint { get; init; }

    /// <summary>Environment variable holding the endpoint (e.g. AZURE_OPENAI_ENDPOINT). Wins over <see cref="Endpoint"/> when set.</summary>
    public SecretReference? EndpointVariable { get; init; }

    /// <summary>Environment variable holding the API key. Null for keyless (local) providers.</summary>
    public SecretReference? ApiKey { get; init; }

    /// <summary>True for models served on this machine or LAN (Ollama, LM Studio, vLLM ...).</summary>
    public bool IsLocal { get; init; }

    public string? DefaultModel { get; init; }

    /// <summary>
    /// Models tried in order when the default model is unavailable (retired, not
    /// enabled for the key ...). Providers retire model ids regularly; a fallback
    /// list keeps the agent working without a configuration change.
    /// </summary>
    public IReadOnlyList<string> FallbackModels { get; init; } = [];

    /// <summary>
    /// Small, quick model for latency-sensitive internal calls (expert routing).
    /// Resolved through the "fast" model keyword; falls back to the normal candidates.
    /// </summary>
    public string? FastModel { get; init; }

    /// <summary>
    /// Maximum time for one model call. A call that takes longer counts as an
    /// unavailable model and the agent fails over. Null = default (60 s online, 180 s local).
    /// </summary>
    public TimeSpan? RequestTimeout { get; init; }

    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? (IsLocal ? TimeSpan.FromSeconds(180) : TimeSpan.FromSeconds(60));

    /// <summary>Default model first, then the fallbacks (no duplicates).</summary>
    public IEnumerable<string> CandidateModels =>
        new[] { DefaultModel }.Concat(FallbackModels)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

    public bool Enabled { get; init; } = true;
}
