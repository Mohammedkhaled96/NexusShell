using NexusShell.Agents.Domain.Configuration;

namespace NexusShell.Agents.Application.Abstractions.Configuration;

/// <summary>Port: loads the agent configuration (providers, experts, MCP servers, policies).</summary>
public interface IAgentConfigurationProvider
{
    Task<NexusAgentConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default);

    /// <summary>Forget the cached configuration so the next call re-reads it from disk.</summary>
    void Invalidate();
}
