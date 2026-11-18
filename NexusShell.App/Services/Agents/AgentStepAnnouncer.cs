using System;
using System.Collections.Generic;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using NexusShell.Agents.Application.Progress;

namespace NexusShell.App.Services.Agents
{
    /// <summary>
    /// Speaks what NexusAgent is doing while the user waits for a reply:
    /// "Message sent", "Choosing the best experts", "Asking Windows Expert",
    /// "Windows Expert: checking system information", approvals, model switches,
    /// "Still working... 30 seconds" during long waits, and "Answer ready".
    /// <para>
    /// Every step is kept, in order. Steps are paced a little apart so screen
    /// readers can queue them without cutting each other off, and an identical step
    /// is never repeated twice in a row. Runs on the UI thread (DispatcherTimer).
    /// </para>
    /// </summary>
    public sealed class AgentStepAnnouncer : IDisposable
    {
        private static readonly TimeSpan MinimumGap = TimeSpan.FromMilliseconds(600);
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

        private readonly Action<string, bool> _speak;
        private readonly Func<bool> _isEnabled;
        private readonly ILogger _logger;
        private readonly Queue<(string Text, bool Important)> _queue = new();
        private readonly DispatcherTimer _pump;
        private readonly DispatcherTimer _heartbeat;

        private DateTime _lastSpokenAt = DateTime.MinValue;
        private DateTime _startedAt;
        private DateTime _lastStepAt;
        private string? _lastQueued;
        private string? _currentStep;
        private bool _active;

        /// <param name="speak">Delivers (text, important) to the screen reader on the UI thread. Important = approvals and the final result.</param>
        /// <param name="isEnabled">Read on every step so the user's setting applies immediately.</param>
        public AgentStepAnnouncer(Action<string, bool> speak, Func<bool> isEnabled, ILogger logger, Dispatcher? dispatcher = null)
        {
            _speak = speak;
            _isEnabled = isEnabled;
            _logger = logger;
            var d = dispatcher ?? Dispatcher.CurrentDispatcher;
            _pump = new DispatcherTimer(MinimumGap, DispatcherPriority.Normal, (_, _) => Pump(), d);
            _pump.Stop();
            _heartbeat = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Heartbeat(), d);
            _heartbeat.Stop();
        }

        public NarrationLanguage Language { get; set; } = NarrationLanguage.English;

        /// <summary>Text of the step in progress (for the visual status line).</summary>
        public string? CurrentStep => _currentStep;

        public void Begin()
        {
            _queue.Clear();
            _pump.Stop();
            _lastQueued = null;
            _currentStep = null;
            _startedAt = _lastStepAt = DateTime.UtcNow;
            _active = true;
            _heartbeat.Start();
            Enqueue(AgentProgressNarrator.Sent(Language));
        }

        /// <summary>Returns the spoken sentence for the step (or null when the step is silent).</summary>
        public string? Report(AgentProgress step)
        {
            string? text = AgentProgressNarrator.Describe(step, Language);
            if (text is null) return null;

            _currentStep = text;
            _lastStepAt = DateTime.UtcNow;
            Enqueue(text, important: step.Kind == AgentProgressKind.ApprovalRequested);
            return text;
        }

        public void Completed(IReadOnlyList<string> experts) => Finish(AgentProgressNarrator.Answered(experts, Language));

        public void Failed() => Finish(AgentProgressNarrator.NoAnswer(Language));

        public void Stopped()
        {
            // The user pressed Stop: drop pending steps, confirm immediately.
            _queue.Clear();
            Finish(AgentProgressNarrator.Stopped(Language));
        }

        public void Dispose()
        {
            _active = false;
            _pump.Stop();
            _heartbeat.Stop();
            _queue.Clear();
        }

        private void Finish(string text)
        {
            _active = false;
            _heartbeat.Stop();
            _currentStep = null;
            Enqueue(text, important: true);
        }

        private void Enqueue(string text, bool important = false)
        {
            if (string.Equals(text, _lastQueued, StringComparison.Ordinal)) return;
            _lastQueued = text;
            if (!_isEnabled()) return;

            _queue.Enqueue((text, important));
            Pump();
        }

        private void Pump()
        {
            if (_queue.Count == 0)
            {
                _pump.Stop();
                return;
            }

            if (DateTime.UtcNow - _lastSpokenAt >= MinimumGap)
            {
                var (text, important) = _queue.Dequeue();
                Speak(text, important);
            }

            if (_queue.Count > 0) _pump.Start();
            else _pump.Stop();
        }

        private void Heartbeat()
        {
            if (!_active) return;
            var now = DateTime.UtcNow;
            if (now - _lastStepAt < HeartbeatInterval) return;

            _lastStepAt = now;
            Enqueue(AgentProgressNarrator.StillWorking(_currentStep, now - _startedAt, Language));
        }

        private void Speak(string text, bool important)
        {
            _lastSpokenAt = DateTime.UtcNow;
            _logger.LogInformation("Agent step: {Step}", text);
            try
            {
                _speak(text, important);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Speaking an agent step failed.");
            }
        }
    }
}
