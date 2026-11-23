using System.Collections.ObjectModel;
using System.Globalization;
using NexusShell.Agents.Application.Memory;
using NexusShell.Agents.Domain.Memory;

namespace NexusShell.App.ViewModels
{
    /// <summary>One saved memory as the list shows and speaks it.</summary>
    public sealed class MemoryRow
    {
        public MemoryRow(MemoryEntry entry) => Entry = entry;

        public MemoryEntry Entry { get; }

        public string Id => Entry.Id;

        public string Text => (Entry.Pinned ? "[pinned] " : string.Empty) + Entry.Content;

        public string AutomationName
        {
            get
            {
                string source = Entry.Source switch
                {
                    MemorySource.User => "you said it",
                    MemorySource.Tool => "verified with a tool",
                    _ => "agent's own note, unverified",
                };
                string where = Entry.Scope is null ? "all folders" : "only in " + Entry.Scope;
                return $"{Entry.Content}. {KindText(Entry.Kind)}, {source}, {where}, saved {Entry.UpdatedAt.LocalDateTime.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}" +
                       (Entry.Pinned ? ", pinned" : string.Empty) + $", used {Entry.UseCount} times. Id {Entry.Id}.";
            }
        }

        public static string KindText(MemoryKind kind) => kind switch
        {
            MemoryKind.Preference => "preference",
            MemoryKind.Project => "project fact",
            MemoryKind.Correction => "correction",
            MemoryKind.Decision => "decision",
            _ => "fact",
        };
    }

    /// <summary>
    /// The agent's long-term memory, for the user: read every memory, search, add, correct,
    /// pin (always recalled) and delete. Memories never contain secrets (refused when saving).
    /// </summary>
    public sealed class AgentMemoryViewModel : ViewModelBase
    {
        private readonly IAgentMemory _memory;
        private List<MemoryEntry> _all = new();

        public AgentMemoryViewModel(IAgentMemory memory)
        {
            _memory = memory;
            foreach (var kind in Enum.GetValues<MemoryKind>())
            {
                string text = char.ToUpperInvariant(MemoryRow.KindText(kind)[0]) + MemoryRow.KindText(kind)[1..];
                Kinds.Add(new ChoiceItem(kind.ToString(), text, text));
            }
            _selectedKind = Kinds[0];
        }

        public ObservableCollection<MemoryRow> Memories { get; } = new();

        public ObservableCollection<ChoiceItem> Kinds { get; } = new();

        /// <summary>The chat's working folder (for "only in this folder").</summary>
        private string? _workingFolder;
        public string? WorkingFolder
        {
            get => _workingFolder;
            set
            {
                if (SetProperty(ref _workingFolder, value))
                {
                    OnPropertyChanged(nameof(HasWorkingFolder));
                    OnPropertyChanged(nameof(FolderOnlyLabel));
                }
            }
        }

        public bool HasWorkingFolder => !string.IsNullOrWhiteSpace(WorkingFolder);

        public string FolderOnlyLabel => HasWorkingFolder ? $"Only for the working folder ({WorkingFolder})" : "Only for the working folder (choose one in the chat first)";

