using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Chats;
using NexusShell.Agents.Domain.Chats;

namespace NexusShell.Agents.Infrastructure.Chats;

/// <summary>Where chats are saved. The host may change <see cref="Directory"/> at any time (the user's "chats folder" setting).</summary>
public sealed class ChatStorageOptions
{
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusShell", "chats");

    public string Directory { get; set; } = DefaultDirectory;

    /// <summary>Deleted chats go to the Recycle Bin (Windows) so a mistake can be undone.</summary>
    public bool UseRecycleBin { get; set; } = true;
}

/// <summary>
/// One folder per chat under the chats folder:
/// <code>
/// &lt;chats&gt;\20261123-150512-a3f9\
///     chat.json          title, dates, pinned, agent choices
///     messages.json      what the chat window shows
///     agent-state.json   the agent's own state (to continue with full context)
/// </code>
/// Files are written atomically. Deleting moves the folder to the Recycle Bin on Windows.
/// </summary>
internal sealed partial class JsonChatStore(ChatStorageOptions options, ILogger<JsonChatStore> logger) : IChatStore
{
    private const string SummaryFile = "chat.json";
    private const string MessagesFile = "messages.json";
    private const string StateFile = "agent-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,63}$")]
    private static partial Regex SafeId();

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string RootDirectory => options.Directory;

    public string FolderOf(string chatId) => Path.Combine(RootDirectory, CheckId(chatId));

    public async Task<IReadOnlyList<ChatSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var summaries = new List<ChatSummary>();
        foreach (string folder in Directory.EnumerateDirectories(RootDirectory))
        {
            string file = Path.Combine(folder, SummaryFile);
            if (!SafeId().IsMatch(Path.GetFileName(folder)) || !File.Exists(file)) continue;
            try
            {
                await using var stream = File.OpenRead(file);
                var summary = await JsonSerializer.DeserializeAsync<ChatSummary>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                if (summary is not null && summary.Id == Path.GetFileName(folder)) summaries.Add(summary);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Chat folder {Folder} could not be read: {Reason}", Path.GetFileName(folder), ex.Message);
            }
        }
        return summaries;
    }

    public async Task<SavedChat?> LoadAsync(string chatId, CancellationToken cancellationToken = default)
    {
        string folder = FolderOf(chatId);
        string summaryFile = Path.Combine(folder, SummaryFile);
        if (!File.Exists(summaryFile)) return null;

        var summary = await ReadAsync<ChatSummary>(summaryFile, cancellationToken).ConfigureAwait(false);
        if (summary is null) return null;
        var messages = await ReadAsync<List<SavedChatMessage>>(Path.Combine(folder, MessagesFile), cancellationToken).ConfigureAwait(false) ?? [];
        string stateFile = Path.Combine(folder, StateFile);
        string? state = File.Exists(stateFile) ? await File.ReadAllTextAsync(stateFile, cancellationToken).ConfigureAwait(false) : null;
        return new SavedChat { Summary = summary, Messages = messages, AgentState = string.IsNullOrWhiteSpace(state) ? null : state };
    }

    public async Task SaveAsync(SavedChat chat, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        string folder = FolderOf(chat.Summary.Id);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(folder);
            await WriteAsync(Path.Combine(folder, MessagesFile), JsonSerializer.Serialize(chat.Messages, JsonOptions), cancellationToken).ConfigureAwait(false);
            string stateFile = Path.Combine(folder, StateFile);
            if (chat.AgentState is { Length: > 0 } state) await WriteAsync(stateFile, state, cancellationToken).ConfigureAwait(false);
            else if (File.Exists(stateFile) && chat.Messages.Count == 0) File.Delete(stateFile);
            // The summary last: a chat is listed only once its messages are on disk.
            await WriteAsync(Path.Combine(folder, SummaryFile), JsonSerializer.Serialize(chat.Summary, JsonOptions), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<bool> DeleteAsync(string chatId, CancellationToken cancellationToken = default)
    {
        string folder = FolderOf(chatId);
        if (!Directory.Exists(folder)) return Task.FromResult(false);

        if (OperatingSystem.IsWindows() && options.UseRecycleBin)
        {
            // Recoverable: the user can restore a chat deleted by mistake from the Recycle Bin.
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                folder,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        else
        {
            Directory.Delete(folder, recursive: true);
        }
        return Task.FromResult(true);
    }

    public async Task<int> MoveRootAsync(string newRoot, CancellationToken cancellationToken = default)
    {
        string target = Path.GetFullPath(newRoot);
        string source = Path.GetFullPath(RootDirectory);
        if (string.Equals(target.TrimEnd('\\', '/'), source.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return 0;
        if (target.StartsWith(source.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a folder outside the current chats folder.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        int moved = 0;
        try
        {
            Directory.CreateDirectory(target);
            if (Directory.Exists(source))
            {
                foreach (string folder in Directory.EnumerateDirectories(source).Where(f => SafeId().IsMatch(Path.GetFileName(f))).ToList())
                {
                    string destination = Path.Combine(target, Path.GetFileName(folder));
                    if (Directory.Exists(destination)) continue; // never overwrite a chat
                    if (string.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.Move(folder, destination);
                    }
                    else
                    {
                        CopyFolder(folder, destination);
                        Directory.Delete(folder, recursive: true);
                    }
                    moved++;
                }
            }
            options.Directory = target;
        }
        finally
        {
            _gate.Release();
        }

        logger.LogInformation("Moved {Count} chats to the new chats folder.", moved);
        return moved;
    }

    private static void CopyFolder(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    }

    private static string CheckId(string chatId)
    {
        if (string.IsNullOrWhiteSpace(chatId) || !SafeId().IsMatch(chatId)) throw new ArgumentException($"'{chatId}' is not a chat id.", nameof(chatId));
        return chatId;
    }

    private static async Task<T?> ReadAsync<T>(string file, CancellationToken cancellationToken) where T : class
    {
        if (!File.Exists(file)) return null;
        await using var stream = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAsync(string file, string content, CancellationToken cancellationToken)
    {
        string temp = file + ".tmp";
        await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temp, file, overwrite: true);
    }
}
