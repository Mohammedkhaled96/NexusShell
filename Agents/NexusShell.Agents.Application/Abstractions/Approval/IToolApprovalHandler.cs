using NexusShell.Agents.Domain.Tools;

namespace NexusShell.Agents.Application.Abstractions.Approval;

public sealed record ToolApprovalRequest(string AgentName, string ToolName, string ArgumentsPreview, ToolRisk? Risk);

public enum ToolApprovalDecision
{
    Deny,
    ApproveOnce,

    /// <summary>Approve this tool for the rest of the conversation.</summary>
    ApproveForConversation,
}

/// <summary>
/// Port: human-in-the-loop approval for risky tool calls. The UI implements it
/// with a dialog; without a UI the default implementation denies everything.
/// </summary>
public interface IToolApprovalHandler
{
    Task<ToolApprovalDecision> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>Secure default for headless hosts: every risky tool call is denied.</summary>
public sealed class DenyAllToolApprovalHandler : IToolApprovalHandler
{
    public Task<ToolApprovalDecision> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ToolApprovalDecision.Deny);
}
