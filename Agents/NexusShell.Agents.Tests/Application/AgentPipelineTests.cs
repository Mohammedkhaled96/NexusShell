using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NexusShell.Agents.Application.Abstractions.Approval;
using NexusShell.Agents.Application.Abstractions.Configuration;
using NexusShell.Agents.Application.Conversations;
using NexusShell.Agents.Application.Orchestration;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Tests.Fakes;

namespace NexusShell.Agents.Tests.Application;

/// <summary>
/// End-to-end tests through the real Microsoft Agent Framework pipeline
/// (ChatClientAgent, function invocation, tool approval, middleware, workflows);
/// only the model is scripted.
/// </summary>
public sealed class AgentPipelineTests : IDisposable
{
    private readonly AgentTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static bool Is(IReadOnlyList<ChatMessage> messages, ChatOptions? options, string role) =>
        ScriptedChatClient.Prompt(messages, options).Contains("## Your role: " + role, StringComparison.Ordinal);

    private async Task<(INexusAgentConversation Conversation, ServiceProvider Services)> StartAsync()
    {
        var services = _host.Build();
        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync();
        return (conversation, services);
    }

    [Fact]
    public async Task Single_selected_expert_answers_directly_without_synthesis()
    {
        _host.Expert("alpha", "Alpha", "apples")
             .Expert("beta", "Beta", "bananas")
             .Script((messages, options) => ScriptedChatClient.Text(Is(messages, options, "Alpha") ? "alpha answer" : "unexpected"));

        var (conversation, services) = await StartAsync();
        await using var _ = services;

        var result = await conversation.SendAsync("tell me about apples");

        Assert.Equal("alpha answer", result.Text);
        var expert = Assert.Single(result.Experts);
        Assert.Equal("alpha", expert.ExpertId);
        Assert.Equal(1.0, expert.Weight, 3);
        Assert.Single(_host.Chat.Calls); // no router call (keyword gate), no synthesizer call
    }

    [Fact]
    public async Task Mixture_of_experts_runs_experts_in_parallel_and_synthesizes()
    {
        _host.Expert("alpha", "Alpha", "apples")
             .Expert("beta", "Beta", "bananas")
             .Script((messages, options) =>
             {
                 string prompt = ScriptedChatClient.Prompt(messages, options);
                 if (prompt.Contains("combiner of a mixture-of-experts", StringComparison.Ordinal))
                 {
                     string request = messages.Last().Text;
                     return ScriptedChatClient.Text($"SYNTH[{request.Contains("alpha says", StringComparison.Ordinal)}|{request.Contains("beta says", StringComparison.Ordinal)}]");
                 }
                 return ScriptedChatClient.Text(Is(messages, options, "Alpha") ? "alpha says apples are red" : "beta says bananas are yellow");
             });

        var (conversation, services) = await StartAsync();
        await using var _ = services;

        var progress = new List<AgentProgress>();
        var result = await conversation.SendAsync("compare apples and bananas", new SyncProgress(progress));

        Assert.Equal("SYNTH[True|True]", result.Text);
        Assert.Equal(new[] { "alpha", "beta" }, result.Experts.Select(e => e.ExpertId).Order().ToArray());
        Assert.Equal(1.0, result.Experts.Sum(e => e.Weight), 3);
        Assert.Equal(3, _host.Chat.Calls.Count); // two experts + one synthesizer
        Assert.Contains(progress, p => p.Kind == AgentProgressKind.ExpertsSelected);
        Assert.Contains(progress, p => p.Kind == AgentProgressKind.Synthesizing);
    }

