using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Approval;
using NexusShell.Agents.Application.Abstractions.Mcp;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Application.Abstractions.Skills;
using NexusShell.Agents.Application.Abstractions.Tools;
using NexusShell.Agents.Application.Middleware;
using NexusShell.Agents.Application.Models;
using NexusShell.Agents.Application.SubAgents;
using NexusShell.Agents.Application.Tools;
using NexusShell.Agents.Domain.Configuration;
using NexusShell.Agents.Domain.Experts;
using NexusShell.Agents.Domain.Tools;

namespace NexusShell.Agents.Application.Agents;

public sealed record ExpertAgent(ExpertProfile Profile, AIAgent Agent, ResolvedModel Model);

/// <summary>Builds a ready-to-run agent for an expert definition.</summary>
public interface IExpertAgentFactory
{
    Task<ExpertAgent> CreateAsync(ExpertProfile expert, NexusAgentConfiguration configuration, int depth = 0, CancellationToken cancellationToken = default);
}

/// <summary>Thrown when an expert cannot run because no model is available (no API key set and no local model reachable).</summary>
public sealed class AgentUnavailableException(string message) : Exception(message);

public static class AgentTelemetry
{
    /// <summary>OpenTelemetry ActivitySource / Meter name for every agent and chat call.</summary>
    public const string SourceName = "NexusShell.Agents";
}

