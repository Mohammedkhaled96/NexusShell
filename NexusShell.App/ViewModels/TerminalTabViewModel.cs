using Microsoft.Extensions.Logging;
using NexusShell.App.Commands;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;
using NexusShell.App.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Documents; // Added for FlowDocument

namespace NexusShell.App.ViewModels
{
    public class TerminalTabViewModel : ViewModelBase, IDisposable
    {
        private readonly ITerminalSession _terminal;
        private readonly ICommandHistory _history;
        private readonly ILogger _logger;
        private readonly IConfigManager _configManager;
        private readonly IAIService _aiService;
        private readonly IWindowService _windowService; // New field
        private readonly INotificationService _notificationService;
        private readonly ISoundService _soundService;
        private readonly IInteractiveScreenService _interactiveScreenService;
        public IInteractiveScreenService InteractiveScreenService => _interactiveScreenService;
        private readonly ShellProfile _profile;
        private readonly string? _workingDirectory;
        private readonly InteractivePromptDetector _promptDetector;

        // ── Command-completion notification (long-running command done while window unfocused) ──
        private static readonly TimeSpan NotifyThreshold = TimeSpan.FromSeconds(5);
        private DateTime _commandStartUtc;
        private string   _runningCommandLabel = string.Empty;

        // ── Idle-watchdog completion detection ──────────────────────────────────
        // The in-stream prompt regex can miss a completion (prompt split across read
        // chunks, ANSI cursor redraws, or the noise filter deduping a repeated
        // prompt), leaving the Run/Stop toggle stuck on "Stop". This watchdog is the
        // robust fallback: it taps the RAW terminal tail (which the filter pipeline
        // can't alter) and, once output has settled, completes ONLY if that tail ends
        // at a real shell prompt — so a genuinely-running silent command is never
        // falsely marked complete.
        private const int RawTailCap          = 512;  // chars of raw tail kept for prompt scan
        private const int IdleCheckIntervalMs = 350;  // how often the watchdog ticks while running
        private const int IdleQuietMs         = 700;  // output must be silent this long before completing
        private readonly DispatcherTimer _idleTimer;
        private readonly System.Text.StringBuilder _rawTail = new System.Text.StringBuilder(RawTailCap + 64);
        private readonly object _rawTailLock = new object();
        private long _lastOutputTicks;

