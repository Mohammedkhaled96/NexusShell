using Microsoft.Extensions.AI;
using NexusShell.Agents.Application.Agents;
using NexusShell.Agents.Application.Routing;
using NexusShell.Agents.Domain.Experts;
using NexusShell.Agents.Domain.Orchestration;
using NexusShell.Agents.Domain.Routing;

namespace NexusShell.Agents.Tests.Application;

public class RoutingTests
{
    private static readonly IReadOnlyList<ExpertProfile> Experts =
    [
        Expert("shell-expert", "Shell Expert", "Writes PowerShell and bash commands.", "powershell", "bash", "script", "أمر"),
        Expert("devops-expert", "DevOps Expert", "Git, Docker and CI pipelines.", "git", "rebase", "docker", "pipeline"),
        Expert("code-expert", "Code Expert", "Debugs code and exceptions.", "c#", ".net", "exception", "bug"),
    ];

    private static ExpertProfile Expert(string id, string name, string description, params string[] keywords) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        Instructions = "x",
        Keywords = keywords,
    };

    private static RoutingRequest Request(string message, int topK = 2) =>
        new(message, Experts, [], new RouterSettings { Strategy = RoutingStrategyKind.Keyword, TopK = topK, MinScore = 0.25 });

    private static async Task<IReadOnlyList<ExpertScore>> TopK(string message, int topK = 2)
    {
        var scores = await new KeywordRoutingGate().ScoreAsync(Request(message, topK), CancellationToken.None);
        return RoutingMath.SelectTopK(scores!, Request(message, topK).Settings);
    }

    [Fact]
    public async Task Keyword_gate_routes_git_to_devops()
    {
        var selected = await TopK("how do I fix a git rebase conflict?");
        Assert.Equal("devops-expert", Assert.Single(selected).ExpertId);
    }

    [Fact]
    public async Task Keyword_gate_activates_two_experts_for_mixed_requests()
    {
        var selected = await TopK("my powershell script throws an exception in C# interop");
        Assert.Equal(new[] { "code-expert", "shell-expert" }, selected.Select(s => s.ExpertId).Order().ToArray());
    }

    [Fact]
    public async Task Keyword_gate_understands_arabic_keywords()
    {
        var selected = await TopK("اكتب لي أمر لحذف الملفات");
        Assert.Equal("shell-expert", Assert.Single(selected).ExpertId);
    }

    [Fact]
    public async Task Explicit_mention_of_an_expert_wins()
    {
        var selected = await TopK("ask the code expert about this", topK: 1);
        Assert.Equal("code-expert", Assert.Single(selected).ExpertId);
    }

    [Fact]
    public async Task Unrelated_request_selects_nobody()
    {
        Assert.Empty(await TopK("what a lovely day"));
    }

    [Fact]
    public void Top_k_drops_weak_secondary_experts()
    {
        var selected = RoutingMath.SelectTopK(
            [new ExpertScore("a", 0.9), new ExpertScore("b", 0.3), new ExpertScore("c", 0.85), new ExpertScore("d", 0.1)],
            new RouterSettings { TopK = 3, MinScore = 0.2 });

        Assert.Equal(new[] { "a", "c" }, selected.Select(s => s.ExpertId).ToArray());
    }

    [Fact]
    public void Llm_gate_parses_json_wrapped_in_prose_and_ignores_unknown_ids()
    {
        const string output = "Sure! Here you go:\n```json\n{\"experts\":[{\"id\":\"DEVOPS-EXPERT\",\"score\":\"0.8\",\"reason\":\"git\"},{\"id\":\"made-up\",\"score\":1}]}\n```";
        var scores = LlmRoutingGate.Parse(output, Request("x"));

        var only = Assert.Single(scores!);
        Assert.Equal("devops-expert", only.ExpertId);
        Assert.Equal(0.8, only.Score, 3);
    }

    [Theory]
    [InlineData("I think the shell expert")]
    [InlineData("{ not json")]
    [InlineData("{\"experts\": []}")]
    public void Llm_gate_returns_null_on_unusable_output(string output) =>
        Assert.Null(LlmRoutingGate.Parse(output, Request("x")));

    [Fact]
    public async Task Chat_reducer_keeps_window_aligned_to_a_user_turn()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "u1"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "tool")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "r")]),
            new(ChatRole.Assistant, "a1"),
            new(ChatRole.User, "u2"),
            new(ChatRole.Assistant, "a2"),
        };

        var reduced = (await new RecentMessagesChatReducer(4).ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.Equal(ChatRole.User, reduced[0].Role);
        Assert.Equal("u2", reduced[0].Text);
        Assert.DoesNotContain(reduced, m => m.Role == ChatRole.Tool);
    }
}
