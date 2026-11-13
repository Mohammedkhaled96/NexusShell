using System.Text.RegularExpressions;

namespace NexusShell.Agents.Domain.Secrets;

/// <summary>
/// Expands <c>${env:NAME}</c> environment references and enforces the
/// "no literal secrets in configuration" rule.
/// </summary>
public static partial class SecretTemplate
{
    [GeneratedRegex(@"\$\{env:(?<name>[A-Za-z_][A-Za-z0-9_]{0,127})\}", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceRegex();

    // Names that almost certainly carry credentials.
    [GeneratedRegex(@"(KEY|TOKEN|SECRET|PASSWORD|PASSWD|PWD|CREDENTIAL|COOKIE|AUTH|BEARER|SESSION|PRIVATE|(^|_)PAT($|_))",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveNameRegex();

    public static bool ContainsReference(string? value) =>
        !string.IsNullOrEmpty(value) && ReferenceRegex().IsMatch(value);

    public static IReadOnlyList<SecretReference> GetReferences(string? value)
    {
        if (string.IsNullOrEmpty(value)) return [];
        return ReferenceRegex().Matches(value)
            .Select(m => new SecretReference(m.Groups["name"].Value))
            .Distinct()
            .ToList();
    }

    /// <summary>Replaces every environment reference using <paramref name="resolve"/>; unresolved names are reported in <paramref name="missing"/>.</summary>
    public static string Expand(string value, Func<SecretReference, string?> resolve, out IReadOnlyList<SecretReference> missing)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(resolve);

        var missingList = new List<SecretReference>();
        string expanded = ReferenceRegex().Replace(value, match =>
        {
            var reference = new SecretReference(match.Groups["name"].Value);
            string? secret = resolve(reference);
            if (secret is null)
            {
                missingList.Add(reference);
                return string.Empty;
            }
            return secret;
        });

        missing = missingList;
        return expanded;
    }

    /// <summary>True when a setting, header or variable NAME suggests it carries a credential.</summary>
    public static bool IsSensitiveName(string? name) =>
        !string.IsNullOrEmpty(name) && SensitiveNameRegex().IsMatch(name);

    /// <summary>
    /// A sensitive-looking entry whose value is a literal (not a <c>${env:...}</c>
    /// reference) is a secret leaked into configuration and must be rejected.
    /// </summary>
    public static bool IsLiteralSecret(string name, string? value) =>
        IsSensitiveName(name) && !string.IsNullOrWhiteSpace(value) && !ContainsReference(value);
}
