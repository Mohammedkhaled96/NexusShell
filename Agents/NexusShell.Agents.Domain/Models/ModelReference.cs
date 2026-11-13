using NexusShell.Agents.Domain.Common;

namespace NexusShell.Agents.Domain.Models;

/// <summary>
/// Points at a model: "provider/model" (the model part may itself contain '/',
/// e.g. "openrouter/meta-llama/llama-3.3-70b-instruct"), "provider" alone
/// (that provider's default model) or "auto" (first available model from the
/// preference list).
/// </summary>
public sealed record ModelReference(string ProviderId, string? ModelId)
{
    public const string AutoKeyword = "auto";

    /// <summary>Like "auto", but prefers each provider's small/quick model (used for routing).</summary>
    public const string FastKeyword = "fast";

    public static bool IsFastKeyword(string? text) =>
        string.Equals(text?.Trim(), FastKeyword, StringComparison.OrdinalIgnoreCase);

    public static ModelReference Auto { get; } = new(AutoKeyword, null);

    public bool IsAuto => string.Equals(ProviderId, AutoKeyword, StringComparison.OrdinalIgnoreCase);

    public static ModelReference Parse(string? text) =>
        TryParse(text, out var reference)
            ? reference
            : throw new FormatException($"'{text}' is not a valid model reference. Use 'provider/model', 'provider' or 'auto'.");

    public static bool TryParse(string? text, out ModelReference reference)
    {
        reference = Auto;
        if (string.IsNullOrWhiteSpace(text)) return true;

        string trimmed = text.Trim();
        if (string.Equals(trimmed, AutoKeyword, StringComparison.OrdinalIgnoreCase)) return true;

        int slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        string provider = slash < 0 ? trimmed : trimmed[..slash];
        string? model = slash < 0 ? null : trimmed[(slash + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(provider) || !provider.Any(char.IsLetterOrDigit)) return false;
        if (slash >= 0 && string.IsNullOrWhiteSpace(model)) return false;

        reference = new ModelReference(Identifier.Normalize(provider), string.IsNullOrWhiteSpace(model) ? null : model);
        return true;
    }

    public override string ToString() =>
        IsAuto ? AutoKeyword : ModelId is null ? ProviderId : $"{ProviderId}/{ModelId}";
}
