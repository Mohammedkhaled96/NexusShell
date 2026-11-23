using System.Globalization;
using System.Text.RegularExpressions;
using NexusShell.Agents.Domain.Scheduling;

namespace NexusShell.Agents.Application.Scheduling;

/// <summary>
/// Reads the "when" and "repeat" the agent (or a command) gives, in the user's time zone:
/// <list type="bullet">
/// <item>when: "2026-11-24 09:00", "2026-11-24T09:00", "09:00" (today, or tomorrow if passed), "tomorrow 18:30", "in 30 minutes", "in 2 hours", "now".</item>
/// <item>repeat: "once", "daily" / "every day", "weekdays", "weekly" (with days), "hourly", "every 15 minutes", "every 2 hours".</item>
/// </list>
/// </summary>
public static partial class ScheduleParser
{
    [GeneratedRegex(@"^in\s+(\d+)\s*(minute|minutes|min|mins|hour|hours|h)$", RegexOptions.IgnoreCase)]
    private static partial Regex Relative();

    [GeneratedRegex(@"^(?:every\s+)?(\d+)\s*(minute|minutes|min|mins|hour|hours|h)$", RegexOptions.IgnoreCase)]
    private static partial Regex Interval();

    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss",
        "yyyy/MM/dd HH:mm", "dd/MM/yyyy HH:mm",
    ];

    private static readonly string[] TimeFormats = ["HH:mm", "H:mm", "h:mm tt", "h tt", "htt", "h:mmtt"];

    public static DateTimeOffset ParseStart(string? when, DateTimeOffset now, TimeZoneInfo zone)
    {
        string text = (when ?? string.Empty).Trim();
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        if (text.Length == 0 || text.Equals("now", StringComparison.OrdinalIgnoreCase)) return now.AddMinutes(1);

        var relative = Relative().Match(text);
        if (relative.Success)
        {
            int amount = int.Parse(relative.Groups[1].Value, CultureInfo.InvariantCulture);
            return relative.Groups[2].Value.StartsWith('h') ? now.AddHours(amount) : now.AddMinutes(amount);
        }

        if (DateTime.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return Local(exact, zone);

        bool tomorrow = text.StartsWith("tomorrow", StringComparison.OrdinalIgnoreCase);
        string timeText = tomorrow ? text["tomorrow".Length..].Trim().TrimStart('a', 't').Trim() : text;
        if (DateTime.TryParseExact(timeText, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            var candidate = Local(localNow.Date.AddDays(tomorrow ? 1 : 0) + time.TimeOfDay, zone);
            return !tomorrow && candidate <= now ? Local(localNow.Date.AddDays(1) + time.TimeOfDay, zone) : candidate;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose))
            return Local(loose, zone);

        throw new ArgumentException($"I could not read the time \"{text}\". Use for example 2026-11-24 09:00, 18:30, tomorrow 08:00 or in 30 minutes.");
    }

    public static TaskSchedule ParseSchedule(DateTimeOffset start, string? repeat, string? days)
    {
        string text = (repeat ?? "once").Trim().ToLowerInvariant();
        var schedule = new TaskSchedule { StartAt = start };
        switch (text)
        {
            case "" or "once" or "one time" or "none" or "no":
                return schedule with { Repeat = RepeatKind.Once };
            case "daily" or "every day" or "everyday":
                return schedule with { Repeat = RepeatKind.Daily };
            case "weekdays" or "every weekday" or "workdays" or "business days":
                return schedule with { Repeat = RepeatKind.Weekdays };
            case "weekly" or "every week":
                return schedule with { Repeat = RepeatKind.Weekly, Days = ParseDays(days) };
            case "hourly" or "every hour":
                return schedule with { Repeat = RepeatKind.EveryMinutes, IntervalMinutes = 60 };
        }

        var interval = Interval().Match(text);
        if (interval.Success)
        {
            int amount = int.Parse(interval.Groups[1].Value, CultureInfo.InvariantCulture);
            int minutes = interval.Groups[2].Value.StartsWith('h') ? amount * 60 : amount;
            if (minutes < TaskSchedule.MinimumIntervalMinutes)
                throw new ArgumentException($"Repeat at most every {TaskSchedule.MinimumIntervalMinutes} minutes.");
            return schedule with { Repeat = RepeatKind.EveryMinutes, IntervalMinutes = minutes };
        }

        // "every monday and thursday"
        var named = ParseDays(text.Replace("every", string.Empty, StringComparison.Ordinal));
        if (named.Count > 0) return schedule with { Repeat = RepeatKind.Weekly, Days = named };

        throw new ArgumentException($"I could not read the repeat \"{repeat}\". Use once, daily, weekdays, weekly, hourly or every N minutes.");
    }

    internal static IReadOnlyList<DayOfWeek> ParseDays(string? days)
    {
        if (string.IsNullOrWhiteSpace(days)) return [];
        var result = new List<DayOfWeek>();
        foreach (string part in days.Split([',', ' ', ';', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length < 3) continue; // "and", "on" ...
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                if (day.ToString().StartsWith(part[..3], StringComparison.OrdinalIgnoreCase) && !result.Contains(day))
                    result.Add(day);
            }
        }
        return result;
    }

    private static DateTimeOffset Local(DateTime wallClock, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
