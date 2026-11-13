using System.Collections.Concurrent;

namespace NexusShell.Agents.Application.Progress;

public enum AgentProgressKind
{
    Routing,
    ExpertsSelected,
    ExpertStarted,
    ExpertCompleted,
    SubAgentStarted,
    ToolStarted,
    ToolCompleted,
    ApprovalRequested,
    Synthesizing,
    Warning,
}

/// <summary>A human-readable progress event (suitable for a status line or a screen-reader announcement).</summary>
public sealed record AgentProgress(AgentProgressKind Kind, string Message, string? AgentName = null);

/// <summary>
/// Ambient per-turn context that flows with async calls into experts, sub-agents
/// and tool middleware (AsyncLocal), so deep components can report progress and
/// share the conversation's "approved for this conversation" tool list without
/// threading parameters through the Agent Framework.
/// </summary>
public sealed class AgentRunScope
{
    private static readonly AsyncLocal<AgentRunScope?> CurrentScope = new();

    public AgentRunScope(IProgress<AgentProgress>? progress, ConcurrentDictionary<string, bool> approvedTools)
    {
        Progress = progress;
        ApprovedTools = approvedTools;
    }

    public static AgentRunScope? Current => CurrentScope.Value;

    public IProgress<AgentProgress>? Progress { get; }

    /// <summary>Tools the user approved for the whole conversation.</summary>
    public ConcurrentDictionary<string, bool> ApprovedTools { get; }

    public static IDisposable Begin(AgentRunScope scope)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = scope;
        return new Restore(previous);
    }

    public static void Report(AgentProgressKind kind, string message, string? agentName = null)
    {
        try
        {
            CurrentScope.Value?.Progress?.Report(new AgentProgress(kind, message, agentName));
        }
        catch
        {
            // Progress is best-effort UI feedback; it must never break an agent run.
        }
    }

    private sealed class Restore(AgentRunScope? previous) : IDisposable
    {
        public void Dispose() => CurrentScope.Value = previous;
    }
}
