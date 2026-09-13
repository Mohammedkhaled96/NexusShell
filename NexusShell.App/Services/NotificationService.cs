using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using NexusShell.App.Interfaces;
using System;

namespace NexusShell.App.Services
{
    /// <summary>
    /// Windows 10/11 native toast notifications via the Action Center.
    /// Uses <see cref="ToastContentBuilder"/> which generates the toast XML and
    /// delivers it through <c>ToastNotificationManagerCompat</c> — the compat
    /// shim auto-registers a per-user AUMID on first show, so no admin rights
    /// or MSIX packaging is required.
    /// </summary>
    public sealed class NotificationService : INotificationService
    {
        private const int MaxCommandLabelChars = 60;

        private readonly ILogger<NotificationService> _logger;

        public NotificationService(ILogger<NotificationService> logger)
        {
            _logger = logger;
        }

        public void NotifyCommandCompleted(string command, TimeSpan duration)
        {
            try
            {
                string label = Truncate(command, MaxCommandLabelChars);
                string body  = $"{label}\nCompleted in {FormatDuration(duration)}";

                new ToastContentBuilder()
                    .AddText("NexusShell — Command Completed")
                    .AddText(body)
                    .AddArgument("action", "focus")
                    .Show();
            }
            catch (Exception ex)
            {
                // Toast subsystem can fail for many environmental reasons
                // (group policy, RDP session, broken AUMID registry). Don't
                // crash the terminal flow over a missed notification.
                _logger.LogWarning(ex, "Could not show command-completed toast notification.");
            }
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) ? string.Empty :
            s.Length <= max ? s : s.Substring(0, max) + "…";

        private static string FormatDuration(TimeSpan d)
        {
            if (d.TotalHours   >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m {d.Seconds}s";
            if (d.TotalMinutes >= 1) return $"{(int)d.TotalMinutes}m {d.Seconds}s";
            return $"{d.TotalSeconds:0.0}s";
        }
    }
}
