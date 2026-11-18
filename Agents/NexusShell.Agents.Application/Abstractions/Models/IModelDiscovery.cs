using NexusShell.Agents.Domain.Providers;

namespace NexusShell.Agents.Application.Abstractions.Models;

/// <summary>
/// Port: asks a provider which models it actually has (Ollama /api/tags, OpenAI-compatible
/// /models, Anthropic /v1/models). Used for local servers (installed models) and for the
/// "Find models" button when adding a provider. Returns an empty list when unreachable.
/// </summary>
public interface IModelDiscovery
{
    Task<IReadOnlyList<string>> ListModelsAsync(ProviderProfile provider, CancellationToken cancellationToken = default);
}
