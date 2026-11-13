using NexusShell.Agents.Domain.Experts;
using NexusShell.Agents.Domain.Orchestration;
using NexusShell.Agents.Domain.Routing;

namespace NexusShell.Agents.Application.Routing;

public sealed record ConversationTurn(string User, string Assistant, IReadOnlyList<string> Experts);

public sealed record RoutingRequest(
    string UserMessage,
    IReadOnlyList<ExpertProfile> Candidates,
    IReadOnlyList<ConversationTurn> RecentHistory,
    RouterSettings Settings);

/// <summary>The gating network of the Mixture-of-Experts: picks the top-k experts and their weights.</summary>
public interface IExpertRouter
{
    Task<RoutingDecision> RouteAsync(RoutingRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A single gate implementation (Strategy pattern).</summary>
internal interface IRoutingGate
{
    string Name { get; }

    /// <summary>Raw (un-truncated) scores for every candidate; null when the gate cannot decide.</summary>
    Task<IReadOnlyList<ExpertScore>?> ScoreAsync(RoutingRequest request, CancellationToken cancellationToken);
}
