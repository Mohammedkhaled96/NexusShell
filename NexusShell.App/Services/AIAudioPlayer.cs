using System;
using System.IO;
using System.Media;

namespace NexusShell.App.Services
{
    /// <summary>
    /// Plays Wait.wav in a loop while an AI request is in-flight and stops it
    /// when the response is returned.  Thread-safe; safe to call from any thread.
    /// </summary>
    internal static class AIAudioPlayer
    {
        private static SoundPlayer? _player;
        private static readonly object _lock = new object();

        private static readonly string _audioPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Audios", "Wait.wav");

        /// <summary>Starts looping Wait.wav.  No-op if the file is missing.</summary>
        public static void Play()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_audioPath)) return;

                    // Stop + dispose any previous instance before creating a new one.
                    _player?.Stop();
                    _player?.Dispose();

                    _player = new SoundPlayer(_audioPath);
                    _player.PlayLooping();
                }
                catch
                {
                    // Audio is non-critical — swallow all playback errors silently.
                }
            }
        }

        /// <summary>Stops the looping audio started by <see cref="Play"/>.</summary>
        public static void Stop()
        {
            lock (_lock)
            {
                try
                {
                    _player?.Stop();
                    _player?.Dispose();
                    _player = null;
                }
                catch
                {
                    // Swallow — audio is non-critical.
                }
            }
        }
    }
}
