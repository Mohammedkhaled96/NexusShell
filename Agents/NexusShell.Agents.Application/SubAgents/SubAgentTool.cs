using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Domain.Experts;

namespace NexusShell.Agents.Application.SubAgents;

/// <summary>
/// Exposes an expert as a callable sub-agent tool (<c>ask_&lt;expert&gt;</c>).
/// The sub-agent is built lazily on first use (no cost for sub-agents that are
/// never called) and keeps its own session, so follow-up delegations within the
/// same conversation have memory.
/// </summary>
internal static class SubAgentTool
{
    public static string ToolName(string expertId) => "ask_" + expertId.Replace('-', '_');

    public static AIFunction Create(ExpertProfile expert, Func<CancellationToken, Task<AIAgent>> agentFactory)
    {
        var gate = new SemaphoreSlim(1, 1);
        AIAgent? agent = null;
        AgentSession? session = null;

        async Task<string> AskAsync(
            [Description("The complete, self-contained task or question for this expert, including all context it needs (it cannot see your conversation).")]
            string request,
            CancellationToken cancellationToken)
        {
            // One run at a time per sub-agent session (sessions are not thread-safe).
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                agent ??= await agentFactory(cancellationToken).ConfigureAwait(false);
                session ??= await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

                AgentRunScope.Report(AgentProgressKind.SubAgentStarted, $"Delegating to {expert.Name}", expert.Name);
                var response = await agent.RunAsync(request, session, null, cancellationToken).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(response.Text) ? "(the sub-agent returned no text)" : response.Text;
            }
            finally
            {
                gate.Release();
            }
        }

        return AIFunctionFactory.Create(AskAsync, new AIFunctionFactoryOptions
        {
            Name = ToolName(expert.Id),
            Description = $"Delegate a task to the '{expert.Name}' sub-agent. {expert.Description}",
        });
    }
}
