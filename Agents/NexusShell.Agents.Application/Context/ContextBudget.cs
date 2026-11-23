using NexusShell.Agents.Domain.Models;
using NexusShell.Agents.Domain.Providers;

namespace NexusShell.Agents.Application.Context;

/// <summary>
/// How one model's context window is shared, so large windows are used fully and small
/// ones never overflow (an overflowing prompt is cut by the server and the model then
/// answers without the facts it lost):
/// <code>
/// context window
///  ├─ reserved for the answer          1/8 (1K..16K tokens)
///  └─ usable
///      ├─ expert history                45%  (summary of old turns + recent turns)
///      ├─ catch-up from other experts   10%
///      ├─ recalled memories              5%  (and at most the configured memory budget)
///      ├─ attachments                   25%
///      └─ instructions, tools, skills   15%
/// </code>
/// </summary>
public sealed record ContextBudget(int ContextWindow)
{
    public int ReservedOutput => Math.Clamp(ContextWindow / 8, 1_024, 16_384);

    public int Usable => Math.Max(1_024, ContextWindow - ReservedOutput);

    public int HistoryTokens => (int)(Usable * 0.45);

    public int CatchUpTokens => (int)(Usable * 0.10);

    /// <summary>Notes that replace summarized turns (part of the history share).</summary>
    public int SummaryTokens => Math.Clamp(HistoryTokens / 5, 300, 8_000);

    public int MemoryTokens(int configuredMaximum) => Math.Min(Math.Max(200, configuredMaximum), Math.Max(200, (int)(Usable * 0.05)));

    /// <summary>Attachment text in characters (3 per token: pessimistic for code and non-Latin text).</summary>
    public int AttachmentChars => Math.Clamp((int)(Usable * 0.25) * 3, 8_000, 3_000_000);

    public static ContextBudget For(ProviderProfile provider, string modelId) => new(ModelContextWindows.Resolve(provider, modelId));
}
