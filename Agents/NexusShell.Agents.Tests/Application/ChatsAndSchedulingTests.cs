using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NexusShell.Agents.Application.Abstractions.Approval;
using NexusShell.Agents.Application.Chats;
using NexusShell.Agents.Application.Conversations;
using NexusShell.Agents.Application.Scheduling;
using NexusShell.Agents.Domain.Chats;
using NexusShell.Agents.Domain.Scheduling;
using NexusShell.Agents.Infrastructure.Chats;
using NexusShell.Agents.Tests.Fakes;

namespace NexusShell.Agents.Tests.Application;

/// <summary>Chats saved on the device (one folder each), continuing them with full context, and scheduled tasks.</summary>
public sealed class ChatsAndSchedulingTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero); // Monday 08:00 UTC
    private readonly AgentTestHost _host = new();
    private readonly ManualTime _time = new(Start);

    public void Dispose() => _host.Dispose();

    private string ChatsFolder => Path.Combine(_host.Root, "chats");

    private ServiceProvider Build() => _host.Build(s =>
    {
        s.AddSingleton(new ChatStorageOptions { Directory = ChatsFolder, UseRecycleBin = false });
        s.AddSingleton<TimeProvider>(_time);
    });

    private AgentTestHost Experts() => _host.Expert("alpha", "Alpha", "apples", "tools: [test]").Expert("beta", "Beta", "bananas");

    private static SavedChat Chat(string id, params (string Role, string Text)[] messages) => new()
    {
        Summary = new ChatSummary { Id = id, Title = "New chat" },
        Messages = messages.Select(m => new SavedChatMessage(m.Role, m.Role == "user" ? "You" : "NexusAgent", m.Text, Start)).ToList(),
    };

    // ---------- Schedules ----------

    [Fact]
    public void Daily_weekday_weekly_and_interval_schedules_find_the_next_run()
    {
        var utc = TimeZoneInfo.Utc;
        var nine = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero); // Friday 09:00

        Assert.Equal(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero), new TaskSchedule { Repeat = RepeatKind.Daily, StartAt = nine }.NextAfter(Start, utc));
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero), new TaskSchedule { Repeat = RepeatKind.Daily, StartAt = nine }.NextAfter(Start.AddHours(2), utc));
        var saturdayNoon = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero), new TaskSchedule { Repeat = RepeatKind.Weekdays, StartAt = nine }.NextAfter(saturdayNoon, utc));
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero),
            new TaskSchedule { Repeat = RepeatKind.Weekly, StartAt = nine, Days = [DayOfWeek.Thursday] }.NextAfter(Start, utc));
        Assert.Equal(Start.AddMinutes(30), new TaskSchedule { Repeat = RepeatKind.EveryMinutes, IntervalMinutes = 30, StartAt = Start.AddHours(-2) }.NextAfter(Start.AddMinutes(1), utc));
        Assert.Null(new TaskSchedule { Repeat = RepeatKind.Once, StartAt = nine }.NextAfter(Start, utc));

        Assert.Equal("every weekday (Monday to Friday) at 09:00", new TaskSchedule { Repeat = RepeatKind.Weekdays, StartAt = nine }.Describe(utc));
        Assert.Equal("every Monday and Thursday at 09:00",
            new TaskSchedule { Repeat = RepeatKind.Weekly, StartAt = nine, Days = [DayOfWeek.Thursday, DayOfWeek.Monday] }.Describe(utc));
    }

    [Theory]
    [InlineData("18:30", "2026-11-23T18:30:00")]
    [InlineData("07:00", "2026-11-24T07:00:00")] // already passed today: tomorrow
    [InlineData("tomorrow 08:15", "2026-11-24T08:15:00")]
    [InlineData("in 30 minutes", "2026-11-23T08:30:00")]
    [InlineData("in 2 hours", "2026-11-23T10:00:00")]
    [InlineData("2026-12-01 09:00", "2026-12-01T09:00:00")]
    public void Start_times_are_read_in_the_users_time_zone(string text, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected + "+00:00", System.Globalization.CultureInfo.InvariantCulture), ScheduleParser.ParseStart(text, Start, TimeZoneInfo.Utc));

    [Theory]
    [InlineData("daily", RepeatKind.Daily, 0)]
    [InlineData("weekdays", RepeatKind.Weekdays, 0)]
    [InlineData("hourly", RepeatKind.EveryMinutes, 60)]
    [InlineData("every 15 minutes", RepeatKind.EveryMinutes, 15)]
    [InlineData("every 2 hours", RepeatKind.EveryMinutes, 120)]
    [InlineData("every monday and friday", RepeatKind.Weekly, 0)]
    public void Repeats_are_read(string text, RepeatKind kind, int minutes)
    {
        var schedule = ScheduleParser.ParseSchedule(Start, text, null);
        Assert.Equal(kind, schedule.Repeat);
        if (minutes > 0) Assert.Equal(minutes, schedule.IntervalMinutes);
        if (kind == RepeatKind.Weekly) Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Friday }, schedule.Days);
    }

    [Fact]
    public void Unreadable_times_and_too_frequent_repeats_are_refused()
    {
        Assert.Throws<ArgumentException>(() => ScheduleParser.ParseStart("whenever", Start, TimeZoneInfo.Utc));
        Assert.Throws<ArgumentException>(() => ScheduleParser.ParseSchedule(Start, "every 2 minutes", null));
        Assert.Throws<ArgumentException>(() => ScheduleParser.ParseSchedule(Start, "sometimes", null));
    }

    // ---------- Saved chats ----------

    [Fact]
    public async Task Chats_are_saved_one_folder_each_with_an_automatic_title_and_listed_newest_first()
    {
        await using var services = Build();
        var library = services.GetRequiredService<IChatLibrary>();

        var first = await library.SaveAsync(Chat("20261123-080000-aaaa", ("user", "How do I list large files in PowerShell?\nThanks"), ("assistant", "Use Get-ChildItem ...")));
        _time.Advance(TimeSpan.FromMinutes(5));
        await library.SaveAsync(Chat("20261123-080500-bbbb", ("user", "/model auto"), ("note", "Model: automatic"), ("user", "Second question"), ("assistant", "Second answer")));

        Assert.Equal("How do I list large files in PowerShell?", first.Title);
        Assert.Equal(2, first.MessageCount);
        Assert.Equal("Use Get-ChildItem ...", first.Preview);
        var list = await library.ListAsync();
        Assert.Equal(new[] { "20261123-080500-bbbb", "20261123-080000-aaaa" }, list.Select(c => c.Id).ToArray());
        Assert.Equal("Second question", list[0].Title); // commands are not titles
        Assert.True(File.Exists(Path.Combine(ChatsFolder, "20261123-080000-aaaa", "chat.json")));
        Assert.True(File.Exists(Path.Combine(ChatsFolder, "20261123-080000-aaaa", "messages.json")));
    }

    [Fact]
    public async Task Secrets_typed_in_a_chat_are_masked_before_it_is_saved()
    {
        await using var services = Build();
        var library = services.GetRequiredService<IChatLibrary>();
        string key = "sk-proj-" + string.Concat(Enumerable.Repeat("k", 30));

        await library.SaveAsync(Chat("20261123-080000-cccc", ("user", "my key is " + key), ("assistant", "Never share keys.")));

        string saved = File.ReadAllText(Path.Combine(ChatsFolder, "20261123-080000-cccc", "messages.json"));
        Assert.DoesNotContain(key, saved, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chats_can_be_renamed_pinned_duplicated_exported_and_deleted()
    {
        await using var services = Build();
        var library = services.GetRequiredService<IChatLibrary>();
        await library.SaveAsync(Chat("20261123-080000-dddd", ("user", "Plan the release"), ("assistant", "Step 1 ...")));
        _time.Advance(TimeSpan.FromMinutes(1));
        await library.SaveAsync(Chat("20261123-080100-eeee", ("user", "Other topic"), ("assistant", "Sure")));

        var renamed = await library.RenameAsync("20261123-080000-dddd", "Release plan");
        await library.SetPinnedAsync("20261123-080000-dddd", true);
        var copy = await library.DuplicateAsync("20261123-080000-dddd");
        string? text = await library.ExportTextAsync("20261123-080000-dddd");

        Assert.True(renamed!.TitleSetByUser);
        var list = await library.ListAsync();
        Assert.Equal("Release plan", list[0].Title); // pinned first
        Assert.Equal("Copy of Release plan", copy!.Title);
        Assert.Contains("Plan the release", text, StringComparison.Ordinal);

        // A renamed title survives later saves.
        var reopened = await library.OpenAsync("20261123-080000-dddd");
        await library.SaveAsync(reopened! with { Messages = reopened.Messages.Append(new SavedChatMessage("user", "You", "more", Start)).ToList() });
        Assert.Equal("Release plan", (await library.OpenAsync("20261123-080000-dddd"))!.Summary.Title);

        Assert.True(await library.DeleteAsync("20261123-080000-dddd"));
        Assert.False(Directory.Exists(Path.Combine(ChatsFolder, "20261123-080000-dddd")));
        await Assert.ThrowsAsync<ArgumentException>(() => library.OpenAsync(@"..\..\Windows"));
    }

    [Fact]
    public async Task Chats_move_to_a_new_folder()
    {
        await using var services = Build();
        var library = services.GetRequiredService<IChatLibrary>();
        await library.SaveAsync(Chat("20261123-080000-ffff", ("user", "hello"), ("assistant", "hi")));
        string newFolder = Path.Combine(_host.Root, "Documents", "My chats");

        int moved = await library.MoveAsync(newFolder);

        Assert.Equal(1, moved);
        Assert.Equal(newFolder, library.RootDirectory);
        Assert.Single(await library.ListAsync());
        Assert.True(File.Exists(Path.Combine(newFolder, "20261123-080000-ffff", "chat.json")));
    }

    // ---------- Continuing a chat ----------

    [Fact]
    public async Task A_saved_chat_continues_with_the_agents_full_state()
    {
        Experts();
        _host.Script((messages, _) => ScriptedChatClient.Text("seen " + messages.Count(m => m.Role == ChatRole.User)));
        await using var services = Build();
        var agent = services.GetRequiredService<INexusAgentService>();

        var first = await agent.StartConversationAsync();
        await first.SendAsync("apples are green here");
        await first.SendAsync("apples are sweet here");
        string? state = await first.ExportStateAsync();
        Assert.NotNull(state);

        var resumed = await agent.StartConversationAsync(null, ConversationOptions.Default, new ConversationResume(state, []));
        await resumed.SendAsync("apples: what did I say?");

        var last = _host.Chat.Calls.Last().Messages;
        Assert.Contains(last, m => m.Text.Contains("apples are green here", StringComparison.Ordinal));
        Assert.Contains(last, m => m.Text.Contains("apples are sweet here", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_the_saved_state_is_unreadable_the_saved_turns_are_used()
    {
        Experts();
        _host.Script((_, _) => ScriptedChatClient.Text("ok"));
        await using var services = Build();
        var chat = Chat("20261123-080000-gggg", ("user", "apples grow on the hill"), ("assistant", "noted"));

        var resumed = await services.GetRequiredService<INexusAgentService>()
            .StartConversationAsync(null, ConversationOptions.Default, ConversationResume.FromChat(chat with { AgentState = "{ not json" }));
        await resumed.SendAsync("apples: where do they grow?");

        Assert.Contains(_host.Chat.Calls.Last().Messages, m => m.Text.Contains("apples grow on the hill", StringComparison.Ordinal));
    }

    // ---------- Scheduled tasks ----------

    private static ScheduledTaskDraft Draft(string prompt, TaskSchedule schedule, bool runIfMissed = true) =>
        new("Apple check", prompt, schedule, RunIfMissed: runIfMissed);

    [Fact]
    public async Task Invalid_tasks_are_refused_with_a_clear_reason()
    {
        await using var services = Build();
        var scheduler = services.GetRequiredService<IAgentScheduler>();

        var past = await Assert.ThrowsAsync<ArgumentException>(() => scheduler.AddAsync(Draft("apples", new TaskSchedule { StartAt = Start.AddHours(-1) })));
        var secret = await Assert.ThrowsAsync<ArgumentException>(() => scheduler.AddAsync(Draft("use password = " + new string('x', 12), new TaskSchedule { StartAt = Start.AddHours(1) })));
        var frequent = await Assert.ThrowsAsync<ArgumentException>(() => scheduler.AddAsync(Draft("apples", new TaskSchedule { Repeat = RepeatKind.EveryMinutes, IntervalMinutes = 1, StartAt = Start })));

        Assert.Contains("already passed", past.Message, StringComparison.Ordinal);
        Assert.Contains("passwords", secret.Message, StringComparison.Ordinal);
        Assert.Contains("every 5 minutes", frequent.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_due_task_runs_unattended_saves_a_chat_and_moves_to_the_next_time()
    {
        Experts();
        _host.Script((_, _) => ScriptedChatClient.Text("Apples checked: all fine."));
        await using var services = Build();
        var scheduler = services.GetRequiredService<IAgentScheduler>();
        var completed = new List<ScheduledRunResult>();
        scheduler.TaskCompleted += completed.Add;

        var task = await scheduler.AddAsync(Draft("check the apples", new TaskSchedule { Repeat = RepeatKind.Daily, StartAt = Start.AddMinutes(10) }));
        Assert.Equal(Start.AddMinutes(10), task.NextRunAt);

        Assert.Equal(0, await ((AgentScheduler)scheduler).RunDueAsync(CancellationToken.None)); // not yet
        _time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, await ((AgentScheduler)scheduler).RunDueAsync(CancellationToken.None));

        var run = Assert.Single(completed);
        Assert.True(run.Succeeded);
        var after = Assert.Single(await scheduler.ListAsync());
        Assert.Equal(TaskRunStatus.Succeeded, after.LastStatus);
        Assert.Equal(Start.AddDays(1).AddMinutes(10), after.NextRunAt);

        var chat = await services.GetRequiredService<IChatLibrary>().OpenAsync(run.ChatId!);
        Assert.StartsWith("Scheduled: Apple check", chat!.Summary.Title, StringComparison.Ordinal);
        Assert.Equal(task.Id, chat.Summary.ScheduledTaskId);
        Assert.Contains(chat.Messages, m => m.Role == "assistant" && m.Content.StartsWith("Apples checked", StringComparison.Ordinal));
        Assert.NotNull(chat.AgentState); // the user can open the chat and continue
    }

    [Fact]
    public async Task Unattended_runs_never_take_actions_that_need_approval()
    {
        Experts();
        int call = 0;
        _host.Script((messages, _) => Interlocked.Increment(ref call) == 1
            ? ScriptedChatClient.Call("c1", "danger_tool", new Dictionary<string, object?> { ["target"] = "the apples" })
            : ScriptedChatClient.Text("Could not delete: it needs your approval."));
        await using var services = Build();
        var scheduler = services.GetRequiredService<IAgentScheduler>();

        var task = await scheduler.AddAsync(Draft("apples: delete the old apples", new TaskSchedule { StartAt = Start.AddMinutes(5) }));
        var result = await scheduler.RunNowAsync(task.Id);

        Assert.True(result!.Succeeded);
        Assert.Empty(_host.Tools.Deleted);         // the destructive tool never ran
        Assert.Empty(_host.Approvals.Requests);    // and no dialog was opened for nobody
    }

    [Fact]
    public async Task The_agent_can_schedule_a_task_when_the_user_approves()
    {
        Experts();
        int call = 0;
        _host.Script((messages, _) => Interlocked.Increment(ref call) == 1
            ? ScriptedChatClient.Call("c1", "schedule_task", new Dictionary<string, object?>
            {
                ["name"] = "Water the apples",
                ["instructions"] = "Remind me to water the apple trees.",
                ["start"] = "18:00",
                ["repeat"] = "weekdays",
            })
            : ScriptedChatClient.Text("done: " + ScriptedChatClient.LastToolResult(messages)?.Result));
        await using var services = Build();

        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync();
        var reply = await conversation.SendAsync("apples: every weekday at 6 pm remind me to water them");

        var task = Assert.Single(await services.GetRequiredService<IAgentScheduler>().ListAsync());
        Assert.Equal("Water the apples", task.Name);
        Assert.Equal(RepeatKind.Weekdays, task.Schedule.Repeat);
        Assert.Equal("agent", task.CreatedBy);
        Assert.Contains(_host.Approvals.Requests, r => r.ToolName == "schedule_task");
        Assert.Contains("every weekday", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denied_schedules_are_not_created()
    {
        Experts();
        _host.Approvals.Decision = ToolApprovalDecision.Deny;
        int call = 0;
        _host.Script((_, _) => Interlocked.Increment(ref call) == 1
            ? ScriptedChatClient.Call("c1", "schedule_task", new Dictionary<string, object?> { ["name"] = "x", ["instructions"] = "apples", ["start"] = "18:00" })
            : ScriptedChatClient.Text("ok"));
        await using var services = Build();

        var conversation = await services.GetRequiredService<INexusAgentService>().StartConversationAsync();
        await conversation.SendAsync("apples later");

        Assert.Empty(await services.GetRequiredService<IAgentScheduler>().ListAsync());
    }

    [Fact]
    public async Task Runs_missed_while_closed_catch_up_or_move_on()
    {
        await using var services = Build();
        var scheduler = services.GetRequiredService<IAgentScheduler>();
        var skip = await scheduler.AddAsync(Draft("apples", new TaskSchedule { Repeat = RepeatKind.Daily, StartAt = Start.AddMinutes(30) }, runIfMissed: false));
        var catchUp = await scheduler.AddAsync(Draft("bananas", new TaskSchedule { Repeat = RepeatKind.Daily, StartAt = Start.AddMinutes(30) }) with { Name = "Banana check" });
        _time.Advance(TimeSpan.FromHours(3)); // NexusShell was closed at 08:30

        await scheduler.StartAsync();
        await scheduler.StopAsync();

        var tasks = (await scheduler.ListAsync()).ToDictionary(t => t.Id);
        Assert.Equal(TaskRunStatus.Missed, tasks[skip.Id].LastStatus);
        Assert.Equal(Start.AddDays(1).AddMinutes(30), tasks[skip.Id].NextRunAt);
        // Caught up: either still due (runs on the first tick) or already run once by the started clock.
        Assert.True(tasks[catchUp.Id].NextRunAt <= _time.GetUtcNow() || tasks[catchUp.Id].LastRunAt is not null);
        Assert.NotEqual(TaskRunStatus.Missed, tasks[catchUp.Id].LastStatus);
    }

    /// <summary>A clock the test moves by hand (UTC time zone).</summary>
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan by) => _now += by;
    }
}