        // Prompt at end of the last raw line: PowerShell/cmd end with '>', POSIX
        // shells with '$' / '#' / '%'. Used by the watchdog, not the in-stream path.
        private static readonly Regex _ansiStripForScan = new Regex(
            @"\x1B\[[0-9;?=>]*[a-zA-Z]|\x1B\].*?(\x07|\x1B\\)|\x1B[@-Z\\-_]",
            RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex _ctrlStripForScan = new Regex(
            @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.Compiled);

        // ── TUI animation suppression ───────────────────────────────────────────
        // Full-screen TUIs (e.g. Claude Code / Ink) redraw in place by moving the cursor
        // UP (ESC[nA) / to a previous line (ESC[nF) and rewriting. Normal scrolling output
        // never moves the cursor up. A rapid streak of these = an animating app, during
        // which the interactive-prompt detector must NOT turn animation frames (spinner
        // words like "Wandering…") into fake prompts. Touched on the single reader thread,
        // except _animatingUntilTicks which is read on the UI thread (Interlocked).
        private const int RedrawWindowMs        = 800;   // max gap to keep a redraw streak alive
        private const int RedrawStreakThreshold = 3;     // cursor-ups within the window => animating
        private const int AnimatingCooldownMs   = 2500;  // suppress prompts this long after last redraw
        private static readonly Regex _redrawScan = new Regex(@"\x1B\[\d*[AF]", RegexOptions.Compiled);
        private long _lastRedrawTicks;
        private int  _redrawStreak;
        private long _animatingUntilTicks;

        public string Header { get; set; }

        public event Action? ClearScreenRequested;
        public event Action? RefreshRequested;

        /// <summary>
        /// Raw, UNFILTERED ConPTY output, forwarded to the WebView output renderer
        /// where a hidden xterm.js resolves it into clean text (no C# filter pipeline).
        /// </summary>
        public event Action<string>? RawOutputReceived;

        /// <summary>
        /// Fired when the user submits a command, so the renderer opens a new
        /// accessible command-block labelled with the command text (v4.0.0 model).
        /// </summary>
        public event Action<string>? CommandStarted;

        private string _currentCommand = string.Empty;
        public string CurrentCommand
        {
            get => _currentCommand;
            set => SetProperty(ref _currentCommand, value);
        }

        private bool _isSearchVisible;
        public bool IsSearchVisible
        {
            get => _isSearchVisible;
            set => SetProperty(ref _isSearchVisible, value);
        }

        private bool _isFirstCommand = true;

        // Detects a Windows/PowerShell/CMD prompt at the end of a chunk,
        // e.g. "PS C:\Users\mo> " or "C:\Windows\System32> "
        private static readonly Regex _promptDetect = new Regex(
            @"(?:PS\s+)?[A-Za-z]:\\.{0,200}>\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private bool _isCommandRunning;
        /// <summary>True while a command is executing; false when idle at a shell prompt.</summary>
        public bool IsCommandRunning
        {
            get => _isCommandRunning;
            private set => SetProperty(ref _isCommandRunning, value);
        }

        public ICommand SendCommand { get; }
        public ICommand CloseTabCommand { get; }
        public ICommand PreviousCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand ExplainAllOutputCommand { get; }
        public ICommand ExplainLatestOutputCommand { get; }
        public ICommand SummarizeAllOutputCommand { get; }
        public ICommand SummarizeLatestOutputCommand { get; }
        public ICommand CleanAllOutputCommand { get; }
        public ICommand CleanLatestOutputCommand { get; }
        public ICommand ChatAboutAllOutputCommand    { get; }
        public ICommand ChatAboutLatestOutputCommand { get; }
        public ICommand OpenSearchCommand { get; }

        public event Action<TerminalTabViewModel>? CloseRequested;

        // Persist output history to survive View recreation (e.g., Tab switching or Sleep/Wake).
        // _outputHistory holds ANSI-STRIPPED text (consumed by the AI explain/summarize features);
        // _rawHistory holds the RAW VT stream, replayed into a freshly-created WebView renderer.
        private readonly System.Text.StringBuilder _outputHistory = new System.Text.StringBuilder();
        private readonly System.Text.StringBuilder _rawHistory = new System.Text.StringBuilder();
        private const int RawHistoryMaxBytes    = 4 * 1024 * 1024; // 4 MB rolling cap
        private const int RawHistoryTrimPercent = 20;

        public TerminalTabViewModel(
            string header,
            ShellProfile profile,
            ITerminalSession terminal,
            ICommandHistory history,
            ILogger logger,
            IConfigManager configManager,
            IAIService aiService,
            IWindowService windowService,
            INotificationService notificationService,
            ISoundService soundService,
            IInteractiveScreenService interactiveScreenService,
            string? workingDirectory = null)
        {
            Header = header;
            _profile = profile;
            _workingDirectory = workingDirectory;
            _terminal = terminal;
            _history = history;
            _logger = logger;
            _configManager = configManager;
            _aiService = aiService;
            _windowService = windowService;
            _notificationService = notificationService;
            _soundService = soundService;
            _interactiveScreenService = interactiveScreenService;

            // ── Interactive prompt auto-detection ──────────────────────────
            _promptDetector = new InteractivePromptDetector();
            _promptDetector.PromptDetected += OnPromptDetected;

            SendCommand = new RelayCommand(ExecuteSendCommand);
            CloseTabCommand = new RelayCommand(_ => CloseRequested?.Invoke(this));
            PreviousCommand = new RelayCommand(_ => CurrentCommand = _history.GetPrevious() ?? CurrentCommand);
            NextCommand = new RelayCommand(_ => CurrentCommand = _history.GetNext() ?? string.Empty);     
            StopCommand = new RelayCommand(_ => StopCurrentExecution());
            RefreshCommand = new RelayCommand(_ => 
            {
                // 1. Flush the backend (ConPTY)
                _terminal.Refresh();
                // 2. Refresh the UI (AvalonEdit)
                RefreshRequested?.Invoke();
            });
            ExplainAllOutputCommand    = new RelayCommand(async (_) => await ExecuteExplainOutput(all: true));
            ExplainLatestOutputCommand = new RelayCommand(async (_) => await ExecuteExplainOutput(all: false));
            SummarizeAllOutputCommand    = new RelayCommand(async (_) => await ExecuteSummarizeOutput(all: true));
            SummarizeLatestOutputCommand = new RelayCommand(async (_) => await ExecuteSummarizeOutput(all: false));
            CleanAllOutputCommand    = new RelayCommand(async (_) => await ExecuteCleanOutput(all: true));
            CleanLatestOutputCommand = new RelayCommand(async (_) => await ExecuteCleanOutput(all: false));
            ChatAboutAllOutputCommand    = new RelayCommand(_ => ExecuteChatAboutOutput(all: true));
            ChatAboutLatestOutputCommand = new RelayCommand(_ => ExecuteChatAboutOutput(all: false));
            OpenSearchCommand = new RelayCommand(_ => IsSearchVisible = true);

            _terminal.OutputReceived += OnTerminalOutput;

            // Idle-watchdog timer (UI thread). Runs only while a command executes.
            _idleTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(IdleCheckIntervalMs),
                DispatcherPriority.Background,
                IdleTimer_Tick,
                Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);
            _idleTimer.Stop(); // the 4-arg ctor starts immediately; idle until a command runs

            // Start in background to prevent UI freeze and handle timing via Smart Wait
            Task.Run(() => InitializeShell(_profile, _workingDirectory));
        }

        // Latest output = last 3000 characters of the output buffer
        private const int LatestOutputCharLimit    = 3000;
        private const int OutputHistoryMaxBytes    = 5 * 1024 * 1024; // 5 MB rolling cap
        private const int OutputHistoryTrimPercent = 20;               // drop oldest 20 % when cap hit
        private const int ShellReadyMaxRetries     = 30;
        private const int ShellReadyRetryDelayMs   = 100;

        private async Task ExecuteExplainOutput(bool all)
        {
            string output;
            lock (_outputHistory)
            {
                output = _outputHistory.ToString();
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                _windowService.ShowAIResponseWindow("Explain Output", "No output available to analyze.");
                return;
            }

            string label;
            if (all)
            {
                label = "Explain All Output";
            }
            else
            {
                label = "Explain Latest Output";
                if (output.Length > LatestOutputCharLimit)
                    output = output.Substring(output.Length - LatestOutputCharLimit);
            }

            AIAudioPlayer.Play();
            try
            {
                var explanation = await _aiService.ExplainOutputAsync(output);
                _windowService.ShowAIResponseWindow(label, explanation);
            }
            finally
            {
                AIAudioPlayer.Stop();
            }
        }

        private async Task ExecuteSummarizeOutput(bool all)
        {
            string output;
            lock (_outputHistory)
            {
                output = _outputHistory.ToString();
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                _windowService.ShowAIResponseWindow("Summarize Output", "No output available to summarize.");
                return;
            }

            string label;
            if (all)
            {
                label = "Summarize All Output";
            }
            else
            {
                label = "Summarize Latest Output";
                if (output.Length > LatestOutputCharLimit)
                    output = output.Substring(output.Length - LatestOutputCharLimit);
            }

            AIAudioPlayer.Play();
            try
            {
                var summary = await _aiService.SummarizeOutputAsync(output);
                _windowService.ShowAIResponseWindow(label, summary);
            }
            finally
            {
                AIAudioPlayer.Stop();
            }
        }

        private async Task ExecuteCleanOutput(bool all)
        {
            string output;
            lock (_outputHistory)
            {
                output = _outputHistory.ToString();
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                _windowService.ShowAIResponseWindow("Clean Output", "No output available to clean.");
                return;
            }

            string label;
            if (all)
            {
                label = "Clean All Output";
            }
            else
            {
                label = "Clean Latest Output";
                if (output.Length > LatestOutputCharLimit)
                    output = output.Substring(output.Length - LatestOutputCharLimit);
            }

            var cleaned = await _aiService.CleanOutputAsync(output, all);
            _windowService.ShowAIResponseWindow(label, cleaned);
        }

        private void ExecuteChatAboutOutput(bool all)
        {
            string output;
            lock (_outputHistory)
            {
                output = _outputHistory.ToString();
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                _windowService.ShowAIResponseWindow("Chat About Output", "No output available to chat about.");
                return;
            }

            string label;
            if (all)
            {
                label = "All Terminal Output";
            }
            else
            {
                label = "Latest Terminal Output";
                if (output.Length > LatestOutputCharLimit)
                    output = output.Substring(output.Length - LatestOutputCharLimit);
            }

            _windowService.ShowAIChatWindow(output, label);
        }

        public string GetHistory()
        {
            lock (_outputHistory)
            {
                return _outputHistory.ToString();
            }
        }

        /// <summary>Raw VT scrollback snapshot, replayed when the WebView renderer is (re)created.</summary>
        public string GetRawHistory()
        {
            lock (_rawHistory)
            {
                return _rawHistory.ToString();
            }
        }

        private void InitializeShell(ShellProfile profile, string? workingDirectory)
        {
            try
            {
                // SMART WAIT LOOP:
                // Wait until the View has subscribed to the TextReceived event.
                // This guarantees that the UI is ready to display output before we start the shell.
                // We timeout after 3 seconds to avoid blocking indefinitely.
                // Wait until the WebView output renderer has subscribed so the shell's
                // startup banner is captured (not raced away before the page is ready).
                int maxRetries = ShellReadyMaxRetries;
                while (RawOutputReceived == null && maxRetries > 0)
                {
                    System.Threading.Thread.Sleep(ShellReadyRetryDelayMs);
                    maxRetries--;
                }

                var settings = _configManager.Load();
                string args = profile.Arguments;

                // Dynamic Argument Handling based on Settings
                // Ensures header visibility based on user preference, overriding -NoLogo if needed.
                if (settings.ShowShellHeader)
                {
                    // If user wants header, remove -NoLogo so PowerShell displays it
                    args = args.Replace("-NoLogo", "", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    // If user wants clean screen, ensure -NoLogo is present
                    if (!args.Contains("-NoLogo", StringComparison.OrdinalIgnoreCase) && 
                        profile.Command.Contains("powershell", StringComparison.OrdinalIgnoreCase))
                    {
                        args = "-NoLogo " + args;
                    }
                }

                string command = profile.Command;
                if (command.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) || 
                    command.Equals("powershell", StringComparison.OrdinalIgnoreCase))
                {
                    // Resolve to full path to avoid potential 0xc0000142 or path issues
                    string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
                    string fullPath = Path.Combine(systemRoot, @"System32\WindowsPowerShell\v1.0\powershell.exe");
                    if (File.Exists(fullPath)) command = fullPath;
                }

                string? actualWorkingDirectory = workingDirectory;
                if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
                {
                    bool hasDlls = false;
                    try
                    {
                        hasDlls = Directory.EnumerateFiles(workingDirectory, "*.dll").Any();
                    }
                    catch { }

                    if (hasDlls)
                    {
                        // Safe startup to prevent 0xc0000142 DLL initialization failure.
                        // Start the process in the system directory, and navigate to the target directory via startup arguments.
                        string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
                        actualWorkingDirectory = Path.Combine(systemRoot, "System32");

                        if (command.Contains("powershell", StringComparison.OrdinalIgnoreCase))
                        {
                            string escapedPath = workingDirectory.Replace("'", "''");
                            args += $" -NoExit -Command \"Set-Location -LiteralPath '{escapedPath}'\"";
                        }
                        else if (command.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            args = GetModifiedArgsForCmd(args, workingDirectory);
                        }
                        else if (command.Contains("wsl.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            args += $" --cd \"{workingDirectory}\"";
                        }
                    }
                }

                string cmd = $"\"{command}\" {args}";
                _terminal.Start(cmd, actualWorkingDirectory);

                // Auto-Clean Logic (Clean Screen) - REMOVED
                // The previous logic waited 1.5s and then cleared the screen to hide "startup noise".
                // However, this caused a race condition where valid user output (if executed quickly)
                // was wiped out unexpectedly. 
                // We now rely solely on command-line arguments (like -NoLogo) to handle startup cleanliness.
                /*
                if (!settings.ShowShellHeader)
                {
                    // ... removed ...
                }
                */
            }
            catch (Exception ex)
            {
                _soundService.Play(AppSound.Error);
                RawOutputReceived?.Invoke($"\r\n[ERROR] Failed to start shell: {ex.Message}\r\n");
            }
        }

        private void MaybeNotifyCommandCompleted(string command, TimeSpan elapsed)
        {
            if (elapsed < NotifyThreshold)               return;
            if (string.IsNullOrWhiteSpace(command))      return;
            if (Application.Current?.MainWindow is { } mw && mw.IsActive) return; // user is watching
            _notificationService.NotifyCommandCompleted(command, elapsed);
        }

        /// <summary>
        /// Raw ConPTY output tap (reader thread). Forwards the RAW stream to the WebView
        /// output renderer (the hidden xterm.js resolves it to clean text — no C# filter
        /// pipeline), then maintains the ANSI-stripped bookkeeping the rest of the app
        /// still relies on: AI history, sound, prompt-completion, interactive-prompt
        /// detection, and the idle-watchdog raw tail.
        /// </summary>
        private void OnTerminalOutput(string data)
        {
            if (string.IsNullOrEmpty(data)) return;

            // 1) Drive the display with the RAW, unfiltered stream → clean output, no dup.
            RawOutputReceived?.Invoke(data);
            lock (_rawHistory)
            {
                _rawHistory.Append(data);
                if (_rawHistory.Length > RawHistoryMaxBytes)
                    _rawHistory.Remove(0, _rawHistory.Length * RawHistoryTrimPercent / 100);
            }

            Interlocked.Exchange(ref _lastOutputTicks, Environment.TickCount64);

            // Track in-place redraw animation on the RAW stream (escapes intact). A rapid
            // streak of cursor-up/previous-line sequences means a TUI is animating; while it
            // is, OnPromptDetected skips the overlay so spinner frames aren't faked into prompts.
            int redraws = _redrawScan.Matches(data).Count;
            if (redraws > 0)
            {
                long nowR = Environment.TickCount64;
                _redrawStreak = (nowR - _lastRedrawTicks <= RedrawWindowMs) ? _redrawStreak + redraws : redraws;
                _lastRedrawTicks = nowR;
                if (_redrawStreak >= RedrawStreakThreshold)
                    Interlocked.Exchange(ref _animatingUntilTicks, nowR + AnimatingCooldownMs);
            }

            string stripped = StripForPromptScan(data);
            if (stripped.Length == 0) return;

            // 2) Idle-watchdog raw tail (prompt recognition once output settles).
            lock (_rawTailLock)
            {
                _rawTail.Append(stripped);
                if (_rawTail.Length > RawTailCap)
                    _rawTail.Remove(0, _rawTail.Length - RawTailCap);
            }

            // 3) AI explain/summarize history (clean text).
            lock (_outputHistory)
            {
                _outputHistory.Append(stripped);
                if (_outputHistory.Length > OutputHistoryMaxBytes)
                    _outputHistory.Remove(0, _outputHistory.Length * OutputHistoryTrimPercent / 100);
            }

            // 4) Fast-path completion: a bare shell prompt means the command finished.
            if (_isCommandRunning && _promptDetect.IsMatch(stripped))
                Application.Current?.Dispatcher.BeginInvoke(() => MarkCommandCompleted());

            // 5) Output tick (throttled in the service).
            _soundService.Play(AppSound.Output);
        }

        /// <summary>
        /// Fires on the UI thread while a command runs. Once output has been silent
        /// for <see cref="IdleQuietMs"/> and the raw tail ends at a shell prompt, the
        /// command is considered complete. Requiring a prompt keeps a silent
        /// long-running command (e.g. Start-Sleep) correctly marked as running.
        /// </summary>
        private void IdleTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isCommandRunning) { _idleTimer.Stop(); return; }
            if (Environment.TickCount64 - Interlocked.Read(ref _lastOutputTicks) < IdleQuietMs) return;

            string tail;
            lock (_rawTailLock) { tail = _rawTail.ToString(); }
            if (LooksLikePrompt(tail))
                MarkCommandCompleted();
        }