        private string _searchText = string.Empty;
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value)) ApplyFilter();
            }
        }

        private MemoryRow? _selectedMemory;
        public MemoryRow? SelectedMemory
        {
            get => _selectedMemory;
            set
            {
                if (!SetProperty(ref _selectedMemory, value)) return;
                OnPropertyChanged(nameof(HasSelection));
                if (value is null) return;
                MemoryText = value.Entry.Content;
                SelectedKind = Kinds.First(k => k.Value == value.Entry.Kind.ToString());
            }
        }

        public bool HasSelection => SelectedMemory is not null;

        private string _memoryText = string.Empty;
        public string MemoryText { get => _memoryText; set => SetProperty(ref _memoryText, value); }

        private ChoiceItem _selectedKind;
        public ChoiceItem SelectedKind { get => _selectedKind; set => SetProperty(ref _selectedKind, value ?? Kinds[0]); }

        private bool _folderOnly;
        public bool FolderOnly { get => _folderOnly; set => SetProperty(ref _folderOnly, value); }

        private bool _pinNew;
        public bool PinNew { get => _pinNew; set => SetProperty(ref _pinNew, value); }

        private string _summary = string.Empty;
        public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

        private string _statusMessage = "These are the facts NexusAgent remembers between conversations. It recalls the relevant ones for each message. Memories never contain passwords or keys.";
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

        public async Task LoadAsync()
        {
            _all = (await _memory.ListAsync()).ToList();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string? selected = SelectedMemory?.Id;
            string query = SearchText.Trim();
            var rows = _all
                .Where(e => query.Length == 0
                    || e.Content.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || e.Id.Equals(query, StringComparison.OrdinalIgnoreCase)
                    || e.Tags.Any(t => t.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
                .Select(e => new MemoryRow(e))
                .ToList();

            Memories.Clear();
            foreach (var row in rows) Memories.Add(row);
            _selectedMemory = Memories.FirstOrDefault(r => r.Id == selected);
            OnPropertyChanged(nameof(SelectedMemory));
            OnPropertyChanged(nameof(HasSelection));

            int pinned = _all.Count(e => e.Pinned);
            Summary = _all.Count == 0
                ? "No memories yet"
                : query.Length == 0
                    ? $"{_all.Count} memories, {pinned} pinned"
                    : $"{rows.Count} of {_all.Count} memories match";
        }

        public async Task<bool> AddAsync()
        {
            var result = await _memory.RememberAsync(new MemoryDraft(
                MemoryText,
                Enum.Parse<MemoryKind>(SelectedKind.Value!),
                MemorySource.User,
                Evidence: "Added in the Memory window",
                Scope: FolderOnly && HasWorkingFolder ? WorkingFolder : null,
                Pinned: PinNew));
            return await AfterSaveAsync(result, result.Updated ? "An almost identical memory existed; it was updated" : "Saved");
        }

        public async Task<bool> SaveChangesAsync()
        {
            if (SelectedMemory is null)
            {
                StatusMessage = "Choose a memory in the list first, or use Add as new.";
                return false;
            }
            var result = await _memory.UpdateAsync(SelectedMemory.Id, MemoryText);
            return await AfterSaveAsync(result, "Changes saved");
        }

        private async Task<bool> AfterSaveAsync(MemorySaveResult result, string done)
        {
            if (!result.Saved)
            {
                StatusMessage = result.Refusal ?? "Not saved.";
                return false;
            }
            await LoadAsync();
            _selectedMemory = Memories.FirstOrDefault(r => r.Id == result.Entry!.Id);
            OnPropertyChanged(nameof(SelectedMemory));
            OnPropertyChanged(nameof(HasSelection));
            StatusMessage = $"{done}: {result.Entry!.Content}";
            return true;
        }

        public async Task TogglePinAsync()
        {
            if (SelectedMemory is not { } row) return;
            bool pin = !row.Entry.Pinned;
            await _memory.SetPinnedAsync(row.Id, pin);
            await LoadAsync();
            StatusMessage = pin ? "Pinned: it is recalled in every conversation." : "Unpinned: it is recalled only when relevant.";
        }

        public async Task DeleteAsync()
        {
            if (SelectedMemory is not { } row) return;
            int index = Memories.IndexOf(row);
            await _memory.ForgetAsync(row.Id);
            await LoadAsync();
            _selectedMemory = Memories.Count == 0 ? null : Memories[Math.Min(index, Memories.Count - 1)];
            OnPropertyChanged(nameof(SelectedMemory));
            OnPropertyChanged(nameof(HasSelection));
            StatusMessage = $"Deleted: {row.Entry.Content}";
        }

        public async Task ClearAsync()
        {
            int removed = await _memory.ClearAsync();
            await LoadAsync();
            MemoryText = string.Empty;
            StatusMessage = $"Deleted all {removed} memories.";
        }
    }
}
