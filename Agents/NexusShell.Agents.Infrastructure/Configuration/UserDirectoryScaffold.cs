using Microsoft.Extensions.Logging;

namespace NexusShell.Agents.Infrastructure.Configuration;

/// <summary>Creates the user's agent folder on first run. Never overwrites existing files.</summary>
internal static class UserDirectoryScaffold
{
    private const string Readme =
        """
        # NexusShell agent — your folder

        Everything here overrides or extends the defaults shipped in `<NexusShell>\Agent`.
        Changes are picked up when you start a new agent conversation.

        | Path | What it is |
        |---|---|
        | `agent.json` | Providers, model preferences, orchestration (moe / coordinator / sequential), tool approval policy. Only the fields you write override the defaults. |
        | `instructions.md` | House rules shared by every expert (replaces the bundled file). |
        | `mcp.json` | MCP servers (`mcpServers` format, same as Claude Desktop / Cursor). |
        | `experts/*.md` | Experts: front-matter (name, description, model, keywords, tools, skills, mcp, subagents) + instructions. Same file name as a bundled expert = override; `enabled: false` = switch it off. |
        | `skills/<name>/SKILL.md` | Agent Skills (folder name must equal the skill `name`). |

        ## Secrets (API keys, tokens)

        Never put a key in these files. Configuration only holds the NAME of an
        environment variable (`"apiKeyEnv": "OPENAI_API_KEY"`, or `${env:NAME}` in
        mcp.json). Set the value as a user environment variable, either from the
        NexusShell AI chat ("Agent keys" button) or in a terminal with the setx
        command (variable name first, then the key).

        Configuration entries that contain literal secrets are rejected.
        """;

    private const string McpTemplate =
        """
        {
          // MCP servers for the NexusShell agent.
          // Secrets MUST be ${env:NAME} references - literal tokens are rejected.
          // Experts opt in with "mcp: [server-name]" (or "mcp: ['*']") in their front-matter.
          //
          // Example:
          // "mcpServers": {
          //   "github": {
          //     "command": "npx",
          //     "args": ["-y", "@modelcontextprotocol/server-github"],
          //     "env": { "GITHUB_PERSONAL_ACCESS_TOKEN": "${env:GITHUB_PERSONAL_ACCESS_TOKEN}" }
          //   }
          // }
          "mcpServers": {}
        }
        """;

    public static void Ensure(NexusAgentPathsOptions paths, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(paths.UserDirectory);
            Directory.CreateDirectory(paths.UserExpertsDirectory);
            Directory.CreateDirectory(paths.UserSkillsDirectory);
            WriteIfMissing(Path.Combine(paths.UserDirectory, "README.md"), Readme);
            WriteIfMissing(Path.Combine(paths.UserDirectory, "mcp.json"), McpTemplate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not create the agent user folder {Folder}.", paths.UserDirectory);
        }
    }

    private static void WriteIfMissing(string path, string content)
    {
        if (!File.Exists(path)) File.WriteAllText(path, content);
    }
}
