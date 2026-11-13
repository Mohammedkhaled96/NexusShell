using Microsoft.Extensions.Logging;
using NexusShell.Agents.Domain.Routing;

namespace NexusShell.Agents.Application.Routing;

/// <summary>
/// Router facade. Strategy is chosen per request from <see cref="Domain.Orchestration.RouterSettings.Strategy"/>:
/// <list type="bullet">
/// <item><b>Keyword</b>: lexical gate only.</item>
/// <item><b>Llm</b>: LLM gate; keyword gate only if the LLM fails.</item>
/// <item><b>Hybrid</b> (default): blend 75% LLM + 25% keyword evidence; keyword gate if the LLM fails.</item>
/// </list>
/// The blended scores go through sparse top-k selection.
/// </summary>
internal sealed class ExpertRouter(
    KeywordRoutingGate keywordGate,
    LlmRoutingGate llmGate,
    ILogger<ExpertRouter> logger) : IExpertRouter
{
    private const double LlmWeight = 0.75;

    public async Task<RoutingDecision> RouteAsync(RoutingRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Candidates.Count == 0) return RoutingDecision.Empty("none", "No routable experts are enabled.");

        if (request.Candidates.Count == 1)
            return new RoutingDecision([new ExpertScore(request.Candidates[0].Id, 1.0, "only expert")], "single");

        var keyword = await keywordGate.ScoreAsync(request, cancellationToken).ConfigureAwait(false) ?? [];

        if (request.Settings.Strategy == RoutingStrategyKind.Keyword)
            return Decide(keyword, request, "keyword");

        var llm = await llmGate.ScoreAsync(request, cancellationToken).ConfigureAwait(false);
        if (llm is null)
            return Decide(keyword, request, "keyword-fallback");

        if (request.Settings.Strategy == RoutingStrategyKind.Llm)
            return Decide(llm, request, "llm");

        var keywordById = keyword.ToDictionary(s => s.ExpertId, StringComparer.OrdinalIgnoreCase);
        var llmById = llm.ToDictionary(s => s.ExpertId, StringComparer.OrdinalIgnoreCase);

        var blended = request.Candidates.Select(c =>
        {
            llmById.TryGetValue(c.Id, out var l);
            keywordById.TryGetValue(c.Id, out var k);
            double score = (LlmWeight * (l?.Score ?? 0)) + ((1 - LlmWeight) * (k?.Score ?? 0));
            return new ExpertScore(c.Id, score, l?.Reason ?? k?.Reason);
        });

        return Decide(blended, request, "hybrid");
    }

    private RoutingDecision Decide(IEnumerable<ExpertScore> scores, RoutingRequest request, string strategy)
    {
        var selected = RoutingMath.SelectTopK(scores, request.Settings);
        logger.LogInformation(
            "Router ({Strategy}) selected {Experts}",
            strategy,
            selected.Count == 0 ? "(none)" : string.Join(", ", selected.Select(s => $"{s.ExpertId}:{s.Score:0.00}")));
        return new RoutingDecision(selected, strategy);
    }
}
