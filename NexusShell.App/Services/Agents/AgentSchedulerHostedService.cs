using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Scheduling;
using NexusShell.Agents.Domain.Scheduling;
using NexusShell.App.Interfaces;
using NexusShell.App.Services;

namespace NexusShell.App.Services.Agents
{
    /// <summary>
    /// Runs NexusAgent's scheduled tasks for as long as NexusShell is open, and tells the user
    /// when one finishes: spoken by the screen reader, a sound, a Windows notification, and the
    /// result appears in every chat window's chat history.
    /// </summary>
    public sealed class AgentSchedulerHostedService : IHostedService
    {
        private readonly IAgentScheduler _scheduler;
        private readonly IScreenReaderAnnouncer _announcer;
        private readonly ISoundService _sound;
        private readonly INotificationService _notifications;
        private readonly ILogger<AgentSchedulerHostedService> _logger;

        public AgentSchedulerHostedService(
            IAgentScheduler scheduler,
            IScreenReaderAnnouncer announcer,
            ISoundService sound,
            INotificationService notifications,
            ILogger<AgentSchedulerHostedService> logger)
        {
            _scheduler = scheduler;
            _announcer = announcer;
            _sound = sound;
            _notifications = notifications;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _scheduler.TaskStarted += OnStarted;
            _scheduler.TaskCompleted += OnCompleted;
            try
            {
                await _scheduler.StartAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // Scheduling is optional: NexusShell keeps working without it.
                _logger.LogError(ex, "The scheduler could not start.");
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _scheduler.TaskStarted -= OnStarted;
            _scheduler.TaskCompleted -= OnCompleted;
            await _scheduler.StopAsync(cancellationToken);
        }

        private void OnStarted(ScheduledTask task) =>
            Speak($"Scheduled task {task.Name} started.");

        private void OnCompleted(ScheduledRunResult result)
        {
            string title = result.Succeeded
                ? $"Scheduled task finished: {result.Task.Name}"
                : $"Scheduled task failed: {result.Task.Name}";
            string firstLine = result.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

            Speak($"{title}. {Shorten(firstLine, 200)} Open it from Chat history.");
            _sound.Play(result.Succeeded ? AppSound.Notify : AppSound.Error);
            _notifications.NotifyScheduledTask(title, firstLine);
            ChatHub.RaiseChatsChanged(this);
        }

        private void Speak(string text)
        {
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is null) return;
                dispatcher.BeginInvoke(() => _announcer.Announce(text));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Scheduled task announcement failed.");
            }
        }

        private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "...";
    }
}
