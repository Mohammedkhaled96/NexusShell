using System.Text;

namespace NexusShell.Agents.Domain.Common;

/// <summary>
/// Normalises human-written names (expert ids, provider ids, MCP server names)
/// into stable identifiers: lower-case kebab-case.
/// </summary>
public static class Identifier
{
    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var sb = new StringBuilder(value.Length);
        bool lastWasDash = false;
        foreach (char ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasDash = false;
            }
            else if (!lastWasDash && sb.Length > 0)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        string result = sb.ToString().TrimEnd('-');
        if (result.Length == 0)
            throw new ArgumentException($"'{value}' does not contain any letters or digits.", nameof(value));
        return result;
    }

    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;
}
