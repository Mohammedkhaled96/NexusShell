using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Application.Conversations;
using NexusShell.Agents.Application.Models;
using NexusShell.Agents.Domain.Providers;
using NexusShell.Agents.Infrastructure.Providers;
using NexusShell.Agents.Tests.Fakes;

namespace NexusShell.Agents.Tests.Application;

/// <summary>Providers retire model ids; the agent must recover by itself and explain clearly.</summary>
public sealed class ModelFallbackTests : IDisposable
{
    private const string AgentJson =
        """
        {
          "orchestration": { "router": { "strategy": "keyword" }, "defaultExpert": "alpha" },
          "models": { "preference": ["fake"] },
          "providers": {
            "fake": {
              "kind": "openai-compatible", "endpoint": "https://fake.invalid/v1", "apiKeyEnv": "FAKE_API_KEY",
              "defaultModel": "retired-model", "fallbackModels": ["good-model"]
            }
          }
        }
        """;

    private static readonly ProviderProfile Provider = new()
    {
        Id = "fake",
        Kind = ProviderKind.OpenAICompatible,
        ApiKey = new NexusShell.Agents.Domain.Secrets.SecretReference("FAKE_API_KEY"),
        DefaultModel = "retired-model",
        FallbackModels = ["good-model"],
    };

