using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using NexusShell.Agents.Application.Abstractions.Tools;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Domain.Tools;

namespace NexusShell.Agents.Application.Scheduling;

/// <summary>
/// Category "schedule": the agent schedules work for later when the user asks ("every weekday
/// at 9 check the backups", "remind me at 18:00 ..."). Creating or cancelling a schedule always
/// asks the user (it is a standing automation), and is never possible from a scheduled run.
/// </summary>
internal sealed class ScheduleToolProvider(IAgentScheduler scheduler, TimeProvider time) : IAgentToolProvider
{
    public const string CategoryName = "schedule";

    public string Category => CategoryName;

    public IEnumerable<AgentToolDescriptor> GetTools()
    {
        yield return new(AIFunctionFactory.Create(ScheduleAsync, "schedule_task",
            "Schedule a task the agent will do later, once or repeatedly (the user approves it). Use when the user asks to do something at a time or regularly."),
            ToolRisk.Mutating, CategoryName, ForceApproval: true);
        yield return new(AIFunctionFactory.Create(ListAsync, "list_scheduled_tasks",
            "List the scheduled tasks with their next run and last result."),
            ToolRisk.ReadOnly, CategoryName, SkipApproval: true);
        yield return new(AIFunctionFactory.Create(CancelAsync, "cancel_scheduled_task",
            "Delete a scheduled task by its id (the user approves it)."),
            ToolRisk.Mutating, CategoryName, ForceApproval: true);
    }

    private async Task<string> ScheduleAsync(
        [Description("Short name, e.g. \"Morning disk check\".")] string name,
        [Description("What the agent should do at that time, as a complete instruction.")] string instructions,
        [Description("When to start, in the user's local time: \"2026-11-24 09:00\", \"18:30\", \"tomorrow 08:00\" or \"in 30 minutes\".")] string start,
        [Description("once, daily, weekdays, weekly, hourly, \"every 30 minutes\" or \"every 2 hours\".")] string repeat = "once",
        [Description("For weekly: days such as \"monday, thursday\".")] string? days = null,
        [Description("ask (default), edits (file edits without asking) or readonly.")] string permission = "ask",
        [Description("True to continue one chat on every run, so the agent remembers earlier runs.")] bool sameChat = false,
        CancellationToken cancellationToken = default)
    {
        if (AgentRunScope.Current?.Unattended == true) return "Scheduled runs cannot create other scheduled tasks.";
        try
        {
            var zone = scheduler.TimeZone;
            var schedule = ScheduleParser.ParseSchedule(ScheduleParser.ParseStart(start, time.GetUtcNow(), zone), repeat, days);
            var task = await scheduler.AddAsync(new ScheduledTaskDraft(
                name,
                instructions,
                schedule,
                permission.Trim().ToLowerInvariant() switch { "edits" or "autoapproveedits" => "AutoApproveEdits", "readonly" or "read-only" or "plan" => "ReadOnly", _ => "Ask" },
                WorkingDirectory: AgentRunScope.Current?.WorkingDirectory,
                SameChat: sameChat,
                CreatedBy: "agent"), cancellationToken).ConfigureAwait(false);

            return $"Scheduled \"{task.Name}\" [{task.Id}]: {task.Schedule.Describe(zone)}. Next run: {Local(task.NextRunAt)}. " +
                   "NexusShell must be running at that time; a missed run happens when it starts. Results are saved as chats in the chat history.";
        }
        catch (ArgumentException ex)
        {
            return "Not scheduled: " + ex.Message;
        }
    }

    private async Task<string> ListAsync(CancellationToken cancellationToken = default)
    {
        var tasks = await scheduler.ListAsync(cancellationToken).ConfigureAwait(false);
        if (tasks.Count == 0) return "There are no scheduled tasks.";
        var sb = new StringBuilder();
        foreach (var task in tasks)
        {
            sb.Append('[').Append(task.Id).Append("] ").Append(task.Name).Append(": ").Append(task.Schedule.Describe(scheduler.TimeZone))
              .Append(task.Enabled ? $"; next {Local(task.NextRunAt)}" : "; turned off")
              .Append("; last result: ").AppendLine(task.LastStatus.ToString().ToLowerInvariant());
        }
        return sb.ToString();
    }

    private async Task<string> CancelAsync(
        [Description("Scheduled task id, e.g. t3f9a2c.")] string id,
        CancellationToken cancellationToken = default)
    {
        if (AgentRunScope.Current?.Unattended == true) return "Scheduled runs cannot change scheduled tasks.";
        return await scheduler.DeleteAsync(id, cancellationToken).ConfigureAwait(false) ? $"Scheduled task {id} deleted." : $"There is no scheduled task {id}.";
    }

    private string Local(DateTimeOffset? when) => when is { } value
        ? TimeZoneInfo.ConvertTime(value, scheduler.TimeZone).ToString("dddd d MMMM yyyy 'at' HH:mm", CultureInfo.InvariantCulture)
        : "none";
}
