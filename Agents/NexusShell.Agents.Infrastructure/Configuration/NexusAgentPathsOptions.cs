namespace NexusShell.Agents.Infrastructure.Configuration;

/// <summary>
/// Where the agent reads its extensible content. Two roots:
/// <list type="bullet">
/// <item><b>Bundled</b> (<c>&lt;app&gt;\Agent</c>): defaults shipped with NexusShell.</item>
/// <item><b>User</b> (<c>%AppData%\NexusShell\agent</c>): user additions and overrides; wins on conflicts.</item>
/// </list>
/// Layout of each root:
/// <code>
/// agent.json          providers, model preferences, orchestration, tool policy
/// instructions.md     house rules shared by every expert
/// mcp.json            MCP servers ("mcpServers" format)
/// experts/*.md        one expert per file (front-matter + instructions)
/// skills/&lt;name&gt;/SKILL.md   Agent Skills
/// </code>
/// </summary>
public sealed class NexusAgentPathsOptions
{
    public string UserDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusShell", "agent");

    public string BundledDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Agent");

    /// <summary>Create the user folder skeleton (README, empty experts/skills folders, mcp.json template) on first run.</summary>
    public bool ScaffoldUserDirectory { get; set; } = true;

    public string BundledSkillsDirectory => Path.Combine(BundledDirectory, "skills");

    public string UserSkillsDirectory => Path.Combine(UserDirectory, "skills");

    public string BundledExpertsDirectory => Path.Combine(BundledDirectory, "experts");

    public string UserExpertsDirectory => Path.Combine(UserDirectory, "experts");
}