    [Fact]
    public async Task Approved_tool_call_runs_and_the_run_continues()
    {
        _host.Expert("alpha", "Alpha", "apples", "tools: [test]")
             .Script((messages, options) =>
                 ScriptedChatClient.LastToolResult(messages) is { } result
                     ? ScriptedChatClient.Text("finished: " + result.Result)
                     : ScriptedChatClient.Call("call-1", "danger_tool", new Dictionary<string, object?> { ["target"] = "apples.txt" }));

        var (conversation, services) = await StartAsync();
        await using var _ = services;

        var reply = await conversation.SendAsync("delete my apples file");

        var request = Assert.Single(_host.Approvals.Requests);
        Assert.Equal("danger_tool", request.ToolName);
        Assert.Contains("apples.txt", request.ArgumentsPreview, StringComparison.Ordinal);
        Assert.Equal(new[] { "apples.txt" }, _host.Tools.Deleted.ToArray());
        Assert.Equal("finished: deleted apples.txt", reply.Text);
    }

    [Fact]
    public async Task Denied_tool_call_is_never_executed()
    {
        _host.Approvals.Decision = ToolApprovalDecision.Deny;
        _host.Expert("alpha", "Alpha", "apples", "tools: [test]")
             .Script((messages, options) =>
                 ScriptedChatClient.LastToolResult(messages) is not null
                     ? ScriptedChatClient.Text("ok, I will not delete it")
                     : ScriptedChatClient.Call("call-1", "danger_tool", new Dictionary<string, object?> { ["target"] = "apples.txt" }));

        var (conversation, services) = await StartAsync();
        await using var _ = services;

        var reply = await conversation.SendAsync("delete my apples file");

        Assert.Single(_host.Approvals.Requests);
        Assert.Empty(_host.Tools.Deleted);
        Assert.Equal("ok, I will not delete it", reply.Text);
    }

    [Fact]
    public async Task Approve_for_conversation_is_remembered()
    {
        _host.Approvals.Decision = ToolApprovalDecision.ApproveForConversation;
        int turn = 0;
        _host.Expert("alpha", "Alpha", "apples", "tools: [test]")
             .Script((messages, options) =>
                 messages.Last().Role == ChatRole.Tool
                     ? ScriptedChatClient.Text("done")
                     : ScriptedChatClient.Call("call-" + Interlocked.Increment(ref turn), "danger_tool", new Dictionary<string, object?> { ["target"] = "file" + turn }));

        var (conversation, services) = await StartAsync();
        await using var _ = services;

        await conversation.SendAsync("delete apples one");
        await conversation.SendAsync("delete apples two");

        Assert.Single(_host.Approvals.Requests); // second call auto-approved
        Assert.Equal(2, _host.Tools.Deleted.Count);
    }

