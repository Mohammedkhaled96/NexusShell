using System.Text;

namespace NexusShell.Agents.Infrastructure.Mcp;

internal static class McpToolNaming
{
    // OpenAI-style function names: ^[a-zA-Z0-9_-]{1,64}$
    private const int MaxLength = 64;

    /// <summary>Server-prefixed, provider-safe tool name, e.g. "mcp_github_create_issue".</summary>
    public static string Create(string serverName, string toolName)
    {
        string name = "mcp_" + Sanitize(serverName) + "_" + Sanitize(toolName);
        return name.Length <= MaxLength ? name : name[..MaxLength];
    }

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char ch in value)
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        return sb.ToString().Trim('_');
    }
}
