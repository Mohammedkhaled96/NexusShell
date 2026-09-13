using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;

namespace NexusShell.App.Services
{
    /// <summary>
    /// Polyphonic UI sound-effects player. Each <see cref="AppSound"/> maps to a WAV
    /// in the Audios folder and gets its own <see cref="MediaPlayer"/>, so distinct
    /// effects overlap instead of cutting each other off (the monophonic
    /// <see cref="System.Media.SoundPlayer"/> used elsewhere cannot do this).
    ///
    /// <para>MediaPlayer has Dispatcher affinity, so every player is created and driven
    /// on the UI thread; <see cref="Play"/> may be called from any thread and marshals
    /// internally. All playback errors are swallowed — audio must never break the app.</para>
    /// </summary>
    public sealed class SoundService : ISoundService
    {
        private readonly ILogger<SoundService> _logger;
        private readonly string _audioDir = Path.Combine(AppContext.BaseDirectory, "Audios");

        // Touched only on the UI thread.
        private readonly Dictionary<AppSound, MediaPlayer> _players = new();

        // Touched from any thread — guarded by _gate.
        private readonly Dictionary<AppSound, long> _lastPlayTicks = new();
        private readonly object _gate = new object();

        private volatile bool _enabled = true;
        private double _volume = 0.6;

        // file name, per-sound relative loudness (0..1), and minimum replay gap in ms
        // (0 = no throttle). Quiet/high-frequency effects (key, output) are scaled down.
        private static readonly Dictionary<AppSound, (string File, double Scale, int MinGapMs)> Map = new()
        {
            [AppSound.KeyType]          = ("key.wav",      0.55, 0),
            [AppSound.CommandSent]      = ("send.wav",     0.90, 0),
            [AppSound.CommandCompleted] = ("complete.wav", 1.00, 0),
            [AppSound.Output]           = ("output.wav",   0.40, 220),
            [AppSound.OpenManager]      = ("manager.wav",  0.90, 0),
            [AppSound.OpenAI]           = ("ai.wav",       0.95, 0),
            [AppSound.OpenSettings]     = ("settings.wav", 0.90, 0),
            [AppSound.Toggle]           = ("toggle.wav",   0.70, 30),
            // 150 ms gap collapses the button-click + programmatic/Closed-handler
            // pair into a single tone (a Cancel button and the window's own Closed
            // event would otherwise both fire CloseWindow back-to-back).
            [AppSound.CloseWindow]      = ("close.wav",    0.85, 150),
            [AppSound.Error]            = ("error.wav",    0.95, 250),
            [AppSound.NewTab]           = ("tab.wav",      0.85, 150),
            [AppSound.Notify]           = ("notify.wav",   0.95, 0),
        };

        public SoundService(ILogger<SoundService> logger)
        {
            _logger = logger;
        }

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public double Volume
        {
            get { lock (_gate) { return _volume; } }
            set { lock (_gate) { _volume = Math.Clamp(value, 0.0, 1.0); } }
        }

        public void ApplySettings(AppSettings settings)
        {
            if (settings == null) return;
            Enabled = settings.EnableSoundEffects;
            Volume  = settings.SoundEffectsVolume;
        }

        public void Preload()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            dispatcher.BeginInvoke(() =>
            {
                foreach (var sound in Map.Keys)
                {
                    GetOrCreatePlayer(sound);
                }
            }, DispatcherPriority.Background);
        }

        public void Play(AppSound sound)
        {
            if (!_enabled) return;
            if (!Map.TryGetValue(sound, out var info)) return;

            // Throttle on the CALLING thread, before dispatching, so a flood of
            // background output events cannot swamp the UI dispatcher queue.
            if (info.MinGapMs > 0)
            {
                long now = Environment.TickCount64;
                lock (_gate)
                {
                    if (_lastPlayTicks.TryGetValue(sound, out var last) && now - last < info.MinGapMs)
                        return;
                    _lastPlayTicks[sound] = now;
                }
            }

            double master;
            lock (_gate) { master = _volume; }
            double vol = Math.Clamp(master * info.Scale, 0.0, 1.0);

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var player = GetOrCreatePlayer(sound);
                    if (player == null) return;
                    player.Volume = vol;
                    player.Position = TimeSpan.Zero; // rewind so rapid retriggers replay
                    player.Play();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Sound playback failed for {Sound}.", sound);
                }
            }, DispatcherPriority.Normal);
        }

        // MUST be called on the UI thread (MediaPlayer has Dispatcher affinity).
        private MediaPlayer? GetOrCreatePlayer(AppSound sound)
        {
            if (_players.TryGetValue(sound, out var existing)) return existing;
            if (!Map.TryGetValue(sound, out var info)) return null;

            string path = Path.Combine(_audioDir, info.File);
            if (!File.Exists(path))
            {
                _logger.LogDebug("Sound file missing: {Path}", path);
                return null;
            }

            try
            {
                var player = new MediaPlayer();
                player.Open(new Uri(path, UriKind.Absolute));
                _players[sound] = player;
                return player;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to open sound {Sound} from {Path}.", sound, path);
                return null;
            }
        }
    }
}
