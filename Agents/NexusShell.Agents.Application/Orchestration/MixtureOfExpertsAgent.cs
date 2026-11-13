using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Application.Agents;
using NexusShell.Agents.Application.Models;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Application.Routing;
using NexusShell.Agents.Domain.Configuration;
using NexusShell.Agents.Domain.Experts;
using NexusShell.Agents.Domain.Routing;

namespace NexusShell.Agents.Application.Orchestration;

/// <summary>
/// Mixture-of-Experts agent, implemented as a first-class Agent Framework
/// <see cref="AIAgent"/> so it can itself be wrapped in middleware, used as a
/// sub-agent, exposed as an <c>IChatClient</c>, or placed in a workflow.
/// <para>Per request:</para>
/// <list type="number">
/// <item><b>Gate</b>: the router scores every routable expert and keeps the sparse top-k.</item>
/// <item><b>Experts</b>: the selected experts run in parallel, each with its own model,
///   tools, MCP servers, skills, sub-agents and private session memory.</item>
/// <item><b>Combine</b>: one expert -> its answer as-is; several -> weighted synthesis.</item>
/// </list>
/// Expert agents are created lazily, so unselected experts cost nothing.
/// </summary>
public sealed class MixtureOfExpertsAgent : AIAgent
{
    /// <summary>AdditionalProperties key holding the <see cref="ExpertResult"/> list of a response.</summary>
    public const string ExpertResultsKey = "nexus.experts";

    /// <summary>AdditionalProperties key holding the <see cref="RoutingDecision"/> of a response.</summary>
    public const string RoutingDecisionKey = "nexus.routing";

    private readonly NexusAgentConfiguration _configuration;
    private readonly IExpertRouter _router;
    private readonly IExpertAgentFactory _expertFactory;
    private readonly ResponseSynthesizer _synthesizer;
    private readonly IModelHealthRegistry _modelHealth;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<ExpertAgent>>> _experts = new(StringComparer.OrdinalIgnoreCase);

    internal MixtureOfExpertsAgent(
        NexusAgentConfiguration configuration,
        IExpertRouter router,
        IExpertAgentFactory expertFactory,
        ResponseSynthesizer synthesizer,
        IModelHealthRegistry modelHealth,
        ILogger logger)
    {
        _configuration = configuration;
        _router = router;
        _expertFactory = expertFactory;
        _synthesizer = synthesizer;
        _modelHealth = modelHealth;
        _logger = logger;
    }

    public override string? Name => "nexus-moe";

    public override string? Description => "NexusShell mixture-of-experts agent: routes each request to the best specialist experts and merges their answers.";

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<AgentSession>(new MixtureOfExpertsSession());

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        var moeSession = AsMoeSession(session) ?? new MixtureOfExpertsSession();
        var input = messages.ToList();
        string request = ExtractRequest(input);
        var settings = _configuration.Orchestration;

        // 1. Gate.
        AgentRunScope.Report(AgentProgressKind.Routing, "Choosing the best experts");
        var candidates = _configuration.EnabledExperts.Where(e => e.Routable).ToList();
        var decision = await _router.RouteAsync(
            new RoutingRequest(request, candidates, moeSession.RecentTurns(settings.SharedHistoryTurns), settings.Router),
            cancellationToken).ConfigureAwait(false);

        var selected = decision.Selected
            .Select(s => (Score: s, Profile: _configuration.FindExpert(s.ExpertId)))
            .Where(x => x.Profile is { Enabled: true })
            .Select(x => (x.Score, Profile: x.Profile!))
            .ToList();

        if (selected.Count == 0)
        {
            var fallback = DefaultExpert();
            if (fallback is null)
                return Respond("No experts are enabled. Add or enable an expert in the agent configuration.", [], decision, null);
            selected.Add((new ExpertScore(fallback.Id, 1.0, "default expert"), fallback));
        }

        AgentRunScope.Report(
            AgentProgressKind.ExpertsSelected,
            "Experts: " + string.Join(", ", selected.Select(s => s.Profile.Name)));

        // 2. Experts in parallel.
        var results = (await Task.WhenAll(selected.Select(s =>
                RunExpertAsync(s.Profile, s.Score.Score, input, moeSession, cancellationToken)))
            .ConfigureAwait(false)).ToList();

        var succeeded = results.Where(r => r.Succeeded).ToList();
        if (succeeded.Count == 0)
        {
            var fallback = DefaultExpert();
            if (fallback is not null && results.All(r => !string.Equals(r.ExpertId, fallback.Id, StringComparison.OrdinalIgnoreCase)))
            {
                AgentRunScope.Report(AgentProgressKind.Warning, $"Selected experts failed; trying {fallback.Name}");
                var retry = await RunExpertAsync(fallback, 1.0, input, moeSession, cancellationToken).ConfigureAwait(false);
                results.Add(retry);
                if (retry.Succeeded) succeeded.Add(retry);
            }
        }

        UsageDetails? usage = Sum(results.Select(r => r.Usage));

