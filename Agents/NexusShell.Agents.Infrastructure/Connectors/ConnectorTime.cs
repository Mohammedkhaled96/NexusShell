using System.Globalization;
using NexusShell.Agents.Application.Scheduling;

namespace NexusShell.Agents.Infrastructure.Connectors;

/// <summary>Dates and times the agent passes to calendar tools, in the user's time zone.</summary>
internal static class ConnectorTime
{
    /// <summary>Start of a day: "today", "tomorrow", "yesterday" or a date ("2026-11-24"); else a full time.</summary>
    public static DateTimeOffset DayOrTime(string? text, DateTimeOffset now, TimeZoneInfo zone)
    {
        string value = (text ?? "today").Trim().ToLowerInvariant();
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        DateTime? day = value switch
        {
            "" or "today" or "now" => today,
            "tomorrow" => today.AddDays(1),
            "yesterday" => today.AddDays(-1),
            _ => DateTime.TryParseExact(value, ["yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null,
        };
        return day is { } d ? Local(d, zone) : ScheduleParser.ParseStart(text, now, zone);
    }

    /// <summary>An event start: "2026-11-24 14:00", "tomorrow 09:30", "15:00", "in 2 hours".</summary>
    public static DateTimeOffset Start(string text, DateTimeOffset now, TimeZoneInfo zone)
    {
        try
        {
            return ScheduleParser.ParseStart(text, now, zone);
        }
        catch (ArgumentException ex)
        {
            throw new ConnectorException(ex.Message);
        }
    }

    public static string Describe(DateTimeOffset start, DateTimeOffset? end, TimeZoneInfo zone)
    {
        var s = TimeZoneInfo.ConvertTime(start, zone);
        string text = s.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
        if (end is { } e) text += "-" + TimeZoneInfo.ConvertTime(e, zone).ToString(TimeZoneInfo.ConvertTime(e, zone).Date == s.Date ? "HH:mm" : "ddd d MMM HH:mm", CultureInfo.InvariantCulture);
        return text;
    }

    public static string Rfc3339(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static DateTimeOffset Local(DateTime wallClock, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
