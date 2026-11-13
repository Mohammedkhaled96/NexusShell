using NexusShell.Agents.Domain.Common;
using NexusShell.Agents.Domain.Experts;
using NexusShell.Agents.Domain.Mcp;
using NexusShell.Agents.Domain.Orchestration;
using NexusShell.Agents.Domain.Providers;
using NexusShell.Agents.Domain.Tools;

namespace NexusShell.Agents.Domain.Configuration;

public sealed record SkillSettings
{
    /// <summary>Allow skills to run bundled scripts (always behind user approval). Off by default.</summary>
    public bool EnableScripts { get; init; }
}

/// <summary>
/// Aggregate root: the complete, validated agent configuration
/// (providers, model preferences, experts, MCP servers, policies).
/// Contains no secret values by construction.
/// </summary>
public sealed record NexusAgentConfiguration
{
    public IReadOnlyDictionary<string, ProviderProfile> Providers { get; init; } =
        new Dictionary<string, ProviderProfile>(Identifier.Comparer);

    /// <summary>Ordered model preferences used to resolve "auto" (first available wins).</summary>
    public IReadOnlyList<string> ModelPreference { get; init; } = [];

    public OrchestrationSettings Orchestration { get; init; } = new();

    public ToolApprovalPolicy ToolPolicy { get; init; } = ToolApprovalPolicy.Default;

    public SkillSettings Skills { get; init; } = new();

    public IReadOnlyList<ExpertProfile> Experts { get; init; } = [];

    public IReadOnlyList<McpServerDefinition> McpServers { get; init; } = [];

    /// <summary>Instructions shared by every expert (house rules: tone, accessibility, safety).</summary>
    public string SharedInstructions { get; init; } = string.Empty;

    /// <summary>Non-fatal problems found while loading (invalid entries are skipped, not fatal).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public ExpertProfile? FindExpert(string id) =>
        Experts.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ExpertProfile> EnabledExperts => Experts.Where(e => e.Enabled);
}
