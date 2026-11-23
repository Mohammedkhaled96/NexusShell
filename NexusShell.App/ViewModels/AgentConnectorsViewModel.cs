using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusShell.Agents.Application.Connectors;
using NexusShell.Agents.Domain.Connectors;

namespace NexusShell.App.ViewModels
{
    /// <summary>A value the user types for a connector (client id, token, address ...).</summary>
    public sealed class ConnectorFieldRow : ObservableObject
    {
        public ConnectorFieldRow(ConnectorField field, bool isSet)
        {
            Field = field;
            _isSet = isSet;
        }

        public ConnectorField Field { get; }

        public string Variable => Field.Variable;

        public string Label => Field.Label + (Field.Optional ? " (optional)" : string.Empty);

        public bool IsSecret => Field.IsSecret;

        public bool IsPlain => !Field.IsSecret;

        public string Help => Field.Help;

        private bool _isSet;
        public bool IsSet
        {
            get => _isSet;
            set
            {
                if (SetProperty(ref _isSet, value)) OnPropertyChanged(nameof(StateText));
            }
        }

        public string StateText => IsSet ? $"Saved in {Variable}" : $"Not set ({Variable})";

        /// <summary>Typed text for non-secret fields (secrets are read from the password box once).</summary>
        private string _text = string.Empty;
        public string Text { get => _text; set => SetProperty(ref _text, value); }

        public string AutomationName => $"{Field.Label}{(Field.Optional ? ", optional" : string.Empty)}, {(IsSet ? "already saved" : "not set")}";
    }

    /// <summary>A connector or MCP template in the list.</summary>
    public sealed class ConnectorRow
    {
        public ConnectorRow(ConnectorInfo info)
        {
            Id = info.Definition.Id;
            Name = info.Definition.Name;
            Description = info.Definition.Description;
            IsMcp = false;
            IsOAuth = info.Definition.Auth == ConnectorAuthKind.OAuth;
            State = info.State;
            StateText = info.State switch
            {
                ConnectorState.Connected => "connected",
                ConnectorState.NeedsSignIn => "ready to sign in",
                _ => "needs setup",
            };
            Fields = info.Definition.Fields.Select(f => new ConnectorFieldRow(f, info.FieldsSet.GetValueOrDefault(f.Variable))).ToList();
            Steps = Number(info.Definition.SetupSteps);
            Abilities = string.Join("\n", info.Definition.Abilities.Select(a => "- " + a.Description + (a.AsksFirst ? " (asks you first)" : string.Empty)));
            SetupUrl = info.Definition.SetupUrl;
            SetupLinks = info.Definition.SetupLinks;
            SupportsClientFile = info.Definition.SupportsClientFile;
            Note = string.Empty;
        }

