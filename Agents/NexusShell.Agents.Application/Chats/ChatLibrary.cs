using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Chats;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Domain.Chats;
using NexusShell.Agents.Domain.Secrets;

namespace NexusShell.Agents.Application.Chats;

/// <summary>
/// Saved chats for the chat window and the scheduler: list, open, save (titles, previews and
/// masking of secret values done here), rename, pin, duplicate, delete, export and move.
/// </summary>
public interface IChatLibrary
{
    string RootDirectory { get; }

    string FolderOf(string chatId);

    /// <summary>Pinned chats first, then the most recently updated.</summary>
    Task<IReadOnlyList<ChatSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<SavedChat?> OpenAsync(string chatId, CancellationToken cancellationToken = default);

    /// <summary>Saves (creating the folder the first time); fills title, preview, count and dates; masks secrets.</summary>
    Task<ChatSummary> SaveAsync(SavedChat chat, CancellationToken cancellationToken = default);

    Task<ChatSummary?> RenameAsync(string chatId, string title, CancellationToken cancellationToken = default);

    Task<ChatSummary?> SetPinnedAsync(string chatId, bool pinned, CancellationToken cancellationToken = default);

    /// <summary>A copy of the chat (with the agent's state) to try another direction without changing the original.</summary>
    Task<ChatSummary?> DuplicateAsync(string chatId, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string chatId, CancellationToken cancellationToken = default);

    /// <summary>Plain text transcript (for export).</summary>
    Task<string?> ExportTextAsync(string chatId, CancellationToken cancellationToken = default);

    Task<int> MoveAsync(string newRoot, CancellationToken cancellationToken = default);

    /// <summary>New sortable chat id, e.g. "20261123-150512-a3f9".</summary>
    string NewId();
}

internal sealed class ChatLibrary(
    IChatStore store,
    ISecretRedactor redactor,
    TimeProvider time,
    ILogger<ChatLibrary> logger) : IChatLibrary
{
    private const int TitleLength = 60;
    private const int PreviewLength = 140;

    public string RootDirectory => store.RootDirectory;

    public string FolderOf(string chatId) => store.FolderOf(chatId);

    public async Task<IReadOnlyList<ChatSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        (await store.ListAsync(cancellationToken).ConfigureAwait(false))
            .OrderByDescending(c => c.Pinned)
            .ThenByDescending(c => c.UpdatedAt)
            .ToList();

    public Task<SavedChat?> OpenAsync(string chatId, CancellationToken cancellationToken = default) => store.LoadAsync(chatId, cancellationToken);

    public async Task<ChatSummary> SaveAsync(SavedChat chat, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        var now = time.GetUtcNow();
        var messages = chat.Messages.Select(m => m with { Content = Mask(m.Content) }).ToList();
        var summary = chat.Summary;

        string title = summary.TitleSetByUser && !string.IsNullOrWhiteSpace(summary.Title)
            ? summary.Title.Trim()
            : AutoTitle(summary.Title, messages);
        string preview = messages.LastOrDefault(m => m.Role == "assistant")?.Content ?? messages.LastOrDefault()?.Content ?? string.Empty;

        var saved = chat with
        {
            Summary = summary with
            {
                Title = title,
                CreatedAt = summary.CreatedAt == default ? now : summary.CreatedAt,
                UpdatedAt = now,
                MessageCount = messages.Count,
                Preview = Shorten(preview, PreviewLength),
            },
            Messages = messages,
        };

        await store.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
        return saved.Summary;
    }

    public Task<ChatSummary?> RenameAsync(string chatId, string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Type a name for the chat.", nameof(title));
        string clean = Shorten(Mask(title.Trim()), 120);
        return ChangeAsync(chatId, s => s with { Title = clean, TitleSetByUser = true }, touch: false, cancellationToken);
    }

    public Task<ChatSummary?> SetPinnedAsync(string chatId, bool pinned, CancellationToken cancellationToken = default) =>
        ChangeAsync(chatId, s => s with { Pinned = pinned }, touch: false, cancellationToken);

    public async Task<ChatSummary?> DuplicateAsync(string chatId, CancellationToken cancellationToken = default)
    {
        var chat = await store.LoadAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (chat is null) return null;
        var copy = chat with
        {
            Summary = chat.Summary with
            {
                Id = NewId(),
                Title = Shorten("Copy of " + chat.Summary.Title, 120),
                TitleSetByUser = true,
                Pinned = false,
                CreatedAt = time.GetUtcNow(),
                ScheduledTaskId = null,
            },
        };
        await store.SaveAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.Summary;
    }

    public async Task<bool> DeleteAsync(string chatId, CancellationToken cancellationToken = default)
    {
        bool deleted = await store.DeleteAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (deleted) logger.LogInformation("Chat {Chat} deleted.", chatId);
        return deleted;
    }

    public async Task<string?> ExportTextAsync(string chatId, CancellationToken cancellationToken = default)
    {
        var chat = await store.LoadAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (chat is null) return null;
        var sb = new StringBuilder()
            .AppendLine(chat.Summary.Title)
            .Append("Saved ").AppendLine(chat.Summary.UpdatedAt.ToLocalTime().ToString("dddd d MMMM yyyy HH:mm", CultureInfo.InvariantCulture))
            .AppendLine();
        foreach (var message in chat.Messages)
        {
            sb.Append(message.Speaker).Append(" (").Append(message.At.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)).AppendLine("):")
              .AppendLine(message.Content.TrimEnd())
              .AppendLine();
        }
        return sb.ToString();
    }

    public Task<int> MoveAsync(string newRoot, CancellationToken cancellationToken = default) => store.MoveRootAsync(newRoot, cancellationToken);

    public string NewId() =>
        time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
        Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();

    private async Task<ChatSummary?> ChangeAsync(string chatId, Func<ChatSummary, ChatSummary> change, bool touch, CancellationToken cancellationToken)
    {
        var chat = await store.LoadAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (chat is null) return null;
        var summary = change(chat.Summary);
        if (touch) summary = summary with { UpdatedAt = time.GetUtcNow() };
        await store.SaveAsync(chat with { Summary = summary }, cancellationToken).ConfigureAwait(false);
        return summary;
    }

    /// <summary>Known secret values and anything shaped like a credential never reach the disk.</summary>
    private string Mask(string text) => SecretPatterns.Mask(redactor.Redact(text));

    /// <summary>The first question, shortened at a word boundary ("New chat" until there is one).</summary>
    internal static string AutoTitle(string? current, IReadOnlyList<SavedChatMessage> messages)
    {
        if (!string.IsNullOrWhiteSpace(current) && current != "New chat") return current.Trim();
        var first = messages.FirstOrDefault(m => m.Role == "user" && !m.Content.TrimStart().StartsWith('/'));
        if (first is null) return string.IsNullOrWhiteSpace(current) ? "New chat" : current.Trim();
        string line = first.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "New chat";
        return Shorten(line, TitleLength);
    }

    internal static string Shorten(string text, int max)
    {
        string single = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (single.Length <= max) return single;
        int cut = single.LastIndexOf(' ', max);
        return (cut > max / 2 ? single[..cut] : single[..max]).TrimEnd('.', ',', ';', ':') + "...";
    }
}
