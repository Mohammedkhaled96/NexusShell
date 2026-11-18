using System.Text;
using NexusShell.Agents.Domain.Configuration;
using NexusShell.Agents.Domain.Orchestration;
using NexusShell.Agents.Domain.Tools;

namespace NexusShell.Agents.Application.Conversations;

/// <summary>
/// Pure function: the base configuration + the user's choices for one conversation
/// = the configuration that conversation runs with. Nothing is written to disk.
/// </summary>
public static class ConversationOptionsApplier
{
    public static NexusAgentConfiguration Apply(NexusAgentConfiguration configuration, ConversationOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        var result = configuration;

        // 1. Chosen model: every expert, the router and the synthesizer use it first.
        //    Automatic failover still applies when it fails (quota, offline, retired ...).
        if (!options.IsAutomaticModel)
        {
            string model = options.Model!.Trim();
            result = result with
            {
                Experts = result.Experts.Select(e => e with { Model = model }).ToList(),
                Orchestration = result.Orchestration with
                {
                    SynthesizerModel = model,
                    Router = result.Orchestration.Router with { Model = model },
                },
            };
        }

        // 2. Chosen expert: only that expert answers, and every other expert is its sub-agent
        //    (ask_<id> tools), so a task needing tools it lacks (files, commands) is delegated, not refused.
        if (!string.IsNullOrWhiteSpace(options.Expert) && result.FindExpert(options.Expert) is { Enabled: true } chosen)
        {
            result = result with
            {
                Experts = result.Experts.Select(e => string.Equals(e.Id, chosen.Id, StringComparison.OrdinalIgnoreCase)
                    ? e with { Routable = true, SubAgents = ["*"] }
                    : e with { Routable = false }).ToList(),
                Orchestration = result.Orchestration with
                {
                    Mode = OrchestrationMode.MixtureOfExperts,
                    DefaultExpert = chosen.Id,
                },
            };
        }

        // 3. Permission mode.
        result = options.Permission switch
        {
            PermissionMode.AutoApproveEdits => result with
            {
                ToolPolicy = result.ToolPolicy with { AutoApprove = Union(result.ToolPolicy.AutoApprove, ToolRisk.ReadOnly, ToolRisk.Mutating) },
            },
            PermissionMode.ReadOnly => result with
            {
                ToolPolicy = result.ToolPolicy with { Blocked = Union(result.ToolPolicy.Blocked, ToolRisk.Mutating, ToolRisk.Destructive) },
                Skills = result.Skills with { EnableScripts = false },
            },
            _ => result,
        };

        // 4. Tools, skills and MCP servers the user turned off.
        if (options.DisabledMcpServers.Count > 0)
        {
            result = result with
            {
                McpServers = result.McpServers
                    .Select(s => options.DisabledMcpServers.Contains(s.Name) ? s with { Enabled = false } : s)
                    .ToList(),
            };
        }

        result = result with
        {
            DisabledTools = new HashSet<string>(result.DisabledTools.Concat(options.DisabledTools), StringComparer.OrdinalIgnoreCase),
            DisabledSkills = new HashSet<string>(result.DisabledSkills.Concat(options.DisabledSkills), StringComparer.OrdinalIgnoreCase),
        };

        // 5. Working folder: tools resolve relative paths against it; experts are told about it.
        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            string folder = options.WorkingDirectory.Trim();
            result = result with
            {
                WorkingDirectory = folder,
                SharedInstructions = new StringBuilder(result.SharedInstructions)
                    .AppendLine().AppendLine()
                    .AppendLine("## Working folder")
                    .Append("The user is working in: ").AppendLine(folder)
                    .AppendLine("Relative paths, file tools and shell commands refer to this folder unless the user says otherwise.")
                    .ToString()
                    .Trim(),
            };
        }

        if (options.Permission == PermissionMode.ReadOnly)
        {
            result = result with
            {
                SharedInstructions = (result.SharedInstructions + Environment.NewLine + Environment.NewLine +
                    "## Read-only mode" + Environment.NewLine +
                    "The user chose read-only (plan) mode: investigate and propose a plan or exact commands, but do not change files or the system. " +
                    "When the user asks for a change (create, edit or delete a file, run a command), say in one sentence that read-only mode is on " +
                    "and that they can allow changes by setting Permissions to \"Ask before changes\" (Alt+P) or by typing /mode ask, then give the plan.").Trim(),
            };
        }

        return result;
    }

    private static HashSet<ToolRisk> Union(IReadOnlySet<ToolRisk> existing, params ToolRisk[] extra) =>
        new(existing.Concat(extra));
}
