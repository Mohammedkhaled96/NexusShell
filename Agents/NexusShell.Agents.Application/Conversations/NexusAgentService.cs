using System.Collections.Concurrent;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Attachments;
using NexusShell.Agents.Application.Abstractions.Configuration;
using NexusShell.Agents.Application.Abstractions.Mcp;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Application.Abstractions.Skills;
using NexusShell.Agents.Application.Models;
using NexusShell.Agents.Application.Orchestration;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Application.Tools;
using NexusShell.Agents.Domain.Configuration;
using NexusShell.Agents.Domain.Orchestration;
using NexusShell.Agents.Domain.Providers;

namespace NexusShell.Agents.Application.Conversations;

internal sealed class NexusAgentService(
    IAgentConfigurationProvider configurationProvider,
    IAgentOrchestratorFactory orchestratorFactory,
    IModelResolver modelResolver,
    IMcpToolProvider mcpToolProvider,
    ISkillsProviderFactory skillsProviderFactory,
    ISecretRedactor redactor,
    IModelDiscovery discovery,
    IModelHealthRegistry modelHealth,
    IToolCatalog toolCatalog,
    IAttachmentReader attachmentReader,
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

    public async Task<AgentCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var config = await configurationProvider.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var statuses = (await modelResolver.GetProviderStatusesAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(s => s.ProviderId, StringComparer.OrdinalIgnoreCase);

        // Preference order first, then the remaining providers.
        var order = config.ModelPreference
            .Select(p => p.Split('/')[0].Trim())
            .Concat(config.Providers.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var models = new List<ModelOption>();
        foreach (var providerId in order)
        {
            if (!config.Providers.TryGetValue(providerId, out var provider) || !provider.Enabled) continue;
            statuses.TryGetValue(provider.Id, out var status);
            bool online = status?.IsAvailable == true;
            string detail = status?.Detail ?? string.Empty;

            var known = provider.KnownModels.ToList();
            IReadOnlyList<string> installed = provider.IsLocal && online
                ? await discovery.ListModelsAsync(provider, cancellationToken).ConfigureAwait(false)
                : [];

            // Installed models not configured yet ("llama3.2:latest" is the configured "llama3.2").
            var extra = installed.Where(i => !known.Any(k => ModelResolver.SameModel(i, k)));
            foreach (string model in known.Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                bool notInstalled = installed.Count > 0 && !installed.Any(i => ModelResolver.SameModel(i, model)) && !installed.Contains(model, StringComparer.OrdinalIgnoreCase);
                bool usable = online && !notInstalled && modelHealth.IsUsable(provider.Id, model);
                string why = notInstalled
                    ? provider.Kind == ProviderKind.Ollama ? $"not installed; install it with: ollama pull {model}" : "not loaded on this server"
                    : modelHealth.GetProviderIssue(provider.Id) is { } issue ? "skipped for now: " + issue
                    : detail;
                models.Add(new ModelOption(provider.Id, model, provider.IsLocal, usable, why));
            }
        }

        var experts = config.Experts.Where(e => e.Enabled)
            .Select(e => new ExpertOption(e.Id, e.Name, e.Description, e.Routable))
            .ToList();

        var tools = toolCatalog.All
            .Select(t => new ToolOption(t.Name, t.Category, t.Risk, t.Function.Description))
            .ToList();

        var mcpStatus = (await mcpToolProvider.GetStatusAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var mcp = config.McpServers
            .Select(s => mcpStatus.TryGetValue(s.Name, out var st)
                ? new McpOption(s.Name, s.Enabled, st.IsConnected, st.ToolCount, st.Detail)
                : new McpOption(s.Name, s.Enabled, false, 0, s.Enabled ? "Configured" : "Disabled"))
            .ToList();

        return new AgentCatalog(
            models.OrderByDescending(m => m.IsAvailable).ToList(),
            experts,
            tools,
            skillsProviderFactory.ListSkills(),
            mcp);
    }

    public Task<INexusAgentConversation> StartConversationAsync(string? initialContext = null, CancellationToken cancellationToken = default) =>
        StartConversationAsync(initialContext, ConversationOptions.Default, cancellationToken);

    public async Task<INexusAgentConversation> StartConversationAsync(string? initialContext, ConversationOptions options, CancellationToken cancellationToken = default)
    {
        var conversation = new NexusAgentConversation(
            BuildAsync,
            string.IsNullOrWhiteSpace(initialContext) ? null : redactor.Redact(initialContext),
            redactor,
            attachmentReader,
            loggerFactory.CreateLogger<NexusAgentConversation>());
        await conversation.UpdateOptionsAsync(options, cancellationToken).ConfigureAwait(false);
        return conversation;
    }

    public void ReloadConfiguration() => configurationProvider.Invalidate();

    /// <summary>Builds the agent for the latest configuration with the conversation's options applied.</summary>
    private async Task<(AIAgent Agent, NexusAgentConfiguration Configuration)> BuildAsync(ConversationOptions options, CancellationToken cancellationToken)
    {
        var baseConfig = await configurationProvider.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var config = ConversationOptionsApplier.Apply(baseConfig, options);
        var agent = await orchestratorFactory.CreateAsync(config, cancellationToken).ConfigureAwait(false);
        return (agent, config);
    }
}

internal sealed class NexusAgentConversation(
    Func<ConversationOptions, CancellationToken, Task<(AIAgent Agent, NexusAgentConfiguration Configuration)>> build,
    string? initialContext,
    ISecretRedactor redactor,
    IAttachmentReader attachmentReader,
    ILogger logger) : INexusAgentConversation
{
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _approvedTools = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingContext = initialContext;
    private AIAgent? _agent;
    private AgentSession? _session;
    private NexusAgentConfiguration? _configuration;
    private string? _fingerprint;

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public OrchestrationMode Mode => _configuration?.Orchestration.Mode ?? OrchestrationMode.MixtureOfExperts;

    public ConversationOptions Options { get; private set; } = ConversationOptions.Default;

    public async Task UpdateOptionsAsync(ConversationOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string fingerprint = options.Fingerprint();
        if (_agent is not null && fingerprint == _fingerprint) return;

        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (agent, configuration) = await build(options, cancellationToken).ConfigureAwait(false);
            var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

            // Keep the conversation: experts of the new setup catch up from the shared transcript.
            if (_session is MixtureOfExpertsSession previous && session is MixtureOfExpertsSession next)
            {
                foreach (var turn in previous.Transcript) next.AppendTurn(turn);
            }

            _agent = agent;
            _session = session;
            _configuration = configuration;
            _fingerprint = fingerprint;
            Options = options;
            logger.LogInformation("Conversation {ConversationId} options applied: {Options}", Id, fingerprint);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        _fingerprint = null; // forces a rebuild with the same options
        return UpdateOptionsAsync(Options, cancellationToken);
    }

    public Task<AgentTurnResult> SendAsync(string message, IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default) =>
        SendAsync(message, [], progress, cancellationToken);

    public async Task<AgentTurnResult> SendAsync(string message, IReadOnlyList<string> attachments, IProgress<AgentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (_agent is null) await UpdateOptionsAsync(Options, cancellationToken).ConfigureAwait(false);

        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Secrets typed or pasted by the user never leave the machine.
            var text = new StringBuilder(redactor.Redact(message.Trim()));
            if (_pendingContext is not null)
            {
                text.Insert(0, "Context from the terminal:\n---\n" + _pendingContext + "\n---\n\n");
                _pendingContext = null;
            }

            IReadOnlyList<string> notes = [];
            if (attachments.Count > 0)
            {
                var bundle = await attachmentReader.ReadAsync(attachments, cancellationToken).ConfigureAwait(false);
                notes = bundle.Notes;
                if (!string.IsNullOrWhiteSpace(bundle.Text))
                    text.AppendLine().AppendLine().Append(redactor.Redact(bundle.Text));
            }

            var scope = new AgentRunScope(progress, _approvedTools) { WorkingDirectory = _configuration?.WorkingDirectory };
            using var _ = AgentRunScope.Begin(scope);
            var response = await _agent!.RunAsync(new ChatMessage(ChatRole.User, text.ToString()), _session, null, cancellationToken).ConfigureAwait(false);

            var experts = ExtractExperts(response);
            string answer = Mode == OrchestrationMode.Sequential ? FormatPipeline(response) : response.Text;

            logger.LogInformation(
                "Conversation {ConversationId} turn completed ({Mode}; experts: {Experts}; tokens: {Tokens}; attachments: {Attachments}).",
                Id, Mode, string.Join(", ", experts.Select(e => e.ExpertId)), response.Usage?.TotalTokenCount, attachments.Count);

            return new AgentTurnResult(redactor.Redact(answer), experts, response.Usage) { AttachmentNotes = notes };
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

        string name = _agent?.Name ?? "agent";
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
