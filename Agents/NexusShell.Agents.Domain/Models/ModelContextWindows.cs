using NexusShell.Agents.Domain.Providers;

namespace NexusShell.Agents.Domain.Models;

/// <summary>
/// How many tokens a model can read at once (its context window). The agent sizes
/// conversation history, recalled memories and attachments from it, so large-context
/// models (Gemini, GPT-4.1, Claude) get long conversations and small local models are
/// never overflowed (an overflowing prompt is silently cut and causes made-up answers).
/// Order: the provider's "contextWindow" setting, then known model families, then a
/// conservative default.
/// </summary>
public static class ModelContextWindows
{
    /// <summary>Default for online models whose family is unknown.</summary>
    public const int DefaultOnline = 128_000;

    /// <summary>Default for Ollama: NexusAgent asks Ollama for this many tokens (num_ctx).</summary>
    public const int DefaultOllama = 16_384;

    /// <summary>Default for other local servers (LM Studio, llama.cpp, vLLM, Jan): their own setting decides; stay safe.</summary>
    public const int DefaultLocal = 8_192;

    private const int Minimum = 2_048;

    // Most specific first. Values are conservative where a family has several sizes.
    private static readonly (string Pattern, int Tokens)[] KnownFamilies =
    [
        ("gemini", 1_048_576),
        ("gpt-4.1", 1_047_576),
        ("gpt-5", 400_000),
        ("gpt-4o", 128_000),
        ("o3", 200_000),
        ("o4", 200_000),
        ("gpt-oss", 131_072),
        ("claude", 200_000),
        ("llama-3.3", 131_072),
        ("llama3.3", 131_072),
        ("llama-3.1", 131_072),
        ("llama3.1", 131_072),
        ("llama3.2", 131_072),
        ("llama-4", 131_072),
        ("qwen3", 32_768),
        ("qwen2.5", 32_768),
        ("deepseek", 65_536),
        ("mistral-large", 131_072),
        ("mistral-medium", 131_072),
        ("mistral", 32_768),
        ("phi4", 16_384),
        ("gemma3", 131_072),
        ("grok", 131_072),
        ("command-r", 128_000),
        ("openrouter/auto", 128_000),
    ];

    /// <summary>The model's context window in tokens.</summary>
    public static int Resolve(ProviderProfile provider, string modelId)
    {
        ArgumentNullException.ThrowIfNull(provider);
        int? known = KnownFor(modelId);

        if (provider.Kind == ProviderKind.Ollama)
        {
            // Ollama only uses what it is asked for (num_ctx); never ask more than the model supports.
            int requested = provider.ContextWindow ?? DefaultOllama;
            return Math.Max(Minimum, known is int max ? Math.Min(requested, max) : requested);
        }

        if (provider.ContextWindow is int configured) return Math.Max(Minimum, configured);
        if (provider.IsLocal) return DefaultLocal;
        return known ?? DefaultOnline;
    }

    /// <summary>Known window for a model id (case-insensitive family match), or null.</summary>
    public static int? KnownFor(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        string id = modelId.Trim().ToLowerInvariant();
        foreach (var (pattern, tokens) in KnownFamilies)
        {
            if (id.Contains(pattern, StringComparison.Ordinal)) return tokens;
        }
        return null;
    }

    /// <summary>"1M", "200K", "8K" for display.</summary>
    public static string Describe(int tokens) => tokens >= 1_000_000
        ? $"{tokens / 1_000_000.0:0.#}M"
        : $"{Math.Round(tokens / 1000.0):0}K";
}
