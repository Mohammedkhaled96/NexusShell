using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Application.Models;
using NexusShell.Agents.Application.Routing;

namespace NexusShell.Agents.Application.Orchestration;

public sealed record ExpertResult(
    string ExpertId,
    string ExpertName,
    double Weight,
    string Model,
    string Text,
    UsageDetails? Usage,
    string? Error)
{
    public bool Succeeded => Error is null && !string.IsNullOrWhiteSpace(Text);

    /// <summary>Set when the expert failed because of its model / provider.</summary>
    public ModelProviderException? ModelFailure { get; init; }
}

/// <summary>
/// The combiner of the Mixture-of-Experts: merges several expert answers into one,
/// weighting them by their gate scores. Falls back to a deterministic merge if
/// the synthesizer model is unavailable.
/// </summary>
internal sealed class ResponseSynthesizer(
    IModelResolver modelResolver,
    IChatClientFactory chatClientFactory,
    IModelHealthRegistry modelHealth,
    ILogger<ResponseSynthesizer> logger)
{
    private const int MaxExpertChars = 12_000;

    public async Task<(string Text, UsageDetails? Usage)> SynthesizeAsync(
        string userRequest,
        IReadOnlyList<ExpertResult> results,
        IReadOnlyList<ConversationTurn> history,
        string sharedInstructions,
        string synthesizerModel,
        CancellationToken cancellationToken)
    {
        if (results.Count == 1) return (results[0].Text, null);

        var model = await modelResolver.ResolveAsync(synthesizerModel, cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            logger.LogWarning("No synthesizer model available; merging expert answers deterministically.");
            return (MergeDeterministically(results), null);
        }

        IChatClient client = chatClientFactory.CreateChatClient(model.Provider, model.ModelId);
        try
        {
            var response = await client.GetResponseAsync(
                BuildPrompt(userRequest, results, history, sharedInstructions),
                new ChatOptions { Temperature = 0.2f },
                cancellationToken).ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(response.Text)
                ? (MergeDeterministically(results), response.Usage)
                : (response.Text, response.Usage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ModelFailures.Find(ex) is { CanFallBack: true } failure)
                modelHealth.ReportUnusable(failure.ProviderId, failure.ModelId, failure.Message, failure.QuarantineDuration);
            logger.LogWarning(ex, "Synthesizer ({Model}) failed; merging expert answers deterministically.", model);
            return (MergeDeterministically(results), null);
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    internal static List<ChatMessage> BuildPrompt(
        string userRequest,
        IReadOnlyList<ExpertResult> results,
        IReadOnlyList<ConversationTurn> history,
        string sharedInstructions)
    {
        var system = new StringBuilder()
            .AppendLine("You are the combiner of a mixture-of-experts assistant inside the NexusShell terminal.")
            .AppendLine("Several specialist experts answered the same request. Each answer has a gate weight: higher means more relevant.")
            .AppendLine("Write ONE final answer for the user:")
            .AppendLine("- keep every correct and relevant detail; remove duplication;")
            .AppendLine("- when experts disagree, prefer the higher-weighted expert and briefly mention a real disagreement;")
            .AppendLine("- never invent commands, flags, paths or facts that are not in the expert answers;")
            .AppendLine("- copy commands, code and file paths exactly as written;")
            .AppendLine("- do not talk about the experts or the merging process unless it helps the user.");

        if (!string.IsNullOrWhiteSpace(sharedInstructions))
            system.AppendLine().AppendLine(sharedInstructions.Trim());

        var user = new StringBuilder();
        if (history.Count > 0)
        {
            user.AppendLine("Earlier in this conversation:");
            foreach (var turn in history.TakeLast(3))
                user.Append("User: ").AppendLine(Truncate(turn.User, 500)).Append("Assistant: ").AppendLine(Truncate(turn.Assistant, 800));
            user.AppendLine();
        }

        user.AppendLine("Request:").AppendLine(userRequest).AppendLine();
        foreach (var result in results.OrderByDescending(r => r.Weight))
        {
            user.Append("### Expert: ").Append(result.ExpertName).Append(" (weight ").Append(result.Weight.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).AppendLine(")")
                .AppendLine(Truncate(result.Text, MaxExpertChars))
                .AppendLine();
        }

        return [new ChatMessage(ChatRole.System, system.ToString()), new ChatMessage(ChatRole.User, user.ToString())];
    }

    internal static string MergeDeterministically(IReadOnlyList<ExpertResult> results)
    {
        if (results.Count == 1) return results[0].Text;
        var sb = new StringBuilder();
        foreach (var result in results.OrderByDescending(r => r.Weight))
        {
            if (sb.Length > 0) sb.AppendLine().AppendLine();
            sb.Append("## ").AppendLine(result.ExpertName).AppendLine(result.Text.Trim());
        }
        return sb.ToString();
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + " ...";
}
