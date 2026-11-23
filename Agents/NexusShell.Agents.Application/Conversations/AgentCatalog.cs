using NexusShell.Agents.Domain.Tools;

namespace NexusShell.Agents.Application.Conversations;

/// <summary>A model the user can pick in the agent window.</summary>
public sealed record ModelOption(string ProviderId, string ModelId, bool IsLocal, bool IsAvailable, string Detail)
{
    /// <summary>"provider/model" reference to pass back in <see cref="ConversationOptions.Model"/>.</summary>
    public string Reference => ProviderId + "/" + ModelId;

    /// <summary>Tokens the model reads at once (history, memories and attachments are sized from it).</summary>
    public int ContextWindow { get; init; }
}

public sealed record ExpertOption(string Id, string Name, string Description, bool Routable);

public sealed record ToolOption(string Name, string Category, ToolRisk Risk, string Description);

public sealed record SkillOption(string Name, string Description);

public sealed record McpOption(string Name, bool Enabled, bool IsConnected, int ToolCount, string Detail);

/// <summary>Everything the agent window offers for selection.</summary>
public sealed record AgentCatalog(
    IReadOnlyList<ModelOption> Models,
    IReadOnlyList<ExpertOption> Experts,
    IReadOnlyList<ToolOption> Tools,
    IReadOnlyList<SkillOption> Skills,
    IReadOnlyList<McpOption> McpServers);