        /// <summary>
        /// Single completion path (fast-path prompt detection AND idle watchdog route
        /// here). Idempotent: the first caller flips state, stops the watchdog and
        /// plays the completion cue; later callers no-op, so there is no double sound.
        /// MUST run on the UI thread.
        /// </summary>
        private void MarkCommandCompleted()
        {
            if (!_isCommandRunning) return;

            var elapsed = DateTime.UtcNow - _commandStartUtc;
            var label   = _runningCommandLabel;

            IsCommandRunning = false;
            _idleTimer.Stop();
            _soundService.Play(AppSound.CommandCompleted);
            MaybeNotifyCommandCompleted(label, elapsed);
        }

        /// <summary>True when the last non-blank line of <paramref name="tail"/> ends at a shell prompt.</summary>
        private static bool LooksLikePrompt(string tail)
        {
            if (string.IsNullOrEmpty(tail)) return false;

            int nl = tail.LastIndexOf('\n');
            string lastLine = (nl >= 0 ? tail.Substring(nl + 1) : tail).TrimEnd();
            if (lastLine.Length == 0) return false;

            char last = lastLine[lastLine.Length - 1];
            if (last == '>') return true; // PowerShell / cmd

            // POSIX shells (bash/zsh/wsl). Require a plausible prompt char before the
            // sigil so a lone '$'/'#' in program output is far less likely to match.
            if (last == '$' || last == '#' || last == '%')
            {
                if (lastLine.Length < 2) return false;
                char prev = lastLine[lastLine.Length - 2];
                return prev == ' ' || prev == '~' || prev == '/' || prev == ')' ||
                       prev == ']' || char.IsLetterOrDigit(prev);
            }
            return false;
        }

