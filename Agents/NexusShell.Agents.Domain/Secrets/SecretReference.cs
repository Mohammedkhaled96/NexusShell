namespace NexusShell.Agents.Domain.Secrets;

/// <summary>
/// The NAME of a secret (an environment variable), never its value.
/// Configuration files may only contain these references, so no key, token or
/// password is ever written to disk or compiled into code.
/// </summary>
public sealed record SecretReference
{
    public const int MaxNameLength = 128;

    public SecretReference(string name)
    {
        if (!IsValidName(name))
            throw new ArgumentException(
                $"'{name}' is not a valid environment variable name. Use letters, digits and '_' (must not start with a digit).",
                nameof(name));
        Name = name;
    }

    public string Name { get; }

    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength) return false;
        if (!(char.IsAsciiLetter(name[0]) || name[0] == '_')) return false;
        foreach (char ch in name)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_')) return false;
        }
        return true;
    }

    public static SecretReference? FromNullable(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : new SecretReference(name.Trim());

    /// <summary>Template form used inside configuration values.</summary>
    public string ToTemplate() => "${env:" + Name + "}";

    public override string ToString() => ToTemplate();
}
