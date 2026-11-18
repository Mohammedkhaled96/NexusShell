using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Application.Conversations;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Domain.Providers;
using NexusShell.Agents.Infrastructure.Providers;
using NexusShell.Agents.Tests.Fakes;

namespace NexusShell.Agents.Tests.Application;

/// <summary>When a provider's quota runs out, NexusAgent must keep answering from another provider.</summary>
public sealed class QuotaSwitchingTests : IDisposable
{
    private const string TwoProviders =
        """
        {
          "orchestration": { "router": { "strategy": "STRATEGY", "model": "fast" }, "defaultExpert": "alpha" },
          "models": { "preference": ["limited", "backup"] },
          "providers": {
            "limited": { "kind": "openai-compatible", "endpoint": "https://limited.invalid/v1", "defaultModel": "m1", "fallbackModels": ["m2"] },
            "backup":  { "kind": "openai-compatible", "endpoint": "https://backup.invalid/v1", "defaultModel": "good" }
          }
        }
        """;

    private readonly AgentTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private AgentTestHost Experts()
    {
        _host.WriteBundled(Path.Combine("experts", "alpha.md"), "---\nname: Alpha\ndescription: Alpha handles apples questions.\nkeywords: [apples]\n---\nAnswer.");
        _host.WriteBundled(Path.Combine("experts", "beta.md"), "---\nname: Beta\ndescription: Beta handles bananas questions.\nkeywords: [bananas]\n---\nAnswer.");
        return _host;
    }

    /// <summary>"limited" always answers HTTP 429 (quota); "backup" works and plays router, experts and synthesizer.</summary>
    private sealed class QuotaFactory(List<string> calls) : IChatClientFactory
    {
        public IChatClient CreateChatClient(ProviderProfile provider, string modelId)
        {
            lock (calls) calls.Add(provider.Id + "/" + modelId);
            IChatClient inner = provider.Id == "backup"
                ? new ScriptedChatClient((messages, options) =>
                {
                    string prompt = ScriptedChatClient.Prompt(messages, options);
                    if (prompt.Contains("gating network", StringComparison.Ordinal))
                        return ScriptedChatClient.Text("{\"experts\":[{\"id\":\"alpha\",\"score\":0.9},{\"id\":\"beta\",\"score\":0.8}]}");
                    if (prompt.Contains("combiner", StringComparison.Ordinal))
                        return ScriptedChatClient.Text("SYNTH from backup");
                    return ScriptedChatClient.Text(prompt.Contains("Your role: Alpha", StringComparison.Ordinal) ? "alpha from backup" : "beta from backup");
                })
                : new FailingClient(new HttpRequestException("You exceeded your current quota. Please retry in 30s.", null, HttpStatusCode.TooManyRequests));
            return new ProviderErrorChatClient(inner, provider, modelId);
        }
    }

    [Fact]
    public async Task Quota_exhausted_provider_is_replaced_by_another_provider_for_experts_and_synthesis()
    {
        Experts().WriteBundled("agent.json", TwoProviders.Replace("STRATEGY", "keyword", StringComparison.Ordinal));
        var calls = new List<string>();
        await using var services = _host.Build(s => s.AddSingleton<IChatClientFactory>(new QuotaFactory(calls)));

        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync();
        var steps = new List<AgentProgress>();
        var reply = await conversation.SendAsync("apples and bananas?", new SyncProgress(steps));

        Assert.Equal("SYNTH from backup", reply.Text);
        Assert.All(reply.Experts, e => Assert.True(e.Succeeded));
        Assert.All(reply.Experts, e => Assert.StartsWith("backup/", e.Model, StringComparison.Ordinal));
        // The whole provider is skipped after its quota error: its fallback model m2 is never tried.
        Assert.DoesNotContain("limited/m2", calls);
        Assert.Contains(steps, s => s.Kind == AgentProgressKind.ModelUnavailable && s.Failure == ModelFailureKind.RateLimited && s.Provider == "limited");
    }

    [Fact]
    public async Task Router_switches_provider_when_its_provider_is_over_quota()
    {
        Experts().WriteBundled("agent.json", TwoProviders.Replace("STRATEGY", "llm", StringComparison.Ordinal));
        var calls = new List<string>();
        await using var services = _host.Build(s => s.AddSingleton<IChatClientFactory>(new QuotaFactory(calls)));

        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync();
        var reply = await conversation.SendAsync("which fruit is healthier?"); // no keywords: only the LLM gate can pick both experts

        Assert.Equal("limited/m1", calls[0]);   // router tried the quota-exhausted provider first...
        Assert.Equal("backup/good", calls[1]);  // ...then switched provider and routed successfully
        Assert.Equal(new[] { "alpha", "beta" }, reply.Experts.Select(e => e.ExpertId).Order().ToArray());
        Assert.Equal("SYNTH from backup", reply.Text);
    }

