using NexusShell.Agents.Application.Progress;

namespace NexusShell.Agents.Infrastructure.Tools;

/// <summary>
/// Path handling shared by the built-in tools: environment variables are expanded and
/// relative paths are resolved from the working folder the user chose in the agent
/// window (falling back to the process directory when none was chosen).
/// </summary>
internal static class ToolPaths
{
    /// <summary>The folder chosen for the current agent run, when it still exists.</summary>
    public static string? WorkingDirectory =>
        AgentRunScope.Current?.WorkingDirectory is { Length: > 0 } folder && Directory.Exists(folder) ? folder : null;

    public static string Resolve(string path)
    {
        string expanded = Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(path) ? "." : path.Trim());
        if (expanded.StartsWith('~'))
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + expanded[1..];

        string? root = WorkingDirectory;
        return root is not null && !Path.IsPathRooted(expanded)
            ? Path.GetFullPath(Path.Combine(root, expanded))
            : Path.GetFullPath(expanded);
    }
}