    [Fact]
    public async Task Secrets_in_tool_results_never_reach_the_model()
    {
        Environment.SetEnvironmentVariable(TestToolProvider.SecretVariable, "redaction-test-value-4242");
        try
        {
            _host.Expert("alpha", "Alpha", "apples", "tools: [test]")
                 .Script((messages, options) =>
                     ScriptedChatClient.LastToolResult(messages) is { } result
                         ? ScriptedChatClient.Text("model saw: " + result.Result)
                         : ScriptedChatClient.Call("c1", "leak_tool"));

            var (conversation, services) = await StartAsync();
            await using var _ = services;

            var reply = await conversation.SendAsync("show apples config");

            Assert.DoesNotContain("redaction-test-value-4242", reply.Text, StringComparison.Ordinal);
            Assert.Contains("[REDACTED:" + TestToolProvider.SecretVariable + "]", reply.Text, StringComparison.Ordinal);
            Assert.Empty(_host.Approvals.Requests); // read-only tool: auto-approved
            Assert.All(_host.Chat.Calls.SelectMany(c => c.Messages), m => Assert.DoesNotContain("redaction-test-value-4242", m.Text ?? string.Empty, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestToolProvider.SecretVariable, null);
        }
    }

    [Fact]
    public async Task Expert_delegates_to_a_sub_agent()
    {
        _host.Expert("alpha", "Alpha", "apples", "subagents: [helper]")
             .Expert("helper", "Helper", "zzz", "routable: false")
             .Script((messages, options) =>
             {
                 if (Is(messages, options, "Helper")) return ScriptedChatClient.Text("helper: " + messages.Last().Text);
                 return ScriptedChatClient.LastToolResult(messages) is { } result
                     ? ScriptedChatClient.Text("alpha used " + result.Result)
                     : ScriptedChatClient.Call("c1", "ask_helper", new Dictionary<string, object?> { ["request"] = "count the apples" });
             });

        var (conversation, services) = await StartAsync();
        await using var _ = services;

        var reply = await conversation.SendAsync("apples please");

        Assert.Equal("alpha used helper: count the apples", reply.Text);
        Assert.Empty(_host.Approvals.Requests); // delegation itself needs no approval
        var alphaTools = _host.Chat.Calls.First(c => Is(c.Messages, c.Options, "Alpha")).Options!.Tools!;
        Assert.Contains(alphaTools, t => t.Name == "ask_helper");
    }

    [Fact]
    public async Task Expert_with_file_skills_advertises_only_its_skills()
    {
        // Regression: file skills with scripts disabled used to throw at agent build time.
        _host.WriteBundled(Path.Combine("skills", "apple-care", "SKILL.md"), "---\nname: apple-care\ndescription: How to store apples.\n---\nKeep apples cold.\n")
             .WriteBundled(Path.Combine("skills", "apple-care", "scripts", "count.ps1"), "Write-Output SCRIPT-RAN-MARKER")
             .WriteBundled(Path.Combine("skills", "banana-care", "SKILL.md"), "---\nname: banana-care\ndescription: How to ripen bananas.\n---\nKeep bananas warm.\n")
             .Expert("alpha", "Alpha", "apples", "skills: [apple-care]")
             .Script((messages, options) =>
             {
                 var results = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
                 return results.Count switch
                 {
                     0 => ScriptedChatClient.Call("c1", "load_skill", new Dictionary<string, object?> { ["skillName"] = "apple-care" }),
                     1 => ScriptedChatClient.Call("c2", "run_skill_script", new Dictionary<string, object?> { ["skillName"] = "apple-care", ["scriptName"] = "count.ps1" }),
                     _ => ScriptedChatClient.Text(string.Join(" || ", results.Select(r => r.Result?.ToString()))),
                 };
             });

        var (conversation, services) = await StartAsync();
        await using var _ = services;

        var reply = await conversation.SendAsync("apples?");

        // Only this expert's skill is advertised.
        var first = _host.Chat.Calls[0];
        string advertised = ScriptedChatClient.Prompt(first.Messages, first.Options) + string.Join("\n", first.Messages.Select(m => m.Text));
        Assert.Contains("apple-care", advertised, StringComparison.Ordinal);
        Assert.DoesNotContain("banana-care", advertised, StringComparison.Ordinal);

        // Skill instructions load on demand (progressive disclosure)...
        Assert.Contains("Keep apples cold.", reply.Text, StringComparison.Ordinal);
        // ...but with scripts disabled the script is neither listed nor executed.
        Assert.DoesNotContain("count.ps1", reply.Text.Split("||")[0], StringComparison.Ordinal);
        Assert.DoesNotContain("SCRIPT-RAN-MARKER", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Late_joining_expert_receives_the_turns_it_missed()
    {
        _host.Expert("alpha", "Alpha", "apples")
             .Expert("beta", "Beta", "bananas")
             .Script((messages, options) =>
             {
                 if (Is(messages, options, "Beta"))
                 {
                     bool sawHistory = messages.Any(m => m.Role == ChatRole.Assistant && m.Text == "apples are red");
                     return ScriptedChatClient.Text("beta saw history: " + sawHistory);
                 }
                 return ScriptedChatClient.Text("apples are red");
             });

        var (conversation, services) = await StartAsync();
        await using var _ = services;

        await conversation.SendAsync("apples?");
        var second = await conversation.SendAsync("and bananas?");

        Assert.Equal("beta saw history: True", second.Text);
    }

    [Fact]
    public async Task Moe_session_survives_serialization()
    {
        _host.Expert("alpha", "Alpha", "apples").Script((_, _) => ScriptedChatClient.Text("red"));
        await using var services = _host.Build();

        var config = await services.GetRequiredService<IAgentConfigurationProvider>().GetConfigurationAsync();
        var agent = await services.GetRequiredService<IAgentOrchestratorFactory>().CreateAsync(config);
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("apples?", session);

        var json = await agent.SerializeSessionAsync(session);
        var restored = (MixtureOfExpertsSession)await agent.DeserializeSessionAsync(json);

        var turn = Assert.Single(restored.Transcript);
        Assert.Equal("apples?", turn.User);
        Assert.Equal("red", turn.Assistant);
        Assert.Equal(new[] { "alpha" }, turn.Experts);
    }

    [Fact]
    public async Task Coordinator_mode_exposes_every_other_expert_as_a_sub_agent()
    {
        _host.WriteBundled("agent.json", AgentTestHost.DefaultAgentJson.Replace("\"mode\": \"moe\"", "\"mode\": \"coordinator\"", StringComparison.Ordinal))
             .Expert("alpha", "Alpha", "apples")
             .Expert("beta", "Beta", "bananas")
             .Expert("gamma", "Gamma", "grapes")
             .Script((_, _) => ScriptedChatClient.Text("coordinated"));

        var (conversation, services) = await StartAsync();
        await using var _ = services;

        var reply = await conversation.SendAsync("anything");

        Assert.Equal("coordinated", reply.Text);
        var tools = _host.Chat.Calls.Single().Options!.Tools!.Select(t => t.Name).Order().ToArray();
        Assert.Equal(new[] { "ask_beta", "ask_gamma" }, tools);
    }

    [Fact]
    public async Task Sequential_mode_runs_the_workflow_pipeline_in_order()
    {
        _host.WriteBundled("agent.json", AgentTestHost.DefaultAgentJson.Replace("\"mode\": \"moe\"", "\"mode\": \"sequential\"", StringComparison.Ordinal))
             .Expert("alpha", "Alpha", "apples")
             .Expert("beta", "Beta", "bananas")
             .Script((messages, options) =>
                 Is(messages, options, "Alpha")
                     ? ScriptedChatClient.Text("draft from alpha")
                     : ScriptedChatClient.Text("beta reviewed: " + messages.Any(m => m.Text.Contains("draft from alpha", StringComparison.Ordinal))));

        var (conversation, services) = await StartAsync();
        await using var _ = services;

        var reply = await conversation.SendAsync("build it");

        Assert.Contains("draft from alpha", reply.Text, StringComparison.Ordinal);
        Assert.Contains("beta reviewed: True", reply.Text, StringComparison.Ordinal);
        Assert.True(reply.Text.IndexOf("draft from alpha", StringComparison.Ordinal) < reply.Text.IndexOf("beta reviewed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unavailable_models_produce_a_helpful_answer_instead_of_a_crash()
    {
        _host.WriteBundled("agent.json", """{ "models": { "preference": [] }, "providers": {} }""")
             .Expert("alpha", "Alpha", "apples");

        var (conversation, services) = await StartAsync();
        await using var _ = services;

        var reply = await conversation.SendAsync("apples?");

        Assert.Contains("No model is available", reply.Text, StringComparison.Ordinal);
        Assert.False(Assert.Single(reply.Experts).Succeeded);
    }

    [Fact]
    public async Task Health_report_lists_experts_and_providers()
    {
        _host.Expert("alpha", "Alpha", "apples");
        await using var services = _host.Build();

        var health = await services.GetRequiredService<INexusAgentService>().GetHealthAsync();

        Assert.True(health.IsReady);
        Assert.Equal("fake", Assert.Single(health.Providers).ProviderId);
        Assert.Equal("alpha", Assert.Single(health.Experts).Id);
        Assert.Empty(health.Warnings);
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; tests need synchronous capture.</summary>
    private sealed class SyncProgress(List<AgentProgress> sink) : IProgress<AgentProgress>
    {
        public void Report(AgentProgress value)
        {
            lock (sink) sink.Add(value);
        }
    }
}
