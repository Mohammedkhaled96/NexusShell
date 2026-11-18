using System.Text;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Attachments;
using NexusShell.Agents.Infrastructure.Tools.FileSystem;

namespace NexusShell.Agents.Infrastructure.Attachments;

/// <summary>
/// Turns attached files and folders into a Markdown block the model can read.
/// Limits keep the prompt small (per file, total and number of files); binary files,
/// build output and credential files (keys, certificates, .env) are skipped and
/// reported back to the user in <see cref="AttachmentBundle.Notes"/>.
/// For folders the agent receives the tree plus the main text files; it can read
/// the rest with its file tools.
/// </summary>
internal sealed class FileAttachmentReader(ILogger<FileAttachmentReader> logger) : IAttachmentReader
{
    internal const int MaxFileChars = 60_000;
    internal const int MaxTotalChars = 200_000;
    internal const int MaxFilesPerFolder = 40;
    internal const int MaxTreeEntries = 300;
    private const int BinaryProbeBytes = 8_192;

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".vscode", ".idea", "bin", "obj", "node_modules", "packages", "dist", "build", "out", "target",
        "__pycache__", ".venv", "venv", ".mypy_cache", ".pytest_cache", ".next", "coverage", "TestResults",
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".so", ".dylib", ".bin", ".obj", ".o", ".a", ".lib", ".zip", ".7z", ".rar", ".gz", ".tar",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff", ".mp3", ".wav", ".mp4", ".avi", ".mov",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".woff", ".woff2", ".ttf", ".otf", ".db", ".sqlite", ".nupkg",
    };

    public Task<AttachmentBundle> ReadAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Task.Run(() => Read(paths, cancellationToken), cancellationToken);
    }

    internal AttachmentBundle Read(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var notes = new List<string>();
        int included = 0;
        int budget = MaxTotalChars;

        sb.AppendLine("Attached by the user (read-only copies of their content):");
        foreach (string raw in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string full;
            try
            {
                full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw.Trim()));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                notes.Add($"{raw}: not a valid path.");
                continue;
            }

            if (Directory.Exists(full))
            {
                included += AppendFolder(full, sb, notes, ref budget, cancellationToken);
            }
            else if (File.Exists(full))
            {
                if (AppendFile(full, Path.GetFileName(full), sb, notes, ref budget)) included++;
            }
            else
            {
                notes.Add($"{Path.GetFileName(full)}: not found.");
            }
        }

        if (included == 0 && sb.Length < 80) sb.Clear();
        logger.LogInformation("Attachments read: {Files} files, {Notes} notes.", included, notes.Count);
        return new AttachmentBundle(sb.ToString().TrimEnd(), included, notes);
    }

    private int AppendFolder(string folder, StringBuilder sb, List<string> notes, ref int budget, CancellationToken cancellationToken)
    {
        sb.AppendLine().Append("### Folder ").AppendLine(folder);

        var files = new List<string>();
        var tree = new StringBuilder();
        int entries = 0;
        bool truncated = false;
        Walk(folder, 0);

        sb.AppendLine("Contents:").AppendLine("```").Append(tree).AppendLine(truncated ? "... (more entries not listed)" : string.Empty).AppendLine("```");

        int included = 0;
        // Small, important files first (README, project files), then the rest by size.
        foreach (string file in files.OrderBy(Priority).ThenBy(f => SafeLength(f)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (included >= MaxFilesPerFolder || budget <= 0)
            {
                notes.Add($"{Path.GetFileName(folder)}: only {included} of {files.Count} files were included; the agent can read others with its file tools.");
                break;
            }
            if (AppendFile(file, Path.GetRelativePath(folder, file), sb, notes, ref budget, quiet: true)) included++;
        }
        return included;

        void Walk(string directory, int depth)
        {
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory).OrderBy(e => !Directory.Exists(e)).ThenBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (string child in children)
            {
                if (entries >= MaxTreeEntries)
                {
                    truncated = true;
                    return;
                }
                string name = Path.GetFileName(child);
                bool isDirectory = Directory.Exists(child);
                if (isDirectory && (SkippedFolders.Contains(name) || name.StartsWith('.'))) continue;

                entries++;
                tree.Append(new string(' ', depth * 2)).Append(isDirectory ? name + "/" : name).AppendLine();
                if (isDirectory)
                {
                    if (depth < 6) Walk(child, depth + 1);
                }
                else if (!FileSystemToolProvider.IsBlocked(child) && !BinaryExtensions.Contains(Path.GetExtension(child)))
                {
                    files.Add(child);
                }
            }
        }
    }

    private static bool AppendFile(string path, string label, StringBuilder sb, List<string> notes, ref int budget, bool quiet = false)
    {
        string name = Path.GetFileName(path);
        if (FileSystemToolProvider.IsBlocked(path))
        {
            notes.Add($"{name}: skipped, it looks like a credentials file.");
            return false;
        }
        if (BinaryExtensions.Contains(Path.GetExtension(path)))
        {
            if (!quiet) notes.Add($"{name}: skipped, binary files (images, documents, archives) cannot be read as text.");
            return false;
        }

        if (budget <= 0)
        {
            notes.Add($"{name}: skipped, the attachment size limit was reached.");
            return false;
        }

        string text;
        try
        {
            if (LooksBinary(path))
            {
                if (!quiet) notes.Add($"{name}: skipped, it is not a text file.");
                return false;
            }
            text = ReadPrefix(path, Math.Min(MaxFileChars, budget) + 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"{name}: could not be read ({ex.GetType().Name}).");
            return false;
        }

        int limit = Math.Min(MaxFileChars, budget);
        bool cut = text.Length > limit;
        if (cut)
        {
            text = text[..limit];
            notes.Add($"{name}: only the first {limit:N0} characters were included.");
        }

        budget -= text.Length;
        string fence = text.Contains("```", StringComparison.Ordinal) ? "````" : "```";
        sb.AppendLine().Append("#### File ").AppendLine(label)
            .Append(fence).AppendLine(LanguageOf(path))
            .AppendLine(text.TrimEnd())
            .AppendLine(cut ? "... (truncated)" : string.Empty)
            .AppendLine(fence);
        return true;
    }

    private static string ReadPrefix(string path, int maxChars)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[Math.Max(1, maxChars)];
        int read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    internal static bool LooksBinary(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[BinaryProbeBytes];
        int read = stream.Read(buffer, 0, buffer.Length);
        if (read >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF))) return false; // UTF-16 BOM
        return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
    }

    private static int Priority(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        if (name.StartsWith("readme", StringComparison.Ordinal)) return 0;
        if (name is "package.json" or "pyproject.toml" or "cargo.toml" or "go.mod" or "pom.xml" or "build.gradle" or "makefile" or "dockerfile"
            || name.EndsWith(".csproj", StringComparison.Ordinal) || name.EndsWith(".sln", StringComparison.Ordinal) || name.EndsWith(".slnx", StringComparison.Ordinal)) return 1;
        return 2;
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    private static string LanguageOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".ps1" or ".psm1" => "powershell",
        ".py" => "python",
        ".js" or ".mjs" or ".cjs" => "javascript",
        ".ts" or ".tsx" => "typescript",
        ".json" => "json",
        ".xml" or ".csproj" or ".props" or ".targets" or ".xaml" => "xml",
        ".md" => "markdown",
        ".yml" or ".yaml" => "yaml",
        ".sh" => "bash",
        ".cmd" or ".bat" => "bat",
        ".sql" => "sql",
        ".html" or ".htm" => "html",
        ".css" => "css",
        _ => string.Empty,
    };
}
