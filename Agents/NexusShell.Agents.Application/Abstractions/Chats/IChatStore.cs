using NexusShell.Agents.Domain.Chats;
using NexusShell.Agents.Domain.Scheduling;

namespace NexusShell.Agents.Application.Abstractions.Chats;

/// <summary>Port: chats saved on the user's device, one folder per chat.</summary>
public interface IChatStore
{
    /// <summary>Folder that holds one sub-folder per chat.</summary>
    string RootDirectory { get; }

    string FolderOf(string chatId);

    Task<IReadOnlyList<ChatSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<SavedChat?> LoadAsync(string chatId, CancellationToken cancellationToken = default);

    Task SaveAsync(SavedChat chat, CancellationToken cancellationToken = default);

    /// <summary>Removes a chat (to the Recycle Bin where available). False when it does not exist.</summary>
    Task<bool> DeleteAsync(string chatId, CancellationToken cancellationToken = default);

    /// <summary>Moves every chat to <paramref name="newRoot"/> and uses it from now on; returns how many moved.</summary>
    Task<int> MoveRootAsync(string newRoot, CancellationToken cancellationToken = default);
}

/// <summary>Port: scheduled tasks (a local file per Windows user).</summary>
public interface IScheduledTaskStore
{
    Task<IReadOnlyList<ScheduledTask>> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IReadOnlyList<ScheduledTask> tasks, CancellationToken cancellationToken = default);
}