/// <summary>
/// Builder + Decorator pipeline for one expert:
/// <code>
/// IChatClient (provider adapter)
///   -> ChatClientAgent (instructions, tools, MCP tools, sub-agent tools, skills, bounded memory)
///   -> function middleware (audit, progress, secret redaction)
///   -> OpenTelemetry
///   -> InlineToolApprovalAgent (human-in-the-loop)
/// </code>
/// </summary>
internal sealed class ExpertAgentFactory(
    IModelResolver modelResolver,
    IChatClientFactory chatClientFactory,
    IToolCatalog toolCatalog,
    IMcpToolProvider mcpToolProvider,
    ISkillsProviderFactory skillsProviderFactory,
    IToolApprovalHandler approvalHandler,
    ISecretRedactor redactor,
    ToolInvocationMiddleware toolMiddleware,
    ILoggerFactory loggerFactory,
    IServiceProvider services,
    IModelHealthRegistry modelHealth) : IExpertAgentFactory
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ExpertAgentFactory>();

    public async Task<ExpertAgent> CreateAsync(
        ExpertProfile expert,
        NexusAgentConfiguration configuration,
        int depth = 0,
        CancellationToken cancellationToken = default)
    {
        var model = await modelResolver.ResolveAsync(expert.Model, cancellationToken).ConfigureAwait(false)
            ?? throw new AgentUnavailableException(NoModelMessage(expert, configuration));

        IChatClient chatClient = chatClientFactory.CreateChatClient(model.Provider, model.ModelId);

        var tools = new List<AITool>();
        var risks = new Dictionary<string, ToolRisk>(StringComparer.OrdinalIgnoreCase);

        void AddTool(AgentToolDescriptor descriptor)
        {
            if (risks.ContainsKey(descriptor.Name)) return;
            // Turned off by the user (by name or category), or blocked by read-only mode.
            if (configuration.DisabledTools.Contains(descriptor.Name)
                || configuration.DisabledTools.Contains(descriptor.Category)
                || configuration.ToolPolicy.IsBlocked(descriptor.Risk))
            {
                return;
            }
            risks[descriptor.Name] = descriptor.Risk;
            bool requiresApproval = descriptor.ForceApproval || (!descriptor.SkipApproval && configuration.ToolPolicy.RequiresApproval(descriptor.Risk));
            tools.Add(requiresApproval ? new ApprovalRequiredAIFunction(descriptor.Function) : descriptor.Function);
        }

        foreach (var descriptor in toolCatalog.Resolve(expert.Tools))
            AddTool(descriptor);

        if (expert.McpServers.Count > 0)
        {
            foreach (var descriptor in await mcpToolProvider.GetToolsAsync(expert.McpServers, cancellationToken).ConfigureAwait(false))
                AddTool(descriptor);
        }

        var subAgents = new List<ExpertProfile>();
        if (depth < configuration.Orchestration.MaxSubAgentDepth)
        {
            foreach (var sub in ResolveSubAgents(expert, configuration))
            {
                var captured = sub;
                tools.Add(SubAgentTool.Create(
                    captured,
                    async token => (await CreateAsync(captured, configuration, depth + 1, token).ConfigureAwait(false)).Agent));
                subAgents.Add(captured);
            }
        }

        var skills = skillsProviderFactory.CreateFor(expert, configuration);

        var options = new ChatClientAgentOptions
        {
            // Id-style name: providers reject spaces in message author names.
            Name = expert.Id,
            Description = expert.Description,
            ChatOptions = new ChatOptions
            {
                Instructions = ComposeInstructions(configuration, expert, subAgents),
                Tools = tools,
                Temperature = expert.Temperature,
                MaxOutputTokens = expert.MaxOutputTokens,
            },
            ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
            {
                ChatReducer = new RecentMessagesChatReducer(Math.Max(4, configuration.Orchestration.ExpertMemoryMessages)),
            }),
            AIContextProviders = skills is null ? null : [skills],
        };

        AIAgent agent = chatClient
            .AsAIAgent(options, loggerFactory, services)
            .AsBuilder()
            .Use((inner, context, next, token) => toolMiddleware.InvokeAsync(inner, context, next, token, expert.Name))
            .UseOpenTelemetry(AgentTelemetry.SourceName)
            .Build(services);

        agent = new InlineToolApprovalAgent(
            agent,
            approvalHandler,
            redactor,
            risks,
            configuration.Orchestration.MaxApprovalRounds,
            loggerFactory.CreateLogger<InlineToolApprovalAgent>(),
            expert.Name);

        _logger.LogInformation(
            "Expert {Expert} ready on {Model} (depth {Depth}, {ToolCount} tools, {SubAgentCount} sub-agents, skills: {HasSkills}).",
            expert.Id, model, depth, tools.Count, subAgents.Count, skills is not null);

        return new ExpertAgent(expert, agent, model);
    }

    /// <summary>Explains WHY nothing is usable: providers skipped for now (quota, unreachable, key) or none configured.</summary>
    private string NoModelMessage(ExpertProfile expert, NexusAgentConfiguration configuration)
    {
        var skipped = configuration.Providers.Values
            .Where(p => p.Enabled)
            .Select(p => (p.Id, Issue: modelHealth.GetProviderIssue(p.Id)))
            .Where(x => x.Issue is not null)
            .Select(x => $"{x.Id} ({x.Issue})")
            .ToList();

        if (skipped.Count > 0)
        {
            return $"No model is available for '{expert.Name}' right now. Skipped for now: {string.Join("; ", skipped)}. " +
                   "NexusAgent uses them again automatically when they recover; you can also add another provider's key in Agent keys.";
        }

        return modelHealth.TimeUntilNextRecovery() is not null
            ? $"No model is available for '{expert.Name}' right now: the configured models are temporarily unavailable. Try again shortly."
            : $"No model is available for '{expert.Name}'. Set a provider API key (as a user environment variable) or start a local model server such as Ollama.";
    }

    private static IEnumerable<ExpertProfile> ResolveSubAgents(ExpertProfile expert, NexusAgentConfiguration configuration)
    {
        if (expert.SubAgents.Count == 0) return [];

        bool all = expert.SubAgents.Any(s => s.Trim() == "*");
        return configuration.EnabledExperts
            .Where(e => !string.Equals(e.Id, expert.Id, StringComparison.OrdinalIgnoreCase))
            .Where(e => all || expert.SubAgents.Contains(e.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    internal static string ComposeInstructions(NexusAgentConfiguration configuration, ExpertProfile expert, IReadOnlyList<ExpertProfile> subAgents)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(configuration.SharedInstructions))
            sb.AppendLine(configuration.SharedInstructions.Trim()).AppendLine();

        sb.Append("## Your role: ").AppendLine(expert.Name)
          .AppendLine(expert.Instructions.Trim());

        if (subAgents.Count > 0)
        {
            sb.AppendLine()
              .AppendLine("## Sub-agents")
              .AppendLine("You can delegate focused sub-tasks to these specialists. Delegate only when it clearly improves the answer, and give them complete context.")
              .AppendLine("When a task needs a tool you do not have (for example writing files or running commands), delegate it to a specialist that has it instead of asking the user to do it:");
            foreach (var sub in subAgents)
                sb.Append("- ").Append(SubAgentTool.ToolName(sub.Id)).Append(": ").AppendLine(sub.Description);
        }

        return sb.ToString();
    }
}
