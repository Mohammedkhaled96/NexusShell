using System.Globalization;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Domain.Memory;

namespace NexusShell.Agents.Application.Memory;

/// <summary>
/// Agent Framework context provider: before every model call it recalls the long-term
/// memories relevant to the user's message (within a token budget) and adds them to the
/// instructions, with how each fact was learned and the rules for using and saving memory.
/// A failure here never breaks the turn: the expert simply answers without memories.
/// </summary>
internal sealed class MemoryContextProvider(
    IAgentMemory memory,
    MemorySettings settings,
    int budgetTokens,
    string expertName,
    ILogger logger) : AIContextProvider
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        string query = string.Join('\n', (context.AIContext.Messages ?? [])
            .Where(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))
            .TakeLast(2)
            .Select(m => m.Text));

        IReadOnlyList<MemoryEntry> recalled = [];
        try
        {
            if (query.Length > 0)
            {
                recalled = await memory.RecallAsync(query, AgentRunScope.Current?.WorkingDirectory, settings.RecallTopK, budgetTokens, cancellationToken).ConfigureAwait(false);
                // Retries on another model recall the same memories: count and announce them once per message.
                if (recalled.Count > 0 && AgentRunScope.FirstTimeThisTurn("memory-recalled:" + expertName))
                {
                    await memory.MarkUsedAsync(recalled.Select(e => e.Id).ToList(), cancellationToken).ConfigureAwait(false);
                    AgentRunScope.Report(new AgentProgress(AgentProgressKind.MemoryRecalled, $"{expertName}: using {recalled.Count} saved memories", expertName) { Count = recalled.Count });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recalling memories failed for {Expert}; answering without them.", expertName);
        }

        return new AIContext { Instructions = MemoryPrompt.Compose(recalled) };
    }
}

/// <summary>The memory section of an expert's instructions.</summary>
public static class MemoryPrompt
{
    public const string Header = "## Long-term memory";

    private const string Rules =
        """
        How to use memory:
        - Base answers on these facts, the conversation and tool results, not on guesses. You may cite a memory as [id].
        - Facts about the machine or a project (versions, paths, settings, file contents) can change: confirm them with a tool before acting on them.
        - If the user now says something different, the user is right: correct the memory with update_memory, or remove it with forget_memory.
        - Save with remember only lasting, useful facts: the user's preferences, facts about their projects and machine, decisions, and corrections of your mistakes. Source "user" for what the user said, "tool" for what a tool showed, "agent" for your own conclusions.
        - Never save secrets (passwords, keys, tokens), one-time details or anything you are unsure about.
        - When the user asks you to remember or forget something, do it with the memory tools and confirm in one sentence.
        """;

    public static string Compose(IReadOnlyList<MemoryEntry> recalled)
    {
        var sb = new StringBuilder().AppendLine(Header);
        if (recalled.Count == 0)
        {
            sb.AppendLine("No saved memory matches this message. Use recall_memory if you need to look further.");
        }
        else
        {
            sb.AppendLine("Facts saved in earlier conversations (id, how it was learned, date):");
            foreach (var entry in recalled)
            {
                sb.Append("- [").Append(entry.Id).Append("] (").Append(SourceText(entry.Source))
                  .Append(", ").Append(entry.UpdatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                if (entry.Scope is not null) sb.Append(", for ").Append(entry.Scope);
                if (entry.Pinned) sb.Append(", pinned");
                sb.Append(") ").AppendLine(entry.Content.Replace('\n', ' ').Trim());
            }
        }
        return sb.AppendLine().Append(Rules).ToString();
    }

    public static string SourceText(MemorySource source) => source switch
    {
        MemorySource.User => "the user said",
        MemorySource.Tool => "verified with a tool",
        _ => "your own unverified note",
    };
}
