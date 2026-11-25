using System.Collections.ObjectModel;
using System.Text;
using NexusShell.Agents.Application.ToolPackages;
using NexusShell.Agents.Domain.ToolPackages;

namespace NexusShell.App.ViewModels
{
    /// <summary>A NexusShell tool in the list (transcribe ...).</summary>
    public sealed class ToolRow
    {
        public ToolRow(ToolPackageInfo info, string outputFolder)
        {
            var package = info.Package;
            Name = package.Name;
            Title = package.Title;
            Description = package.Description;
            State = info.State;
            SetupSize = package.SetupSize;
            OutputFolder = outputFolder;
            Origin = package.Bundled ? "Comes with NexusShell" : "Added by you or a developer";
            StateText = info.State switch
            {
                ToolPackageState.Ready => "ready",
                ToolPackageState.SettingUp => "setting up now",
                _ => package.SetupSize.Length > 0 ? "needs a one-time setup" : "needs a one-time setup (small)",
            };
            Extensions = package.FileExtensions;
            Inputs = string.Join("\n", package.Parameters.Select(Describe));
        }

        public string Name { get; }

        public string Title { get; }

        public string Description { get; }

        public ToolPackageState State { get; }

        public bool IsReady => State == ToolPackageState.Ready;

        public bool NeedsSetup => State == ToolPackageState.NeedsSetup;

        public bool IsSettingUp => State == ToolPackageState.SettingUp;

        public string StateText { get; }

        public string SetupSize { get; }

        public string SetupText => NeedsSetup
            ? $"The first time, NexusShell downloads what this tool needs: {(SetupSize.Length > 0 ? SetupSize : "a few files")}. It is kept in your user folder, and Remove frees the space again."
            : IsReady ? "Installed. Remove frees the downloaded space; the tool can be set up again any time." : "Setting up: progress is shown below.";

        public string Origin { get; }

        public string OutputFolder { get; }

        public string Inputs { get; }

        public IReadOnlyList<string> Extensions { get; }

        public bool TakesFiles => Extensions.Count > 0 || Inputs.Contains("(file", StringComparison.Ordinal);

        /// <summary>For the file dialog: "Supported files|*.mp3;*.mp4|All files|*.*".</summary>
        public string FileFilter => Extensions.Count == 0
            ? "All files|*.*"
            : $"Files this tool reads|{string.Join(";", Extensions.Select(e => "*" + e))}|All files|*.*";

        public string Text => $"{Title}  -  {StateText}";

        public string AutomationName => $"{Title}, {StateText}";

        private static string Describe(ToolParameter parameter)
        {
            var text = new StringBuilder("- ").Append(parameter.Name).Append(" (").Append(parameter.Kind.ToString().ToLowerInvariant())
                .Append(parameter.Required ? ", required" : string.Empty).Append("): ").Append(parameter.Description);
            if (parameter.Choices.Count > 0) text.Append(" Choices: ").Append(string.Join(", ", parameter.Choices)).Append('.');
            if (parameter.Default is { Length: > 0 } value) text.Append(" Default: ").Append(value).Append('.');
            return text.ToString();
        }
    }

    /// <summary>
    /// NexusShell tools: lists the tools, sets one up (downloads what it needs, once, after the user
    /// presses Set up), removes it again, and turns "this tool on this file" into a chat request.
    /// </summary>
    public sealed class AgentToolsViewModel : ViewModelBase
    {
        private readonly IToolPackageService _tools;
        private CancellationTokenSource? _setup;
        private string _lastStep = string.Empty;
        private DateTime _lastSpoken = DateTime.MinValue;

        public AgentToolsViewModel(IToolPackageService tools) => _tools = tools;

        public ObservableCollection<ToolRow> Items { get; } = new();

        private ToolRow? _selected;
        public ToolRow? Selected
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

        private string _problems = string.Empty;
        public string Problems { get => _problems; private set => SetProperty(ref _problems, value); }

        public bool HasProblems => Problems.Length > 0;

        private string _statusMessage = "Choose a tool. Use with a file sends it to NexusAgent with the file you pick; anything you typed in the chat box is added as instructions.";
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

        /// <summary>The chat request to send when the window closes (Use with a file), or null.</summary>
        public string? Request { get; private set; }

        /// <summary>What the user had typed in the chat box, sent along as instructions.</summary>
        public string? Instructions { get; set; }

        /// <param name="reread">Read the tool folders again (a developer may have added or changed a tool).</param>
        public void Load(bool reread = false)
        {
            if (reread) _tools.Reload();
            string? selected = Selected?.Name;
            var rows = _tools.List().Select(i => new ToolRow(i, _tools.OutputFolder(i.Package.Name))).ToList();
            Items.Clear();
            foreach (var row in rows) Items.Add(row);
            Selected = Items.FirstOrDefault(r => r.Name == selected) ?? Items.FirstOrDefault();
            int ready = rows.Count(r => r.IsReady);
            Summary = rows.Count == 0 ? "No tools yet" : $"{rows.Count} tools, {ready} ready to use";
            Problems = _tools.Problems.Count == 0 ? string.Empty : "Tools that could not be read: " + string.Join("; ", _tools.Problems);
            OnPropertyChanged(nameof(HasProblems));
        }

        public async Task SetUpAsync()
        {
            if (Selected is not { NeedsSetup: true } row) return;
            IsBusy = true;
            _setup = new CancellationTokenSource();
            StatusMessage = $"Setting up {row.Title}. This downloads {(row.SetupSize.Length > 0 ? row.SetupSize : "what it needs")} and can take a while; you can keep using NexusShell.";
            var progress = new Progress<ToolProgress>(Report);
            try
            {
                await _tools.SetUpAsync(row.Name, progress, _setup.Token);
                StatusMessage = $"{row.Title} is ready. Press Use with a file, or ask NexusAgent in the chat.";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = $"The setup of {row.Title} was stopped. Press Set up to start it again.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"The setup of {row.Title} failed: {ex.Message}";
            }
            finally
            {
                _setup.Dispose();
                _setup = null;
                IsBusy = false;
                Load();
            }
        }

        public void CancelSetup() => _setup?.Cancel();

        public async Task RemoveAsync()
        {
            if (Selected is not { IsReady: true } row) return;
            IsBusy = true;
            try
            {
                await _tools.RemoveAsync(row.Name);
                StatusMessage = $"{row.Title}: its downloads were removed. Set it up again whenever you need it.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not remove {row.Title}: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
                Load();
            }
        }

        /// <summary>Remembers the request for the chat; the window then closes.</summary>
        public void UseWith(string file)
        {
            if (Selected is not { } row) return;
            Request = _tools.RequestFor(row.Name, file, Instructions);
        }

        /// <summary>Progress in the status line (read by screen readers): each new step at once, percentages every 10 seconds.</summary>
        private void Report(ToolProgress p)
        {
            string step = p.Message.Split('(')[0].Trim();
            if (step == _lastStep && DateTime.UtcNow - _lastSpoken < TimeSpan.FromSeconds(10)) return;
            _lastStep = step;
            _lastSpoken = DateTime.UtcNow;
            StatusMessage = p.Percent is { } percent ? $"{p.Message}: {percent:0}%" : p.Message;
        }
    }
}