        if (succeeded.Count == 0)
        {
            string errors = string.Join(Environment.NewLine, results.Select(r => $"- {r.ExpertName}: {r.Error ?? "empty answer"}"));
            return Respond("I could not get an answer from any expert:" + Environment.NewLine + errors, results, decision, usage);
        }

        // 3. Combine (weights renormalised over the experts that answered).
        double total = succeeded.Sum(r => r.Weight);
        var weighted = succeeded.Select(r => r with { Weight = total > 0 ? r.Weight / total : 1.0 / succeeded.Count }).ToList();

        string finalText;
        if (weighted.Count == 1)
        {
            finalText = weighted[0].Text;
        }
        else
        {
            AgentRunScope.Report(AgentProgressKind.Synthesizing, "Combining the experts' answers");
            var (text, synthesisUsage) = await _synthesizer.SynthesizeAsync(
                request, weighted, moeSession.RecentTurns(settings.SharedHistoryTurns),
                _configuration.SharedInstructions, settings.SynthesizerModel, cancellationToken).ConfigureAwait(false);
            finalText = text;
            usage = Sum([usage, synthesisUsage]);
        }

        moeSession.AppendTurn(new ConversationTurn(request, finalText, weighted.Select(r => r.ExpertId).ToList()));

        var normalizedById = weighted.ToDictionary(r => r.ExpertId, StringComparer.OrdinalIgnoreCase);
        var reported = results.Select(r => normalizedById.TryGetValue(r.ExpertId, out var n) ? n : r).ToList();
        return Respond(finalText, reported, decision, usage);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Experts run in parallel and are combined at the end, so the answer is
        // produced as a whole; progress is reported through AgentRunScope meanwhile.
        var response = await RunCoreAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToAgentResponseUpdates())
            yield return update;
    }

    protected override async ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions,
        CancellationToken cancellationToken)
    {
        var moeSession = AsMoeSession(session) ?? throw new ArgumentException("Session was not created by this agent.", nameof(session));

        var transcript = new JsonArray();
        foreach (var turn in moeSession.Transcript)
        {
            transcript.Add(new JsonObject
            {
                ["user"] = turn.User,
                ["assistant"] = turn.Assistant,
                ["experts"] = new JsonArray(turn.Experts.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
            });
        }

        var experts = new JsonObject();
        foreach (var (id, state) in moeSession.ExpertStates)
        {
            JsonElement? serialized = state.PendingSerializedSession;
            if (state.Session is not null && _experts.TryGetValue(id, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
            {
                serialized = await lazy.Value.Result.Agent
                    .SerializeSessionAsync(state.Session, jsonSerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            var node = new JsonObject { ["seenTurns"] = state.SeenTurns };
            if (serialized is not null) node["session"] = JsonNode.Parse(serialized.Value.GetRawText());
            experts[id] = node;
        }

        var root = new JsonObject { ["version"] = 1, ["transcript"] = transcript, ["experts"] = experts };
        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions,
        CancellationToken cancellationToken)
    {
        var session = new MixtureOfExpertsSession();

        if (serializedState.TryGetProperty("transcript", out var transcript) && transcript.ValueKind == JsonValueKind.Array)
        {
            foreach (var turn in transcript.EnumerateArray())
            {
                string user = turn.TryGetProperty("user", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                string assistant = turn.TryGetProperty("assistant", out var a) ? a.GetString() ?? string.Empty : string.Empty;
                var experts = turn.TryGetProperty("experts", out var e) && e.ValueKind == JsonValueKind.Array
                    ? e.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList()
                    : [];
                session.AppendTurn(new ConversationTurn(user, assistant, experts));
            }
        }

        if (serializedState.TryGetProperty("experts", out var expertStates) && expertStates.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in expertStates.EnumerateObject())
            {
                var state = session.GetExpertState(property.Name);
                if (property.Value.TryGetProperty("seenTurns", out var seen) && seen.TryGetInt32(out int seenTurns))
                    state.SeenTurns = seenTurns;
                if (property.Value.TryGetProperty("session", out var expertSession))
                    state.PendingSerializedSession = expertSession.Clone();
            }
        }

        return ValueTask.FromResult<AgentSession>(session);
    }

    /// <summary>
    /// Runs one expert. When its model turns out to be unavailable ("model not
    /// found": retired, renamed, not enabled for the key), the model is
    /// quarantined and the expert is rebuilt on the next available model.
    /// </summary>
    private async Task<ExpertResult> RunExpertAsync(
        ExpertProfile profile,
        double weight,
        IReadOnlyList<ChatMessage> input,
        MixtureOfExpertsSession session,
        CancellationToken cancellationToken)
    {
        const int MaxModelAttempts = 4;
        ExpertResult result = null!;
        ModelProviderException? lastModelFailure = null;
        for (int attempt = 1; attempt <= MaxModelAttempts; attempt++)
        {
            result = await RunExpertOnceAsync(profile, weight, input, session, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded) return result;

            if (result.ModelFailure is not { CanFallBack: true } failure)
            {
                // Ran out of models after retirements: report the real cause, not just "no model available".
                return lastModelFailure is null
                    ? result
                    : result with { Error = lastModelFailure.Message + " No other configured model is available; add a working model to \"fallbackModels\" in agent.json or set another provider's key." };
            }

            lastModelFailure = failure;
            _modelHealth.ReportUnusable(failure.ProviderId, failure.ModelId, failure.Message, failure.QuarantineDuration);
            _experts.TryRemove(profile.Id, out _);
            var state = session.GetExpertState(profile.Id);
            state.Session = null;     // the next model starts a fresh session...
            state.SeenTurns = 0;      // ...and catches up from the shared transcript
            AgentRunScope.Report(AgentProgressKind.Warning, $"{profile.Name}: model {failure.ModelId} is unavailable, trying another model", profile.Name);
        }
        return result;
    }

    private async Task<ExpertResult> RunExpertOnceAsync(
        ExpertProfile profile,
        double weight,
        IReadOnlyList<ChatMessage> input,
        MixtureOfExpertsSession session,
        CancellationToken cancellationToken)
    {
        string model = "unresolved";
        try
        {
            var expert = await GetExpertAsync(profile).ConfigureAwait(false);
            model = expert.Model.ToString();
            var state = session.GetExpertState(profile.Id);

            await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (state.Session is null)
                {
                    state.Session = state.PendingSerializedSession is { } pending
                        ? await expert.Agent.DeserializeSessionAsync(pending, null, cancellationToken).ConfigureAwait(false)
                        : await expert.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
                    state.PendingSerializedSession = null;
                }

                // Catch-up: the shared turns this expert has not seen yet.
                var messages = new List<ChatMessage>();
                foreach (var turn in session.TurnsSince(state.SeenTurns, _configuration.Orchestration.SharedHistoryTurns))
                {
                    messages.Add(new ChatMessage(ChatRole.User, turn.User));
                    messages.Add(new ChatMessage(ChatRole.Assistant, turn.Assistant));
                }
                messages.AddRange(input);

                AgentRunScope.Report(AgentProgressKind.ExpertStarted, $"{profile.Name} is working ({model})", profile.Name);
                var response = await expert.Agent.RunAsync(messages, state.Session, null, cancellationToken).ConfigureAwait(false);
                state.SeenTurns = session.TurnCount + 1;
                AgentRunScope.Report(AgentProgressKind.ExpertCompleted, $"{profile.Name} finished", profile.Name);

                return new ExpertResult(profile.Id, profile.Name, weight, model, response.Text, response.Usage, null);
            }
            finally
            {
                state.Gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var modelFailure = ModelFailures.Find(ex);
            _logger.LogWarning(ex, "Expert {Expert} failed.", profile.Id);
            string error = modelFailure?.Message
                ?? (ex is AgentUnavailableException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
            return new ExpertResult(profile.Id, profile.Name, weight, model, string.Empty, null, error) { ModelFailure = modelFailure };
        }
    }

    private async Task<ExpertAgent> GetExpertAsync(ExpertProfile profile)
    {
        var lazy = _experts.GetOrAdd(profile.Id, _ => new Lazy<Task<ExpertAgent>>(
            () => _expertFactory.CreateAsync(profile, _configuration, depth: 0, CancellationToken.None)));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            // Do not cache failures: the user may set an API key or start Ollama and retry.
            _experts.TryRemove(new KeyValuePair<string, Lazy<Task<ExpertAgent>>>(profile.Id, lazy));
            throw;
        }
    }

    private ExpertProfile? DefaultExpert() =>
        _configuration.FindExpert(_configuration.Orchestration.DefaultExpert) is { Enabled: true } preferred
            ? preferred
            : _configuration.EnabledExperts.FirstOrDefault();

    private AgentResponse Respond(string text, IReadOnlyList<ExpertResult> results, RoutingDecision decision, UsageDetails? usage) =>
        new(new ChatMessage(ChatRole.Assistant, text) { AuthorName = Name })
        {
            AgentId = Id,
            Usage = usage,
            CreatedAt = DateTimeOffset.UtcNow,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ExpertResultsKey] = results,
                [RoutingDecisionKey] = decision,
            },
        };

    private static string ExtractRequest(IReadOnlyList<ChatMessage> messages)
    {
        var userText = messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        return userText.Count > 0
            ? string.Join(Environment.NewLine, userText)
            : string.Join(Environment.NewLine, messages.Select(m => m.Text));
    }

    private static MixtureOfExpertsSession? AsMoeSession(AgentSession? session) => session switch
    {
        null => null,
        MixtureOfExpertsSession moe => moe,
        _ => throw new ArgumentException($"Expected a {nameof(MixtureOfExpertsSession)}; create sessions with {nameof(CreateSessionAsync)}.", nameof(session)),
    };

    private static UsageDetails? Sum(IEnumerable<UsageDetails?> items)
    {
        UsageDetails? total = null;
        foreach (var item in items)
        {
            if (item is null) continue;
            total ??= new UsageDetails();
            total.Add(item);
        }
        return total;
    }
}
