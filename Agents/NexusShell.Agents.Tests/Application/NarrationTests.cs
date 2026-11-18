using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NexusShell.Agents.Application.Conversations;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Tests.Fakes;

namespace NexusShell.Agents.Tests.Application;

public class NarrationTests
{
    private static string? En(AgentProgress p) => AgentProgressNarrator.Describe(p, NarrationLanguage.English);

    private static string? Ar(AgentProgress p) => AgentProgressNarrator.Describe(p, NarrationLanguage.Arabic);

    [Fact]
    public void Steps_are_spoken_as_natural_english_sentences()
    {
        Assert.Equal("Choosing the best experts.", En(new(AgentProgressKind.Routing, "x")));
        Assert.Equal("Asking Shell Expert and Windows Expert.", En(new(AgentProgressKind.ExpertsSelected, "x") { Experts = ["Shell Expert", "Windows Expert"] }));
        Assert.Equal("Asking A, B and C.", En(new(AgentProgressKind.ExpertsSelected, "x") { Experts = ["A", "B", "C"] }));
        Assert.Equal("Windows Expert is working.", En(new(AgentProgressKind.ExpertStarted, "x", "Windows Expert")));
        Assert.Equal("Windows Expert: checking system information.", En(new(AgentProgressKind.ToolStarted, "x", "Windows Expert") { ToolName = "get_system_info" }));
        Assert.Equal("Shell Expert needs your approval to run a command.", En(new(AgentProgressKind.ApprovalRequested, "x", "Shell Expert") { ToolName = "run_shell_command" }));
        Assert.Equal("Shell Expert: the model is not responding, switching to another model.", En(new(AgentProgressKind.ModelUnavailable, "x", "Shell Expert")));
        Assert.Equal("Combining the answers.", En(new(AgentProgressKind.Synthesizing, "x")));
        Assert.Equal("Asking Reviewer for help.", En(new(AgentProgressKind.SubAgentStarted, "x", "Reviewer")));
    }

    [Fact]
    public void Steps_are_spoken_in_arabic_when_the_ai_language_is_arabic()
    {
        Assert.Equal(NarrationLanguage.Arabic, AgentProgressNarrator.LanguageFrom("Arabic"));
        Assert.Equal("جارٍ اختيار أفضل الخبراء.", Ar(new(AgentProgressKind.Routing, "x")));
        Assert.Equal("Windows Expert: جارٍ فحص معلومات النظام.", Ar(new(AgentProgressKind.ToolStarted, "x", "Windows Expert") { ToolName = "get_system_info" }));
        Assert.Equal("Shell Expert يحتاج موافقتك على تشغيل أمر.", Ar(new(AgentProgressKind.ApprovalRequested, "x", "Shell Expert") { ToolName = "run_shell_command" }));
        Assert.Equal("الإجابة جاهزة من Windows Expert.", AgentProgressNarrator.Answered(["Windows Expert"], NarrationLanguage.Arabic));
    }

    [Theory]
    [InlineData(NexusShell.Agents.Application.Abstractions.Models.ModelFailureKind.RateLimited, "Shell Expert: gemini quota is used up, switching to another provider.")]
    [InlineData(NexusShell.Agents.Application.Abstractions.Models.ModelFailureKind.Unreachable, "Shell Expert: gemini cannot be reached from this computer, switching to another provider.")]
    [InlineData(NexusShell.Agents.Application.Abstractions.Models.ModelFailureKind.Unauthorized, "Shell Expert: gemini rejected the API key, switching to another provider.")]
    [InlineData(NexusShell.Agents.Application.Abstractions.Models.ModelFailureKind.ModelNotFound, "Shell Expert: that model is not available, switching to another model.")]
    [InlineData(NexusShell.Agents.Application.Abstractions.Models.ModelFailureKind.Unavailable, "Shell Expert: the model is not responding, switching to another model.")]
    public void Model_switches_say_the_real_reason(NexusShell.Agents.Application.Abstractions.Models.ModelFailureKind failure, string expected) =>
        Assert.Equal(expected, En(new(AgentProgressKind.ModelUnavailable, "x", "Shell Expert") { Provider = "gemini", Failure = failure }));

