using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Application.Models;

namespace NexusShell.Agents.Application.Context;

/// <summary>
/// Turns old conversation turns into compact notes with the quickest available model
/// ("fast"), merging them with the previous notes. The prompt forbids adding anything
/// that is not in the conversation, so the notes never become a source of made-up facts.
/// </summary>
public sealed class ConversationSummarizer(
    IModelResolver modelResolver,
    IChatClientFactory chatClientFactory,
    ILogger<ConversationSummarizer> logger)
{
    private const int MaxToolResultChars = 2_000;

    private const string Instructions =
        """
        You compress a conversation between a user and an AI assistant into working notes the assistant will read instead of the old messages.
        Keep: the user's goals and requests, facts the user stated, decisions, preferences, file and folder paths, commands that were run and their important results, errors and how they were solved, and tasks that are still open.
        Rules:
        - Write only what is in the conversation or the previous notes. Never add, guess or "fix" facts.
        - Keep exact names, paths, versions, numbers and commands.
        - Merge the previous notes with the new messages; drop what was superseded or finished.
        - Plain short lines starting with "- ", no headings, no Markdown emphasis.
        - Use the language the user wrote in.
        """;

    public async Task<string?> SummarizeAsync(IReadOnlyList<ChatMessage> oldMessages, string? previousSummary, int maxTokens, CancellationToken cancellationToken)
    {
        if (oldMessages.Count == 0) return previousSummary;

        var model = await modelResolver.ResolveAsync("fast", cancellationToken).ConfigureAwait(false);
        if (model is null) return null;

        // The transcript must fit the summarizing model itself: keep its newest part.
        int transcriptBudget = (int)(ContextBudget.For(model.Provider, model.ModelId).Usable * 0.7);
        string transcript = Transcript(oldMessages, transcriptBudget);

        var request = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(previousSummary))
            request.AppendLine("Previous notes:").AppendLine(previousSummary.Trim()).AppendLine();
        request.AppendLine("Messages to add to the notes:").AppendLine(transcript).AppendLine()
            .Append("Write the merged notes in at most ").Append(Math.Max(80, maxTokens * 3 / 4)).Append(" words.");

        var client = chatClientFactory.CreateChatClient(model.Provider, model.ModelId);
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, request.ToString())],
            new ChatOptions { Temperature = 0, MaxOutputTokens = Math.Max(256, maxTokens) },
            cancellationToken).ConfigureAwait(false);

        string notes = response.Text.Trim();
        logger.LogInformation("Summarized {Count} older messages with {Model} into {Tokens} tokens of notes.", oldMessages.Count, model, TokenEstimator.Estimate(notes));
        return notes.Length == 0 ? null : notes;
    }

    internal static string Transcript(IReadOnlyList<ChatMessage> messages, int budgetTokens)
    {
        var lines = new List<string>();
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                string? line = content switch
                {
                    TextContent text when !string.IsNullOrWhiteSpace(text.Text) => $"{Speaker(message.Role)}: {text.Text.Trim()}",
                    FunctionCallContent call => $"Assistant ran tool {call.Name}({string.Join(", ", (call.Arguments ?? new Dictionary<string, object?>()).Select(a => $"{a.Key}={a.Value}"))})",
                    FunctionResultContent result => $"Tool result: {Clip(result.Result?.ToString(), MaxToolResultChars)}",
                    _ => null,
                };
                if (line is not null) lines.Add(line);
            }
        }

        // Newest lines first until the budget is used, then restore chronological order.
        var kept = new List<string>();
        int used = 0;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            int cost = TokenEstimator.Estimate(lines[i]);
            if (used + cost > budgetTokens && kept.Count > 0) break;
            kept.Add(lines[i]);
            used += cost;
        }
        kept.Reverse();
        return string.Join('\n', kept);
    }

    private static string Speaker(ChatRole role) => role == ChatRole.User ? "User" : role == ChatRole.Tool ? "Tool" : "Assistant";

    private static string Clip(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max] + " ...";
}