        private static string StripForPromptScan(string s)
        {
            s = _ansiStripForScan.Replace(s, string.Empty);
            s = _ctrlStripForScan.Replace(s, string.Empty);
            return s;
        }

        private void ExecuteSendCommand(object? obj)
        {
            if (string.IsNullOrWhiteSpace(CurrentCommand)) return;

            _soundService.Play(AppSound.CommandSent);

            var command = CurrentCommand.Trim();
            
            // Apply Aliases
            var settings = _configManager.Load();
            var alias = settings.Aliases.Find(a => a.Trigger.Equals(command, StringComparison.OrdinalIgnoreCase));
            if (alias != null)
            {
                command = alias.Command;
            }

            CurrentCommand = string.Empty;

            _history.Add(command);

            if (command.Equals("cls", StringComparison.OrdinalIgnoreCase) || command.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                lock (_rawHistory) { _rawHistory.Clear(); }
                ClearScreenRequested?.Invoke();
            }
            else
            {
                _commandStartUtc     = DateTime.UtcNow;
                _runningCommandLabel = command;
                IsCommandRunning     = true;

                // Open a new accessible command-block in the renderer, labelled with the command.
                CommandStarted?.Invoke(command);

                // Arm the idle watchdog for this command. Clear the previous prompt
                // from the raw tail so a stale prompt can't trigger instant completion.
                lock (_rawTailLock) { _rawTail.Clear(); }
                Interlocked.Exchange(ref _lastOutputTicks, Environment.TickCount64);
                _idleTimer.Start();

                // VISUAL SEPARATION FOR INPUT
                if (_isFirstCommand)
                {
                    _terminal.WriteInput("\r\n" + command);
                    _isFirstCommand = false;
                }
                else
                {
                    _terminal.WriteInput(command);
                }
            }
        }

