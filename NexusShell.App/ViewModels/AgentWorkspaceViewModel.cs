using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusShell.Agents.Application.Conversations;
using NexusShell.Agents.Domain.Tools;
using NexusShell.App.Interfaces;

namespace NexusShell.App.ViewModels
{
    /// <summary>
    /// One entry of a combo box. <see cref="AutomationName"/> is what screen readers say for the item (short);
    /// <see cref="HelpText"/> is the longer explanation they read after it.
    /// </summary>
    public sealed class ChoiceItem
    {
        public ChoiceItem(string? value, string text, string automationName, bool isAvailable = true, string detail = "", string helpText = "")
        {
            Value = value;
            Text = text;
            AutomationName = automationName;
            IsAvailable = isAvailable;
            Detail = detail;
            HelpText = helpText;
        }

        public string HelpText { get; }

        /// <summary>The explanation when there is one, otherwise the spoken name.</summary>
        public string Description => string.IsNullOrWhiteSpace(HelpText) ? AutomationName : HelpText;

        /// <summary>null = automatic.</summary>
        public string? Value { get; }

        public string Text { get; }

        public string AutomationName { get; }

        public bool IsAvailable { get; }

        public string Detail { get; }

        public override string ToString() => Text;
    }

    public enum CapabilityKind { Tool, Skill, McpServer }

    /// <summary>A tool, skill or MCP server the user can turn on or off for the agent.</summary>
    public sealed class CapabilityItem : ObservableObject
    {
        public CapabilityItem(CapabilityKind kind, string name, string detail, string description, bool isEnabled)
        {
            Kind = kind;
            Name = name;
            Detail = detail;
            Description = description;
            _isEnabled = isEnabled;
        }

        public CapabilityKind Kind { get; }

        public string Name { get; }

        public string Detail { get; }

        public string Description { get; }

        public string Label => string.IsNullOrWhiteSpace(Detail) ? Name : $"{Name}  ({Detail})";

        public string AutomationName => string.IsNullOrWhiteSpace(Detail) ? Name : $"{Name}, {Detail}";

