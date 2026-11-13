using System.Text;
using NexusShell.Agents.Domain.Experts;
using NexusShell.Agents.Domain.Routing;

namespace NexusShell.Agents.Application.Routing;

/// <summary>
/// Offline lexical gate. Scores experts by keyword/phrase hits, explicit mentions
/// of the expert and overlap with its description. Free, instant, deterministic,
/// and works with no model at all, so it is also the fallback for the LLM gate.
/// Unicode-aware (works for Arabic keywords too).
/// </summary>
internal sealed class KeywordRoutingGate : IRoutingGate
{
    private const double KeywordWeight = 2.0;
    private const double MentionWeight = 4.0;
    private const double DescriptionWeight = 0.35;
    private const double MaxDescriptionEvidence = 1.5;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "from", "your", "into", "about", "what", "when", "where",
        "which", "how", "can", "you", "are", "use", "using", "help", "please", "need", "want", "make", "have",
        "expert", "experts", "tasks", "task", "questions", "question", "other", "such", "also", "them", "they",
    };

    public string Name => "keyword";

    public Task<IReadOnlyList<ExpertScore>?> ScoreAsync(RoutingRequest request, CancellationToken cancellationToken)
    {
        string text = " " + Normalize(request.UserMessage) + " ";
        var messageTokens = Tokenize(request.UserMessage).ToHashSet(StringComparer.Ordinal);

        var scores = request.Candidates
            .Select(expert => Score(expert, text, messageTokens))
            .ToList();

        return Task.FromResult<IReadOnlyList<ExpertScore>?>(scores);
    }

    private static ExpertScore Score(ExpertProfile expert, string paddedText, HashSet<string> messageTokens)
    {
        double evidence = 0;
        var hits = new List<string>();

        foreach (string keyword in expert.Keywords)
        {
            string normalized = Normalize(keyword);
            if (normalized.Length == 0) continue;
            if (paddedText.Contains(" " + normalized + " ", StringComparison.Ordinal))
            {
                evidence += KeywordWeight;
                hits.Add(keyword);
            }
        }

        string id = Normalize(expert.Id.Replace('-', ' '));
        string name = Normalize(expert.Name);
        if (paddedText.Contains(" " + id + " ", StringComparison.Ordinal) ||
            (name.Length > 0 && paddedText.Contains(" " + name + " ", StringComparison.Ordinal)))
        {
            evidence += MentionWeight;
            hits.Add("@" + expert.Id);
        }

        double descriptionEvidence = Tokenize(expert.Description)
            .Where(t => t.Length >= 4 && !StopWords.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .Count(messageTokens.Contains) * DescriptionWeight;
        evidence += Math.Min(descriptionEvidence, MaxDescriptionEvidence);

        string? reason = hits.Count > 0 ? "matched: " + string.Join(", ", hits) : null;
        return new ExpertScore(expert.Id, RoutingMath.Confidence(evidence), reason);
    }

    private static IEnumerable<string> Tokenize(string text) =>
        Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Lower-case, letters/digits only, single spaces. Keeps '.', '#', '+' inside tokens (".net", "c#", "c++").</summary>
    internal static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool lastSpace = true;
        foreach (char raw in text)
        {
            char ch = char.ToLowerInvariant(raw);
            bool keep = char.IsLetterOrDigit(ch) || ch is '#' or '+' || (ch == '.' && !lastSpace);
            if (keep)
            {
                sb.Append(ch);
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }
        return sb.ToString().Trim().TrimEnd('.');
    }
}
