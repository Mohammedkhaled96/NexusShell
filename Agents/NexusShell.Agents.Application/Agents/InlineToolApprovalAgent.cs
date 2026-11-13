using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Approval;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Domain.Tools;

namespace NexusShell.Agents.Application.Agents;

/// <summary>
/// Decorator (DelegatingAIAgent) that resolves Agent Framework tool-approval
/// requests in place: when the inner agent pauses with
/// <see cref="ToolApprovalRequestContent"/>, the user is asked through
/// <see cref="IToolApprovalHandler"/> and the run continues in the same session.
/// Because approval is handled inside the agent, an expert behaves the same
/// whether it runs inside the Mixture-of-Experts, as a sub-agent tool, or as a
/// step of a workflow.
/// </summary>
internal sealed class InlineToolApprovalAgent(
    AIAgent innerAgent,
    IToolApprovalHandler approvalHandler,
    ISecretRedactor redactor,
    IReadOnlyDictionary<string, ToolRisk> toolRisks,
    int maxApprovalRounds,
    ILogger logger) : DelegatingAIAgent(innerAgent)
{
    private const int MaxArgumentPreviewChars = 600;

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        // A session is required to continue the same run after the user answers.
        session ??= await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

        var collected = new List<ChatMessage>();
        UsageDetails? usage = null;
        AgentResponse? last = null;
        IEnumerable<ChatMessage> input = messages;

        for (int round = 0; ; round++)
        {
            last = await InnerAgent.RunAsync(input, session, options, cancellationToken).ConfigureAwait(false);
            usage = Merge(usage, last.Usage);

            var requests = new List<ToolApprovalRequestContent>();
            foreach (var message in last.Messages)
            {
                var visible = message.Contents.Where(c => c is not ToolApprovalRequestContent).ToList();
                requests.AddRange(message.Contents.OfType<ToolApprovalRequestContent>());
                if (visible.Count > 0)
                    collected.Add(new ChatMessage(message.Role, visible) { AuthorName = message.AuthorName, MessageId = message.MessageId });
            }

            if (requests.Count == 0) break;

            bool limitReached = round >= maxApprovalRounds;
            var replies = new List<AIContent>(requests.Count);
            foreach (var request in requests)
            {
                bool approved = !limitReached && await DecideAsync(request, cancellationToken).ConfigureAwait(false);
                replies.Add(request.CreateResponse(
                    approved,
                    approved ? null : limitReached ? "Approval limit reached for this request." : "The user denied this tool call. Do not retry it; explain what you would have done instead."));
            }

            input = [new ChatMessage(ChatRole.User, replies)];
        }

        return new AgentResponse(collected)
        {
            AgentId = last.AgentId ?? Id,
            ResponseId = last.ResponseId,
            CreatedAt = last.CreatedAt,
            FinishReason = last.FinishReason,
            Usage = usage,
        };
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Approvals need the complete response of each round; stream the final result.
        var response = await RunCoreAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToAgentResponseUpdates())
            yield return update;
    }

    private async Task<bool> DecideAsync(ToolApprovalRequestContent request, CancellationToken cancellationToken)
    {
        string toolName = request.ToolCall is FunctionCallContent call ? call.Name : request.ToolCall.GetType().Name;
        var scope = AgentRunScope.Current;

        if (scope is not null && scope.ApprovedTools.ContainsKey(toolName))
        {
            logger.LogInformation("Tool {Tool} auto-approved (approved earlier in this conversation).", toolName);
            return true;
        }

        string agentName = Name ?? Id;
        AgentRunScope.Report(AgentProgressKind.ApprovalRequested, $"{agentName} wants to run {toolName}", agentName);

        toolRisks.TryGetValue(toolName, out var risk);
        var approval = new ToolApprovalRequest(
            agentName,
            toolName,
            BuildPreview(request.ToolCall as FunctionCallContent),
            toolRisks.ContainsKey(toolName) ? risk : null);

        ToolApprovalDecision decision;
        try
        {
            decision = await approvalHandler.RequestApprovalAsync(approval, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Approval handler failed for {Tool}; denying.", toolName);
            decision = ToolApprovalDecision.Deny;
        }

        logger.LogInformation("Tool {Tool} requested by {Agent}: {Decision}.", toolName, agentName, decision);

        if (decision == ToolApprovalDecision.ApproveForConversation)
            scope?.ApprovedTools.TryAdd(toolName, true);

        return decision != ToolApprovalDecision.Deny;
    }

    private string BuildPreview(FunctionCallContent? call)
    {
        if (call?.Arguments is null || call.Arguments.Count == 0) return "(no arguments)";

        string json;
        try
        {
            json = JsonSerializer.Serialize(call.Arguments, AIJsonUtilities.DefaultOptions);
        }
        catch (Exception)
        {
            json = string.Join(", ", call.Arguments.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        json = redactor.Redact(json);
        return json.Length <= MaxArgumentPreviewChars ? json : json[..MaxArgumentPreviewChars] + " ...";
    }

    private static UsageDetails? Merge(UsageDetails? total, UsageDetails? next)
    {
        if (next is null) return total;
        if (total is null)
        {
            total = new UsageDetails();
        }
        total.Add(next);
        return total;
    }
}
