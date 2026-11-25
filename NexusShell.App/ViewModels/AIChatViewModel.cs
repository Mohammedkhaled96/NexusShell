using NexusShell.Agents.Application.Conversations;
using NexusShell.Agents.Application.Memory;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Domain.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusShell.App.Commands;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;
using NexusShell.App.Services.Agents;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace NexusShell.App.ViewModels
{
    public partial class AIChatViewModel : ViewModelBase
    {
        private const string DefaultStatusText = "NexusAI is generating response...";

        private readonly IAIService _aiService;
        private readonly IConfigManager _configManager;
        private readonly INexusAgentService? _agentService;
        private readonly Action? _openAgentKeys;
        private readonly Action? _openProviders;
        private readonly IAgentMemory? _memory;
        private readonly Action<string?>? _openMemory;
        private readonly NexusShell.Agents.Application.Connectors.IConnectorService? _connectorService;
        private readonly Action? _openConnectors;
        private readonly Func<string?, string?>? _openTools;
        private readonly string _initialContext;
        private readonly List<Dictionary<string, string>> _history;

        // NexusAgent conversation (Mixture-of-Experts). Created lazily on the first agent message.
        private INexusAgentConversation? _conversation;
        private bool _userChoseMode;
        private CancellationTokenSource? _sendCts;

        // Speaks each agent step (experts, tools, approvals, model switches) while the user waits.
        private readonly AgentStepAnnouncer _steps;

        private string _userLabel = "You";
        private string _aiLabel = "NexusAI";
        private const string AgentLabel = "NexusAgent";
        private const string ShellLabel = "NexusShell";

        public ObservableCollection<ChatMessage> Messages { get; } = new();

        /// <summary>Model, expert, permissions, working folder, attachments and capabilities of the agent.</summary>
        public AgentWorkspaceViewModel Workspace { get; }

        private string _currentMessage = "";
        public string CurrentMessage
        {
            get => _currentMessage;
            set
            {
                // Text set without typing (dictation, paste through UI Automation) must enable Send too.
                if (SetProperty(ref _currentMessage, value)) CommandManager.InvalidateRequerySuggested();
            }
        }

        private bool _isSending;
        public bool IsSending
        {
            get => _isSending;
            set => SetProperty(ref _isSending, value);
        }

        private string _windowTitle = "NexusAI Chat";
        public string WindowTitle { get => _windowTitle; set => SetProperty(ref _windowTitle, value); }

        /// <summary>Live status line (announced politely by screen readers): routing, experts, tools.</summary>
        private string _statusText = DefaultStatusText;
        public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

        private bool _isAgentAvailable;
        public bool IsAgentAvailable { get => _isAgentAvailable; private set => SetProperty(ref _isAgentAvailable, value); }

        /// <summary>True = answer with NexusAgent (experts, tools, MCP); false = classic NexusAI chat.</summary>
        private bool _useAgent;
        public bool UseAgent
        {
            get => _useAgent;
            set
            {
                if (SetProperty(ref _useAgent, value)) _userChoseMode = true;
            }
        }

        private string _agentStatus = "Checking NexusAgent...";
        public string AgentStatus { get => _agentStatus; set => SetProperty(ref _agentStatus, value); }

        /// <summary>Speak every agent step through the screen reader while waiting (saved in settings).</summary>
        private bool _speakAgentSteps;
        public bool SpeakAgentSteps
        {
            get => _speakAgentSteps;
            set
            {
                if (!SetProperty(ref _speakAgentSteps, value)) return;
                try
                {
                    var settings = _configManager.Load();
                    settings.AI.AnnounceAgentSteps = value;
                    _configManager.Save(settings);
                }
                catch (Exception)
                {
                    // Preference still applies for this window even if it could not be saved.
                }
            }
        }

        /// <summary>Raised with (text, important) the screen reader should speak now (the view delivers it).</summary>
        public event Action<string, bool>? SpeakRequested;

        /// <summary>Raised when this chat starts an agent request (the view makes itself the narrating window).</summary>
        public event Action? AgentRunStarted;

        public ICommand SendMessageCommand { get; }
        public ICommand ClearChatCommand   { get; }
        public ICommand CopyMessageCommand { get; }
        public ICommand ManageAgentKeysCommand { get; }
        public ICommand ManageProvidersCommand { get; }
        public ICommand OpenMemoryCommand { get; }
        public ICommand OpenConnectorsCommand { get; }

        /// <summary>NexusShell tools: pick a tool and a file; the request is sent with what was typed as instructions.</summary>
        public ICommand OpenToolsCommand { get; }
        public ICommand RefreshAgentCommand { get; }
        public ICommand ClearWorkingFolderCommand { get; }
        public ICommand RemoveAttachmentCommand { get; }
        public ICommand ClearAttachmentsCommand { get; }
        public ICommand StopCommand { get; }

        // Fired when a new message is added — view subscribes to scroll to bottom
        public event Action? MessageAdded;

        public AIChatViewModel(
            IAIService aiService,
            IConfigManager configManager,
            string initialContext = "",
            string contextLabel = "",
            INexusAgentService? agentService = null,
            Action? openAgentKeys = null,
            ILoggerFactory? loggerFactory = null,
            Action? openProviders = null,
            IAgentMemory? memory = null,
            Action<string?>? openMemory = null,
            NexusShell.Agents.Application.Chats.IChatLibrary? chats = null,
            NexusShell.Agents.Application.Scheduling.IAgentScheduler? scheduler = null,
            Action<Action<string>?>? openSchedule = null,
            NexusShell.Agents.Application.Connectors.IConnectorService? connectors = null,
            Action? openConnectors = null,
            Func<string?, string?>? openTools = null)
        {
            _aiService = aiService;
            _configManager = configManager;
            _agentService = agentService;
            _openAgentKeys = openAgentKeys;
            _openProviders = openProviders;
            _memory = memory;
            _openMemory = openMemory;
            _connectorService = connectors;
            _openConnectors = openConnectors;
            _openTools = openTools;
            _initialContext = initialContext ?? string.Empty;

            var settings = _configManager.Load();
            SetupLocalization(settings.AI.Language);
            _speakAgentSteps = settings.AI.AnnounceAgentSteps;
            _steps = new AgentStepAnnouncer(
                (text, important) => SpeakRequested?.Invoke(text, important),
                () => SpeakAgentSteps,
                (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AgentStepAnnouncer>());

            Workspace = new AgentWorkspaceViewModel(configManager);
            Workspace.OptionsChanged += OnWorkspaceChanged;
            Workspace.Attachments.CollectionChanged += (_, _) => CommandManager.InvalidateRequerySuggested();

            var systemPrompt =
                "You are NexusAI, an expert Windows terminal assistant embedded inside NexusShell.\n" +
                "You help users understand terminal output, debug errors, write shell commands, and work effectively in their shell environment.\n" +
                "When you suggest commands, always wrap them in code blocks using triple backticks.\n" +
                "Be helpful, precise, and conversational.\n" +
                $"IMPORTANT: ALWAYS respond in the following language: {settings.AI.Language}.";

            if (!string.IsNullOrWhiteSpace(initialContext))
            {
                systemPrompt +=
                    "\n\nThe user is asking about the following context. Use it to inform your answers:\n\n" +
                    "--- CONTEXT START ---\n" +
                    initialContext + "\n" +
                    "--- CONTEXT END ---";

                WindowTitle = string.IsNullOrWhiteSpace(contextLabel)
                    ? "NexusAI Chat"
                    : "NexusAI Chat - " + contextLabel;
            }

            _history = new List<Dictionary<string, string>>
            {
                new() { ["role"] = "system", ["content"] = systemPrompt }
            };

            SendMessageCommand = new RelayCommand(
                async _ => await ExecuteSendMessage(),
                _ => !IsSending && !string.IsNullOrWhiteSpace(CurrentMessage));

            ClearChatCommand = new RelayCommand(_ => NewChat(), _ => !IsSending);

            CopyMessageCommand = new RelayCommand(obj =>
            {
                if (obj is string content)
                {
                    try { System.Windows.Clipboard.SetText(content); } catch { }
                }
            });

            StopCommand = new RelayCommand(_ =>
            {
                StatusText = "Stopping...";
                _sendCts?.Cancel();
            });

            ManageAgentKeysCommand = new RelayCommand(
                async _ =>
                {
                    _openAgentKeys?.Invoke();
                    await ReloadAgentAsync();
                },
                _ => _openAgentKeys != null);

            ManageProvidersCommand = new RelayCommand(
                async _ =>
                {
                    _openProviders?.Invoke();
                    await ReloadAgentAsync();
                },
                _ => _openProviders != null);

            OpenConnectorsCommand = new RelayCommand(
                async _ =>
                {
                    _openConnectors?.Invoke();
                    await ReloadAgentAsync(); // new connections give the agent their tools
                },
                _ => _openConnectors != null);

            OpenToolsCommand = new RelayCommand(
                async _ =>
                {
                    string? instructions = string.IsNullOrWhiteSpace(CurrentMessage) ? null : CurrentMessage.Trim();
                    string? request = _openTools?.Invoke(instructions);
                    // A tool added or set up in the window is offered at once (the agent keeps its history).
                    await ReloadAgentAsync();
                    if (request is null) return;
                    CurrentMessage = request;
                    await ExecuteSendMessage();
                },
                _ => _openTools != null && UseAgent && !IsSending);

            OpenMemoryCommand = new RelayCommand(
                _ => _openMemory?.Invoke(Workspace.WorkingDirectory),
                _ => _openMemory != null);

            RefreshAgentCommand = new RelayCommand(async _ =>
            {
                await ReloadAgentAsync();
                Speak($"Agent refreshed. {Workspace.Models.Count(m => m.Value is not null && m.IsAvailable)} models available. {AgentStatus}");
            });

            ClearWorkingFolderCommand = new RelayCommand(_ => Workspace.SetWorkingDirectory(null), _ => Workspace.HasWorkingDirectory);

            RemoveAttachmentCommand = new RelayCommand(item =>
            {
                if (item is not AttachmentItem attachment) return;
                Workspace.RemoveAttachment(attachment);
                Speak($"Removed {attachment.Name}. {Workspace.AttachmentsSummary}.");
            }, item => item is AttachmentItem);

            ClearAttachmentsCommand = new RelayCommand(_ =>
            {
                Workspace.TakeAttachments();
                Speak("Attachments removed.");
            }, _ => Workspace.HasAttachments);

            // Greet the user
            string greeting;
            if (!string.IsNullOrWhiteSpace(initialContext))
            {
                greeting = settings.AI.Language == "Arabic"
                    ? "لقد راجعت السياق الذي قدمته. ماذا تريد أن تعرف أو تفعل؟"
                    : "I've reviewed the context. What would you like to know or do?";
            }
            else
            {
                greeting = settings.AI.Language == "Arabic"
                    ? "مرحباً! أنا NexusAI. كيف يمكنني مساعدتك في سطر الأوامر اليوم؟"
                    : "Hello! I'm NexusAI. How can I help you with your terminal today?";
            }

            AddAiMessage(greeting);

            InitializeChats(chats, scheduler, openSchedule);
            _ = RefreshAgentStatusAsync();
        }

        private void SetupLocalization(string language)
        {
            if (language == "Arabic")
            {
                _userLabel = "أنت";
                _aiLabel = "NexusAI";
            }
            else if (language == "French") { _userLabel = "Vous"; _aiLabel = "NexusAI"; }
            else if (language == "Spanish") { _userLabel = "Usted"; _aiLabel = "NexusAI"; }
            else if (language == "German") { _userLabel = "Sie"; _aiLabel = "NexusAI"; }
            else { _userLabel = "You"; _aiLabel = "NexusAI"; }
        }

        private void AddUserMessage(string content)
        {
            Messages.Add(new ChatMessage { Role = "user", Content = content, IsUser = true, SpeakerName = _userLabel });
            MessageAdded?.Invoke();
        }

        private void AddAiMessage(string content, string? speaker = null)
        {
            Messages.Add(new ChatMessage { Role = "assistant", Content = content, IsUser = false, SpeakerName = speaker ?? _aiLabel });
            MessageAdded?.Invoke();
        }

        /// <summary>Speaks a short confirmation now (always, even when step narration is off).</summary>
        private void Speak(string text) => SpeakRequested?.Invoke(text, true);

        /// <summary>Lets the view announce results of its own actions (file pickers, drag and drop).</summary>
        public void Announce(string text) => Speak(text);

        /// <summary>Checks whether any model (API key or local server) is available and switches Agent mode on accordingly.</summary>
        private async Task RefreshAgentStatusAsync()
        {
            if (_agentService is null)
            {
                IsAgentAvailable = false;
                AgentStatus = "NexusAgent is not installed.";
                return;
            }

            try
            {
                var health = await _agentService.GetHealthAsync();
                var ready = health.Providers.Where(p => p.IsAvailable).Select(p => p.ProviderId).ToList();
                IsAgentAvailable = ready.Count > 0;

                AgentStatus = IsAgentAvailable
                    ? $"NexusAgent ready ({ModeName(health.Mode)}): {health.Experts.Count(e => e.Enabled && e.Routable)} experts; models from {string.Join(", ", ready)}."
                    : "NexusAgent needs a model: add a provider or an API key, or start a local model server such as Ollama or LM Studio.";

                if (health.Warnings.Count > 0)
                    AgentStatus += $" {health.Warnings.Count} configuration warning(s) are listed in the log.";

                // Memory and the context window are always on: say so when the chat opens.
                if (IsAgentAvailable)
                {
                    int facts = _memory is null ? 0 : (await _memory.ListAsync()).Count;
                    AgentStatus += $" Memory on ({facts} saved facts); context sized to each model; chats saved automatically.";
                    if (_connectorService is not null) AgentStatus += " " + AgentConnectorsViewModel.Describe(_connectorService);
                }

                if (!_userChoseMode || !IsAgentAvailable)
                {
                    _useAgent = IsAgentAvailable;
                    OnPropertyChanged(nameof(UseAgent));
                }

                await Workspace.LoadAsync(_agentService);
                // Choices restored from settings now apply to a conversation started before the lists loaded.
                if (_conversation is not null) await _conversation.UpdateOptionsAsync(Workspace.BuildOptions());
            }
            catch (Exception ex)
            {
                IsAgentAvailable = false;
                AgentStatus = "NexusAgent could not start: " + ex.Message;
            }
        }

        /// <summary>New keys, providers or models: reload the configuration and keep the conversation.</summary>
        private async Task ReloadAgentAsync()
        {
            _agentService?.ReloadConfiguration();
            await RefreshAgentStatusAsync();
            if (_conversation is not null)
            {
                try
                {
                    await _conversation.ReloadAsync();
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Agent conversation could not be reloaded; starting a new one on the next message.");
                    _conversation = null;
                }
            }
        }

        // While a slash command runs, its confirmation is shown in the chat and spoken once by the command.
        private bool _runningCommand;
        private string? _commandEcho;

        // Arrowing through a combo box changes the choice on every key press: wait until the user
        // settles before speaking the confirmation and rebuilding the agent, so the screen reader
        // is not interrupted on each item.
        private static readonly TimeSpan WorkspaceSettleDelay = TimeSpan.FromMilliseconds(1200);
        private CancellationTokenSource? _workspaceDebounce;

        private void OnWorkspaceChanged(string announcement)
        {
            _workspaceDebounce?.Cancel();
            if (_runningCommand)
            {
                _commandEcho = announcement;
                _ = ApplyWorkspaceAsync();
                return;
            }

            var debounce = _workspaceDebounce = new CancellationTokenSource();
            _ = AnnounceWhenSettledAsync(announcement, debounce.Token);
        }

        private async Task AnnounceWhenSettledAsync(string announcement, CancellationToken token)
        {
            try
            {
                await Task.Delay(WorkspaceSettleDelay, token);
            }
            catch (OperationCanceledException)
            {
                return; // a newer choice replaced this one
            }
            Speak(announcement);
            await ApplyWorkspaceAsync();
        }

        private async Task ApplyWorkspaceAsync()
        {
            if (_conversation is null) return; // applied when the conversation starts
            try
            {
                // Waits for a running turn to finish; history is kept.
                await _conversation.UpdateOptionsAsync(Workspace.BuildOptions());
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Agent options could not be applied.");
                AddAiMessage("Could not apply the new agent settings: " + ex.Message, ShellLabel);
            }
        }

        private static string ModeName(OrchestrationMode mode) => mode switch
        {
            OrchestrationMode.Coordinator => "coordinator with sub-agents",
            OrchestrationMode.Sequential => "sequential pipeline",
            _ => "mixture of experts",
        };

        private async Task ExecuteSendMessage()
        {
            var msg = CurrentMessage.Trim();
            if (string.IsNullOrEmpty(msg)) return;

            if (await TryRunSlashCommandAsync(msg))
            {
                CurrentMessage = "";
                return;
            }

            CurrentMessage = "";
            IsSending = true;
            _sendCts = new CancellationTokenSource();
            var token = _sendCts.Token;

            bool viaAgent = UseAgent && _agentService is not null;

            // Files and folders for this message: attached ones plus @mentions ("explain @src\Program.cs").
            IReadOnlyList<string> attachments = [];
            if (viaAgent)
            {
                Workspace.AddAttachments(FindMentions(msg));
                attachments = Workspace.TakeAttachments();
            }

            AddUserMessage(attachments.Count == 0
                ? msg
                : msg + "\n\nAttached: " + string.Join(", ", attachments.Select(Path.GetFileName)));
            _history.Add(new() { ["role"] = "user", ["content"] = msg });

            try
            {
                string response;
                if (viaAgent)
                {
                    response = await SendViaAgentAsync(msg, attachments, token);
                }
                else
                {
                    response = await _aiService.SendChatMessageAsync(_history, token);
                    // The classic service reports cancellation as text instead of throwing.
                    token.ThrowIfCancellationRequested();
                    AddAiMessage(response);
                }

                // Keep the classic history in sync so switching modes keeps context.
                _history.Add(new() { ["role"] = "assistant", ["content"] = response });
                if (_history.Count > 21)
                    _history.RemoveRange(1, _history.Count - 21);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _history.RemoveAt(_history.Count - 1);
                if (viaAgent) _steps.Stopped();
                AddAiMessage("Stopped. You can ask again or rephrase the question.");
            }
            catch (Exception ex)
            {
                if (viaAgent) _steps.Failed();
                AddAiMessage("Error: " + ex.Message);
            }
            finally
            {
                _sendCts.Dispose();
                _sendCts = null;
                IsSending = false;
                StatusText = DefaultStatusText;
            }

            // Every answer is saved on the device, so the chat can be reopened and continued later.
            await SaveChatAsync();
        }

        private async Task<string> SendViaAgentAsync(string message, IReadOnlyList<string> attachments, CancellationToken cancellationToken)
        {
            // Narrate in the user's AI language (read each time: the setting may have changed).
            _steps.Language = AgentProgressNarrator.LanguageFrom(_configManager.Load().AI.Language);
            AgentRunStarted?.Invoke();
            _steps.Begin();
            StatusText = AgentProgressNarrator.Sent(_steps.Language);

            // The latest choices always apply, even when they were made a moment ago (still settling).
            if (_conversation is null)
            {
                // A reopened chat continues with the agent's saved state (full context).
                _conversation = await _agentService!.StartConversationAsync(_initialContext, Workspace.BuildOptions(), _resume, cancellationToken);
                _resume = null;
            }
            else await _conversation.UpdateOptionsAsync(Workspace.BuildOptions(), cancellationToken);

            // Progress<T> captures the UI SynchronizationContext: steps arrive on the UI thread, in order.
            var progress = new Progress<AgentProgress>(step =>
            {
                string? spoken = _steps.Report(step);
                if (spoken is not null) StatusText = spoken;
            });
            var result = await _conversation.SendAsync(message, attachments, progress, cancellationToken);

            var answeredBy = result.Experts.Where(e => e.Succeeded).Select(e => e.ExpertName).Distinct().ToList();
            if (result.Experts.Count > 0 && answeredBy.Count == 0) _steps.Failed();
            else _steps.Completed(answeredBy);

            var answer = new StringBuilder(result.Text);
            if (_conversation.Mode == OrchestrationMode.MixtureOfExperts && result.Experts.Count > 0)
            {
                // Which model answered matters: it may differ from the chosen one after an automatic switch.
                var credits = result.Experts.Select(e => e.Succeeded
                    ? string.IsNullOrWhiteSpace(e.Model) ? $"{e.ExpertName} {e.Weight:P0}" : $"{e.ExpertName} {e.Weight:P0} with {e.Model}"
                    : $"{e.ExpertName} (unavailable)");
                answer.Append("\n\n(Answered by: ").Append(string.Join(", ", credits)).Append(')');
            }
            if (result.AttachmentNotes.Count > 0)
                answer.Append("\n\nAttachment notes: ").Append(string.Join(" ", result.AttachmentNotes));

            AddAiMessage(answer.ToString(), AgentLabel);
            return result.Text;
        }

        // ---------- @mentions ----------

        [GeneratedRegex(@"(?:^|\s)@(?:""(?<q>[^""]+)""|(?<p>[^\s""]+))")]
        private static partial Regex MentionPattern();

        /// <summary>"@path" or "@\"path with spaces\"": existing files or folders, relative to the working folder.</summary>
        private IEnumerable<string> FindMentions(string message)
        {
            foreach (Match match in MentionPattern().Matches(message))
            {
                string raw = match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["p"].Value.TrimEnd('.', ',', ';', ':', ')', '?', '!');
                if (raw.Length == 0) continue;
                string? full = null;
                try
                {
                    full = Path.IsPathRooted(raw)
                        ? Path.GetFullPath(raw)
                        : Workspace.WorkingDirectory is { } folder ? Path.GetFullPath(Path.Combine(folder, raw)) : null;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                }
                if (full is not null && (File.Exists(full) || Directory.Exists(full))) yield return full;
            }
        }

        // ---------- Slash commands (keyboard-first control, like Claude Code) ----------

        private const string SlashHelp =
            "Commands (type them in the message box):\n" +
            "/model  lists the models. /model NAME chooses one (part of the name is enough). /model auto goes back to automatic.\n" +
            "/expert  lists the experts. /expert NAME makes one expert answer. /expert auto uses the mixture of experts.\n" +
            "/mode ask, /mode edits or /mode plan  sets permissions: ask before changes, auto-approve file edits, or read-only planning.\n" +
            "/folder PATH  sets the working folder. /folder clear removes it. /folder alone says the current folder.\n" +
            "/attach PATH  attaches a file or folder to the next message. You can also write @PATH inside a message.\n" +
            "/tools  says which tools, skills and MCP servers are on.\n" +
            "/remember TEXT  saves a fact to NexusAgent's long-term memory. /memory lists what it remembers. /forget ID deletes one memory.\n" +
            "/new  starts a new chat. /chats lists saved chats. /open NUMBER or TITLE reopens one to continue it. /rename TITLE renames this chat.\n" +
            "/schedule  opens scheduled tasks. /tasks lists them. You can also ask: every weekday at 9 check free disk space.\n" +
            "/connectors  opens Connectors (Gmail, Google Calendar, Outlook, GitHub, Notion, Jira, Slack ...) and says which apps are connected.\n" +
            "/use  opens NexusShell tools (Transcribe ...): pick a tool and a file. Or just ask, for example: transcribe the meeting recording in Downloads.\n" +
            "/status  says the agent status and current choices.\n" +
            "/reload  reloads providers, models and MCP servers.\n" +
            "/clear  clears the conversation.";

        private static readonly HashSet<string> SlashCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "help", "?", "model", "models", "expert", "experts", "mode", "permissions", "folder", "cd", "attach", "tools", "status", "reload", "clear",
            "remember", "memory", "memories", "forget",
            "new", "chats", "open", "rename", "schedule", "tasks", "connectors", "connect", "use",
        };

        private async Task<bool> TryRunSlashCommandAsync(string message)
        {
            if (!message.StartsWith('/')) return false;
            int space = message.IndexOf(' ');
            string name = (space < 0 ? message[1..] : message[1..space]).Trim();
            string argument = space < 0 ? string.Empty : message[(space + 1)..].Trim();
            if (!SlashCommands.Contains(name)) return false; // e.g. "/usr/bin ..." is a normal question

            AddUserMessage(message);
            _runningCommand = true;
            _commandEcho = null;
            string reply;
            try
            {
                reply = name.ToLowerInvariant() switch
            {
                "help" or "?" => SlashHelp,
                "model" or "models" => ModelCommand(argument),
                "expert" or "experts" => ExpertCommand(argument),
                "mode" or "permissions" => ModeCommand(argument),
                "folder" or "cd" => FolderCommand(argument),
                "attach" => AttachCommand(argument),
                "tools" => $"{Workspace.CapabilitiesSummary}. Use the Tools, skills and MCP button to change them.",
                "status" => await StatusSummaryAsync(),
                "remember" => await RememberCommandAsync(argument),
                "memory" or "memories" => await MemoryCommandAsync(),
                "forget" => await ForgetCommandAsync(argument),
                "new" => NewChatCommandText(),
                "chats" => await ChatsCommandAsync(),
                "open" => await OpenCommandAsync(argument),
                "rename" => await RenameCurrentAsync(argument),
                "schedule" => ScheduleCommandText(),
                "tasks" => await TasksCommandAsync(),
                "connectors" or "connect" => ConnectorsCommandText(),
                "use" => UseToolCommandText(),
                "reload" => await ReloadCommandAsync(),
                "clear" => ClearCommand(),
                _ => SlashHelp,
                };
                if (string.IsNullOrEmpty(reply)) reply = _commandEcho ?? string.Empty;
            }
            finally
            {
                _runningCommand = false;
            }

            if (!string.IsNullOrEmpty(reply))
            {
                AddAiMessage(reply, ShellLabel);
                Speak(reply.Length > 400 ? reply[..400] + " ... The full list is in the conversation." : reply);
            }
            return true;
        }

        private string ModelCommand(string argument)
        {
            if (argument.Length == 0)
            {
                var available = Workspace.Models.Where(m => m.Value is not null && m.IsAvailable).Select(m => m.Value!).ToList();
                var unavailable = Workspace.Models.Count(m => m.Value is not null && !m.IsAvailable);
                return $"Current model: {Workspace.SelectedModel.Text}.\n" +
                       (available.Count == 0 ? "No model is available right now." : "Available: " + string.Join(", ", available) + ".") +
                       (unavailable > 0 ? $"\n{unavailable} more are listed in the Model box as unavailable." : string.Empty);
            }
            var choice = Workspace.FindModel(argument);
            if (choice is null) return $"No model matches \"{argument}\". Type /model to list them.";
            Workspace.SelectedModel = choice; // announces and applies
            return string.Empty;
        }

        private string ExpertCommand(string argument)
        {
            if (argument.Length == 0)
                return $"Current: {Workspace.SelectedExpert.Text}.\nExperts: " + string.Join(", ", Workspace.Experts.Where(e => e.Value is not null).Select(e => $"{e.Text} ({e.Value})")) + ".";
            var choice = Workspace.FindExpert(argument);
            if (choice is null) return $"No expert matches \"{argument}\". Type /expert to list them.";
            Workspace.SelectedExpert = choice;
            return string.Empty;
        }

        private string ModeCommand(string argument)
        {
            if (argument.Length == 0) return "Permissions: " + Workspace.SelectedPermission.Description + " Options: /mode ask, /mode edits, /mode plan.";
            var choice = Workspace.FindPermission(argument);
            if (choice is null) return "Use /mode ask, /mode edits or /mode plan.";
            Workspace.SelectedPermission = choice;
            return string.Empty;
        }

        private string FolderCommand(string argument)
        {
            if (argument.Length == 0) return Workspace.HasWorkingDirectory ? $"Working folder: {Workspace.WorkingDirectory}" : "No working folder. Use /folder PATH or the Working folder button.";
            if (argument.Equals("clear", StringComparison.OrdinalIgnoreCase) || argument.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                Workspace.SetWorkingDirectory(null);
                return string.Empty;
            }
            string path = Environment.ExpandEnvironmentVariables(argument.Trim('"'));
            if (!Path.IsPathRooted(path) && Workspace.WorkingDirectory is { } current) path = Path.Combine(current, path);
            if (!Directory.Exists(path)) return $"Folder not found: {path}";
            Workspace.SetWorkingDirectory(path);
            return string.Empty;
        }

        private string AttachCommand(string argument)
        {
            if (argument.Length == 0) return "Write /attach followed by a file or folder path, or use the Attach files button.";
            string path = Environment.ExpandEnvironmentVariables(argument.Trim('"'));
            if (!Path.IsPathRooted(path) && Workspace.WorkingDirectory is { } folder) path = Path.Combine(folder, path);
            return Workspace.AddAttachments([path]) > 0
                ? $"Attached {Path.GetFileName(path.TrimEnd('\\'))}. {Workspace.AttachmentsSummary}."
                : $"Could not attach {path}: it does not exist, is already attached, or the limit of {AgentWorkspaceViewModel.MaxAttachments} was reached.";
        }

        private async Task<string> StatusSummaryAsync()
        {
            var options = Workspace.BuildOptions();
            int memories = _memory is null ? 0 : (await _memory.ListAsync()).Count;
            return $"{AgentStatus}\n" +
                   $"Agent mode: {(UseAgent ? "on" : "off")}. Model: {Workspace.SelectedModel.Text}. Experts: {Workspace.SelectedExpert.Text}. " +
                   $"Permissions: {Workspace.SelectedPermission.Text}. Working folder: {options.WorkingDirectory ?? "none"}. " +
                   $"{Workspace.CapabilitiesSummary}. {Workspace.AttachmentsSummary}. Long-term memory: {memories} saved facts.";
        }

        // ---------- Long-term memory commands ----------

        private async Task<string> RememberCommandAsync(string text)
        {
            if (_memory is null) return "Long-term memory is not available.";
            if (text.Length == 0) return "Write /remember followed by the fact, for example: /remember I use PowerShell 7 and prefer short answers.";
            var result = await _memory.RememberAsync(new MemoryDraft(text, Evidence: "Typed with /remember"));
            return result.Saved
                ? $"{(result.Updated ? "Updated the memory" : "Remembered")} [{result.Entry!.Id}]: {result.Entry.Content}"
                : result.Refusal!;
        }

        private async Task<string> MemoryCommandAsync()
        {
            if (_memory is null) return "Long-term memory is not available.";
            var all = await _memory.ListAsync();
            if (all.Count == 0) return "NexusAgent has no memories yet. Tell it \"remember that ...\" or type /remember followed by a fact.";
            var lines = all.Take(10).Select(e => $"[{e.Id}] {(e.Pinned ? "(pinned) " : string.Empty)}{e.Content}");
            return $"{all.Count} memories saved{(all.Count > 10 ? ", the 10 most recent" : string.Empty)}:\n" + string.Join("\n", lines) +
                   "\nOpen the Memory window (Alt+Y) to search, edit or delete them.";
        }

        private async Task<string> ForgetCommandAsync(string id)
        {
            if (_memory is null) return "Long-term memory is not available.";
            if (id.Length == 0) return "Write /forget followed by the memory id, for example /forget m3f9a2c. Type /memory to see the ids.";
            return await _memory.ForgetAsync(id.Trim('[', ']', ' ')) ? $"Forgot memory {id}." : $"There is no memory {id}. Type /memory to see the ids.";
        }

        private async Task<string> ReloadCommandAsync()
        {
            await ReloadAgentAsync();
            return "Reloaded. " + AgentStatus;
        }

        private string ClearCommand()
        {
            ClearChat();
            return string.Empty;
        }

        private string NewChatCommandText()
        {
            NewChat();
            return string.Empty;
        }

        private string UseToolCommandText()
        {
            if (_openTools is null) return "NexusShell tools are not available.";
            // After this command finishes, so the chosen request is sent as a normal message.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (OpenToolsCommand.CanExecute(null)) OpenToolsCommand.Execute(null);
            });
            return "Opening NexusShell tools.";
        }

        private string ConnectorsCommandText()
        {
            if (_connectorService is null || _openConnectors is null) return "Connectors are not available.";
            string text = AgentConnectorsViewModel.Describe(_connectorService);
            if (OpenConnectorsCommand.CanExecute(null)) OpenConnectorsCommand.Execute(null);
            return text + " Opening Connectors.";
        }

        private string ScheduleCommandText()
        {
            if (_openSchedule is null) return "Scheduling is not available.";
            OpenSchedule();
            return string.Empty;
        }

        /// <summary>Stops pending announcements and timers (called when the window closes).</summary>
        public void Shutdown()
        {
            ChatHub.ChatsChanged -= OnChatsChanged;
            _sendCts?.Cancel();
            _workspaceDebounce?.Cancel();
            Workspace.OptionsChanged -= OnWorkspaceChanged;
            _steps.Dispose();
        }

        /// <summary>"Clear" starts a new chat; the cleared one stays in Chat history.</summary>
        private void ClearChat() => NewChat();
    }
}