        public ConnectorRow(McpConnectorInfo info)
        {
            Id = info.Preset.Id;
            Name = info.Preset.Name;
            Description = info.Preset.Description;
            IsMcp = true;
            Added = info.Added;
            StateText = !info.RuntimeFound ? $"needs {info.Preset.Requires}" : info.Ready ? "added and ready" : info.Added ? "added, settings missing" : "not added";
            Fields = info.Preset.Fields.Select(f => new ConnectorFieldRow(f, info.FieldsSet.GetValueOrDefault(f.Variable))).ToList();
            Steps = Number(info.Preset.SetupSteps);
            Abilities = "- " + info.Preset.Description + "\n- Tools that change something ask you first, unless the server marks them read-only.";
            SetupUrl = info.Preset.SetupUrl;
            Note = string.Join(" ", new[] { "Needs: " + info.Preset.Requires, info.Preset.Note }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        public string Id { get; }

        public string Name { get; }

        public string Description { get; }

        public bool IsMcp { get; }

        public bool IsNative => !IsMcp;

        public bool IsOAuth { get; }

        public bool Added { get; }

        public ConnectorState State { get; }

        public bool IsConnected => IsNative && State == ConnectorState.Connected;

        public string StateText { get; }

        public string Group => IsMcp ? "More apps (MCP)" : "Built-in";

        public IReadOnlyList<ConnectorFieldRow> Fields { get; }

        public string Steps { get; }

        public string Abilities { get; }

        public string SetupUrl { get; }

        /// <summary>One button per setup page.</summary>
        public IReadOnlyList<ConnectorLink> SetupLinks { get; } = [];

        public bool HasSetupLinks => SetupLinks.Count > 0;

        public bool HasSingleSetupPage => SetupLinks.Count == 0;

        /// <summary>Google: the downloaded client file can be imported instead of typing.</summary>
        public bool SupportsClientFile { get; }

        public string Note { get; }

        public bool HasNote => Note.Length > 0;

        public string ConnectLabel => IsOAuth ? "_Sign in" : "Conn_ect";

        /// <summary>What screen readers say for the main button.</summary>
        public string ConnectName => IsOAuth ? $"Sign in to {Name} in your browser" : $"Connect {Name} and check the token";

        public string Text => $"{Name}  -  {StateText}";

        public string AutomationName => $"{Name}, {StateText}, {Group}";

        /// <summary>Numbers the steps, unless they already say "Step 1:" (a second number would be read aloud too).</summary>
        private static string Number(IReadOnlyList<string> steps) =>
            steps.Any(s => s.StartsWith("Step ", StringComparison.Ordinal))
                ? string.Join("\n", steps)
                : string.Join("\n", steps.Select((s, i) => $"{i + 1}. {s}"));
    }

    /// <summary>
    /// Connectors window: connect Gmail and Google Calendar, Outlook, GitHub, and add MCP apps
    /// (Notion, Jira and Confluence, Slack ...). Values go to user environment variables; sign-in
    /// happens in the user's own browser.
    /// </summary>
    public sealed class AgentConnectorsViewModel : ViewModelBase
    {
        private readonly IConnectorService _connectors;

        public AgentConnectorsViewModel(IConnectorService connectors) => _connectors = connectors;

        public ObservableCollection<ConnectorRow> Items { get; } = new();

        private ConnectorRow? _selected;
        public ConnectorRow? Selected
        {
            get => _selected;
            set
            {
                if (SetProperty(ref _selected, value)) OnPropertyChanged(nameof(HasSelection));
            }
        }

        public bool HasSelection => Selected is not null;

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle));
            }
        }

        public bool IsIdle => !IsBusy;

        private string _summary = string.Empty;
        public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

        private string _statusMessage = "Connect NexusAgent to your apps. Reading is free; sending, creating and deleting always ask you first. Keys and sign-ins are kept only in your Windows user environment.";
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

        public async Task LoadAsync()
        {
            string? selected = Selected?.Id;
            var rows = _connectors.List().Select(c => new ConnectorRow(c)).ToList();
            rows.AddRange((await _connectors.ListMcpAsync()).Select(m => new ConnectorRow(m)));
            Items.Clear();
            foreach (var row in rows) Items.Add(row);
            Selected = Items.FirstOrDefault(r => r.Id == selected) ?? Items.FirstOrDefault();
            int connected = rows.Count(r => r.IsConnected) + rows.Count(r => r.IsMcp && r.Added);
            Summary = $"{rows.Count} connectors, {connected} connected or added";
        }

        public async Task<bool> SaveFieldAsync(ConnectorFieldRow field, string value)
        {
            if (Selected is not { } row) return false;
            try
            {
                if (row.IsMcp) await _connectors.SaveMcpFieldAsync(row.Id, field.Variable, value);
                else await _connectors.SaveFieldAsync(row.Id, field.Variable, value);
                field.IsSet = true;
                field.Text = string.Empty;
                StatusMessage = $"{field.Field.Label} saved in your user environment ({field.Variable}).";
                await LoadAsync();
                return true;
            }
            catch (ArgumentException ex)
            {
                StatusMessage = "Not saved: " + ex.Message;
                return false;
            }
        }

        public async Task ConnectAsync()
        {
            if (Selected is not { IsNative: true } row) return;
            await RunAsync(row.IsOAuth
                    ? $"Your browser is opening {row.Name}'s sign-in page. Sign in there and allow access, then come back."
                    : $"Checking the {row.Name} token...",
                async () => (await _connectors.ConnectAsync(row.Id)).Detail);
        }

        public async Task ImportClientFileAsync(string path)
        {
            if (Selected is not { SupportsClientFile: true } row) return;
            await RunAsync($"Reading the client file, then your browser opens {row.Name}'s sign-in page. Choose your account there and allow access.",
                async () => (await _connectors.ImportClientFileAsync(row.Id, path)).Detail);
        }

        public Task TestAsync() =>
            Selected is { IsNative: true } row
                ? RunAsync($"Testing {row.Name}...", async () => (await _connectors.TestAsync(row.Id)).Detail)
                : Task.CompletedTask;

        public Task DisconnectAsync() =>
            Selected is { IsNative: true } row
                ? RunAsync($"Disconnecting {row.Name}...", async () =>
                {
                    await _connectors.DisconnectAsync(row.Id);
                    return $"{row.Name} disconnected. The agent no longer has its tools.";
                })
                : Task.CompletedTask;

        public Task AddMcpAsync() =>
            Selected is { IsMcp: true } row
                ? RunAsync($"Adding {row.Name}...", async () =>
                {
                    await _connectors.AddMcpAsync(row.Id);
                    var info = (await _connectors.ListMcpAsync()).Single(m => m.Preset.Id == row.Id);
                    return info.Ready
                        ? $"{row.Name} added. Every expert can use it in new messages."
                        : !info.RuntimeFound
                            ? $"{row.Name} added, but {info.Preset.Requires} was not found. Install it, then restart NexusShell."
                            : $"{row.Name} added. Fill in the missing settings to use it.";
                })
                : Task.CompletedTask;

        public Task RemoveMcpAsync() =>
            Selected is { IsMcp: true } row
                ? RunAsync($"Removing {row.Name}...", async () =>
                {
                    await _connectors.RemoveMcpAsync(row.Id);
                    return $"{row.Name} removed. Its saved settings stay in your environment.";
                })
                : Task.CompletedTask;

        private async Task RunAsync(string working, Func<Task<string>> action)
        {
            IsBusy = true;
            StatusMessage = working;
            try
            {
                StatusMessage = await action();
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
            finally
            {
                IsBusy = false;
                await LoadAsync();
            }
        }

        /// <summary>Every connected app, for the chat's status line and /connectors.</summary>
        public static string Describe(IConnectorService connectors)
        {
            var connected = connectors.List().Where(c => c.State == ConnectorState.Connected).Select(c => c.Definition.Name).ToList();
            var sb = new StringBuilder();
            sb.Append(connected.Count == 0 ? "No apps connected yet." : "Connected apps: " + string.Join(", ", connected) + ".");
            return sb.ToString();
        }
    }
}
