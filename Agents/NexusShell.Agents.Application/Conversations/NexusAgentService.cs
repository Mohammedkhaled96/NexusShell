using System.Collections.Concurrent;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Configuration;
using NexusShell.Agents.Application.Abstractions.Mcp;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Application.Abstractions.Skills;
using NexusShell.Agents.Application.Models;
using NexusShell.Agents.Application.Orchestration;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Domain.Orchestration;

namespace NexusShell.Agents.Application.Conversations;

internal sealed class NexusAgentService(
    IAgentConfigurationProvider configurationProvider,
    IAgentOrchestratorFactory orchestratorFactory,
    IModelResolver modelResolver,
    IMcpToolProvider mcpToolProvider,
    ISkillsProviderFactory skillsProviderFactory,
    ISecretRedactor redactor,
    ILoggerFactory loggerFactory) : INexusAgentService
{
    public async Task<AgentHealthReport> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var config = await configurationProvider.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var providers = await modelResolver.GetProviderStatusesAsync(cancellationToken).ConfigureAwait(false);
        var mcp = await mcpToolProvider.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        var experts = config.Experts
            .Select(e => new ExpertHealth(e.Id, e.Name, e.Model, e.Enabled, e.Routable))
            .ToList();

        return new AgentHealthReport(config.Orchestration.Mode, providers, experts, mcp, skillsProviderFactory.SkillDirectories, config.Warnings);
    }

    public async Task<INexusAgentConversation> StartConversationAsync(string? initialContext = null, CancellationToken cancellationToken = default)
    {
        var config = await configurationProvider.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var agent = await orchestratorFactory.CreateAsync(config, cancellationToken).ConfigureAwait(false);
        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

        return new NexusAgentConversation(
            agent,
            session,
            config.Orchestration.Mode,
            string.IsNullOrWhiteSpace(initialContext) ? null : redactor.Redact(initialContext),
            redactor,
            loggerFactory.CreateLogger<NexusAgentConversation>());
    }

    public void ReloadConfiguration() => configurationProvider.Invalidate();
}

internal sealed class NexusAgentConversation(
    AIAgent agent,
    AgentSession session,
    OrchestrationMode mode,
    string? initialContext,
    ISecretRedactor redactor,
    ILogger logger) : INexusAgentConversation
{
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _approvedTools = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingContext = initialContext;

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public OrchestrationMode Mode { get; } = mode;

    public async Task<AgentTurnResult> SendAsync(string message, IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Secrets typed or pasted by the user never leave the machine.
            string text = redactor.Redact(message.Trim());
            if (_pendingContext is not null)
            {
                text = "Context from the terminal:\n---\n" + _pendingContext + "\n---\n\n" + text;
                _pendingContext = null;
            }

            using var scope = AgentRunScope.Begin(new AgentRunScope(progress, _approvedTools));
            var response = await agent.RunAsync(new ChatMessage(ChatRole.User, text), session, null, cancellationToken).ConfigureAwait(false);

            var experts = ExtractExperts(response);
            string answer = Mode == OrchestrationMode.Sequential ? FormatPipeline(response) : response.Text;

            logger.LogInformation(
                "Conversation {ConversationId} turn completed ({Mode}; experts: {Experts}; tokens: {Tokens}).",
                Id, Mode, string.Join(", ", experts.Select(e => e.ExpertId)), response.Usage?.TotalTokenCount);

            return new AgentTurnResult(redactor.Redact(answer), experts, response.Usage);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    private IReadOnlyList<ExpertContribution> ExtractExperts(AgentResponse response)
    {
        if (response.AdditionalProperties?.TryGetValue(MixtureOfExpertsAgent.ExpertResultsKey, out object? value) == true
            && value is IReadOnlyList<ExpertResult> results)
        {
            return results
                .Select(r => new ExpertContribution(r.ExpertId, r.ExpertName, r.Weight, r.Model, r.Succeeded, r.Error))
                .ToList();
        }

        string name = agent.Name ?? "agent";
        return [new ExpertContribution(name, name, 1.0, string.Empty, true, null)];
    }

    /// <summary>Sequential pipelines return every step; label each step by its author.</summary>
    private static string FormatPipeline(AgentResponse response)
    {
        var sb = new StringBuilder();
        foreach (var group in response.Messages.Where(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text)).GroupBy(m => m.AuthorName ?? "agent"))
        {
            if (sb.Length > 0) sb.AppendLine().AppendLine();
            sb.Append("## ").AppendLine(group.Key);
            foreach (var message in group) sb.AppendLine(message.Text);
        }
        return sb.Length > 0 ? sb.ToString() : response.Text;
    }
}
