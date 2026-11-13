namespace NexusShell.Agents.Domain.Routing;

/// <summary>Gate output for one expert: a normalised weight in [0, 1].</summary>
public sealed record ExpertScore(string ExpertId, double Score, string? Reason = null);

/// <summary>Result of the gating network: the top-k experts and their weights.</summary>
public sealed record RoutingDecision(IReadOnlyList<ExpertScore> Selected, string Strategy, string? Rationale = null)
{
    public bool IsEmpty => Selected.Count == 0;

    public static RoutingDecision Empty(string strategy, string? rationale = null) => new([], strategy, rationale);
}

public enum RoutingStrategyKind
{
    /// <summary>Offline lexical gate over expert keywords and descriptions. Zero cost, deterministic.</summary>
    Keyword,

    /// <summary>A (usually small and fast) LLM classifies the request.</summary>
    Llm,

    /// <summary>LLM gate with automatic fallback to the keyword gate (default).</summary>
    Hybrid,
}