        private bool _isEnabled;
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }
    }

    /// <summary>A file or folder attached to the next message.</summary>
    public sealed class AttachmentItem
    {
        public AttachmentItem(string path)
        {
            Path = path;
            IsFolder = Directory.Exists(path);
            Name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(Name)) Name = path;
        }

        public string Path { get; }

        public string Name { get; }

        public bool IsFolder { get; }

        public string Text => IsFolder ? $"{Name} (folder)" : Name;

        public string AutomationName => $"{Name}, {(IsFolder ? "folder" : "file")}, {Path}. Press Delete to remove.";
    }

    /// <summary>
    /// The agent window's choices: model (or automatic), expert (or mixture of experts),
    /// permission mode, working folder, attachments and turned-off tools / skills / MCP
    /// servers. Choices are saved in the app settings and become <see cref="ConversationOptions"/>.
    /// </summary>
    public sealed class AgentWorkspaceViewModel : ViewModelBase
    {
        public const int MaxAttachments = 20;

        private readonly IConfigManager _configManager;
        private bool _loading;

        public AgentWorkspaceViewModel(IConfigManager configManager)
        {
            _configManager = configManager;

            Permissions.Add(new ChoiceItem(nameof(PermissionMode.Ask), "Ask before changes", "Ask before changes",
                helpText: "Ask before changes: the agent asks before it edits files or runs commands. Default."));
            Permissions.Add(new ChoiceItem(nameof(PermissionMode.AutoApproveEdits), "Auto-approve file edits", "Auto-approve file edits",
                helpText: "Auto-approve file edits: files are written without asking; commands and network access still ask."));
            Permissions.Add(new ChoiceItem(nameof(PermissionMode.ReadOnly), "Read-only (plan mode)", "Read-only plan mode",
                helpText: "Read-only plan mode: the agent can only read and plan. Tools that change anything are turned off."));

            Models.Add(AutomaticModel);
            Experts.Add(AutomaticExpert);

            var ai = Load();
            _loading = true;
            SelectedPermission = Permissions.FirstOrDefault(p => string.Equals(p.Value, ai.AgentPermission, StringComparison.OrdinalIgnoreCase)) ?? Permissions[0];
            SelectedModel = AutomaticModel;
            SelectedExpert = AutomaticExpert;
            _workingDirectory = string.IsNullOrWhiteSpace(ai.AgentWorkingDirectory) || !Directory.Exists(ai.AgentWorkingDirectory) ? null : ai.AgentWorkingDirectory;
            _savedModel = ai.AgentModel;
            _savedExpert = ai.AgentExpert;
            _disabledTools = new HashSet<string>(ai.AgentDisabledTools ?? new(), StringComparer.OrdinalIgnoreCase);
            _disabledSkills = new HashSet<string>(ai.AgentDisabledSkills ?? new(), StringComparer.OrdinalIgnoreCase);
            _disabledMcp = new HashSet<string>(ai.AgentDisabledMcpServers ?? new(), StringComparer.OrdinalIgnoreCase);
            _loading = false;

            Attachments.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasAttachments));
                OnPropertyChanged(nameof(AttachmentsSummary));
            };
        }

        private static readonly ChoiceItem AutomaticModel = new(null, "Automatic (best available, switches on failure)", "Automatic",
            helpText: "Automatic: the best available model; if it stops, another model continues the task.");

        private static readonly ChoiceItem AutomaticExpert = new(null, "Automatic (mixture of experts)", "Automatic, mixture of experts",
            helpText: "Automatic: the mixture of experts picks the best experts for each message.");

        private string? _savedModel;
        private string? _savedExpert;
        private HashSet<string> _disabledTools;
        private HashSet<string> _disabledSkills;
        private HashSet<string> _disabledMcp;

        /// <summary>Raised with a sentence to announce when a choice changes (the conversation must be updated).</summary>
        public event Action<string>? OptionsChanged;

        public ObservableCollection<ChoiceItem> Models { get; } = new();

        public ObservableCollection<ChoiceItem> Experts { get; } = new();

        public ObservableCollection<ChoiceItem> Permissions { get; } = new();

        public ObservableCollection<AttachmentItem> Attachments { get; } = new();

        public ObservableCollection<CapabilityItem> Tools { get; } = new();

        public ObservableCollection<CapabilityItem> Skills { get; } = new();

        public ObservableCollection<CapabilityItem> McpServers { get; } = new();

        private ChoiceItem _selectedModel = AutomaticModel;
        public ChoiceItem SelectedModel
        {
            get => _selectedModel;
            set
            {
                if (value is null || !SetProperty(ref _selectedModel, value) || _loading) return;
                _savedModel = value.Value ?? "auto";
                Save();
                string note = value.Value is null
                    ? "Model: automatic. The best available model answers, and another one takes over if it stops."
                    : value.IsAvailable
                        ? $"Model: {value.Text}. If it stops, another model continues automatically."
                        : $"Model: {value.Text}. It is not available now ({value.Detail}); another model answers until it is.";
                OptionsChanged?.Invoke(note);
            }
        }

        private ChoiceItem _selectedExpert = AutomaticExpert;
        public ChoiceItem SelectedExpert
        {
            get => _selectedExpert;
            set
            {
                if (value is null || !SetProperty(ref _selectedExpert, value) || _loading) return;
                _savedExpert = value.Value ?? "auto";
                Save();
                OptionsChanged?.Invoke(value.Value is null ? "Experts: automatic, mixture of experts." : $"Expert: {value.Text} answers every message.");
            }
        }

        private ChoiceItem _selectedPermission = null!;
        public ChoiceItem SelectedPermission
        {
            get => _selectedPermission;
            set
            {
                if (value is null || !SetProperty(ref _selectedPermission, value) || _loading) return;
                Save();
                OptionsChanged?.Invoke("Permissions: " + value.Description);
            }
        }

        private string? _workingDirectory;
        public string? WorkingDirectory => _workingDirectory;

        public bool HasWorkingDirectory => _workingDirectory is not null;

        public string WorkingDirectoryText => _workingDirectory ?? "No working folder (choose one so the agent works in your project)";

        public bool HasAttachments => Attachments.Count > 0;

        public string AttachmentsSummary => Attachments.Count switch
        {
            0 => "No attachments",
            1 => "1 attachment for the next message",
            _ => $"{Attachments.Count} attachments for the next message",
        };

        public string CapabilitiesSummary =>
            $"{Tools.Count(t => t.IsEnabled)} of {Tools.Count} tools, {Skills.Count(s => s.IsEnabled)} of {Skills.Count} skills, " +
            $"{McpServers.Count(m => m.IsEnabled)} of {McpServers.Count} MCP servers on";

        private bool _isCatalogLoaded;
        public bool IsCatalogLoaded { get => _isCatalogLoaded; private set => SetProperty(ref _isCatalogLoaded, value); }

        /// <summary>Fills the lists from the agent (models of every provider, experts, tools, skills, MCP servers).</summary>
        public async Task LoadAsync(INexusAgentService service, CancellationToken cancellationToken = default)
        {
            var catalog = await service.GetCatalogAsync(cancellationToken);

            _loading = true;
            try
            {
                string? currentModel = _selectedModel.Value ?? (_savedModel is { } saved && saved != "auto" ? saved : null);
                // Refresh in place and keep "Automatic" (index 0): clearing the list would make the
                // combo box drop its selection and not show it again.
                Trim(Models);
                foreach (var model in catalog.Models)
                {
                    string where = model.IsLocal ? $"{model.ProviderId}, local" : model.ProviderId;
                    string text = model.IsAvailable ? $"{model.ModelId} ({where})" : $"{model.ModelId} ({where}, unavailable)";
                    string spoken = model.IsAvailable ? $"{model.ModelId}, {where}" : $"{model.ModelId}, {where}, unavailable";
                    string help = model.IsAvailable ? "Available." : "Unavailable: " + model.Detail;
                    Models.Add(new ChoiceItem(model.Reference, text, spoken, model.IsAvailable, model.Detail, help));
                }
                SelectedModel = Models.FirstOrDefault(m => m.Value is not null && string.Equals(m.Value, currentModel, StringComparison.OrdinalIgnoreCase)) ?? AutomaticModel;

                string? currentExpert = _selectedExpert.Value ?? (_savedExpert is { } e && e != "auto" ? e : null);
                Trim(Experts);
                foreach (var expert in catalog.Experts)
                    Experts.Add(new ChoiceItem(expert.Id, expert.Name, expert.Name, helpText: expert.Description));
                SelectedExpert = Experts.FirstOrDefault(x => x.Value is not null && string.Equals(x.Value, currentExpert, StringComparison.OrdinalIgnoreCase)) ?? AutomaticExpert;

                Fill(Tools, catalog.Tools.OrderBy(t => t.Category).ThenBy(t => t.Name).Select(t =>
                    new CapabilityItem(CapabilityKind.Tool, t.Name, $"{t.Category}, {RiskText(t.Risk)}", t.Description, !_disabledTools.Contains(t.Name))));
                Fill(Skills, catalog.Skills.Select(s =>
                    new CapabilityItem(CapabilityKind.Skill, s.Name, "skill", s.Description, !_disabledSkills.Contains(s.Name))));
                Fill(McpServers, catalog.McpServers.Select(m =>
                    new CapabilityItem(CapabilityKind.McpServer, m.Name,
                        !m.Enabled ? "turned off in mcp.json" : m.IsConnected ? $"connected, {m.ToolCount} tools" : m.Detail,
                        m.Detail, m.Enabled && !_disabledMcp.Contains(m.Name))));
            }
            finally
            {
                _loading = false;
            }

            // Clearing a list makes the combo box drop its selection: show the current choice again.
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(SelectedExpert));
            IsCatalogLoaded = true;
            OnPropertyChanged(nameof(CapabilitiesSummary));
        }

        private static void Trim(ObservableCollection<ChoiceItem> list)
        {
            while (list.Count > 1) list.RemoveAt(list.Count - 1);
        }

        private static void Fill(ObservableCollection<CapabilityItem> target, IEnumerable<CapabilityItem> items)
        {
            target.Clear();
            foreach (var item in items) target.Add(item);
        }

        private static string RiskText(ToolRisk risk) => risk switch
        {
            ToolRisk.ReadOnly => "read-only",
            ToolRisk.Network => "uses the network",
            ToolRisk.Mutating => "changes files",
            ToolRisk.Destructive => "runs commands, always asks",
            _ => risk.ToString(),
        };

        /// <summary>The choices as conversation options (saved choices apply even before the lists are loaded).</summary>
        public ConversationOptions BuildOptions() => new()
        {
            Model = IsCatalogLoaded ? _selectedModel.Value : Saved(_savedModel),
            Expert = IsCatalogLoaded ? _selectedExpert.Value : Saved(_savedExpert),
            Permission = Enum.TryParse<PermissionMode>(_selectedPermission?.Value, out var mode) ? mode : PermissionMode.Ask,
            WorkingDirectory = _workingDirectory,
            DisabledTools = new HashSet<string>(_disabledTools, StringComparer.OrdinalIgnoreCase),
            DisabledSkills = new HashSet<string>(_disabledSkills, StringComparer.OrdinalIgnoreCase),
            DisabledMcpServers = new HashSet<string>(_disabledMcp, StringComparer.OrdinalIgnoreCase),
        };

        private static string? Saved(string? value) =>
            string.IsNullOrWhiteSpace(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : value;

        /// <summary>Sets (or clears, with null) the folder the agent works in.</summary>
        public void SetWorkingDirectory(string? folder)
        {
            string? full = string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder.Trim().Trim('"'));
            if (full is not null && !Directory.Exists(full))
            {
                OptionsChanged?.Invoke($"Folder not found: {full}");
                return;
            }
            if (string.Equals(full, _workingDirectory, StringComparison.OrdinalIgnoreCase)) return;

            _workingDirectory = full;
            OnPropertyChanged(nameof(WorkingDirectory));
            OnPropertyChanged(nameof(HasWorkingDirectory));
            OnPropertyChanged(nameof(WorkingDirectoryText));
            Save();
            OptionsChanged?.Invoke(full is null ? "Working folder cleared." : $"Working folder: {full}. The agent reads and runs commands there.");
        }

        /// <summary>Adds files or folders for the next message. Returns how many were added.</summary>
        public int AddAttachments(IEnumerable<string> paths)
        {
            int added = 0;
            foreach (string raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw) || Attachments.Count >= MaxAttachments) continue;
                string full;
                try
                {
                    full = Path.GetFullPath(raw.Trim().Trim('"'));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }
                if (!File.Exists(full) && !Directory.Exists(full)) continue;
                if (Attachments.Any(a => string.Equals(a.Path, full, StringComparison.OrdinalIgnoreCase))) continue;
                Attachments.Add(new AttachmentItem(full));
                added++;
            }
            return added;
        }

        public void RemoveAttachment(AttachmentItem? item)
        {
            if (item is not null) Attachments.Remove(item);
        }

        /// <summary>Attachments go with one message; the list is emptied when it is sent.</summary>
        public IReadOnlyList<string> TakeAttachments()
        {
            var paths = Attachments.Select(a => a.Path).ToList();
            Attachments.Clear();
            return paths;
        }

        /// <summary>Applies the capability check boxes (after the capabilities dialog closes with OK).</summary>
        public void ApplyCapabilities()
        {
            _disabledTools = new HashSet<string>(Tools.Where(t => !t.IsEnabled).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
            _disabledSkills = new HashSet<string>(Skills.Where(s => !s.IsEnabled).Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            _disabledMcp = new HashSet<string>(McpServers.Where(m => !m.IsEnabled).Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            Save();
            OnPropertyChanged(nameof(CapabilitiesSummary));
            OptionsChanged?.Invoke("Capabilities updated: " + CapabilitiesSummary + ".");
        }

        /// <summary>Selects a model by reference, model id or part of it ("auto" = automatic). Used by /model.</summary>
        public ChoiceItem? FindModel(string query) => Find(Models, query, AutomaticModel);

        public ChoiceItem? FindExpert(string query) => Find(Experts, query, AutomaticExpert);

        public ChoiceItem? FindPermission(string query)
        {
            string q = query.Trim().ToLowerInvariant();
            string? value = q switch
            {
                "ask" or "default" or "normal" => nameof(PermissionMode.Ask),
                "edits" or "edit" or "auto" or "acceptedits" or "auto-approve" => nameof(PermissionMode.AutoApproveEdits),
                "readonly" or "read-only" or "plan" or "read" => nameof(PermissionMode.ReadOnly),
                _ => null,
            };
            return value is null ? null : Permissions.First(p => p.Value == value);
        }

        private static ChoiceItem? Find(IEnumerable<ChoiceItem> items, string query, ChoiceItem automatic)
        {
            string q = query.Trim();
            if (q.Length == 0) return null;
            if (q.Equals("auto", StringComparison.OrdinalIgnoreCase) || q.Equals("automatic", StringComparison.OrdinalIgnoreCase)) return automatic;
            var list = items.Where(i => i.Value is not null).ToList();
            return list.FirstOrDefault(i => string.Equals(i.Value, q, StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault(i => i.Value!.EndsWith("/" + q, StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault(i => string.Equals(i.Text, q, StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault(i => i.Value!.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Text.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        private NexusShell.App.Models.AIConfig Load()
        {
            try
            {
                return _configManager.Load().AI;
            }
            catch (Exception)
            {
                return new NexusShell.App.Models.AIConfig();
            }
        }

        private void Save()
        {
            try
            {
                var settings = _configManager.Load();
                settings.AI.AgentModel = _savedModel ?? "auto";
                settings.AI.AgentExpert = _savedExpert ?? "auto";
                settings.AI.AgentPermission = _selectedPermission?.Value ?? nameof(PermissionMode.Ask);
                settings.AI.AgentWorkingDirectory = _workingDirectory ?? string.Empty;
                settings.AI.AgentDisabledTools = _disabledTools.Order(StringComparer.OrdinalIgnoreCase).ToList();
                settings.AI.AgentDisabledSkills = _disabledSkills.Order(StringComparer.OrdinalIgnoreCase).ToList();
                settings.AI.AgentDisabledMcpServers = _disabledMcp.Order(StringComparer.OrdinalIgnoreCase).ToList();
                _configManager.Save(settings);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "Agent window choices could not be saved.");
            }
        }
    }
}
