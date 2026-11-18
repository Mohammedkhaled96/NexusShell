using System.Collections.Concurrent;
using NexusShell.Agents.Application.Abstractions.Models;

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
    ToolFailed,
    ApprovalRequested,
    ModelUnavailable,
    FallbackExpert,
    WaitingForQuota,
    Synthesizing,
    Warning,
}

/// <summary>
/// One step of an agent run. <see cref="Message"/> is a plain English description
/// (logs, fallback display); the structured fields let the presentation layer
/// narrate the step naturally and in the user's language (screen readers).
/// </summary>
public sealed record AgentProgress(AgentProgressKind Kind, string Message, string? AgentName = null)
{
    /// <summary>Display names of the experts involved (for <see cref="AgentProgressKind.ExpertsSelected"/>).</summary>
    public IReadOnlyList<string> Experts { get; init; } = [];

    /// <summary>Tool being run, approved or that failed.</summary>
    public string? ToolName { get; init; }

    /// <summary>Model in use ("provider/model"), or the model that became unavailable.</summary>
    public string? Model { get; init; }

    /// <summary>Provider involved in a failure (for <see cref="AgentProgressKind.ModelUnavailable"/>).</summary>
    public string? Provider { get; init; }

    /// <summary>Why the model or provider failed, so the narration can say the real reason.</summary>
    public ModelFailureKind? Failure { get; init; }

    /// <summary>How long the agent waits (for <see cref="AgentProgressKind.WaitingForQuota"/>).</summary>
    public TimeSpan? Delay { get; init; }
}

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

    /// <summary>Folder chosen by the user: tools resolve relative paths and run commands there.</summary>
    public string? WorkingDirectory { get; init; }

    public static IDisposable Begin(AgentRunScope scope)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = scope;
        return new Restore(previous);
    }

    public static void Report(AgentProgressKind kind, string message, string? agentName = null) =>
        Report(new AgentProgress(kind, message, agentName));

    public static void Report(AgentProgress progress)
    {
        try
        {
            CurrentScope.Value?.Progress?.Report(progress);
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
