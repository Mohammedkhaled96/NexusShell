using System;

namespace NexusShell.App.Interfaces
{
    /// <summary>
    /// Surface for user-facing system notifications. Implementations should
    /// degrade gracefully on systems where the underlying notification channel
    /// is unavailable (e.g. Server Core, group-policy disabled, missing AUMID).
    /// </summary>
    public interface INotificationService
    {
        /// <summary>
        /// Fires a "command completed" notification with the executed command
        /// text and how long it took. Callers are expected to apply their own
        /// thresholds (don't fire for sub-second commands) and window-focus
        /// checks (don't notify when the user is already watching).
        /// </summary>
        void NotifyCommandCompleted(string command, TimeSpan duration);
    }
}
