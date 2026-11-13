using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using NexusShell.Agents.Application.Abstractions.Tools;
using NexusShell.Agents.Domain.Tools;

namespace NexusShell.Agents.Infrastructure.Tools.FileSystem;

/// <summary>
/// Category "filesystem". Reads are auto-approved; writing asks the user.
/// Credential files (private keys, .env, certificates, password vaults) are never read.
/// </summary>
internal sealed class FileSystemToolProvider : IAgentToolProvider
{
    private const int MaxReadBytes = 2 * 1024 * 1024;
    private const int DefaultMaxChars = 20_000;
    private const int MaxEntries = 500;

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".pfx", ".p12", ".kdbx", ".keystore", ".jks", ".ppk", ".asc", ".gpg",
    };

    private static readonly HashSet<string> BlockedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", ".env", ".netrc", ".npmrc", ".pypirc", ".git-credentials", "credentials", "credentials.json",
    };

    public string Category => "filesystem";

    public IEnumerable<AgentToolDescriptor> GetTools()
    {
        yield return new(AIFunctionFactory.Create(ListDirectory, "list_directory", "List files and folders in a directory."), ToolRisk.ReadOnly, Category);
        yield return new(AIFunctionFactory.Create(ReadTextFile, "read_text_file", "Read a text file (optionally a line range)."), ToolRisk.ReadOnly, Category);
        yield return new(AIFunctionFactory.Create(SearchFiles, "search_files", "Find files by name pattern and optionally by text they contain."), ToolRisk.ReadOnly, Category);
        yield return new(AIFunctionFactory.Create(WriteTextFile, "write_text_file", "Create or overwrite a text file. Requires user approval."), ToolRisk.Mutating, Category);
    }

    private static string ListDirectory(
        [Description("Directory path. Relative paths are resolved from the current directory.")] string path,
        [Description("Optional wildcard filter such as *.cs")] string? pattern = null,
        [Description("Include sub-directories")] bool recursive = false)
    {
        string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!Directory.Exists(full)) return $"Directory not found: {full}";

        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, MaxRecursionDepth = 6 };
        var sb = new StringBuilder().Append("Directory: ").AppendLine(full);
        int count = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(full, string.IsNullOrWhiteSpace(pattern) ? "*" : pattern, options))
        {
            if (++count > MaxEntries)
            {
                sb.AppendLine($"... more than {MaxEntries} entries, narrow the pattern.");
                break;
            }
            bool isDirectory = Directory.Exists(entry);
            string relative = Path.GetRelativePath(full, entry);
            if (isDirectory) sb.Append("[dir]  ").AppendLine(relative);
            else sb.Append("[file] ").Append(relative).Append("  (").Append(new FileInfo(entry).Length).AppendLine(" bytes)");
        }
        return sb.ToString();
    }

    private static string ReadTextFile(
        [Description("File path.")] string path,
        [Description("First line to return (1-based).")] int startLine = 1,
        [Description("Maximum number of lines to return.")] int maxLines = 400)
    {
        string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (IsBlocked(full)) return "Refused: this file looks like it contains credentials (keys, certificates, .env). Ask the user to inspect it themselves.";
        if (!File.Exists(full)) return $"File not found: {full}";

        var info = new FileInfo(full);
        if (info.Length > MaxReadBytes) return $"File is too large ({info.Length} bytes). Read a line range or search it instead.";

        var lines = File.ReadLines(full).Skip(Math.Max(0, startLine - 1)).Take(Math.Clamp(maxLines, 1, 5_000)).ToList();
        string text = string.Join('\n', lines);
        return text.Length <= DefaultMaxChars ? text : text[..DefaultMaxChars] + "\n... (truncated)";
    }

    private static string SearchFiles(
        [Description("Root directory to search.")] string root,
        [Description("File name wildcard, e.g. *.json")] string pattern = "*",
        [Description("Optional text that must appear in the file (case-insensitive).")] string? containsText = null,
        [Description("Maximum results.")] int maxResults = 50)
    {
        string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root));
        if (!Directory.Exists(full)) return $"Directory not found: {full}";

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 12 };
        var results = new List<string>();
        foreach (string file in Directory.EnumerateFiles(full, pattern, options))
        {
            if (results.Count >= Math.Clamp(maxResults, 1, 500)) break;
            if (IsBlocked(file)) continue;
            if (string.IsNullOrEmpty(containsText))
            {
                results.Add(file);
                continue;
            }

            try
            {
                if (new FileInfo(file).Length > MaxReadBytes) continue;
                int lineNumber = 0;
                foreach (string line in File.ReadLines(file))
                {
                    lineNumber++;
                    if (line.Contains(containsText, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add($"{file}:{lineNumber}: {line.Trim()[..Math.Min(line.Trim().Length, 200)]}");
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable files are skipped.
            }
        }

        return results.Count == 0 ? "No matches." : string.Join('\n', results);
    }

    private static string WriteTextFile(
        [Description("File path to write.")] string path,
        [Description("Full text content.")] string content,
        [Description("Overwrite if the file already exists.")] bool overwrite = false)
    {
        string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (IsBlocked(full)) return "Refused: writing credential files is not allowed.";
        if (File.Exists(full) && !overwrite) return $"File already exists: {full}. Set overwrite=true to replace it.";

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return $"Wrote {content.Length} characters to {full}";
    }

    internal static bool IsBlocked(string path)
    {
        string name = Path.GetFileName(path);
        return BlockedNames.Contains(name)
               || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
               || BlockedExtensions.Contains(Path.GetExtension(path))
               || path.Contains(Path.DirectorySeparatorChar + ".ssh" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || path.Contains(Path.DirectorySeparatorChar + ".gnupg" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
