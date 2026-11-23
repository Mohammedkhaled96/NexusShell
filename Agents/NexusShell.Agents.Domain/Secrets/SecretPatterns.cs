using System.Text.RegularExpressions;

namespace NexusShell.Agents.Domain.Secrets;

/// <summary>
/// Recognizes text that looks like a credential (API keys, tokens, private keys,
/// passwords in assignments). Used to refuse storing secrets in long-term memory or
/// configuration; values are never logged or echoed.
/// </summary>
public static partial class SecretPatterns
{
    [GeneratedRegex(
        @"(sk-(proj-|ant-)?[A-Za-z0-9_-]{16,})" +            // OpenAI / Anthropic
        @"|(AIza[0-9A-Za-z_-]{30,})" +                       // Google
        @"|(gsk_[A-Za-z0-9]{20,})" +                         // Groq
        @"|(gh[pousr]_[A-Za-z0-9]{30,})|(github_pat_[A-Za-z0-9_]{30,})" +
        @"|(hf_[A-Za-z0-9]{30,})" +                          // Hugging Face
        @"|(xai-[A-Za-z0-9]{30,})" +
        @"|(xox[abposr]-[A-Za-z0-9-]{10,})" +                // Slack
        @"|(AKIA[0-9A-Z]{16})" +                             // AWS access key id
        @"|(-----BEGIN [A-Z ]*PRIVATE KEY-----)" +
        @"|(eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})" + // JWT
        @"|((password|passwd|pwd|secret|api[_-]?key|access[_-]?token|client[_-]?secret)\s*[:=]\s*\S{6,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool LooksLikeSecret(string? text) => !string.IsNullOrEmpty(text) && Pattern().IsMatch(text);

    /// <summary>Replaces anything shaped like a credential with [REDACTED] (used before saving chats to disk).</summary>
    public static string Mask(string? text) =>
        string.IsNullOrEmpty(text) ? text ?? string.Empty : Pattern().Replace(text, "[REDACTED]");
}
