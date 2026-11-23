using Microsoft.Extensions.AI;
using NexusShell.Agents.Application.Abstractions.Mcp;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Domain.Orchestration;

namespace NexusShell.Agents.Application.Conversations;

/// <summary>
/// Facade used by the presentation layer (NexusShell.App). Hides experts, routing,
/// providers and MCP behind two calls: check health, start a conversation.
/// </summary>
public interface INexusAgentService
{
    Task<AgentHealthReport> GetHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>Models, experts, tools, skills and MCP servers the user can choose from.</summary>
    Task<AgentCatalog> GetCatalogAsync(CancellationToken cancellationToken = default);

    /// <param name="initialContext">Optional context (e.g. terminal output) attached to the first message.</param>
    Task<INexusAgentConversation> StartConversationAsync(string? initialContext = null, CancellationToken cancellationToken = default);

    /// <param name="options">Model, expert, permissions, working folder and turned-off capabilities.</param>
    Task<INexusAgentConversation> StartConversationAsync(string? initialContext, ConversationOptions options, CancellationToken cancellationToken = default);

    /// <summary>Continues a saved chat: the agent gets its full earlier state back (or at least the earlier turns).</summary>
    Task<INexusAgentConversation> StartConversationAsync(string? initialContext, ConversationOptions options, ConversationResume? resume, CancellationToken cancellationToken = default);

    /// <summary>Re-read configuration, experts and MCP definitions on the next conversation.</summary>
    void ReloadConfiguration();
}

public interface INexusAgentConversation
{
    string Id { get; }

    OrchestrationMode Mode { get; }

    ConversationOptions Options { get; }

    /// <summary>Applies new choices; the conversation keeps its history.</summary>
    Task UpdateOptionsAsync(ConversationOptions options, CancellationToken cancellationToken = default);

    /// <summary>Rebuilds the agent from the latest configuration (new providers, models, MCP servers); history is kept.</summary>
    Task ReloadAsync(CancellationToken cancellationToken = default);

    /// <summary>The agent's state as JSON (to save the chat and continue it later), or null when it cannot be saved.</summary>
    Task<string?> ExportStateAsync(CancellationToken cancellationToken = default);

    Task<AgentTurnResult> SendAsync(string message, IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <param name="attachments">Files and folders whose content is shared with the agent for this message.</param>
    Task<AgentTurnResult> SendAsync(string message, IReadOnlyList<string> attachments, IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>What a saved chat hands back to the agent: its serialized state, and the turns as a fallback.</summary>
public sealed record ConversationResume(string? AgentState, IReadOnlyList<Routing.ConversationTurn> Turns)
{
    /// <summary>User/assistant pairs from displayed messages (notes and commands are skipped).</summary>
    public static ConversationResume FromChat(Domain.Chats.SavedChat chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        var turns = new List<Routing.ConversationTurn>();
        string? pendingUser = null;
        foreach (var message in chat.Messages)
        {
            if (message.Role == "user") pendingUser = message.Content;
            else if (message.Role == "assistant" && pendingUser is not null)
            {
                turns.Add(new Routing.ConversationTurn(pendingUser, message.Content, []));
                pendingUser = null;
            }
        }
        return new ConversationResume(chat.AgentState, turns);
    }
}

public sealed record ExpertContribution(string ExpertId, string ExpertName, double Weight, string Model, bool Succeeded, string? Error);

public sealed record AgentTurnResult(string Text, IReadOnlyList<ExpertContribution> Experts, UsageDetails? Usage)
{
    /// <summary>What happened to attachments (skipped files and why).</summary>
    public IReadOnlyList<string> AttachmentNotes { get; init; } = [];
}

public sealed record ExpertHealth(string Id, string Name, string Model, bool Enabled, bool Routable);

public sealed record AgentHealthReport(
    OrchestrationMode Mode,
    IReadOnlyList<ProviderStatus> Providers,
    IReadOnlyList<ExpertHealth> Experts,
    IReadOnlyList<McpServerStatus> McpServers,
    IReadOnlyList<string> SkillDirectories,
    IReadOnlyList<string> Warnings)
{
    /// <summary>At least one model provider is usable.</summary>
    public bool IsReady => Providers.Any(p => p.IsAvailable);
}
