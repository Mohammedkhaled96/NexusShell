using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Progress;

namespace NexusShell.Agents.Application.Context;

/// <summary>Writes notes that replace old turns: (old messages, previous notes) -> new notes, or null when it cannot.</summary>
public delegate Task<string?> HistorySummarizer(IReadOnlyList<ChatMessage> oldMessages, string? previousSummary, CancellationToken cancellationToken);

/// <summary>
/// Keeps an expert's history inside its share of the model's context window WITHOUT
/// forgetting: when the history grows beyond the budget, the oldest turns are replaced by
/// a summary (facts, decisions, paths, commands, errors, open tasks) and the most recent
/// turns are kept word for word. If summarizing fails, old turns are dropped as before
/// and the previous summary is kept. The kept window always starts at a user turn, so a
/// tool result is never separated from the tool call that produced it.
/// </summary>
public sealed class CompactingChatReducer : IChatReducer
{
    public const string SummaryHeader = "## Summary of the earlier conversation (your own notes; details beyond them were not kept)";

    private readonly int _budgetTokens;
    private readonly int _maxMessages;
    private readonly int _summaryTokens;
    private readonly HistorySummarizer? _summarize;
    private readonly ILogger _logger;

    public CompactingChatReducer(int budgetTokens, int maxMessages, int summaryTokens, HistorySummarizer? summarize, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budgetTokens, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 2);
        _budgetTokens = budgetTokens;
        _maxMessages = maxMessages;
        _summaryTokens = Math.Max(100, summaryTokens);
        _summarize = summarize;
        _logger = logger;
    }

    public async Task<IEnumerable<ChatMessage>> ReduceAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var all = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        if (all.Count <= _maxMessages && TokenEstimator.Estimate(all) <= _budgetTokens) return all;

        var system = all.Where(m => m.Role == ChatRole.System && !IsSummary(m)).ToList();
        string? previousSummary = all.Where(IsSummary).Select(SummaryText).LastOrDefault();
        var conversation = all.Where(m => m.Role != ChatRole.System).ToList();

        int start = RecentStart(conversation);
        if (start == 0) return all; // a single huge turn: nothing older to compact

        var old = conversation.Take(start).ToList();
        var recent = conversation.Skip(start).ToList();

        string? summary = previousSummary;
        if (_summarize is not null)
        {
            AgentRunScope.Report(AgentProgressKind.CompactingHistory, "Summarizing the earlier conversation to keep it in context");
            try
            {
                summary = await _summarize(old, previousSummary, cancellationToken).ConfigureAwait(false) ?? previousSummary;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Summarizing the history failed; the oldest turns are dropped instead.");
            }
        }

        var result = new List<ChatMessage>(system);
        if (!string.IsNullOrWhiteSpace(summary))
            result.Add(new ChatMessage(ChatRole.System, SummaryHeader + "\n" + Clip(summary.Trim(), _summaryTokens)));
        result.AddRange(recent);

        _logger.LogInformation(
            "History compacted: {Old} older messages {Action}, {Recent} recent kept (~{Tokens} tokens, budget {Budget}).",
            old.Count, string.IsNullOrWhiteSpace(summary) ? "dropped" : "summarized", recent.Count, TokenEstimator.Estimate(result), _budgetTokens);
        return result;
    }

    /// <summary>Index of the first message kept word for word: recent turns within ~60% of the budget, aligned to a user turn.</summary>
    private int RecentStart(List<ChatMessage> conversation)
    {
        int keepBudget = (int)(_budgetTokens * 0.6);
        int start = conversation.Count;
        int used = 0;
        while (start > 0)
        {
            int cost = TokenEstimator.Estimate(conversation[start - 1]);
            if (used + cost > keepBudget || conversation.Count - start + 1 > _maxMessages - 1) break;
            used += cost;
            start--;
        }

        // Move forward to a user turn so no tool result loses its tool call...
        int aligned = start;
        while (aligned < conversation.Count && conversation[aligned].Role != ChatRole.User) aligned++;
        if (aligned < conversation.Count) return aligned;

        // ...or, when not even the last turn fits, keep the whole last turn anyway.
        aligned = Math.Min(start, conversation.Count - 1);
        while (aligned > 0 && conversation[aligned].Role != ChatRole.User) aligned--;
        return aligned;
    }

    public static bool IsSummary(ChatMessage message) =>
        message.Role == ChatRole.System && message.Text.StartsWith(SummaryHeader, StringComparison.Ordinal);

    private static string SummaryText(ChatMessage message) => message.Text[SummaryHeader.Length..].Trim();

    private static string Clip(string text, int tokens)
    {
        int maxChars = tokens * 3;
        return text.Length <= maxChars ? text : text[..maxChars] + "\n(notes shortened)";
    }
}
