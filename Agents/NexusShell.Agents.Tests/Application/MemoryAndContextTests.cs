using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusShell.Agents.Application.Context;
using NexusShell.Agents.Application.Conversations;
using NexusShell.Agents.Application.Memory;
using NexusShell.Agents.Application.Orchestration;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Application.Routing;
using NexusShell.Agents.Domain.Memory;
using NexusShell.Agents.Domain.Models;
using NexusShell.Agents.Domain.Providers;
using NexusShell.Agents.Domain.Secrets;
using NexusShell.Agents.Infrastructure.Attachments;
using NexusShell.Agents.Infrastructure.Providers;
using NexusShell.Agents.Tests.Fakes;

namespace NexusShell.Agents.Tests.Application;

/// <summary>Long-term memory (save, recall, never secrets) and context-window management (budgets, compaction).</summary>
public sealed class MemoryAndContextTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly AgentTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static MemoryEntry Entry(string id, string content, string? scope = null, bool pinned = false, MemorySource source = MemorySource.User) => new()
    {
        Id = id,
        Content = content,
        Scope = scope,
        Pinned = pinned,
        Source = source,
        CreatedAt = Now.AddDays(-3),
        UpdatedAt = Now.AddDays(-3),
    };

    private AgentTestHost Experts() => _host.Expert("alpha", "Alpha", "apples", "tools: [test]").Expert("beta", "Beta", "bananas");

    // ---------- Ranking ----------

    [Fact]
    public void Relevant_memories_rank_first_and_unrelated_ones_are_left_out()
    {
        var entries = new[]
        {
            Entry("m1", "The user prefers PowerShell 7 over Windows PowerShell."),
            Entry("m2", "The production database server is named atlas-01."),
            Entry("m3", "The user's favourite editor is VS Code."),
        };

        var ranked = MemoryRanker.Rank(entries, "what is the name of my database server?", null, 5, Now);

        Assert.Equal("m2", ranked[0].Entry.Id);
        Assert.DoesNotContain(ranked, r => r.Entry.Id == "m3");
    }

    [Fact]
    public void Arabic_words_match_with_and_without_the_article()
    {
        var entries = new[] { Entry("m1", "اسم المشروع الرئيسي NexusShell"), Entry("m2", "يفضل المستخدم اللغة العربية") };

        var ranked = MemoryRanker.Rank(entries, "ما هو مشروع العمل؟", null, 5, Now);

        Assert.Equal("m1", Assert.Single(ranked).Entry.Id);
    }

    [Fact]
    public void Pinned_memories_always_come_and_folder_memories_only_in_their_folder()
    {
        var entries = new[]
        {
            Entry("pin", "Always answer in short sentences.", pinned: true),
            Entry("proj", "This project builds with dotnet build -c Release.", scope: @"C:\work\app"),
            Entry("other", "That project builds with npm run build.", scope: @"C:\work\web"),
        };

        var inApp = MemoryRanker.Rank(entries, "how does this project build?", @"C:\work\app\src", 5, Now).Select(r => r.Entry.Id).ToList();
        var elsewhere = MemoryRanker.Rank(entries, "how does this project build?", @"D:\other", 5, Now).Select(r => r.Entry.Id).ToList();

        Assert.Equal(new[] { "pin", "proj" }, inApp);
        Assert.Equal(new[] { "pin" }, elsewhere);
    }

    // ---------- Saving rules ----------

    [Fact]
    public async Task Memories_are_saved_to_disk_and_loaded_by_a_new_session()
    {
        await using (var services = _host.Build())
        {
            var result = await services.GetRequiredService<IAgentMemory>().RememberAsync(new MemoryDraft("The user's build server is called forge-7.", MemoryKind.Project));
            Assert.True(result.Saved);
            Assert.Matches("^m[0-9a-f]{6}$", result.Entry!.Id);
        }

        await using var again = _host.Build();
        var entry = Assert.Single(await again.GetRequiredService<IAgentMemory>().ListAsync());
        Assert.Equal("The user's build server is called forge-7.", entry.Content);
        Assert.Equal(MemoryKind.Project, entry.Kind);
        string json = File.ReadAllText(Path.Combine(_host.User, "memory", "memories.json"));
        Assert.Contains("\"kind\": \"project\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("isVerified", json, StringComparison.Ordinal); // computed, never stored
    }

    [Theory]
    [InlineData("sk-", "x", "")]
    [InlineData("my github token is ghp_", "A", "")]
    [InlineData("password = ", "p", "!")]
    public async Task Secrets_are_never_saved(string prefix, string filler, string suffix)
    {
        await using var services = _host.Build();
        string content = prefix + string.Concat(Enumerable.Repeat(filler, 36)) + suffix;

        var result = await services.GetRequiredService<IAgentMemory>().RememberAsync(new MemoryDraft(content));

        Assert.False(result.Saved);
        Assert.Contains("never contain passwords", result.Refusal, StringComparison.Ordinal);
        Assert.Empty(await services.GetRequiredService<IAgentMemory>().ListAsync());
    }

    [Fact]
    public async Task The_value_of_a_secret_environment_variable_is_never_saved()
    {
        string value = "q" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(TestToolProvider.SecretVariable, value);
        try
        {
            await using var services = _host.Build();
            var result = await services.GetRequiredService<IAgentMemory>().RememberAsync(new MemoryDraft("The deploy key is " + value));
            Assert.False(result.Saved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestToolProvider.SecretVariable, null);
        }
    }

    [Fact]
    public async Task Near_duplicates_update_the_existing_memory_and_long_text_is_refused()
    {
        await using var services = _host.Build();
        var memory = services.GetRequiredService<IAgentMemory>();

        var first = await memory.RememberAsync(new MemoryDraft("The user prefers dark themes in every editor."));
        var second = await memory.RememberAsync(new MemoryDraft("The user prefers dark themes in every editor and terminal."));
        var tooLong = await memory.RememberAsync(new MemoryDraft(new string('a', 1_500)));

        Assert.True(second.Updated);
        Assert.Equal(first.Entry!.Id, second.Entry!.Id);
        Assert.Equal("The user prefers dark themes in every editor and terminal.", Assert.Single(await memory.ListAsync()).Content);
        Assert.False(tooLong.Saved);
        Assert.Contains("one short fact", tooLong.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Least_useful_memories_are_evicted_but_pinned_ones_stay()
    {
        _host.WriteBundled("agent.json", AgentTestHost.DefaultAgentJson.TrimEnd().TrimEnd('}') + ", \"memory\": { \"maxEntries\": 10 } }");
        await using var services = _host.Build();
        var memory = services.GetRequiredService<IAgentMemory>();

        var pinned = await memory.RememberAsync(new MemoryDraft("Pinned fact zero about orchids.", Pinned: true));
        var used = await memory.RememberAsync(new MemoryDraft("Frequently used fact about tulips."));
        await memory.MarkUsedAsync([used.Entry!.Id]);
        string[] topics = ["lions", "tigers", "bears", "wolves", "eagles", "sharks", "whales", "camels", "zebras", "pandas", "otters"];
        foreach (string topic in topics) await memory.RememberAsync(new MemoryDraft($"A separate note about {topic} number {topic.Length}."));

        var left = await memory.ListAsync();
        Assert.Equal(10, left.Count);
        Assert.Contains(left, e => e.Id == pinned.Entry!.Id);
        Assert.Contains(left, e => e.Id == used.Entry.Id);
    }

    [Fact]
    public async Task Memories_can_be_corrected_pinned_forgotten_and_cleared()
    {
        await using var services = _host.Build();
        var memory = services.GetRequiredService<IAgentMemory>();
        var saved = await memory.RememberAsync(new MemoryDraft("The office printer is on floor 2."));

        Assert.True((await memory.UpdateAsync(saved.Entry!.Id, "The office printer is on floor 3.")).Saved);
        Assert.True(await memory.SetPinnedAsync(saved.Entry.Id, true));
        var entry = Assert.Single(await memory.ListAsync());
        Assert.Equal("The office printer is on floor 3.", entry.Content);
        Assert.True(entry.Pinned);

        Assert.True(await memory.ForgetAsync(saved.Entry.Id));
        Assert.False(await memory.ForgetAsync(saved.Entry.Id));
        await memory.RememberAsync(new MemoryDraft("One more fact about pears."));
        Assert.Equal(1, await memory.ClearAsync());
        Assert.Empty(await memory.ListAsync());
    }

    // ---------- Memory in the agent ----------

    [Fact]
    public async Task Relevant_memories_and_accuracy_rules_reach_the_model()
    {
        Experts();
        _host.Script((_, _) => ScriptedChatClient.Text("ok"));
        await using var services = _host.Build();
        var saved = await services.GetRequiredService<IAgentMemory>().RememberAsync(new MemoryDraft("The apples orchard server is named atlas-01."));

        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync();
        var steps = new List<AgentProgress>();
        await conversation.SendAsync("which server hosts the apples orchard?", new SyncProgress(steps));

        string prompt = ScriptedChatClient.Prompt(_host.Chat.Calls.Last().Messages, _host.Chat.Calls.Last().Options);
        Assert.Contains("atlas-01", prompt, StringComparison.Ordinal);
        Assert.Contains($"[{saved.Entry!.Id}] (the user said", prompt, StringComparison.Ordinal);
        Assert.Contains("Never save secrets", prompt, StringComparison.Ordinal);
        Assert.Contains(steps, s => s.Kind == AgentProgressKind.MemoryRecalled && s.Count == 1);
        Assert.Equal(1, Assert.Single(await services.GetRequiredService<IAgentMemory>().ListAsync()).UseCount);
    }

    [Fact]
    public async Task The_agent_saves_memories_with_the_remember_tool_even_in_read_only_mode()
    {
        Experts();
        int call = 0;
        _host.Script((messages, _) => Interlocked.Increment(ref call) == 1
            ? ScriptedChatClient.Call("c1", "remember", new Dictionary<string, object?> { ["content"] = "The user grows Fuji apples.", ["kind"] = "preference", ["source"] = "user" })
            : ScriptedChatClient.Text("noted: " + ScriptedChatClient.LastToolResult(messages)?.Result));
        await using var services = _host.Build();

        var conversation = await services.GetRequiredService<INexusAgentService>()
            .StartConversationAsync(null, new ConversationOptions { Permission = PermissionMode.ReadOnly });
        var reply = await conversation.SendAsync("remember that I grow Fuji apples");

        var entry = Assert.Single(await services.GetRequiredService<IAgentMemory>().ListAsync());
        Assert.Equal("The user grows Fuji apples.", entry.Content);
        Assert.Equal(MemoryKind.Preference, entry.Kind);
        Assert.Contains($"Saved memory [{entry.Id}]", reply.Text, StringComparison.Ordinal);
        Assert.Empty(_host.Approvals.Requests); // the agent's own memory needs no approval
    }

    [Fact]
    public async Task Memory_can_be_turned_off()
    {
        Experts().WriteBundled("agent.json", AgentTestHost.DefaultAgentJson.TrimEnd().TrimEnd('}') + ", \"memory\": { \"enabled\": false } }");
        _host.Script((_, _) => ScriptedChatClient.Text("ok"));
        await using var services = _host.Build();

        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync();
        await conversation.SendAsync("apples?");

        var last = _host.Chat.Calls.Last();
        Assert.DoesNotContain(last.Options?.Tools ?? [], t => t.Name == "remember");
        Assert.DoesNotContain(MemoryPrompt.Header, ScriptedChatClient.Prompt(last.Messages, last.Options), StringComparison.Ordinal);
    }

    // ---------- Context windows and budgets ----------

    [Theory]
    [InlineData(ProviderKind.OpenAICompatible, false, null, "gemini-3.5-flash", 1_048_576)]
    [InlineData(ProviderKind.Anthropic, false, null, "claude-sonnet-5-5", 200_000)]
    [InlineData(ProviderKind.OpenAI, false, null, "gpt-4.1-mini", 1_047_576)]
    [InlineData(ProviderKind.OpenAICompatible, false, null, "some-new-model", ModelContextWindows.DefaultOnline)]
    [InlineData(ProviderKind.OpenAICompatible, false, 50_000, "gemini-3.5-flash", 50_000)]
    [InlineData(ProviderKind.Ollama, true, null, "llama3.2", ModelContextWindows.DefaultOllama)]
    [InlineData(ProviderKind.Ollama, true, 500_000, "llama3.2", 131_072)]
    [InlineData(ProviderKind.OpenAICompatible, true, null, "local-model", ModelContextWindows.DefaultLocal)]
    public void Context_windows_come_from_settings_known_models_or_safe_defaults(ProviderKind kind, bool local, int? configured, string model, int expected)
    {
        var provider = new ProviderProfile { Id = "p", Kind = kind, IsLocal = local, ContextWindow = configured };

        Assert.Equal(expected, ModelContextWindows.Resolve(provider, model));
    }

    [Fact]
    public void Budgets_use_large_windows_fully_and_keep_small_ones_safe()
    {
        var large = new ContextBudget(1_048_576);
        var small = new ContextBudget(8_192);

        Assert.True(large.HistoryTokens > 400_000);
        Assert.Equal(2_000, large.MemoryTokens(2_000));
        Assert.True(small.HistoryTokens + small.CatchUpTokens + small.MemoryTokens(2_000) + small.AttachmentChars / 3 < small.Usable);
        Assert.True(small.MemoryTokens(2_000) < 400);
    }

    [Fact]
    public async Task Ollama_is_asked_for_the_full_context_window()
    {
        var inner = new ScriptedChatClient((_, _) => ScriptedChatClient.Text("ok"));
        using var client = new OllamaProviderAdapter.ContextWindowChatClient(inner, 16_384);
        var options = new ChatOptions { Temperature = 0.2f };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        var sent = inner.Calls.Single().Options!;
        Assert.Contains(sent.AdditionalProperties!, p => p.Key.Contains("num_ctx", StringComparison.Ordinal) && Convert.ToInt32(p.Value) == 16_384);
        Assert.Null(options.AdditionalProperties); // the caller's options are untouched
    }

    [Fact]
    public void Attachments_grow_with_the_context_window()
    {
        Assert.Equal(FileAttachmentReader.MaxFileChars, FileAttachmentReader.PerFileLimit(FileAttachmentReader.MaxTotalChars));
        Assert.True(FileAttachmentReader.PerFileLimit(new ContextBudget(1_048_576).AttachmentChars) > 300_000); // ~85K tokens per file
        Assert.Equal(9_000, FileAttachmentReader.PerFileLimit(9_000));
    }

    [Fact]
    public void Catch_up_keeps_the_newest_turns_that_fit()
    {
        var turns = Enumerable.Range(1, 10).Select(i => new ConversationTurn($"question {i} " + new string('q', 400), $"answer {i}", ["alpha"])).ToList();

        var fitted = MixtureOfExpertsAgent.FitTurns(turns, 350);

        Assert.InRange(fitted.Count, 1, 4);
        Assert.StartsWith("question 10", fitted[^1].User, StringComparison.Ordinal);
        Assert.Single(MixtureOfExpertsAgent.FitTurns(turns, 1)); // the last turn always comes
    }

    [Theory]
    [InlineData("sk-proj-", "a", true)]
    [InlineData("AIza", "B", true)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", "", true)]
    [InlineData("api_key: ", "z", true)]
    [InlineData("The user prefers short answers.", "", false)]
    [InlineData("Run dotnet build -c Release in F:\\repo.", "", false)]
    public void Secret_detection(string prefix, string filler, bool secret) =>
        Assert.Equal(secret, SecretPatterns.LooksLikeSecret(prefix + string.Concat(Enumerable.Repeat(filler, filler.Length == 0 ? 0 : 36))));

    // ---------- History compaction ----------

    private static List<ChatMessage> Turns(int count, int size = 400)
    {
        var messages = new List<ChatMessage>();
        for (int i = 1; i <= count; i++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"user {i} " + new string('u', size)));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"assistant {i} " + new string('a', size)));
        }
        return messages;
    }

    [Fact]
    public async Task History_within_budget_is_left_alone()
    {
        var reducer = new CompactingChatReducer(10_000, 100, 500, (_, _, _) => throw new InvalidOperationException("must not summarize"), NullLogger.Instance);
        var messages = Turns(3);

        Assert.Equal(messages, (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList());
    }

    [Fact]
    public async Task Old_turns_are_summarized_and_recent_turns_kept_word_for_word()
    {
        IReadOnlyList<ChatMessage>? summarized = null;
        string? previous = "unset";
        var reducer = new CompactingChatReducer(1_000, 100, 300, (old, prev, _) =>
        {
            summarized = old;
            previous = prev;
            return Task.FromResult<string?>("- the user asked ten questions");
        }, NullLogger.Instance);

        var reduced = (await reducer.ReduceAsync(Turns(10), CancellationToken.None)).ToList();

        Assert.True(CompactingChatReducer.IsSummary(reduced[0]));
        Assert.Contains("the user asked ten questions", reduced[0].Text, StringComparison.Ordinal);
        Assert.Equal(ChatRole.User, reduced[1].Role);
        Assert.StartsWith("assistant 10", reduced[^1].Text, StringComparison.Ordinal);
        Assert.StartsWith("user 1 ", summarized![0].Text, StringComparison.Ordinal);
        Assert.Null(previous);
        Assert.True(TokenEstimator.Estimate(reduced) <= 1_000);

        // Next compaction merges the previous notes.
        var next = (await reducer.ReduceAsync(reduced.Concat(Turns(6)), CancellationToken.None)).ToList();
        Assert.Equal("- the user asked ten questions", previous);
        Assert.Single(next, CompactingChatReducer.IsSummary);
    }

    [Fact]
    public async Task When_summarizing_fails_old_turns_are_dropped_and_tool_pairs_stay_together()
    {
        var reducer = new CompactingChatReducer(600, 100, 300, (_, _, _) => throw new HttpRequestException("offline"), NullLogger.Instance);
        var messages = Turns(6, 300);
        messages.Add(new ChatMessage(ChatRole.User, "list the files"));
        messages.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "list_directory", new Dictionary<string, object?> { ["path"] = "." })]));
        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", new string('f', 600))]));
        messages.Add(new ChatMessage(ChatRole.Assistant, "here are the files"));

        var reduced = (await reducer.ReduceAsync(messages, CancellationToken.None)).ToList();

        Assert.DoesNotContain(reduced, CompactingChatReducer.IsSummary);
        Assert.Equal(ChatRole.User, reduced[0].Role);
        Assert.Contains(reduced, m => m.Contents.OfType<FunctionCallContent>().Any());
        Assert.Contains(reduced, m => m.Contents.OfType<FunctionResultContent>().Any());
    }

    [Fact]
    public async Task Long_conversations_on_a_small_model_are_compacted_into_notes()
    {
        Experts().WriteBundled("agent.json",
            """
            {
              "orchestration": { "router": { "strategy": "keyword" }, "defaultExpert": "alpha" },
              "models": { "preference": ["fake"] },
              "providers": { "fake": { "kind": "openai-compatible", "endpoint": "https://fake.invalid/v1", "defaultModel": "test-model", "contextWindow": 4096 } }
            }
            """);
        int summaries = 0;
        _host.Script((messages, options) =>
        {
            if (ScriptedChatClient.Prompt(messages, options).Contains("You compress a conversation", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref summaries);
                return ScriptedChatClient.Text("- the user is planting apples in rows");
            }
            return ScriptedChatClient.Text("answer " + new string('a', 600));
        });
        await using var services = _host.Build();
        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync();

        for (int i = 0; i < 8; i++) await conversation.SendAsync($"apples question {i} " + new string('q', 600));

        Assert.True(summaries > 0);
        var lastExpertCall = _host.Chat.Calls.Last(c => !ScriptedChatClient.Prompt(c.Messages, c.Options).Contains("You compress a conversation", StringComparison.Ordinal));
        Assert.Contains(lastExpertCall.Messages, m => CompactingChatReducer.IsSummary(m) && m.Text.Contains("planting apples", StringComparison.Ordinal));
        Assert.True(TokenEstimator.Estimate(lastExpertCall.Messages) < 4096);
    }

    private sealed class SyncProgress(List<AgentProgress> sink) : IProgress<AgentProgress>
    {
        public void Report(AgentProgress value)
        {
            lock (sink) sink.Add(value);
        }
    }
}