        public void WriteInput(string input) => _terminal.WriteInput(input);
        
        public void StopCurrentExecution()
        {
            // Send Ctrl+C to interrupt the current process
            _terminal.WriteInput("\x03");

            // Cue completion only if a command was actually running. We clear the
            // flag BEFORE the shell prints its next prompt, so OnTextReceived won't
            // double-fire CommandCompleted — this is the single completion cue for a
            // manual stop. (A bare Ctrl+C at an idle prompt stays silent.)
            bool wasRunning = IsCommandRunning;
            IsCommandRunning = false;
            _idleTimer.Stop();
            if (wasRunning)
                _soundService.Play(AppSound.CommandCompleted);
        }

        public void Clear()
        {
            lock (_outputHistory)
            {
                _outputHistory.Clear();
            }
            lock (_rawHistory)
            {
                _rawHistory.Clear();
            }
            ClearScreenRequested?.Invoke();
        }
        // ── Interactive-prompt detection handler ──────────────────────────

        /// <summary>
        /// Called (on a ThreadPool thread) when the <see cref="InteractivePromptDetector"/>
        /// identifies an interactive prompt in the terminal output.  Marshals to
        /// the UI thread, shows the Interactive Screen overlay, and sends the
        /// user's choice back to ConPTY stdin.
        /// </summary>
        private void OnPromptDetected(DetectedPrompt prompt)
        {
            _logger.LogInformation($"[DEBUG] OnPromptDetected triggered. Type: {prompt.Type}, Title: '{prompt.Title}', Options: {prompt.Options?.Count}");
            Application.Current?.Dispatcher.BeginInvoke(new Action(async () =>
            {
                _logger.LogInformation($"[DEBUG] OnPromptDetected on UI Thread. _isCommandRunning={_isCommandRunning}");
                // Only show when a command is still running (process is blocked on stdin).
                // If the shell prompt has already appeared, skip.
                if (!_isCommandRunning)
                {
                    _logger.LogInformation("[DEBUG] OnPromptDetected skipped because command is not running.");
                    _promptDetector.Resume();
                    return;
                }

                try
                {
                    _logger.LogInformation("[DEBUG] Building config from prompt and showing interactive screen...");
                    var config = BuildConfigFromPrompt(prompt);
                    var result = await _interactiveScreenService.ShowAsync(config);
                    _logger.LogInformation($"[DEBUG] Interactive screen result returned. Cancelled: {result.WasCancelled}");

                    if (!result.WasCancelled)
                    {
                        if (prompt.Type == PromptType.TuiSelector)
                        {
                            if (result.SelectedOption?.Tag is string tagStr && int.TryParse(tagStr, out int targetIdx))
                            {
                                int currentIdx = prompt.CurrentIndex;
                                int diff = targetIdx - currentIdx;
                                if (diff > 0)
                                {
                                    // Send 'diff' Down arrows
                                    for (int i = 0; i < diff; i++)
                                    {
                                        _terminal.WriteRawInput(new byte[] { 0x1B, 0x5B, 0x42 }); // Down
                                        await Task.Delay(20);
                                    }
                                }
                                else if (diff < 0)
                                {
                                    // Send '-diff' Up arrows
                                    for (int i = 0; i < -diff; i++)
                                    {
                                        _terminal.WriteRawInput(new byte[] { 0x1B, 0x5B, 0x41 }); // Up
                                        await Task.Delay(20);
                                    }
                                }

                                // Send Enter
                                _terminal.WriteRawInput(new byte[] { 0x0D }); // Enter
                            }
                        }
                        else if (result.SelectedOption?.Tag is string responseValue)
                        {
                            _terminal.WriteInput(responseValue);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Interactive prompt overlay failed");
                }
                finally
                {
                    _promptDetector.Resume();
                }
            }));
        }

        /// <summary>
        /// Translates a <see cref="DetectedPrompt"/> into an
        /// <see cref="InteractiveScreenConfig"/> ready for the overlay service.
        /// </summary>
        private InteractiveScreenConfig BuildConfigFromPrompt(DetectedPrompt prompt)
        {
            var options = prompt.Options.Select(o => new InteractiveOption
            {
                Text        = o.DisplayText,
                ShortcutKey = prompt.Type == PromptType.TuiSelector ? string.Empty : o.Value,
                IsEnabled   = true,
                Tag         = o.Value   // value sent back to ConPTY
            }).ToList();

            string description = prompt.Type switch
            {
                PromptType.NumberedList
                    => "Use arrow keys to navigate, press a number/letter to jump, Enter to confirm.",
                PromptType.YesNo
                    => "Press Y for Yes, N for No, or use the arrow keys.",
                PromptType.TuiSelector
                    => "Use arrow keys to navigate, Enter to select, or press shortcuts shown.",
                _ => "Select an option."
            };

            Func<Key, string, bool>? keyPressedHandler = null;

            if (prompt.Type == PromptType.TuiSelector)
            {
                keyPressedHandler = (key, keyStr) =>
                {
                    var rawBytes = GetRawBytesForTuiKey(key, keyStr);
                    if (rawBytes != null)
                    {
                        // Send the bytes to the terminal session immediately
                        _terminal.WriteRawInput(rawBytes);

                        // If it was a navigation key, return false so the default WPF ListBox
                        // navigation runs (updates SelectedIndex and moves selection in the UI).
                        bool isNavigation = key == Key.Up || key == Key.Down || key == Key.Left || key == Key.Right;
                        if (isNavigation)
                        {
                            return false;
                        }

                        // For non-navigation keys (Enter, Esc, Tab, ?, etc.), we dismiss the overlay
                        // and return true to consume the key.
                        _interactiveScreenService.Dismiss();
                        return true;
                    }
                    return false;
                };
            }

            return new InteractiveScreenConfig
            {
                Title                = prompt.Title,
                Description          = description,
                PlatformName         = Header,           // tab name
                Options              = options,
                AllowCancel          = true,
                ConfirmText          = "Confirm  [Enter]",
                CancelText           = "Cancel  [Esc]",
                InitialSelectedIndex = prompt.Type == PromptType.TuiSelector ? prompt.CurrentIndex : 0,
                KeyPressedHandler    = keyPressedHandler
            };
        }

        /// <summary>
        /// Translates a WPF key and its string representation into ANSI/ASCII bytes for TUI applications.
        /// </summary>
        private static byte[]? GetRawBytesForTuiKey(Key key, string keyString)
        {
            switch (key)
            {
                case Key.Up:
                    return new byte[] { 0x1B, 0x5B, 0x41 }; // ESC [ A
                case Key.Down:
                    return new byte[] { 0x1B, 0x5B, 0x42 }; // ESC [ B
                case Key.Right:
                    return new byte[] { 0x1B, 0x5B, 0x43 }; // ESC [ C
                case Key.Left:
                    return new byte[] { 0x1B, 0x5B, 0x44 };  // ESC [ D
                case Key.Enter:
                    return new byte[] { 0x0D };             // Carriage Return (\r)
                case Key.Escape:
                    return new byte[] { 0x1B };             // ESC
                case Key.Tab:
                    return new byte[] { 0x09 };             // Tab
                case Key.Space:
                    return new byte[] { 0x20 };             // Space
                case Key.Back:
                    return new byte[] { 0x08 };             // Backspace
            }

            if (key >= Key.D0 && key <= Key.D9)
            {
                return new byte[] { (byte)('0' + (key - Key.D0)) };
            }
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
            {
                return new byte[] { (byte)('0' + (key - Key.NumPad0)) };
            }

            if (key >= Key.A && key <= Key.Z)
            {
                bool isShiftDown = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                char baseChar = isShiftDown ? 'A' : 'a';
                return new byte[] { (byte)(baseChar + (key - Key.A)) };
            }

            switch (key)
            {
                case Key.OemQuestion:
                    {
                        bool isShiftDown = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                        return new byte[] { (byte)(isShiftDown ? '?' : '/') };
                    }
                case Key.OemPlus:
                    {
                        bool isShiftDown = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                        return new byte[] { (byte)(isShiftDown ? '+' : '=') };
                    }
                case Key.OemMinus:
                    {
                        bool isShiftDown = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                        return new byte[] { (byte)(isShiftDown ? '_' : '-') };
                    }
                case Key.OemPeriod:
                    {
                        bool isShiftDown = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                        return new byte[] { (byte)(isShiftDown ? '>' : '.') };
                    }
                case Key.OemComma:
                    {
                        bool isShiftDown = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                        return new byte[] { (byte)(isShiftDown ? '<' : ',') };
                    }
            }

            return null;
        }

        private static string GetModifiedArgsForCmd(string originalArgs, string workingDirectory)
        {
            string cdCommand = $"@cd /d \"{workingDirectory}\"";
            
            if (string.IsNullOrWhiteSpace(originalArgs))
            {
                return $"/K \"{cdCommand}\"";
            }

            int kIdx = originalArgs.IndexOf("/K", StringComparison.OrdinalIgnoreCase);
            if (kIdx >= 0)
            {
                string afterK = originalArgs.Substring(kIdx + 2).Trim();
                if (string.IsNullOrEmpty(afterK))
                {
                    return originalArgs.Substring(0, kIdx) + $"/K \"{cdCommand}\"";
                }
                
                if (afterK.StartsWith("\"") && afterK.EndsWith("\"") && afterK.Length >= 2)
                {
                    string innerCommand = afterK.Substring(1, afterK.Length - 2);
                    return originalArgs.Substring(0, kIdx) + $"/K \"{cdCommand} && {innerCommand}\"";
                }
                else
                {
                    return originalArgs.Substring(0, kIdx) + $"/K \"{cdCommand} && {afterK}\"";
                }
            }

            return originalArgs + $" /K \"{cdCommand}\"";
        }

        public void HandleJsPromptDetected(string json)
        {
            try
            {
                _logger.LogInformation($"[DEBUG] TerminalTabViewModel received JS prompt JSON: {json}");
                var dto = System.Text.Json.JsonSerializer.Deserialize<DetectedPromptDto>(json, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (dto == null) return;

                var prompt = new DetectedPrompt
                {
                    Type = dto.Type == "yesno" ? PromptType.YesNo : (dto.Type == "tui" ? PromptType.TuiSelector : PromptType.NumberedList),
                    Title = dto.Title,
                    CurrentIndex = dto.CurrentIndex,
                    Options = dto.Options.Select(o => (o.Value, o.Text)).ToList()
                };

                _logger.LogInformation($"[DEBUG] Handled JS prompt. Type: {prompt.Type}, Title: '{prompt.Title}', Options Count: {prompt.Options.Count}");
                OnPromptDetected(prompt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse JS prompt JSON");
            }
        }

        public void Dispose()
        {
            _idleTimer.Stop();
            _promptDetector.Dispose();
            _terminal.Dispose();
        }
    }

    public class DetectedPromptDto
    {
        public string Type { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public int CurrentIndex { get; set; }
        public List<OptionDto> Options { get; set; } = new();
    }

    public class OptionDto
    {
        public string Value { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }
}