using NexusShell.Agents.Domain.Connectors;

namespace NexusShell.Agents.Application.Connectors;

/// <summary>
/// A ready-made MCP server for an external application. Adding it writes an entry to the user's
/// mcp.json that refers to the secrets by environment variable name only ("${env:NAME}"), and
/// offers its tools to every expert. Tools that change things ask the user unless the server
/// marks them read-only.
/// </summary>
public sealed record McpConnectorPreset
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>Program to start (stdio servers), e.g. "npx" or "uvx"; null for remote servers.</summary>
    public string? Command { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Remote (HTTP) server address.</summary>
    public string? Url { get; init; }

    /// <summary>Environment of the server process; values are "${env:NAME}" references.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>HTTP headers for remote servers; values are "${env:NAME}" references.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<ConnectorField> Fields { get; init; } = [];

    /// <summary>What must be installed, e.g. "Node.js (npx)".</summary>
    public string Requires { get; init; } = string.Empty;

    public string SetupUrl { get; init; } = string.Empty;

    public IReadOnlyList<string> SetupSteps { get; init; } = [];

    /// <summary>Extra caution shown to the user (deprecations, alternatives).</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>MCP connector templates for applications NexusAgent has no built-in connector for.</summary>
public static class McpConnectorPresets
{
    private static string Env(string name) => "${env:" + name + "}";

    public static IReadOnlyList<McpConnectorPreset> All { get; } =
    [
        new()
        {
            Id = "github-mcp",
            Name = "GitHub (all tools, official MCP server)",
            Description = "GitHub's own remote MCP server: repositories, code, issues, pull requests, Actions, projects and more. The built-in GitHub connector covers the everyday tasks; add this for everything else.",
            Url = "https://api.githubcopilot.com/mcp/",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Env("GITHUB_TOKEN") },
            Fields = [new("GITHUB_TOKEN", "GitHub personal access token", true, "A fine-grained token limited to the repositories you want the agent to use.")],
            Requires = "Nothing to install (remote server).",
            SetupUrl = "https://github.com/settings/personal-access-tokens/new",
            SetupSteps =
            [
                "Open the setup page and create a fine-grained personal access token.",
                "Choose the repositories and the permissions the agent may use (read-only is safest).",
                "Paste the token in the GitHub personal access token box and press Save.",
                "Press Add to NexusAgent.",
            ],
        },
        new()
        {
            Id = "notion",
            Name = "Notion",
            Description = "Search, read and update Notion pages and databases shared with your integration.",
            Command = "npx",
            Arguments = ["-y", "@notionhq/notion-mcp-server"],
            Environment = new Dictionary<string, string> { ["NOTION_TOKEN"] = Env("NOTION_TOKEN") },
            Fields = [new("NOTION_TOKEN", "Notion integration secret", true, "Starts with ntn_. Created on the Notion integrations page.")],
            Requires = "Node.js (npx)",
            SetupUrl = "https://www.notion.so/profile/integrations",
            SetupSteps =
            [
                "Open the setup page and create an internal integration.",
                "Copy its secret, paste it in the Notion integration secret box and press Save.",
                "In Notion, share the pages the agent may use with the integration (page menu, Connections).",
                "Press Add to NexusAgent.",
            ],
            Note = "This is Notion's open-source server. Notion also offers a remote server that signs in with OAuth.",
        },
        new()
        {
            Id = "atlassian",
            Name = "Jira and Confluence",
            Description = "Search and read Jira issues and Confluence pages, create and update issues (changes ask first).",
            Command = "uvx",
            Arguments = ["mcp-atlassian"],
            Environment = new Dictionary<string, string>
            {
                ["JIRA_URL"] = Env("JIRA_URL"),
                ["JIRA_USERNAME"] = Env("JIRA_USERNAME"),
                ["JIRA_API_TOKEN"] = Env("JIRA_API_TOKEN"),
                ["CONFLUENCE_URL"] = Env("CONFLUENCE_URL"),
                ["CONFLUENCE_USERNAME"] = Env("JIRA_USERNAME"),
                ["CONFLUENCE_API_TOKEN"] = Env("JIRA_API_TOKEN"),
            },
            Fields =
            [
                new("JIRA_URL", "Jira address", false, "For example https://your-company.atlassian.net"),
                new("CONFLUENCE_URL", "Confluence address", false, "For example https://your-company.atlassian.net/wiki"),
                new("JIRA_USERNAME", "Atlassian account e-mail", false, "The e-mail you sign in to Atlassian with."),
                new("JIRA_API_TOKEN", "Atlassian API token", true, "Created on the Atlassian API tokens page."),
            ],
            Requires = "uv (uvx)",
            SetupUrl = "https://id.atlassian.com/manage-profile/security/api-tokens",
            SetupSteps =
            [
                "Open the setup page and create an API token.",
                "Fill in the Jira address, the Confluence address, your Atlassian e-mail and the token, pressing Save after each.",
                "Install uv if it is not installed (search for: install uv Python package manager).",
                "Press Add to NexusAgent.",
            ],
        },
        new()
        {
            Id = "slack",
            Name = "Slack",
            Description = "Read channels and threads, post messages and replies (posting asks first).",
            Command = "npx",
            Arguments = ["-y", "@modelcontextprotocol/server-slack"],
            Environment = new Dictionary<string, string>
            {
                ["SLACK_BOT_TOKEN"] = Env("SLACK_BOT_TOKEN"),
                ["SLACK_TEAM_ID"] = Env("SLACK_TEAM_ID"),
            },
            Fields =
            [
                new("SLACK_BOT_TOKEN", "Slack bot token", true, "Starts with xoxb-. From your Slack app's OAuth and Permissions page."),
                new("SLACK_TEAM_ID", "Slack workspace id", false, "Starts with T; it is in the address when Slack is open in a browser."),
            ],
            Requires = "Node.js (npx)",
            SetupUrl = "https://api.slack.com/apps",
            SetupSteps =
            [
                "Open the setup page, create a Slack app and add bot scopes such as channels:history, channels:read, chat:write and users:read.",
                "Install the app to your workspace and copy the Bot User OAuth Token.",
                "Paste the token and the workspace id, pressing Save after each.",
                "Press Add to NexusAgent.",
            ],
            Note = "This reference server is archived by its authors; it still works, but check for a maintained alternative for heavy use.",
        },
        new()
        {
            Id = "playwright",
            Name = "Web browser automation (Playwright)",
            Description = "The agent opens web pages in a real browser, reads them, fills forms and clicks (actions ask first).",
            Command = "npx",
            Arguments = ["@playwright/mcp@latest"],
            Requires = "Node.js (npx)",
            SetupUrl = "https://github.com/microsoft/playwright-mcp",
            SetupSteps = ["Install Node.js if it is not installed.", "Press Add to NexusAgent. The first use downloads the browser."],
        },
        new()
        {
            Id = "context7",
            Name = "Up-to-date library documentation (Context7)",
            Description = "Current documentation and code examples for programming libraries, so answers use today's APIs instead of outdated ones.",
            Command = "npx",
            Arguments = ["-y", "@upstash/context7-mcp"],
            Requires = "Node.js (npx)",
            SetupUrl = "https://github.com/upstash/context7",
            SetupSteps = ["Install Node.js if it is not installed.", "Press Add to NexusAgent."],
        },
    ];
}
