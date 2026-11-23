using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Abstractions.Chats;
using NexusShell.Agents.Application.Chats;
using NexusShell.Agents.Application.Conversations;
using NexusShell.Agents.Domain.Chats;
using NexusShell.Agents.Domain.Scheduling;
using NexusShell.Agents.Domain.Secrets;

namespace NexusShell.Agents.Application.Scheduling;

/// <summary>What to schedule (the id, dates and run state are assigned by the scheduler).</summary>
public sealed record ScheduledTaskDraft(
    string Name,
    string Prompt,
    TaskSchedule Schedule,
    string Permission = "Ask",
    string? Model = null,
    string? Expert = null,
    string? WorkingDirectory = null,
    bool SameChat = false,
    bool RunIfMissed = true,
    string CreatedBy = "user");

/// <summary>Outcome of one run.</summary>
public sealed record ScheduledRunResult(ScheduledTask Task, bool Succeeded, string Message, string? ChatId);

/// <summary>
/// Runs scheduled tasks while NexusShell is open. Each run is an unattended agent conversation
/// (actions that need approval are declined, never taken silently) and is saved as a chat the
/// user can open from the chat history and continue. Runs missed while NexusShell was closed
/// happen once at the next start (when the task allows it).
/// </summary>
public interface IAgentScheduler
{
    event Action<ScheduledTask>? TaskStarted;

    event Action<ScheduledRunResult>? TaskCompleted;

    /// <summary>The list of tasks changed (added, edited, deleted, run).</summary>
    event Action? TasksChanged;

    TimeZoneInfo TimeZone { get; }

    Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken cancellationToken = default);

    /// <exception cref="ArgumentException">The draft is invalid (message is for the user).</exception>
    Task<ScheduledTask> AddAsync(ScheduledTaskDraft draft, CancellationToken cancellationToken = default);

    Task<ScheduledTask?> UpdateAsync(string id, ScheduledTaskDraft draft, CancellationToken cancellationToken = default);

    Task<bool> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    Task<ScheduledRunResult?> RunNowAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Loads the tasks, handles runs missed while closed and starts the clock.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

