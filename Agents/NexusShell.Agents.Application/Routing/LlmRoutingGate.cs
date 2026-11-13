using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Application.Models;
using NexusShell.Agents.Domain.Routing;

namespace NexusShell.Agents.Application.Routing;

/// <summary>
/// LLM gate: a (preferably small, local) model reads the expert catalogue and
/// returns JSON weights. Output parsing is defensive so weak local models that
/// wrap JSON in prose still work; any failure returns null and the router
/// falls back to the keyword gate.
/// </summary>
internal sealed class LlmRoutingGate(
    IModelResolver modelResolver,
    IChatClientFactory chatClientFactory,
    IModelHealthRegistry modelHealth,
    ILogger<LlmRoutingGate> logger) : IRoutingGate
{
    private const int MaxRequestChars = 4_000;

    public string Name => "llm";

    public async Task<IReadOnlyList<ExpertScore>?> ScoreAsync(RoutingRequest request, CancellationToken cancellationToken)
    {
        if (request.Candidates.Count == 0) return [];

        var model = await modelResolver.ResolveAsync(request.Settings.Model, cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            logger.LogInformation("LLM router has no available model; using keyword gate.");
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Settings.Timeout);

        try
        {
            IChatClient client = chatClientFactory.CreateChatClient(model.Provider, model.ModelId);
            try
            {
                var response = await client.GetResponseAsync(
                    BuildPrompt(request),
                    new ChatOptions { Temperature = 0, MaxOutputTokens = 400 },
                    timeout.Token).ConfigureAwait(false);

                var scores = Parse(response.Text, request);
                if (scores is null)
                    logger.LogWarning("LLM router ({Model}) returned unparseable output; using keyword gate.", model);
                return scores;
            }
            finally
            {
                (client as IDisposable)?.Dispose();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("LLM router timed out after {Timeout}; using keyword gate.", request.Settings.Timeout);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ModelFailures.Find(ex) is { CanFallBack: true } failure)
                modelHealth.ReportUnusable(failure.ProviderId, failure.ModelId, failure.Message, failure.QuarantineDuration);
            logger.LogWarning(ex, "LLM router failed; using keyword gate.");
            return null;
        }
    }

    internal static List<ChatMessage> BuildPrompt(RoutingRequest request)
    {
        var catalogue = new StringBuilder();
        foreach (var expert in request.Candidates)
        {
            catalogue.Append("- id: ").Append(expert.Id).Append('\n')
                     .Append("  description: ").Append(expert.Description).Append('\n');
            if (expert.Keywords.Count > 0)
                catalogue.Append("  keywords: ").Append(string.Join(", ", expert.Keywords.Take(25))).Append('\n');
        }

        var history = new StringBuilder();
        foreach (var turn in request.RecentHistory.TakeLast(3))
        {
            history.Append("User: ").Append(Truncate(turn.User, 400)).Append('\n')
                   .Append("Answered by: ").Append(string.Join(", ", turn.Experts)).Append('\n');
        }

        string system =
            "You are the gating network of a mixture-of-experts assistant embedded in a terminal app.\n" +
            "Pick the expert(s) best suited to handle the user's request and give each a relevance score between 0 and 1.\n" +
            $"Select at most {Math.Max(1, request.Settings.TopK)} experts. Select more than one ONLY when the request clearly needs several distinct kinds of expertise.\n" +
            "Respond with JSON only, no prose, exactly in this shape:\n" +
            "{\"experts\":[{\"id\":\"<expert id>\",\"score\":0.9,\"reason\":\"<short reason>\"}]}";

        string user =
            "Experts:\n" + catalogue +
            (history.Length > 0 ? "\nRecent conversation:\n" + history : string.Empty) +
            "\nRequest:\n" + Truncate(request.UserMessage, MaxRequestChars);

        return [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, user)];
    }

    internal static IReadOnlyList<ExpertScore>? Parse(string? text, RoutingRequest request)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        int start = text.IndexOf('{', StringComparison.Ordinal);
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try
        {
            using var doc = JsonDocument.Parse(text.AsMemory(start, end - start + 1), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("experts", out var experts) || experts.ValueKind != JsonValueKind.Array) return null;

            var known = request.Candidates.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
            var scores = new Dictionary<string, ExpertScore>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in experts.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String) continue;

                string id = idElement.GetString()!.Trim();
                if (!known.TryGetValue(id, out var expert)) continue;

                double score = 1.0;
                if (item.TryGetProperty("score", out var scoreElement))
                {
                    if (scoreElement.ValueKind == JsonValueKind.Number) score = scoreElement.GetDouble();
                    else if (scoreElement.ValueKind == JsonValueKind.String && double.TryParse(scoreElement.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)) score = parsed;
                }

                string? reason = item.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
                    ? reasonElement.GetString()
                    : null;

                scores[expert.Id] = new ExpertScore(expert.Id, RoutingMath.Clamp01(score), reason);
            }

            return scores.Count == 0 ? null : scores.Values.ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + " ...";
}
