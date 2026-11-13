namespace NexusShell.Agents.Domain.Tools;

/// <summary>How much harm a tool can do. Drives the human-in-the-loop approval policy.</summary>
public enum ToolRisk
{
    /// <summary>Only reads local, non-sensitive state.</summary>
    ReadOnly,

    /// <summary>Talks to the network (possible data egress).</summary>
    Network,

    /// <summary>Changes files or settings.</summary>
    Mutating,

    /// <summary>Runs arbitrary commands or can delete data.</summary>
    Destructive,
}

/// <summary>Which risk levels run without asking the user.</summary>
public sealed record ToolApprovalPolicy
{
    public static ToolApprovalPolicy Default { get; } = new();

    public IReadOnlySet<ToolRisk> AutoApprove { get; init; } = new HashSet<ToolRisk> { ToolRisk.ReadOnly };

    public bool RequiresApproval(ToolRisk risk) => !AutoApprove.Contains(risk);
}
