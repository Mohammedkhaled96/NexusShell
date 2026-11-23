using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using NexusShell.Agents.Application.Chats;
using NexusShell.Agents.Application.Conversations;
using NexusShell.Agents.Application.Scheduling;
using NexusShell.Agents.Domain.Chats;
using NexusShell.App.Commands;
using NexusShell.App.Models;
using NexusShell.App.Services.Agents;

namespace NexusShell.App.ViewModels
{
    /// <summary>One entry of the "Chat history" box. <see cref="AutomationName"/> is what screen readers say.</summary>
    public sealed class ChatChoice
    {
        public ChatChoice(ChatSummary? summary) => Summary = summary;

        /// <summary>Null for the "New chat" entry.</summary>
        public ChatSummary? Summary { get; }

        public string? Id => Summary?.Id;

        public string Text => Summary is null
            ? "New chat (saved when you send a message)"
            : (Summary.Pinned ? "Pinned: " : string.Empty) + Summary.Title + "  -  " + When(Summary.UpdatedAt) +
              (Summary.ScheduledTaskId is not null ? "  -  scheduled task" : string.Empty);

        public string AutomationName => Summary is null
            ? "New chat, not saved yet"
            : $"{Summary.Title}, {When(Summary.UpdatedAt)}, {Summary.MessageCount} messages" +
              (Summary.Pinned ? ", pinned" : string.Empty) +
              (Summary.ScheduledTaskId is not null ? ", written by a scheduled task" : string.Empty);

        public string HelpText => Summary is null ? "Start a new conversation." : "Last answer: " + Summary.Preview;

        public static string When(DateTimeOffset at)
        {
            var local = at.ToLocalTime();
            var today = DateTime.Today;
            if (local.Date == today) return "today " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (local.Date == today.AddDays(-1)) return "yesterday " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
            return local.ToString(local.Year == today.Year ? "d MMMM HH:mm" : "d MMMM yyyy HH:mm", CultureInfo.InvariantCulture);
        }

        public override string ToString() => Text;
    }

    /// <summary>
    /// Saved chats: every conversation is saved on the device after each answer (one folder per
    /// chat, with the agent's own state), listed in "Chat history" and continued with full context.
    /// </summary>
    public partial class AIChatViewModel
    {
        private static readonly ChatChoice NewChatChoice = new(null);

        private IChatLibrary? _chats;
        private IAgentScheduler? _scheduler;
        private Action<Action<string>?>? _openSchedule;
        private string _baseWindowTitle = "NexusAI Chat";

        private string? _chatId;
        private ChatSummary? _chatSummary;
        private ConversationResume? _resume;
        private string? _savedAgentState;
        private string? _pendingTitle;
        private bool _syncingHistory;

        /// <summary>"New chat" first, then the saved chats (pinned first, newest first).</summary>
        public ObservableCollection<ChatChoice> ChatHistory { get; } = new();

        private ChatChoice? _selectedChat;
        public ChatChoice? SelectedChat
        {
            get => _selectedChat;
            set
            {
                if (value is null || !SetProperty(ref _selectedChat, value) || _syncingHistory) return;
                _ = SelectChatAsync(value);
            }
        }

        public bool HasSavedChat => _chatId is not null;

        public bool CurrentChatPinned => _chatSummary?.Pinned == true;

        public string CurrentChatTitle => _chatSummary?.Title ?? _pendingTitle ?? "New chat";

        public string? CurrentChatFolder => _chatId is null || _chats is null ? null : _chats.FolderOf(_chatId);

        public string ChatsFolder => _chats?.RootDirectory ?? string.Empty;

        public bool ChatsAvailable => _chats is not null;

        public ICommand NewChatCommand { get; private set; } = null!;

        public ICommand OpenScheduleCommand { get; private set; } = null!;

        private void InitializeChats(IChatLibrary? chats, IAgentScheduler? scheduler, Action<Action<string>?>? openSchedule)
        {
            _chats = chats;
            _scheduler = scheduler;
            _openSchedule = openSchedule;
            _baseWindowTitle = WindowTitle;

            NewChatCommand = new RelayCommand(_ => NewChat(), _ => !IsSending);
            OpenScheduleCommand = new RelayCommand(_ => OpenSchedule(), _ => _openSchedule is not null);

            ChatHistory.Add(NewChatChoice);
            _selectedChat = NewChatChoice;
            ChatHub.ChatsChanged += OnChatsChanged;
            _ = RefreshChatHistoryAsync();
        }

