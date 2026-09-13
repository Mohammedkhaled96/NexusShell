using NexusShell.App.Commands;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;

namespace NexusShell.App.ViewModels
{
    public class AIChatViewModel : ViewModelBase
    {
        private readonly IAIService _aiService;
        private readonly IConfigManager _configManager;
        private readonly List<Dictionary<string, string>> _history;

        private string _userLabel = "You";
        private string _aiLabel = "NexusAI";

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

        public ICommand SendMessageCommand { get; }
        public ICommand ClearChatCommand   { get; }
        public ICommand CopyMessageCommand { get; }

        // Fired when a new message is added — view subscribes to scroll to bottom
        public event Action? MessageAdded;

        public AIChatViewModel(IAIService aiService, IConfigManager configManager, string initialContext = "", string contextLabel = "")
        {
            _aiService = aiService;
            _configManager = configManager;

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

        private void AddAiMessage(string content)
        {
            Messages.Add(new ChatMessage { Role = "assistant", Content = content, IsUser = false, SpeakerName = _aiLabel });
            MessageAdded?.Invoke();
        }

        private async Task ExecuteSendMessage()
        {
            var msg = CurrentMessage.Trim();
            if (string.IsNullOrEmpty(msg)) return;

            CurrentMessage = "";
            IsSending = true;

            AddUserMessage(msg);
            _history.Add(new() { ["role"] = "user", ["content"] = msg });

            try
            {
                var response = await _aiService.SendChatMessageAsync(_history);
                _history.Add(new() { ["role"] = "assistant", ["content"] = response });

                AddAiMessage(response);

                if (_history.Count > 21)
                    _history.RemoveRange(1, _history.Count - 21);
            }
            catch (Exception ex)
            {
                AddAiMessage("Error: " + ex.Message);
            }
            finally
            {
                IsSending = false;
            }
        }

        private void ClearChat()
        {
            Messages.Clear();
            if (_history.Count > 1)
                _history.RemoveRange(1, _history.Count - 1);

            var settings = _configManager.Load();
            string msg = settings.AI.Language == "Arabic" ? "تم مسح المحادثة. كيف يمكنني مساعدتك؟" : "Chat cleared. How can I help you?";
            AddAiMessage(msg);
        }
    }
}
