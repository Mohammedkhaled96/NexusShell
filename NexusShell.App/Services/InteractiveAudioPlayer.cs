using System.IO;
using System.Media;

namespace NexusShell.App.Services
{
    /// <summary>
    /// Plays a short, soft notification sound when the Interactive Screen opens.
    /// Uses <see cref="SoundPlayer"/> with the project's Wait.wav if it is a
    /// valid PCM WAV (> 44 bytes header); otherwise falls back to a quiet
    /// programmatic beep via <see cref="Console.Beep(int, int)"/>.
    /// Thread-safe; safe to call from any thread.
    /// </summary>
    internal static class InteractiveAudioPlayer
    {
        private static readonly string _waitWavPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Audios", "Wait.wav");

        // Minimum valid PCM WAV: 44-byte RIFF header + at least 1 sample.
        private const int MinWavBytes = 45;

        /// <summary>
        /// Plays a short "screen opened" tone.  Non-blocking; errors are swallowed.
        /// </summary>
        public static void PlayOpen()
        {
            try
            {
                // Try Wait.wav first (only if it is a real audio file)
                if (File.Exists(_waitWavPath) &&
                    new FileInfo(_waitWavPath).Length >= MinWavBytes)
                {
                    using var player = new SoundPlayer(_waitWavPath);
                    player.Play();   // async, non-looping, returns immediately
                    return;
                }

                // Fallback: quiet two-note chime (440 Hz × 80 ms, 550 Hz × 80 ms)
                // Console.Beep is blocking but very short; run off the UI thread.
                Task.Run(() =>
                {
                    try
                    {
                        Console.Beep(440, 80);
                        Console.Beep(550, 80);
                    }
                    catch { /* headless / no audio device */ }
                });
            }
            catch
            {
                // Audio is non-critical — swallow all errors.
            }
        }
    }
}
