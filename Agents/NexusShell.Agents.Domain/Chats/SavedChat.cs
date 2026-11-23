namespace NexusShell.Agents.Domain.Chats;

/// <summary>What the chat history list shows for one saved chat.</summary>
public sealed record ChatSummary
{
    /// <summary>Sortable, file-name safe id, e.g. "20261123-150512-a3f9". Also the chat's folder name.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>True once the user renamed the chat (the automatic title is then never changed).</summary>
    public bool TitleSetByUser { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Pinned { get; init; }

    public int MessageCount { get; init; }

    /// <summary>Start of the last answer, for the list and screen readers.</summary>
    public string Preview { get; init; } = string.Empty;

    /// <summary>Set when the chat was written by a scheduled task.</summary>
    public string? ScheduledTaskId { get; init; }

    // The chat's agent choices, restored when it is opened again.
    public string? Model { get; init; }

    public string? Expert { get; init; }

    public string? Permission { get; init; }

    public string? WorkingDirectory { get; init; }
}

/// <summary>One message as the chat window displays it.</summary>
/// <param name="Role">"user", "assistant" or "note" (messages from NexusShell itself).</param>
public sealed record SavedChatMessage(string Role, string Speaker, string Content, DateTimeOffset At);

/// <summary>
/// A chat saved on the user's device: what was displayed plus the agent's own state
/// (shared transcript, each expert's history and summaries), so the conversation or a
/// task can be continued later with full context. Secret values are masked before saving.
/// </summary>
public sealed record SavedChat
{
    public required ChatSummary Summary { get; init; }

    public IReadOnlyList<SavedChatMessage> Messages { get; init; } = [];

    /// <summary>Serialized agent session (JSON) or null (classic chat, or not available).</summary>
    public string? AgentState { get; init; }
}
