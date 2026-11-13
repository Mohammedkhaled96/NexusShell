using NexusShell.Agents.Domain.Orchestration;
using NexusShell.Agents.Domain.Routing;

namespace NexusShell.Agents.Application.Routing;

internal static class RoutingMath
{
    /// <summary>
    /// Sparse top-k gating: keep experts above the minimum weight, drop
    /// secondary experts that are much weaker than the winner, cap at k.
    /// </summary>
    public static IReadOnlyList<ExpertScore> SelectTopK(IEnumerable<ExpertScore> scores, RouterSettings settings, double secondaryRatio = 0.5)
    {
        int k = Math.Max(1, settings.TopK);
        var ranked = scores
            .Where(s => s.Score >= settings.MinScore)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.ExpertId, StringComparer.Ordinal)
            .ToList();

        if (ranked.Count == 0) return [];

        double top = ranked[0].Score;
        return ranked
            .Where((s, index) => index == 0 || s.Score >= top * secondaryRatio)
            .Take(k)
            .ToList();
    }

    /// <summary>Saturating confidence: 0 -> 0, grows quickly with evidence, approaches 1.</summary>
    public static double Confidence(double evidence) => evidence <= 0 ? 0 : 1 - Math.Exp(-evidence / 3.0);

    public static double Clamp01(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
}