    private readonly AgentTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Http_404_becomes_a_model_not_found_error_with_a_clear_message()
    {
        var client = new ProviderErrorChatClient(new ThrowingChatClient(new HttpRequestException("Not Found", null, HttpStatusCode.NotFound)), Provider, "retired-model");

        var error = await Assert.ThrowsAsync<ModelProviderException>(() => client.GetResponseAsync("hi"));

        Assert.Equal(ModelFailureKind.ModelNotFound, error.Kind);
        Assert.True(error.IsModelSpecific);
        Assert.Contains("does not serve the model 'retired-model'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ModelFailureKind.Unauthorized, "FAKE_API_KEY")]
    [InlineData(HttpStatusCode.TooManyRequests, ModelFailureKind.RateLimited, "quota")]
    [InlineData(HttpStatusCode.BadGateway, ModelFailureKind.Unavailable, "unreachable")]
    public async Task Other_http_failures_are_classified(HttpStatusCode status, ModelFailureKind kind, string hint)
    {
        var client = new ProviderErrorChatClient(new ThrowingChatClient(new HttpRequestException("x", null, status)), Provider, "m");

        var error = await Assert.ThrowsAsync<ModelProviderException>(() => client.GetResponseAsync("hi"));

        Assert.Equal(kind, error.Kind);
        Assert.False(error.IsModelSpecific);
        Assert.Contains(hint, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_provider_exceptions_pass_through_unchanged()
    {
        var client = new ProviderErrorChatClient(new ThrowingChatClient(new InvalidOperationException("bug")), Provider, "m");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync("hi"));
    }

    [Theory]
    [InlineData("""{"error":{"message":"The model `x` does not exist","code":"model_not_found"}}""", "The model `x` does not exist")]
    [InlineData("""[{"error":{"code":404,"message":"models/x is not found"}}]""", "models/x is not found")]
    [InlineData("""{"error":"model 'llama9' not found"}""", "model 'llama9' not found")]
    public void Provider_error_bodies_are_parsed(string body, string expected) =>
        Assert.Equal(expected, ProviderErrorChatClient.ExtractErrorMessage(body));

    [Fact]
    public async Task Resolver_skips_a_quarantined_model_and_uses_the_fallback()
    {
        _host.WriteBundled("agent.json", AgentJson).Expert("alpha", "Alpha", "apples");
        await using var services = _host.Build();
        var resolver = services.GetRequiredService<IModelResolver>();
        var health = services.GetRequiredService<IModelHealthRegistry>();

        Assert.Equal("fake/retired-model", (await resolver.ResolveAsync("auto"))!.ToString());

        health.ReportUnusable("fake", "retired-model", "404");

        Assert.Equal("fake/good-model", (await resolver.ResolveAsync("auto"))!.ToString());
        Assert.Equal("fake/good-model", (await resolver.ResolveAsync("fake/retired-model"))!.ToString()); // same provider first
    }

    [Fact]
    public async Task Expert_recovers_automatically_when_its_model_is_retired()
    {
        _host.WriteBundled("agent.json", AgentJson).Expert("alpha", "Alpha", "apples");
        var models = new List<string>();
        await using var services = _host.Build(s => s.AddSingleton<IChatClientFactory>(new PerModelFactory(models)));

        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync();
        var reply = await conversation.SendAsync("apples?");

        Assert.Equal("answer from good-model", reply.Text);
        Assert.True(Assert.Single(reply.Experts).Succeeded);
        // The expert pins fake/test-model; it falls back through the provider's default and fallback list.
        Assert.Equal(new[] { "test-model", "retired-model", "good-model" }, models.ToArray());
        var health = services.GetRequiredService<IModelHealthRegistry>();
        Assert.False(health.IsUsable("fake", "test-model"));
        Assert.False(health.IsUsable("fake", "retired-model"));

        // Next turn goes straight to the working model (no repeated 404s).
        models.Clear();
        Assert.Equal("answer from good-model", (await conversation.SendAsync("more apples?")).Text);
        Assert.Empty(models);
    }

    [Fact]
    public async Task When_every_model_is_gone_the_user_gets_an_actionable_message()
    {
        _host.WriteBundled("agent.json", AgentJson.Replace("\"good-model\"", "\"also-retired\"", StringComparison.Ordinal)).Expert("alpha", "Alpha", "apples");
        await using var services = _host.Build(s => s.AddSingleton<IChatClientFactory>(new PerModelFactory([])));

        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync();
        var reply = await conversation.SendAsync("apples?");

        Assert.Contains("does not serve the model", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientResultException", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overloaded_model_fails_over_with_a_short_quarantine()
    {
        var client = new ProviderErrorChatClient(new ThrowingChatClient(new HttpRequestException("The model is overloaded", null, HttpStatusCode.ServiceUnavailable)), Provider, "busy-model");

        var error = await Assert.ThrowsAsync<ModelProviderException>(() => client.GetResponseAsync("hi"));

        Assert.Equal(ModelFailureKind.Unavailable, error.Kind);
        Assert.True(error.CanFallBack);
        Assert.False(error.IsModelSpecific);
        Assert.Equal(TimeSpan.FromMinutes(10), error.QuarantineDuration); // shorter than the 30 min for a missing model
    }

    [Fact]
    public async Task A_hung_model_call_times_out_and_counts_as_unavailable()
    {
        var slow = Provider with { RequestTimeout = TimeSpan.FromMilliseconds(200) };
        var client = new ProviderErrorChatClient(new HangingChatClient(), slow, "hung-model");

        var started = DateTime.UtcNow;
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => client.GetResponseAsync("hi"));

        Assert.Equal(ModelFailureKind.Unavailable, error.Kind);
        Assert.True(error.CanFallBack);
        Assert.Contains("did not answer within", error.Message, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task User_cancellation_is_not_mistaken_for_a_timeout()
    {
        var client = new ProviderErrorChatClient(new HangingChatClient(), Provider, "m");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync("hi", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Expert_fails_over_quickly_when_its_model_hangs()
    {
        _host.WriteBundled("agent.json", AgentJson.Replace("\"fallbackModels\": [\"good-model\"]", "\"fallbackModels\": [\"good-model\"], \"timeoutSeconds\": 1", StringComparison.Ordinal))
             .Expert("alpha", "Alpha", "apples", body: "Answer.")
             .WriteBundled(Path.Combine("experts", "alpha.md"), "---\nname: Alpha\ndescription: Alpha handles apples questions.\nmodel: fake/retired-model\nkeywords: [apples]\n---\nAnswer.");
        var models = new List<string>();
        await using var services = _host.Build(s => s.AddSingleton<IChatClientFactory>(new HangingThenGoodFactory(models)));

        var started = DateTime.UtcNow;
        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync();
        var reply = await conversation.SendAsync("apples?");

        Assert.Equal("answer from good-model", reply.Text);
        Assert.Equal(new[] { "retired-model", "good-model" }, models.ToArray());
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Fast_keyword_prefers_the_providers_fast_model()
    {
        _host.WriteBundled("agent.json", AgentJson.Replace("\"fallbackModels\": [\"good-model\"]", "\"fallbackModels\": [\"good-model\"], \"fastModel\": \"quick-model\"", StringComparison.Ordinal))
             .Expert("alpha", "Alpha", "apples");
        await using var services = _host.Build();
        var resolver = services.GetRequiredService<IModelResolver>();

        Assert.Equal("fake/quick-model", (await resolver.ResolveAsync("fast"))!.ToString());
        Assert.Equal("fake/retired-model", (await resolver.ResolveAsync("auto"))!.ToString());

        services.GetRequiredService<IModelHealthRegistry>().ReportUnusable("fake", "quick-model", "503", TimeSpan.FromMinutes(2));
        Assert.Equal("fake/retired-model", (await resolver.ResolveAsync("fast"))!.ToString());
    }

    [Fact]
    public async Task Thought_signatures_are_captured_and_sent_back_on_the_next_request()
    {
        var cache = new ThoughtSignatureCache();
        var transport = new RecordingTransport();
        using var http = new HttpClient(new ThoughtSignatureHandler(cache) { InnerHandler = transport });
        var uri = new Uri("https://gemini.example/v1beta/openai/chat/completions");

        // 1st call: model asks for a tool and attaches a signature.
        transport.NextResponse = """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_system_info","arguments":"{}"},"extra_content":{"google":{"thought_signature":"SIG-123"}}}]}}]}""";
        var first = await http.PostAsync(uri, new StringContent("""{"messages":[{"role":"user","content":"hi"}]}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Contains("SIG-123", await first.Content.ReadAsStringAsync(), StringComparison.Ordinal); // response body still readable

        // 2nd call: the SDK re-sends the tool call WITHOUT extra_content (it drops unknown fields).
        transport.NextResponse = """{"choices":[{"message":{"role":"assistant","content":"done"}}]}""";
        await http.PostAsync(uri, new StringContent(
            """{"messages":[{"role":"user","content":"hi"},{"role":"assistant","tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_system_info","arguments":"{}"}}]},{"role":"tool","tool_call_id":"call_1","content":"ok"}]}""",
            System.Text.Encoding.UTF8, "application/json"));

        Assert.Contains("\"thought_signature\":\"SIG-123\"", transport.LastRequestBody, StringComparison.Ordinal);
        Assert.Equal("application/json", transport.LastContentType);
    }

    [Fact]
    public void Requests_without_known_signatures_are_left_untouched()
    {
        const string body = """{"messages":[{"role":"assistant","tool_calls":[{"id":"other","type":"function"}]}]}""";
        Assert.Same(body, ThoughtSignatureHandler.Inject(body, new ThoughtSignatureCache()));
    }

    private sealed class RecordingTransport : HttpMessageHandler
    {
        public string NextResponse { get; set; } = "{}";

        public string LastRequestBody { get; private set; } = string.Empty;

        public string? LastContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            LastContentType = request.Content?.Headers.ContentType?.MediaType;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(NextResponse, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>Real ProviderErrorChatClient over a fake transport: only "good-model" exists.</summary>
    private sealed class PerModelFactory(List<string> calls) : IChatClientFactory
    {
        public IChatClient CreateChatClient(ProviderProfile provider, string modelId)
        {
            lock (calls) calls.Add(modelId);
            IChatClient inner = modelId == "good-model"
                ? new ScriptedChatClient((_, _) => ScriptedChatClient.Text("answer from good-model"))
                : new ThrowingChatClient(new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));
            return new ProviderErrorChatClient(inner, provider, modelId);
        }
    }

    /// <summary>Real ProviderErrorChatClient: "good-model" answers, every other model hangs forever.</summary>
    private sealed class HangingThenGoodFactory(List<string> calls) : IChatClientFactory
    {
        public IChatClient CreateChatClient(ProviderProfile provider, string modelId)
        {
            lock (calls) calls.Add(modelId);
            IChatClient inner = modelId == "good-model"
                ? new ScriptedChatClient((_, _) => ScriptedChatClient.Text("answer from good-model"))
                : new HangingChatClient();
            return new ProviderErrorChatClient(inner, provider, modelId);
        }
    }

    /// <summary>Simulates an overloaded provider that never answers.</summary>
    private sealed class HangingChatClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingChatClient(Exception exception) : IChatClient
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
}
