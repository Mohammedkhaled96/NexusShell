using NexusShell.Agents.Domain.Models;

namespace NexusShell.Agents.Application.Conversations;

/// <summary>What the agent may do without asking (like Claude Code's permission modes).</summary>
public enum PermissionMode
{
    /// <summary>Ask before anything that changes files, settings or runs commands (default).</summary>
    Ask,

    /// <summary>File edits run without asking; shell commands and network access still ask.</summary>
    AutoApproveEdits,

    /// <summary>Plan / read-only: tools that change anything are not offered at all.</summary>
    ReadOnly,
}

/// <summary>
/// Per-conversation choices made in the agent window. Defaults keep everything
/// automatic: best available model, mixture of experts, ask before changes.
/// </summary>
public sealed record ConversationOptions
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static ConversationOptions Default { get; } = new();

    /// <summary>"provider/model" chosen by the user; null or "auto" = automatic. Failover still applies.</summary>
    public string? Model { get; init; }

    /// <summary>Expert id to answer every message; null = automatic (mixture of experts).</summary>
    public string? Expert { get; init; }

    public PermissionMode Permission { get; init; } = PermissionMode.Ask;

    /// <summary>Folder the agent works in (relative paths, shell commands).</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Tool names or categories turned off.</summary>
    public IReadOnlySet<string> DisabledTools { get; init; } = None;

    public IReadOnlySet<string> DisabledSkills { get; init; } = None;

    public IReadOnlySet<string> DisabledMcpServers { get; init; } = None;

    /// <summary>
    /// Nobody is watching (scheduled runs): actions that need approval are declined automatically
    /// instead of opening a dialog, and the answer says what still needs the user.
    /// </summary>
    public bool Unattended { get; init; }

    public bool IsAutomaticModel =>
        string.IsNullOrWhiteSpace(Model) || (ModelReference.TryParse(Model, out var reference) && reference.IsAuto);

    /// <summary>Value-based identity (records compare sets by reference), used to detect changes.</summary>
    public string Fingerprint() => string.Join("|",
        IsAutomaticModel ? "auto" : Model!.Trim(),
        Expert ?? "auto",
        Permission,
        WorkingDirectory ?? string.Empty,
        string.Join(",", DisabledTools.Order(StringComparer.OrdinalIgnoreCase)),
        string.Join(",", DisabledSkills.Order(StringComparer.OrdinalIgnoreCase)),
        string.Join(",", DisabledMcpServers.Order(StringComparer.OrdinalIgnoreCase)),
        Unattended ? "unattended" : "attended");
}
