namespace NexusShell.Agents.Application.Abstractions.Configuration;

/// <summary>A provider the user adds or edits. Holds no secret: only the NAME of the key variable.</summary>
public sealed record ProviderSettings
{
    public required string Id { get; init; }

    /// <summary>openai, openai-compatible, azure-openai, anthropic or ollama.</summary>
    public required string Kind { get; init; }

    public string? Endpoint { get; init; }

    /// <summary>Environment variable holding the API key (never the key itself).</summary>
    public string? ApiKeyEnv { get; init; }

    public bool Local { get; init; }

    public string? DefaultModel { get; init; }

    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>Put this provider first in the automatic model order.</summary>
    public bool Prefer { get; init; }
}

/// <summary>
/// Port: saves the user's provider and model choices into the user's agent.json
/// (never into bundled defaults, never secret values).
/// </summary>
public interface IAgentSettingsStore
{
    Task SaveProviderAsync(ProviderSettings provider, CancellationToken cancellationToken = default);

    Task RemoveProviderAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>Adds a model to a provider's list (e.g. a local model the user installed).</summary>
    Task AddModelAsync(string providerId, string modelId, CancellationToken cancellationToken = default);
}
