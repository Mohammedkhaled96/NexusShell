using System.Globalization;
using System.Text;
using NexusShell.Agents.Domain.Memory;

namespace NexusShell.Agents.Application.Memory;

/// <summary>
/// Finds the memories relevant to a message: BM25 text relevance (works offline, no
/// embeddings, English and Arabic), lightly boosted by verification, recency and use.
/// Pinned memories are always included. Pure and deterministic (unit-tested).
/// </summary>
public static class MemoryRanker
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "with", "is", "are", "was", "were", "be", "it", "its",
        "this", "that", "these", "those", "i", "me", "my", "you", "your", "we", "our", "do", "does", "did", "can", "could",
        "what", "which", "who", "how", "why", "when", "where", "please", "about", "from", "at", "by", "as", "if", "so", "not",
        "في", "من", "على", "الى", "إلى", "عن", "ما", "ماذا", "هل", "هذا", "هذه", "انا", "أنا", "انت", "أنت", "مع", "او", "أو", "و",
    };

    public sealed record Scored(MemoryEntry Entry, double Score);

    /// <summary>Relevant memories for <paramref name="query"/> in <paramref name="folder"/>, best first.</summary>
    public static IReadOnlyList<Scored> Rank(IReadOnlyList<MemoryEntry> entries, string query, string? folder, int maxResults, DateTimeOffset now)
    {
        var candidates = entries.Where(e => e.AppliesTo(folder)).ToList();
        if (candidates.Count == 0) return [];

        var queryTerms = Tokenize(query).Distinct(StringComparer.Ordinal).ToList();
        var documents = candidates.Select(e => Tokenize(Document(e))).ToList();
        double averageLength = Math.Max(1, documents.Average(d => d.Count));

        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            foreach (string term in document.Distinct(StringComparer.Ordinal))
                documentFrequency[term] = documentFrequency.GetValueOrDefault(term) + 1;
        }

        var results = new List<Scored>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var entry = candidates[i];
            var document = documents[i];
            double relevance = 0;
            foreach (string term in queryTerms)
            {
                int frequency = document.Count(t => t == term);
                if (frequency == 0) continue;
                int df = documentFrequency[term];
                double idf = Math.Log(1 + (candidates.Count - df + 0.5) / (df + 0.5));
                relevance += idf * frequency * (K1 + 1) / (frequency + K1 * (1 - B + B * document.Count / averageLength));
            }

            if (relevance <= 0 && !entry.Pinned) continue;

            double days = Math.Max(0, (now - (entry.LastUsedAt ?? entry.UpdatedAt)).TotalDays);
            double score = relevance * (entry.IsVerified ? 1.0 : 0.8)
                + (relevance > 0 ? 0.15 * Math.Exp(-days / 90) + 0.05 * Math.Log(1 + entry.UseCount) : 0)
                + (entry.Scope is not null && relevance > 0 ? 0.1 : 0); // the current project's memories first on ties
            results.Add(new Scored(entry, score));
        }

        // Pinned memories always come along, relevant ones by score.
        var pinned = results.Where(r => r.Entry.Pinned).OrderByDescending(r => r.Score);
        var relevant = results.Where(r => !r.Entry.Pinned && r.Score > 0).OrderByDescending(r => r.Score).Take(Math.Max(0, maxResults));
        return pinned.Concat(relevant).ToList();
    }

    /// <summary>Token overlap between two texts (0..1), used to merge near-duplicate memories.</summary>
    public static double Similarity(string a, string b)
    {
        var x = Tokenize(a).ToHashSet(StringComparer.Ordinal);
        var y = Tokenize(b).ToHashSet(StringComparer.Ordinal);
        if (x.Count == 0 || y.Count == 0) return 0;
        return (double)x.Intersect(y).Count() / x.Union(y).Count();
    }

    private static string Document(MemoryEntry entry) =>
        entry.Content + " " + string.Join(' ', entry.Tags) + " " + entry.Kind.ToString().ToLowerInvariant();

    internal static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return tokens;

        var current = new StringBuilder();
        foreach (char raw in text.Normalize(NormalizationForm.FormKC))
        {
            char ch = char.ToLowerInvariant(raw);
            var category = char.GetUnicodeCategory(ch);
            bool wordChar = char.IsLetterOrDigit(ch) || category == UnicodeCategory.NonSpacingMark;
            if (wordChar)
            {
                if (category != UnicodeCategory.NonSpacingMark) current.Append(ch); // drop Arabic diacritics
                continue;
            }
            Flush();
        }
        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length == 0) return;
            string token = Normalize(current.ToString());
            current.Clear();
            if (token.Length >= 2 && !StopWords.Contains(token)) tokens.Add(token);
        }
    }

    /// <summary>Very light stemming: English plurals, the Arabic article, Arabic letter variants.</summary>
    private static string Normalize(string token)
    {
        if (token.Length > 4 && token.StartsWith("ال", StringComparison.Ordinal)) token = token[2..];
        token = token.Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا').Replace('ة', 'ه').Replace('ى', 'ي');
        if (token.Length > 3 && token[^1] == 's' && token[^2] != 's' && token.All(c => c < 128)) token = token[..^1];
        return token;
    }
}
