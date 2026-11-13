using Microsoft.Extensions.AI;
using NexusShell.Agents.Application.Abstractions.Mcp;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Domain.Orchestration;

namespace NexusShell.Agents.Application.Conversations;

/// <summary>
/// Facade used by the presentation layer (NexusShell.App). Hides experts, routing,
/// providers and MCP behind two calls: check health, start a conversation.
/// </summary>
public interface INexusAgentService
{
    Task<AgentHealthReport> GetHealthAsync(CancellationToken cancellationToken = default);

    /// <param name="initialContext">Optional context (e.g. terminal output) attached to the first message.</param>
    Task<INexusAgentConversation> StartConversationAsync(string? initialContext = null, CancellationToken cancellationToken = default);

    /// <summary>Re-read configuration, experts and MCP definitions on the next conversation.</summary>
    void ReloadConfiguration();
}

public interface INexusAgentConversation
{
    string Id { get; }

    OrchestrationMode Mode { get; }

    Task<AgentTurnResult> SendAsync(string message, IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed record ExpertContribution(string ExpertId, string ExpertName, double Weight, string Model, bool Succeeded, string? Error);

public sealed record AgentTurnResult(string Text, IReadOnlyList<ExpertContribution> Experts, UsageDetails? Usage);

public sealed record ExpertHealth(string Id, string Name, string Model, bool Enabled, bool Routable);

public sealed record AgentHealthReport(
    OrchestrationMode Mode,
    IReadOnlyList<ProviderStatus> Providers,
    IReadOnlyList<ExpertHealth> Experts,
    IReadOnlyList<McpServerStatus> McpServers,
    IReadOnlyList<string> SkillDirectories,
    IReadOnlyList<string> Warnings)
{
    /// <summary>At least one model provider is usable.</summary>
    public bool IsReady => Providers.Any(p => p.IsAvailable);
}
