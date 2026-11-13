using NexusShell.Agents.Domain.Routing;

namespace NexusShell.Agents.Domain.Orchestration;

public enum OrchestrationMode
{
    /// <summary>Gate selects top-k experts, they run in parallel, a synthesizer merges their answers.</summary>
    MixtureOfExperts,

    /// <summary>A lead expert answers and delegates to every other expert exposed as a sub-agent tool.</summary>
    Coordinator,

    /// <summary>A fixed pipeline of experts (Agent Framework sequential workflow).</summary>
    Sequential,
}

public sealed record RouterSettings
{
    public RoutingStrategyKind Strategy { get; init; } = RoutingStrategyKind.Hybrid;

    /// <summary>Model used by the LLM gate. Default "fast" = each provider's quick model (local is ideal).</summary>
    public string Model { get; init; } = "fast";

    /// <summary>Maximum number of experts activated per request (k in top-k gating).</summary>
    public int TopK { get; init; } = 2;

    /// <summary>Experts with a lower weight are not activated.</summary>
    public double MinScore { get; init; } = 0.25;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
}

public sealed record OrchestrationSettings
{
    public OrchestrationMode Mode { get; init; } = OrchestrationMode.MixtureOfExperts;

    public RouterSettings Router { get; init; } = new();

    /// <summary>Model that merges multiple expert answers.</summary>
    public string SynthesizerModel { get; init; } = "auto";

    /// <summary>Expert used when the gate selects nothing.</summary>
    public string DefaultExpert { get; init; } = "general";

    /// <summary>Lead expert for <see cref="OrchestrationMode.Coordinator"/>.</summary>
    public string CoordinatorExpert { get; init; } = "general";

    /// <summary>Expert pipeline for <see cref="OrchestrationMode.Sequential"/>.</summary>
    public IReadOnlyList<string> Sequence { get; init; } = [];

    /// <summary>How deep sub-agents may delegate to further sub-agents.</summary>
    public int MaxSubAgentDepth { get; init; } = 2;

    /// <summary>Maximum tool-approval round trips per agent run.</summary>
    public int MaxApprovalRounds { get; init; } = 8;

    /// <summary>Conversation turns shared with the gate, synthesizer and experts that join late.</summary>
    public int SharedHistoryTurns { get; init; } = 8;

    /// <summary>Messages kept in each expert's own session memory.</summary>
    public int ExpertMemoryMessages { get; init; } = 40;
}
