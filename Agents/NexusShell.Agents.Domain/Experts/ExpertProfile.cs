namespace NexusShell.Agents.Domain.Experts;

/// <summary>
/// One expert in the Mixture-of-Experts. Each expert is a specialised agent with
/// its own instructions, model (local or online), tools, skills, MCP servers and
/// sub-agents. Experts are defined in Markdown files (front-matter + instructions)
/// so new experts can be added without touching code.
/// </summary>
public sealed record ExpertProfile
{
    /// <summary>Stable kebab-case id (also the sub-agent tool suffix: ask_&lt;id&gt;).</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>What the expert is good at. Used by the router (gating network) and as the sub-agent tool description.</summary>
    public required string Description { get; init; }

    /// <summary>System instructions (the Markdown body of the expert file).</summary>
    public required string Instructions { get; init; }

    /// <summary>Model reference: "provider/model", "provider" or "auto".</summary>
    public string Model { get; init; } = "auto";

    /// <summary>Routing hints used by the keyword gate (and as LLM-router context).</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Tool selectors: tool names, categories ("filesystem") or "*".</summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>Skill names (folders containing SKILL.md) or "*".</summary>
    public IReadOnlyList<string> Skills { get; init; } = [];

    /// <summary>MCP server names or "*".</summary>
    public IReadOnlyList<string> McpServers { get; init; } = [];

    /// <summary>Other expert ids this expert can delegate to as sub-agents.</summary>
    public IReadOnlyList<string> SubAgents { get; init; } = [];

    public float? Temperature { get; init; }

    public int? MaxOutputTokens { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>False = never picked by the router; reachable only as a sub-agent.</summary>
    public bool Routable { get; init; } = true;

    /// <summary>Where the definition came from (file path), for diagnostics.</summary>
    public string Source { get; init; } = string.Empty;
}