    [Fact]
    public async Task With_a_single_provider_the_agent_waits_for_the_quota_to_reset_and_retries()
    {
        _host.WriteBundled("agent.json",
                """
                {
                  "orchestration": { "router": { "strategy": "keyword" }, "defaultExpert": "alpha" },
                  "models": { "preference": ["solo"] },
                  "providers": { "solo": { "kind": "openai-compatible", "endpoint": "https://solo.invalid/v1", "defaultModel": "m1" } }
                }
                """)
             .WriteBundled(Path.Combine("experts", "alpha.md"), "---\nname: Alpha\ndescription: Alpha handles apples questions.\nkeywords: [apples]\n---\nAnswer.");
        int call = 0;
        _host.Script((_, _) => Interlocked.Increment(ref call) == 1
            ? throw new ModelProviderException("solo", "m1", ModelFailureKind.RateLimited, 429, "solo quota reached", retryAfter: TimeSpan.FromSeconds(1))
            : ScriptedChatClient.Text("answer after the quota reset"));
        await using var services = _host.Build();

        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync();
        var steps = new List<AgentProgress>();
        var started = DateTime.UtcNow;
        var reply = await conversation.SendAsync("apples?", new SyncProgress(steps));

        Assert.Equal("answer after the quota reset", reply.Text);
        var wait = Assert.Single(steps, s => s.Kind == AgentProgressKind.WaitingForQuota);
        Assert.InRange(wait.Delay!.Value.TotalSeconds, 1, 5);
        Assert.InRange((DateTime.UtcNow - started).TotalSeconds, 1, 20);
    }

    [Fact]
    public async Task When_every_provider_is_skipped_the_message_names_them_and_why()
    {
        _host.WriteBundled("agent.json",
                """
                {
                  "orchestration": { "router": { "strategy": "keyword" }, "defaultExpert": "alpha" },
                  "models": { "preference": ["solo"] },
                  "providers": { "solo": { "kind": "openai-compatible", "endpoint": "https://solo.invalid/v1", "defaultModel": "m1" } }
                }
                """)
             .WriteBundled(Path.Combine("experts", "alpha.md"), "---\nname: Alpha\ndescription: Alpha handles apples questions.\nkeywords: [apples]\n---\nAnswer.")
             .Script((_, _) => throw new ModelProviderException("solo", "m1", ModelFailureKind.Unauthorized, 401, "solo rejected the API key"));
        await using var services = _host.Build();

        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync();
        await conversation.SendAsync("apples?");               // key rejected -> provider skipped
        var second = await conversation.SendAsync("apples?");  // starts with nothing usable

        Assert.Contains("Skipped for now: solo (API key rejected)", second.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("start a local model server", second.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("You exceeded your current quota. Please retry in 23.4s.", 23.4)]
    [InlineData("{\"error\":{\"details\":[{\"@type\":\"RetryInfo\",\"retryDelay\":\"7s\"}]}}", 7)]
    [InlineData("Rate limit reached. Please try again in 20s, or retry after 5 seconds.", 5)]
    public void Provider_retry_delay_is_read_from_the_error(string text, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), ProviderErrorChatClient.ParseRetryDelay(text));

    [Fact]
    public void Retry_after_header_and_missing_values()
    {
        Assert.Equal(TimeSpan.FromSeconds(12), ProviderErrorChatClient.ParseRetryAfterHeader("12"));
        Assert.Null(ProviderErrorChatClient.ParseRetryAfterHeader(""));
        Assert.Null(ProviderErrorChatClient.ParseRetryDelay("Something else went wrong."));
    }

    [Fact]
    public void Quota_avoidance_follows_the_providers_retry_time()
    {
        var withHint = new ModelProviderException("p", "m", ModelFailureKind.RateLimited, 429, "x", retryAfter: TimeSpan.FromSeconds(23));
        var withoutHint = new ModelProviderException("p", "m", ModelFailureKind.RateLimited, 429, "x");
        Assert.Equal(TimeSpan.FromSeconds(23), withHint.QuarantineDuration);
        Assert.Equal(TimeSpan.FromMinutes(1), withoutHint.QuarantineDuration);
        Assert.True(withHint.IsProviderWide);
    }

    [Fact]
    public void Long_provider_text_is_cut_to_whole_sentences_for_screen_readers()
    {
        const string gemini = "You exceeded your current quota, please check your plan and billing details. For more information on this error, head to: https://aistudio.google.com/docs/rate-limits. To monitor your current usage, head to: https://ai.dev/rate-limit.";
        Assert.Equal("You exceeded your current quota, please check your plan and billing details.", ProviderErrorChatClient.FirstSentences(gemini, 160));
    }

    [Fact]
    public void Waiting_is_narrated_in_english_and_arabic()
    {
        var step = new AgentProgress(AgentProgressKind.WaitingForQuota, "x", "Shell Expert") { Delay = TimeSpan.FromSeconds(24.6) };
        Assert.Equal("Shell Expert: every provider is at its limit, waiting 25 seconds, then trying again.", AgentProgressNarrator.Describe(step, NarrationLanguage.English));
        Assert.Equal("Shell Expert: كل المزوّدين وصلوا إلى الحد، الانتظار 25 ثانية ثم إعادة المحاولة.", AgentProgressNarrator.Describe(step, NarrationLanguage.Arabic));
    }

    private sealed class FailingClient(Exception exception) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(exception);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw exception;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class SyncProgress(List<AgentProgress> sink) : IProgress<AgentProgress>
    {
        public void Report(AgentProgress value)
        {
            lock (sink) sink.Add(value);
        }
    }
}