        private void OpenSchedule() => _openSchedule?.Invoke(id => _ = OpenChatAsync(id));

        private void OnChatsChanged(object? sender)
        {
            if (ReferenceEquals(sender, this)) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => _ = RefreshChatHistoryAsync()));
        }

        public async Task RefreshChatHistoryAsync()
        {
            if (_chats is null) return;
            IReadOnlyList<ChatSummary> saved;
            try
            {
                saved = await _chats.ListAsync();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Chat history could not be read.");
                return;
            }

            _syncingHistory = true;
            try
            {
                // Refresh in place: "New chat" stays at index 0 so the box never loses its selection.
                while (ChatHistory.Count > 1) ChatHistory.RemoveAt(ChatHistory.Count - 1);
                foreach (var summary in saved) ChatHistory.Add(new ChatChoice(summary));
                _selectedChat = ChatHistory.FirstOrDefault(c => c.Id is not null && c.Id == _chatId) ?? NewChatChoice;
            }
            finally
            {
                _syncingHistory = false;
            }
            OnPropertyChanged(nameof(SelectedChat));
        }

        private async Task SelectChatAsync(ChatChoice choice)
        {
            if (choice.Id is null)
            {
                if (_chatId is not null || Messages.Any(m => m.IsUser)) NewChat();
                return;
            }
            if (choice.Id != _chatId) await OpenChatAsync(choice.Id);
        }

        /// <summary>Shows a saved chat and gives the agent its saved state, so the conversation or task continues.</summary>
        public async Task OpenChatAsync(string chatId)
        {
            if (_chats is null) return;
            if (IsSending)
            {
                Speak("Wait for the answer, or press Stop, before opening another chat.");
                await RefreshChatHistoryAsync();
                return;
            }

            SavedChat? chat;
            try
            {
                chat = await _chats.OpenAsync(chatId);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Chat {Chat} could not be opened.", chatId);
                chat = null;
            }
            if (chat is null)
            {
                Speak("That chat could not be opened; it may have been deleted.");
                await RefreshChatHistoryAsync();
                return;
            }

            _chatId = chat.Summary.Id;
            _chatSummary = chat.Summary;
            _pendingTitle = null;
            _savedAgentState = chat.AgentState;
            _resume = ConversationResume.FromChat(chat);
            _conversation = null; // started with the saved state on the next message

            Messages.Clear();
            foreach (var message in chat.Messages)
            {
                Messages.Add(new ChatMessage
                {
                    Role = message.Role == "user" ? "user" : "assistant",
                    Content = message.Content,
                    IsUser = message.Role == "user",
                    SpeakerName = message.Speaker,
                    At = message.At,
                });
            }
            MessageAdded?.Invoke();

            // The classic chat continues too.
            if (_history.Count > 1) _history.RemoveRange(1, _history.Count - 1);
            foreach (var message in chat.Messages.Where(m => m.Role is "user" or "assistant").TakeLast(20))
                _history.Add(new() { ["role"] = message.Role, ["content"] = message.Content });

            if (chat.Summary.WorkingDirectory is { Length: > 0 } folder && Directory.Exists(folder)
                && !string.Equals(folder, Workspace.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                Workspace.SetWorkingDirectory(folder);
            }

            WindowTitle = "NexusAI Chat - " + chat.Summary.Title;
            await RefreshChatHistoryAsync();
            NotifyChatState();
            Speak($"Opened chat {chat.Summary.Title}, {chat.Messages.Count} messages. NexusAgent remembers this conversation, so you can continue where you left off.");
        }

        /// <summary>Starts a new conversation; the current one is already saved.</summary>
        public void NewChat()
        {
            if (IsSending)
            {
                Speak("Wait for the answer, or press Stop, before starting a new chat.");
                return;
            }

            bool hadChat = _chatId is not null || Messages.Any(m => m.IsUser);
            Messages.Clear();
            if (_history.Count > 1) _history.RemoveRange(1, _history.Count - 1);
            _conversation = null;
            _chatId = null;
            _chatSummary = null;
            _resume = null;
            _savedAgentState = null;
            _pendingTitle = null;
            WindowTitle = _baseWindowTitle;

            bool arabic = _configManager.Load().AI.Language == "Arabic";
            string greeting = arabic
                ? "محادثة جديدة. كيف يمكنني مساعدتك؟" + (hadChat ? " المحادثة السابقة محفوظة في سجل المحادثات." : string.Empty)
                : "New chat. How can I help you?" + (hadChat ? " The previous chat is saved in Chat history." : string.Empty);
            AddAiMessage(greeting);
            _ = RefreshChatHistoryAsync();
            NotifyChatState();
            Speak(greeting);
        }

        /// <summary>Saves the chat after each answer (nothing is saved before the first question).</summary>
        private async Task SaveChatAsync()
        {
            if (_chats is null || !Messages.Any(m => m.IsUser)) return;
            try
            {
                _chatId ??= _chats.NewId();
                if (_conversation is not null && await _conversation.ExportStateAsync() is { } state) _savedAgentState = state;

                var options = Workspace.BuildOptions();
                var summary = (_chatSummary ?? new ChatSummary
                {
                    Id = _chatId,
                    Title = _pendingTitle ?? "New chat",
                    TitleSetByUser = _pendingTitle is not null,
                }) with
                {
                    Model = options.Model,
                    Expert = options.Expert,
                    Permission = options.Permission.ToString(),
                    WorkingDirectory = options.WorkingDirectory,
                };

                var messages = Messages.Select(m => new SavedChatMessage(
                    m.IsUser ? "user" : m.SpeakerName == ShellLabel ? "note" : "assistant",
                    m.SpeakerName,
                    m.Content,
                    m.At)).ToList();

                _chatSummary = await _chats.SaveAsync(new SavedChat { Summary = summary, Messages = messages, AgentState = _savedAgentState });
                _pendingTitle = null;
                WindowTitle = "NexusAI Chat - " + _chatSummary.Title;
                ChatHub.RaiseChatsChanged(this);
                await RefreshChatHistoryAsync();
                NotifyChatState();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "The chat could not be saved.");
            }
        }

        private void NotifyChatState()
        {
            OnPropertyChanged(nameof(HasSavedChat));
            OnPropertyChanged(nameof(CurrentChatPinned));
            OnPropertyChanged(nameof(CurrentChatTitle));
            OnPropertyChanged(nameof(CurrentChatFolder));
            OnPropertyChanged(nameof(ChatsFolder));
            CommandManager.InvalidateRequerySuggested();
        }

        // ---------- Chat actions (menu and commands) ----------

        public async Task<string> RenameCurrentAsync(string title)
        {
            if (_chats is null) return "Saved chats are not available.";
            if (string.IsNullOrWhiteSpace(title)) return "Type a name for the chat.";
            if (_chatId is null || _chatSummary is null)
            {
                _pendingTitle = title.Trim();
                NotifyChatState();
                return $"This chat will be saved as \"{_pendingTitle}\" when you send the first message.";
            }

            var renamed = await _chats.RenameAsync(_chatId, title);
            if (renamed is null) return "The chat could not be renamed.";
            _chatSummary = renamed;
            WindowTitle = "NexusAI Chat - " + renamed.Title;
            ChatHub.RaiseChatsChanged(this);
            await RefreshChatHistoryAsync();
            NotifyChatState();
            return $"Renamed to \"{renamed.Title}\".";
        }

        public async Task<string> TogglePinCurrentAsync()
        {
            if (_chats is null || _chatId is null) return "Send a message first: this chat is not saved yet.";
            var updated = await _chats.SetPinnedAsync(_chatId, !CurrentChatPinned);
            if (updated is null) return "The chat could not be changed.";
            _chatSummary = updated;
            ChatHub.RaiseChatsChanged(this);
            await RefreshChatHistoryAsync();
            NotifyChatState();
            return updated.Pinned ? "Pinned: this chat stays at the top of Chat history." : "Unpinned.";
        }

        public async Task<string> DuplicateCurrentAsync()
        {
            if (_chats is null || _chatId is null) return "Send a message first: this chat is not saved yet.";
            var copy = await _chats.DuplicateAsync(_chatId);
            if (copy is null) return "The chat could not be copied.";
            ChatHub.RaiseChatsChanged(this);
            await RefreshChatHistoryAsync();
            return $"Saved a copy as \"{copy.Title}\". Open it from Chat history to try another direction.";
        }

        public async Task<string> DeleteCurrentAsync()
        {
            if (_chats is null || _chatId is null) return "This chat is not saved, so there is nothing to delete.";
            string title = CurrentChatTitle;
            bool deleted = await _chats.DeleteAsync(_chatId);
            _chatId = null; // the conversation on screen is no longer saved
            _chatSummary = null;
            NewChat();
            ChatHub.RaiseChatsChanged(this);
            return deleted ? $"Deleted \"{title}\". It is in the Recycle Bin if you need it back." : "The chat was already gone.";
        }

        public async Task<string> ExportCurrentAsync(string path)
        {
            if (_chats is null || _chatId is null) return "Send a message first: this chat is not saved yet.";
            string? text = await _chats.ExportTextAsync(_chatId);
            if (text is null) return "The chat could not be exported.";
            await File.WriteAllTextAsync(path, text);
            return $"Exported to {path}.";
        }

        public async Task<string> MoveChatsAsync(string folder)
        {
            if (_chats is null) return "Saved chats are not available.";
            try
            {
                int moved = await _chats.MoveAsync(folder);
                var settings = _configManager.Load();
                settings.AI.ChatsDirectory = _chats.RootDirectory;
                _configManager.Save(settings);
                ChatHub.RaiseChatsChanged(this);
                await RefreshChatHistoryAsync();
                NotifyChatState();
                return $"Chats are now saved in {_chats.RootDirectory}. Moved {moved} chats there.";
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return "The chats could not be moved: " + ex.Message;
            }
        }

        // ---------- Slash commands for chats and schedules ----------

        private async Task<string> ChatsCommandAsync()
        {
            if (_chats is null) return "Saved chats are not available.";
            var list = await _chats.ListAsync();
            if (list.Count == 0) return "No saved chats yet. Chats are saved automatically after the first answer.";
            var lines = list.Take(15).Select((c, i) => $"{i + 1}. {c.Title} ({ChatChoice.When(c.UpdatedAt)}, {c.MessageCount} messages{(c.Pinned ? ", pinned" : string.Empty)})");
            return $"{list.Count} saved chats{(list.Count > 15 ? ", the 15 most recent" : string.Empty)}:\n" + string.Join("\n", lines) +
                   "\nType /open followed by a number or part of the title, or use the Chat history box (Alt+O).";
        }

        private async Task<string> OpenCommandAsync(string argument)
        {
            if (_chats is null) return "Saved chats are not available.";
            if (argument.Length == 0) return "Write /open followed by a number from /chats, or part of a chat's title.";
            var list = await _chats.ListAsync();
            var match = int.TryParse(argument, out int number) && number >= 1 && number <= list.Count
                ? list[number - 1]
                : list.FirstOrDefault(c => c.Title.Contains(argument, StringComparison.CurrentCultureIgnoreCase));
            if (match is null) return $"No saved chat matches \"{argument}\". Type /chats to list them.";
            _ = OpenChatAsync(match.Id);
            return string.Empty;
        }

        private async Task<string> TasksCommandAsync()
        {
            if (_scheduler is null) return "Scheduling is not available.";
            var tasks = await _scheduler.ListAsync();
            if (tasks.Count == 0) return "No scheduled tasks. Ask the agent, for example: every weekday at 9 check free disk space. Or open Schedule (Alt+E).";
            var lines = tasks.Select(t => $"{t.Name}: {t.Schedule.Describe(_scheduler.TimeZone)}; " +
                (t.Enabled && t.NextRunAt is { } next ? "next " + ChatChoice.When(next) : "turned off") +
                $"; last result: {t.LastStatus.ToString().ToLowerInvariant()}");
            return $"{tasks.Count} scheduled tasks:\n" + string.Join("\n", lines) + "\nOpen Schedule (Alt+E) to add, change or run them.";
        }
    }
}
