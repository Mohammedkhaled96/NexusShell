using System.Globalization;

namespace NexusShell.Agents.Domain.Scheduling;

public enum RepeatKind
{
    /// <summary>Runs once at <see cref="TaskSchedule.StartAt"/>.</summary>
    Once,

    /// <summary>Every <see cref="TaskSchedule.IntervalMinutes"/> minutes from the start time.</summary>
    EveryMinutes,

    Daily,

    /// <summary>Monday to Friday.</summary>
    Weekdays,

    /// <summary>On <see cref="TaskSchedule.Days"/> (default: the start day).</summary>
    Weekly,
}

/// <summary>When a scheduled task runs. Times are wall-clock times in the user's time zone.</summary>
public sealed record TaskSchedule
{
    public const int MinimumIntervalMinutes = 5;

    public RepeatKind Repeat { get; init; } = RepeatKind.Once;

    /// <summary>First run; its time of day is used for daily, weekday and weekly repeats.</summary>
    public DateTimeOffset StartAt { get; init; }

    public int IntervalMinutes { get; init; } = 60;

    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];

    /// <summary>The first run strictly after <paramref name="after"/> (and not before the start), or null when there is none.</summary>
    public DateTimeOffset? NextAfter(DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        switch (Repeat)
        {
            case RepeatKind.Once:
                return StartAt > after ? StartAt : null;

            case RepeatKind.EveryMinutes:
            {
                var interval = TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes));
                if (StartAt > after) return StartAt;
                long steps = (after - StartAt).Ticks / interval.Ticks + 1;
                return StartAt + TimeSpan.FromTicks(interval.Ticks * steps);
            }

            default:
            {
                var timeOfDay = TimeZoneInfo.ConvertTime(StartAt, zone).TimeOfDay;
                var from = after < StartAt ? StartAt.AddTicks(-1) : after;
                var fromLocal = TimeZoneInfo.ConvertTime(from, zone);
                for (int day = 0; day <= 8; day++)
                {
                    var date = fromLocal.Date.AddDays(day);
                    if (!RunsOn(date.DayOfWeek, zone)) continue;

                    var local = DateTime.SpecifyKind(date + timeOfDay, DateTimeKind.Unspecified);
                    if (zone.IsInvalidTime(local)) local = local.AddHours(1); // skipped by a daylight-saving change
                    var candidate = new DateTimeOffset(local, zone.GetUtcOffset(local));
                    if (candidate > from) return candidate;
                }
                return null;
            }
        }
    }

    private bool RunsOn(DayOfWeek day, TimeZoneInfo zone) => Repeat switch
    {
        RepeatKind.Daily => true,
        RepeatKind.Weekdays => day is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        RepeatKind.Weekly => Days.Count > 0 ? Days.Contains(day) : day == TimeZoneInfo.ConvertTime(StartAt, zone).DayOfWeek,
        _ => false,
    };

    /// <summary>Plain English, e.g. "every weekday at 09:00", "once on Tuesday 24 November 2026 at 18:30".</summary>
    public string Describe(TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(StartAt, zone);
        var culture = CultureInfo.InvariantCulture;
        string time = local.ToString("HH:mm", culture);
        return Repeat switch
        {
            RepeatKind.Once => $"once on {local.ToString("dddd d MMMM yyyy", culture)} at {time}",
            RepeatKind.EveryMinutes => IntervalMinutes % 60 == 0
                ? $"every {(IntervalMinutes == 60 ? "hour" : $"{IntervalMinutes / 60} hours")} from {local.ToString("d MMMM HH:mm", culture)}"
                : $"every {IntervalMinutes} minutes from {local.ToString("d MMMM HH:mm", culture)}",
            RepeatKind.Daily => $"every day at {time}",
            RepeatKind.Weekdays => $"every weekday (Monday to Friday) at {time}",
            RepeatKind.Weekly => $"every {JoinDays(Days.Count > 0 ? Days : [local.DayOfWeek])} at {time}",
            _ => time,
        };
    }

    private static string JoinDays(IReadOnlyList<DayOfWeek> days)
    {
        var names = days.Distinct().OrderBy(d => ((int)d + 6) % 7).Select(d => d.ToString()).ToList();
        return names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
    }
}

public enum TaskRunStatus
{
    Never,
    Running,
    Succeeded,
    Failed,

    /// <summary>NexusShell was not running at the time and the task is set not to catch up.</summary>
    Missed,
}

/// <summary>
/// A message the agent sends itself at a set time (once or repeating), e.g. "every weekday at
/// 09:00 check free disk space and list folders over 10 GB". Each run is saved as a chat the
/// user can open and continue. Runs are unattended: actions that need approval are not taken.
/// </summary>
public sealed record ScheduledTask
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>What the agent is asked to do.</summary>
    public required string Prompt { get; init; }

    public bool Enabled { get; init; } = true;

    public TaskSchedule Schedule { get; init; } = new();

    public DateTimeOffset? NextRunAt { get; init; }

    public DateTimeOffset? LastRunAt { get; init; }

    public TaskRunStatus LastStatus { get; init; } = TaskRunStatus.Never;

    /// <summary>Start of the last answer or the error, for the list.</summary>
    public string? LastMessage { get; init; }

    /// <summary>Chat holding the last run's result.</summary>
    public string? LastChatId { get; init; }

    // Agent choices for the run (null = automatic).
    public string? Model { get; init; }

    public string? Expert { get; init; }

    /// <summary>Ask, AutoApproveEdits or ReadOnly. Unattended runs never get approval for commands.</summary>
    public string Permission { get; init; } = "Ask";

    public string? WorkingDirectory { get; init; }

    /// <summary>Continue one chat on every run (the agent remembers earlier runs) instead of a new chat each time.</summary>
    public bool SameChat { get; init; }

    /// <summary>The chat continued by every run when <see cref="SameChat"/> is on.</summary>
    public string? ChatId { get; init; }

    /// <summary>When NexusShell was closed at the time, run once as soon as it starts.</summary>
    public bool RunIfMissed { get; init; } = true;

    /// <summary>"user" or "agent".</summary>
    public string CreatedBy { get; init; } = "user";

    public DateTimeOffset CreatedAt { get; init; }
}
