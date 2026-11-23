namespace NexusShell.Agents.Domain.Memory;

/// <summary>What a memory is about.</summary>
public enum MemoryKind
{
    /// <summary>A fact about the user, their machine or their work.</summary>
    Fact,

    /// <summary>How the user likes things done (language, tone, tools, formats).</summary>
    Preference,

    /// <summary>A fact about a project (paths, stack, conventions, commands).</summary>
    Project,

    /// <summary>The user corrected the agent: what was wrong and what is right.</summary>
    Correction,

    /// <summary>A decision taken together with the user.</summary>
    Decision,
}

/// <summary>How a memory was learned. Drives how much the agent may rely on it.</summary>
public enum MemorySource
{
    /// <summary>The user said it (most reliable).</summary>
    User,

    /// <summary>Verified with a tool (file read, command output, system information).</summary>
    Tool,

    /// <summary>The agent's own conclusion: unverified, must be checked before relying on it.</summary>
    Agent,
}

/// <summary>
/// One long-term memory. Memories survive conversations and app restarts and are
/// recalled into the context window when relevant, so the agent answers from facts
/// it was told or verified instead of guessing. Never contains secrets.
/// </summary>
public sealed record MemoryEntry
{
    /// <summary>Short id the agent can cite, e.g. "m3f9a2c".</summary>
    public required string Id { get; init; }

    public required string Content { get; init; }

    public MemoryKind Kind { get; init; } = MemoryKind.Fact;

    public MemorySource Source { get; init; } = MemorySource.User;

    /// <summary>Where it came from (a file, a command, "the user said ..."); optional.</summary>
    public string? Evidence { get; init; }

    /// <summary>Folder (full path) the memory belongs to; null = applies everywhere.</summary>
    public string? Scope { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Pinned memories are always recalled and never evicted.</summary>
    public bool Pinned { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }

    public int UseCount { get; init; }

    /// <summary>Told by the user or checked with a tool (not just the agent's own conclusion).</summary>
    public bool IsVerified => Source != MemorySource.Agent;

    /// <summary>True when the memory applies in <paramref name="folder"/> (global, same folder or a parent folder).</summary>
    public bool AppliesTo(string? folder)
    {
        if (Scope is null) return true;
        if (string.IsNullOrWhiteSpace(folder)) return false;
        string scope = Scope.TrimEnd('\\', '/');
        string current = folder.TrimEnd('\\', '/');
        return current.Equals(scope, StringComparison.OrdinalIgnoreCase)
            || current.StartsWith(scope + "\\", StringComparison.OrdinalIgnoreCase)
            || current.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Long-term memory and context-window behaviour (agent.json "memory").</summary>
public sealed record MemorySettings
{
    /// <summary>Recall memories into every expert's context and offer the remember / recall / forget tools.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Upper bound of stored memories; the least useful unpinned ones are evicted beyond it.</summary>
    public int MaxEntries { get; init; } = 2000;

    /// <summary>Longest single memory (characters). Memories are short facts, not documents.</summary>
    public int MaxEntryChars { get; init; } = 1000;

    /// <summary>How many relevant memories are recalled per message at most (pinned ones are always included).</summary>
    public int RecallTopK { get; init; } = 8;

    /// <summary>Share of the context window recalled memories may use (tokens, upper bound).</summary>
    public int RecallBudgetTokens { get; init; } = 2000;

    /// <summary>
    /// When an expert's history outgrows its share of the context window, summarize the oldest
    /// turns into notes (a model call) instead of dropping them. Off = old turns are dropped.
    /// </summary>
    public bool CompactHistory { get; init; } = true;
}