    [Fact]
    public void Quota_switch_is_spoken_in_arabic() =>
        Assert.Equal("Shell Expert: انتهت حصة gemini، جارٍ التبديل إلى مزوّد آخر.",
            Ar(new(AgentProgressKind.ModelUnavailable, "x", "Shell Expert") { Provider = "gemini", Failure = NexusShell.Agents.Application.Abstractions.Models.ModelFailureKind.RateLimited }));

    [Fact]
    public void Tool_ids_are_never_spoken_as_code()
    {
        Assert.Equal("Alpha: using the MCP tool github create issue.", En(new(AgentProgressKind.ToolStarted, "x", "Alpha") { ToolName = "mcp_github_create_issue" }));
        Assert.Equal("Alpha: asking the code expert.", En(new(AgentProgressKind.ToolStarted, "x", "Alpha") { ToolName = "ask_code_expert" }));
        Assert.Equal("Alpha: using my custom tool.", En(new(AgentProgressKind.ToolStarted, "x", "Alpha") { ToolName = "my_custom_tool" }));
    }

    [Fact]
    public void Noise_steps_are_silent()
    {
        Assert.Null(En(new(AgentProgressKind.ToolCompleted, "x", "Alpha") { ToolName = "get_system_info" }));
        Assert.Null(En(new(AgentProgressKind.ExpertsSelected, "x")));
    }

    [Fact]
    public void Lifecycle_and_heartbeat_sentences()
    {
        Assert.Equal("Message sent. NexusAgent is working.", AgentProgressNarrator.Sent(NarrationLanguage.English));
        Assert.Equal("Answer ready from Shell Expert and Windows Expert.", AgentProgressNarrator.Answered(["Shell Expert", "Windows Expert"], NarrationLanguage.English));
        Assert.Equal("Answer ready.", AgentProgressNarrator.Answered([], NarrationLanguage.English));
        Assert.Equal("Still working. Windows Expert: checking system information. 30 seconds.",
            AgentProgressNarrator.StillWorking("Windows Expert: checking system information.", TimeSpan.FromSeconds(30.2), NarrationLanguage.English));
        Assert.Equal("ما زال العمل جاريًا. 15 ثانية.", AgentProgressNarrator.StillWorking(null, TimeSpan.FromSeconds(15), NarrationLanguage.Arabic));
    }
}

/// <summary>The real pipeline reports steps with the data needed to narrate them.</summary>
public sealed class StepReportingTests : IDisposable
{
    private readonly AgentTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Pipeline_reports_experts_tools_and_approvals_with_display_names()
    {
        _host.Expert("alpha", "Alpha", "apples", "tools: [test]")
             .Script((messages, options) =>
                 ScriptedChatClient.LastToolResult(messages) is not null
                     ? ScriptedChatClient.Text("done")
                     : ScriptedChatClient.Call("c1", "danger_tool", new Dictionary<string, object?> { ["target"] = "f" }));

        await using var services = _host.Build();
        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync();
        var steps = new List<AgentProgress>();
        await conversation.SendAsync("apples?", new SyncProgress(steps));

        Assert.Equal(new[] { "Alpha" }, steps.Single(s => s.Kind == AgentProgressKind.ExpertsSelected).Experts);
        Assert.NotNull(steps.Single(s => s.Kind == AgentProgressKind.ExpertStarted).Model);

        var approval = steps.Single(s => s.Kind == AgentProgressKind.ApprovalRequested);
        Assert.Equal("Alpha", approval.AgentName);           // display name, not the id "alpha"
        Assert.Equal("danger_tool", approval.ToolName);

        var tool = steps.First(s => s.Kind == AgentProgressKind.ToolStarted);
        Assert.Equal("Alpha", tool.AgentName);
        Assert.Equal("danger_tool", tool.ToolName);

        // Order a listener hears: routing -> experts chosen -> expert working -> approval -> tool -> finished.
        var order = steps.Select(s => s.Kind).Where(k => k != AgentProgressKind.ToolCompleted).ToList();
        Assert.Equal(
            new[] { AgentProgressKind.Routing, AgentProgressKind.ExpertsSelected, AgentProgressKind.ExpertStarted, AgentProgressKind.ApprovalRequested, AgentProgressKind.ToolStarted, AgentProgressKind.ExpertCompleted },
            order.ToArray());
    }

    private sealed class SyncProgress(List<AgentProgress> sink) : IProgress<AgentProgress>
    {
        public void Report(AgentProgress value)
        {
            lock (sink) sink.Add(value);
        }
    }
}
