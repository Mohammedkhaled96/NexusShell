using NexusShell.Agents.Application.Conversations;
using NexusShell.Agents.Application.Progress;
using NexusShell.Agents.Domain.Orchestration;
using NexusShell.App.Commands;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace NexusShell.App.ViewModels
{
    public class AIChatViewModel : ViewModelBase
    {
        private const string DefaultStatusText = "NexusAI is generating response...";

        private readonly IAIService _aiService;
        private readonly IConfigManager _configManager;
        private readonly INexusAgentService? _agentService;
        private readonly Action? _openAgentKeys;
        private readonly string _initialContext;
        private readonly List<Dictionary<string, string>> _history;

        // NexusAgent conversation (Mixture-of-Experts). Created lazily on the first agent message.
        private INexusAgentConversation? _conversation;
        private bool _userChoseMode;
        private CancellationTokenSource? _sendCts;

        private string _userLabel = "You";
        private string _aiLabel = "NexusAI";
        private const string AgentLabel = "NexusAgent";

        public ObservableCollection<ChatMessage> Messages { get; } = new();

        private string _currentMessage = "";
        public string CurrentMessage
        {
            get => _currentMessage;
            set => SetProperty(ref _currentMessage, value);
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

        public ICommand SendMessageCommand { get; }
        public ICommand ClearChatCommand   { get; }
        public ICommand CopyMessageCommand { get; }
        public ICommand ManageAgentKeysCommand { get; }
        public ICommand StopCommand { get; }

        // Fired when a new message is added — view subscribes to scroll to bottom
        public event Action? MessageAdded;

        public AIChatViewModel(
            IAIService aiService,
            IConfigManager configManager,
            string initialContext = "",
            string contextLabel = "",
            INexusAgentService? agentService = null,
            Action? openAgentKeys = null)
        {
            _aiService = aiService;
            _configManager = configManager;
            _agentService = agentService;
            _openAgentKeys = openAgentKeys;
            _initialContext = initialContext ?? string.Empty;

            var settings = _configManager.Load();
            SetupLocalization(settings.AI.Language);

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

            ClearChatCommand = new RelayCommand(_ => ClearChat());

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
                    _conversation = null; // pick up new keys / models on the next message
                    await RefreshAgentStatusAsync();
                },
                _ => _openAgentKeys != null);

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
                    : "NexusAgent needs a model: add an API key with the Agent keys button, or start Ollama.";

                if (health.Warnings.Count > 0)
                    AgentStatus += $" {health.Warnings.Count} configuration warning(s) are listed in the log.";

                if (!_userChoseMode || !IsAgentAvailable)
                {
                    _useAgent = IsAgentAvailable;
                    OnPropertyChanged(nameof(UseAgent));
                }
            }
            catch (Exception ex)
            {
                IsAgentAvailable = false;
                AgentStatus = "NexusAgent could not start: " + ex.Message;
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

            CurrentMessage = "";
            IsSending = true;
            _sendCts = new CancellationTokenSource();
            var token = _sendCts.Token;

            AddUserMessage(msg);
            _history.Add(new() { ["role"] = "user", ["content"] = msg });

            try
            {
                string response;
                if (UseAgent && _agentService is not null)
                {
                    response = await SendViaAgentAsync(msg, token);
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
                AddAiMessage("Stopped. You can ask again or rephrase the question.");
            }
            catch (Exception ex)
            {
                AddAiMessage("Error: " + ex.Message);
            }
            finally
            {
                _sendCts.Dispose();
                _sendCts = null;
                IsSending = false;
                StatusText = DefaultStatusText;
            }
        }

        private async Task<string> SendViaAgentAsync(string message, CancellationToken cancellationToken)
        {
            StatusText = "NexusAgent is thinking...";
            _conversation ??= await _agentService!.StartConversationAsync(_initialContext, cancellationToken);

            // Progress<T> captures the UI SynchronizationContext: updates land on the UI thread.
            var progress = new Progress<AgentProgress>(p => StatusText = p.Message);
            var result = await _conversation.SendAsync(message, progress, cancellationToken);

            string answer = result.Text;
            if (_conversation.Mode == OrchestrationMode.MixtureOfExperts && result.Experts.Count > 0)
            {
                var credits = result.Experts.Select(e => e.Succeeded
                    ? $"{e.ExpertName} {e.Weight:P0}"
                    : $"{e.ExpertName} (unavailable)");
                answer += "\n\n(Answered by: " + string.Join(", ", credits) + ")";
            }

            AddAiMessage(answer, AgentLabel);
            return result.Text;
        }

        private void ClearChat()
        {
            Messages.Clear();
            if (_history.Count > 1)
                _history.RemoveRange(1, _history.Count - 1);
            _conversation = null;

            var settings = _configManager.Load();
            string msg = settings.AI.Language == "Arabic" ? "تم مسح المحادثة. كيف يمكنني مساعدتك؟" : "Chat cleared. How can I help you?";
            AddAiMessage(msg);
        }
    }
}