internal sealed class AgentScheduler(
    IScheduledTaskStore store,
    IServiceProvider services,
    IChatLibrary chats,
    TimeProvider time,
    ILogger<AgentScheduler> logger) : IAgentScheduler, IAsyncDisposable
{
    internal const int MaxTasks = 200;
    internal const int MaxPromptChars = 4000;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(20);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private List<ScheduledTask>? _tasks;
    private CancellationTokenSource? _stop;
    private Task? _loop;

    public event Action<ScheduledTask>? TaskStarted;

    public event Action<ScheduledRunResult>? TaskCompleted;

    public event Action? TasksChanged;

    public TimeZoneInfo TimeZone => time.LocalTimeZone;

    public async Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadAsync(cancellationToken).ConfigureAwait(false))
                .OrderByDescending(t => t.Enabled)
                .ThenBy(t => t.NextRunAt ?? DateTimeOffset.MaxValue)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ScheduledTask> AddAsync(ScheduledTaskDraft draft, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var next = Validate(draft, now);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ScheduledTask task;
        try
        {
            var tasks = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (tasks.Count >= MaxTasks) throw new ArgumentException($"There are already {MaxTasks} scheduled tasks. Delete some first.");
            task = Apply(new ScheduledTask
            {
                Id = "t" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant(),
                Name = draft.Name.Trim(),
                Prompt = draft.Prompt.Trim(),
                CreatedAt = now,
                CreatedBy = draft.CreatedBy,
            }, draft) with { NextRunAt = next, Enabled = true };
            tasks.Add(task);
            await store.SaveAsync(tasks, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        logger.LogInformation("Scheduled task {Id} added by {Who}: {Schedule}, next run {Next}.", task.Id, task.CreatedBy, task.Schedule.Describe(TimeZone), task.NextRunAt);
        TasksChanged?.Invoke();
        return task;
    }

    public async Task<ScheduledTask?> UpdateAsync(string id, ScheduledTaskDraft draft, CancellationToken cancellationToken = default)
    {
        var next = Validate(draft, time.GetUtcNow());
        var updated = await MutateAsync(id, t => Apply(t, draft) with { Name = draft.Name.Trim(), Prompt = draft.Prompt.Trim(), NextRunAt = next, Enabled = true }, cancellationToken).ConfigureAwait(false);
        if (updated is not null) TasksChanged?.Invoke();
        return updated;
    }

    public async Task<bool> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var updated = await MutateAsync(id, t => t with
        {
            Enabled = enabled,
            NextRunAt = enabled ? t.Schedule.NextAfter(now, TimeZone) : t.NextRunAt,
        }, cancellationToken).ConfigureAwait(false);
        if (updated is not null) TasksChanged?.Invoke();
        return updated is not null;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        int removed;
        try
        {
            var tasks = await LoadAsync(cancellationToken).ConfigureAwait(false);
            removed = tasks.RemoveAll(t => string.Equals(t.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed > 0) await store.SaveAsync(tasks, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        if (removed > 0)
        {
            logger.LogInformation("Scheduled task {Id} deleted.", id);
            TasksChanged?.Invoke();
        }
        return removed > 0;
    }

    public async Task<ScheduledRunResult?> RunNowAsync(string id, CancellationToken cancellationToken = default)
    {
        var task = (await ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        return task is null ? null : await RunAsync(task, manual: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null) return;
        var now = time.GetUtcNow();

        // Runs missed while NexusShell was closed: catch up once, or move on to the next time.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tasks = await LoadAsync(cancellationToken).ConfigureAwait(false);
            bool changed = false;
            for (int i = 0; i < tasks.Count; i++)
            {
                var task = tasks[i];
                if (task.LastStatus == TaskRunStatus.Running)
                {
                    tasks[i] = task = task with { LastStatus = TaskRunStatus.Failed, LastMessage = "NexusShell closed during the run." };
                    changed = true;
                }
                if (!task.Enabled || task.NextRunAt is not { } due || due > now || task.RunIfMissed) continue;
                tasks[i] = task with
                {
                    LastStatus = TaskRunStatus.Missed,
                    LastMessage = $"Missed at {TimeZoneInfo.ConvertTime(due, TimeZone):dd MMM HH:mm} (NexusShell was closed).",
                    NextRunAt = task.Schedule.NextAfter(now, TimeZone),
                    Enabled = task.Schedule.Repeat != RepeatKind.Once,
                };
                changed = true;
            }
            if (changed) await store.SaveAsync(tasks, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
        logger.LogInformation("Scheduler started.");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_stop is null || _loop is null) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }
        _loop = null;
        logger.LogInformation("Scheduler stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop?.Dispose();
    }

    private async Task LoopAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Tick, time);
        do
        {
            try
            {
                await RunDueAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduler tick failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
    }

    /// <summary>Runs every enabled task whose time has come (one at a time).</summary>
    internal async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var due = (await ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(t => t.Enabled && t.NextRunAt is { } next && next <= now && t.LastStatus != TaskRunStatus.Running)
            .ToList();
        foreach (var task in due)
            await RunAsync(task, manual: false, cancellationToken).ConfigureAwait(false);
        return due.Count;
    }

    private async Task<ScheduledRunResult> RunAsync(ScheduledTask task, bool manual, CancellationToken cancellationToken)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var startedAt = time.GetUtcNow();
            task = await MutateAsync(task.Id, t => t with { LastStatus = TaskRunStatus.Running }, cancellationToken).ConfigureAwait(false) ?? task;
            TaskStarted?.Invoke(task);
            logger.LogInformation("Scheduled task {Id} ({Name}) started{Manual}.", task.Id, task.Name, manual ? " by the user" : string.Empty);

            bool succeeded;
            string message;
            string? chatId = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RunTimeout);
            try
            {
                (chatId, message) = await ExecuteAsync(task, startedAt, timeout.Token).ConfigureAwait(false);
                succeeded = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await MutateAsync(task.Id, t => t with { LastStatus = TaskRunStatus.Failed, LastMessage = "Stopped: NexusShell is closing." }, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                succeeded = false;
                message = ex is OperationCanceledException ? $"Stopped after {RunTimeout.TotalMinutes:0} minutes." : ex.Message;
                logger.LogWarning(ex, "Scheduled task {Id} failed.", task.Id);
            }

            var now = time.GetUtcNow();
            var finished = await MutateAsync(task.Id, t => t with
            {
                LastRunAt = startedAt,
                LastStatus = succeeded ? TaskRunStatus.Succeeded : TaskRunStatus.Failed,
                LastMessage = ChatLibrary.Shorten(message, 300),
                LastChatId = chatId ?? t.LastChatId,
                ChatId = t.SameChat ? chatId ?? t.ChatId : t.ChatId,
                // A manual "run now" keeps the planned time; a scheduled run moves on to the next one.
                NextRunAt = manual && t.NextRunAt > now ? t.NextRunAt : t.Schedule.NextAfter(now, TimeZone),
                Enabled = t.Enabled && (manual || t.Schedule.Repeat != RepeatKind.Once),
            }, CancellationToken.None).ConfigureAwait(false) ?? task;

            var result = new ScheduledRunResult(finished, succeeded, message, chatId);
            logger.LogInformation("Scheduled task {Id} {Outcome}; next run {Next}.", task.Id, succeeded ? "finished" : "failed", finished.NextRunAt);
            TaskCompleted?.Invoke(result);
            TasksChanged?.Invoke();
            return result;
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>One unattended agent conversation; the result is saved as a chat. Returns (chat id, answer).</summary>
    private async Task<(string ChatId, string Answer)> ExecuteAsync(ScheduledTask task, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var options = new ConversationOptions
        {
            Model = task.Model,
            Expert = task.Expert,
            Permission = Enum.TryParse<PermissionMode>(task.Permission, true, out var mode) ? mode : PermissionMode.Ask,
            WorkingDirectory = task.WorkingDirectory,
            Unattended = true,
        };

        SavedChat? chat = task.SameChat && task.ChatId is { } existing
            ? await chats.OpenAsync(existing, cancellationToken).ConfigureAwait(false)
            : null;

        // Resolved when needed: the agent's tool catalog contains the schedule tools, which use this scheduler.
        var agentService = services.GetRequiredService<INexusAgentService>();
        var conversation = await agentService.StartConversationAsync(
            null, options, chat is null ? null : ConversationResume.FromChat(chat), cancellationToken).ConfigureAwait(false);

        string request = task.Prompt +
            "\n\n(This is the scheduled task \"" + task.Name + "\", running automatically without the user watching. " +
            "Actions that need the user's approval cannot be taken now: say clearly what still needs them.)";
        var result = await conversation.SendAsync(request, progress: null, cancellationToken).ConfigureAwait(false);

        var credits = result.Experts.Where(e => e.Succeeded).Select(e => string.IsNullOrWhiteSpace(e.Model) ? e.ExpertName : $"{e.ExpertName} with {e.Model}").ToList();
        string answer = result.Text + (credits.Count > 0 ? "\n\n(Answered by: " + string.Join(", ", credits) + ")" : string.Empty);
        var now = time.GetUtcNow();
        var messages = (chat?.Messages ?? []).ToList();
        messages.Add(new SavedChatMessage("note", "Scheduled task", $"Scheduled task \"{task.Name}\" ran at {TimeZoneInfo.ConvertTime(startedAt, TimeZone):dddd d MMMM HH:mm}.", startedAt));
        messages.Add(new SavedChatMessage("user", "Scheduled task", task.Prompt, startedAt));
        messages.Add(new SavedChatMessage("assistant", "NexusAgent", answer, now));

        var summary = (chat?.Summary ?? new ChatSummary
        {
            Id = chats.NewId(),
            Title = task.SameChat ? $"Scheduled: {task.Name}" : $"Scheduled: {task.Name} ({TimeZoneInfo.ConvertTime(startedAt, TimeZone):d MMM HH:mm})",
            TitleSetByUser = true,
            CreatedAt = startedAt,
        }) with
        {
            ScheduledTaskId = task.Id,
            Model = task.Model,
            Expert = task.Expert,
            Permission = task.Permission,
            WorkingDirectory = task.WorkingDirectory,
        };

        var saved = await chats.SaveAsync(new SavedChat
        {
            Summary = summary,
            Messages = messages,
            AgentState = await conversation.ExportStateAsync(cancellationToken).ConfigureAwait(false),
        }, cancellationToken).ConfigureAwait(false);

        return (saved.Id, result.Text);
    }

    private DateTimeOffset Validate(ScheduledTaskDraft draft, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Name)) throw new ArgumentException("Give the task a name.");
        if (draft.Name.Trim().Length > 100) throw new ArgumentException("Keep the name under 100 characters.");
        if (string.IsNullOrWhiteSpace(draft.Prompt)) throw new ArgumentException("Write what the agent should do.");
        if (draft.Prompt.Trim().Length > MaxPromptChars) throw new ArgumentException($"Keep the instructions under {MaxPromptChars} characters.");
        if (SecretPatterns.LooksLikeSecret(draft.Prompt) || SecretPatterns.LooksLikeSecret(draft.Name))
            throw new ArgumentException("Do not put passwords, keys or tokens in a scheduled task. Store keys in environment variables.");
        if (draft.Schedule.Repeat == RepeatKind.EveryMinutes && draft.Schedule.IntervalMinutes < TaskSchedule.MinimumIntervalMinutes)
            throw new ArgumentException($"Repeat at most every {TaskSchedule.MinimumIntervalMinutes} minutes.");
        if (draft.WorkingDirectory is { Length: > 0 } folder && !Directory.Exists(folder))
            throw new ArgumentException($"The folder {folder} does not exist.");

        // A start a moment in the past still counts (the clock moves while the user fills the form).
        var from = draft.Schedule.Repeat == RepeatKind.Once && draft.Schedule.StartAt > now.AddMinutes(-2) && draft.Schedule.StartAt <= now
            ? draft.Schedule.StartAt.AddTicks(-1)
            : now;
        return draft.Schedule.NextAfter(from, TimeZone)
            ?? throw new ArgumentException("That time has already passed. Choose a time in the future.");
    }

    private static ScheduledTask Apply(ScheduledTask task, ScheduledTaskDraft draft) => task with
    {
        Schedule = draft.Schedule,
        Permission = Enum.TryParse<PermissionMode>(draft.Permission, true, out var mode) ? mode.ToString() : nameof(PermissionMode.Ask),
        Model = string.IsNullOrWhiteSpace(draft.Model) || draft.Model.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : draft.Model.Trim(),
        Expert = string.IsNullOrWhiteSpace(draft.Expert) || draft.Expert.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : draft.Expert.Trim(),
        WorkingDirectory = string.IsNullOrWhiteSpace(draft.WorkingDirectory) ? null : draft.WorkingDirectory.Trim(),
        SameChat = draft.SameChat,
        RunIfMissed = draft.RunIfMissed,
    };

    private async Task<ScheduledTask?> MutateAsync(string id, Func<ScheduledTask, ScheduledTask> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tasks = await LoadAsync(cancellationToken).ConfigureAwait(false);
            int index = tasks.FindIndex(t => string.Equals(t.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0) return null;
            tasks[index] = change(tasks[index]);
            await store.SaveAsync(tasks, cancellationToken).ConfigureAwait(false);
            return tasks[index];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<ScheduledTask>> LoadAsync(CancellationToken cancellationToken) =>
        _tasks ??= (await store.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
}
