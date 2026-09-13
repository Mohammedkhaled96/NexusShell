using NexusShell.App.Models;

namespace NexusShell.App.Interfaces
{
    /// <summary>
    /// Logical UI sound effects. Each value maps to a WAV file in the Audios folder.
    /// </summary>
    public enum AppSound
    {
        KeyType,          // a character typed into the command input
        CommandSent,      // a command was submitted
        CommandCompleted, // a running command finished (prompt returned)
        Output,           // new terminal output arrived (throttled)
        OpenManager,      // a manager / utility window opened
        OpenAI,           // an AI window opened
        OpenSettings,     // the settings window opened
        Toggle,           // a setting was toggled / category changed
        CloseWindow,      // a window closed
        Error,            // an error / failure occurred
        NewTab,           // a new terminal tab was created
        Notify            // generic notification
    }

    /// <summary>
    /// Plays short UI sound effects. Implementations must be safe to call from any
    /// thread and must never throw — audio is purely cosmetic.
    /// </summary>
    public interface ISoundService
    {
        /// <summary>Master on/off switch.</summary>
        bool Enabled { get; set; }

        /// <summary>Master volume, 0.0 – 1.0.</summary>
        double Volume { get; set; }

        /// <summary>Plays the given effect (fire-and-forget, non-blocking).</summary>
        void Play(AppSound sound);

        /// <summary>Pre-opens all sound players so the first play has no latency.</summary>
        void Preload();

        /// <summary>Applies the persisted enable/volume preferences.</summary>
        void ApplySettings(AppSettings settings);
    }
}
