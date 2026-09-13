using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using NexusShell.App.Commands;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;

namespace NexusShell.App.ViewModels
{
    /// <summary>
    /// Drives the Flows manager: lists install/uninstall/status flows, runs the selected one
    /// (profile-aware, with detect-before-step and prerequisite prompts), streams progress, and
    /// supports import/export/delete of user flows.
    /// </summary>
    public class FlowsManagerViewModel : ViewModelBase
    {
        private readonly IFlowLibrary _library;
        private readonly IFlowRunner _runner;
        private readonly MainViewModel _mainViewModel;
        private readonly IDialogService _dialogService;
        private CancellationTokenSource? _cts;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public ObservableCollection<Flow> Flows { get; } = new();
        public ObservableCollection<string> Log { get; } = new();

        private Flow? _selectedFlow;
        public Flow? SelectedFlow
        {
            get => _selectedFlow;
            set => SetProperty(ref _selectedFlow, value);
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set { if (SetProperty(ref _isRunning, value)) OnPropertyChanged(nameof(IsIdle)); }
        }
        public bool IsIdle => !_isRunning;

        // Bound to an assertive live-region TextBlock so screen readers announce progress.
        private string _liveStatus = "";
        public string LiveStatus
        {
            get => _liveStatus;
            private set => SetProperty(ref _liveStatus, value);
        }

        public ICommand RunCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand PreviewCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand ImportCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand RefreshCommand { get; }

        public FlowsManagerViewModel(IFlowLibrary library, IFlowRunner runner, MainViewModel mainViewModel, IDialogService dialogService)
        {
            _library = library;
            _runner = runner;
            _mainViewModel = mainViewModel;
            _dialogService = dialogService;

            LoadFlows();

            RunCommand     = new RelayCommand(async _ => await RunSelectedAsync(), _ => SelectedFlow != null && !IsRunning);
            CancelCommand  = new RelayCommand(_ => _cts?.Cancel(), _ => IsRunning);
            PreviewCommand = new RelayCommand(_ => PreviewSelected(), _ => SelectedFlow != null);
            DeleteCommand  = new RelayCommand(_ => DeleteSelected(), _ => SelectedFlow is { IsBuiltIn: false } && !IsRunning);
            ImportCommand  = new RelayCommand(_ => ImportFlows(), _ => !IsRunning);
            ExportCommand  = new RelayCommand(_ => ExportFlows());
            RefreshCommand = new RelayCommand(_ => { _library.Reload(); LoadFlows(); }, _ => !IsRunning);
        }

        private void LoadFlows()
        {
            Flows.Clear();
            foreach (var f in _library.GetFlows()
                         .OrderBy(f => f.Tool)
                         .ThenBy(f => f.Kind)
                         .ThenBy(f => f.DisplayName))
                Flows.Add(f);
        }

        // ── Run ─────────────────────────────────────────────────────────────────
        private async Task RunSelectedAsync()
        {
            var flow = SelectedFlow;
            if (flow == null || IsRunning) return;

            if (flow.Kind == FlowKind.Uninstall &&
                MessageBox.Show($"Run \"{flow.DisplayName}\"?\nThis will uninstall {flow.Tool}.",
                    "Confirm uninstall", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            Log.Clear();
            IsRunning = true;
            _cts = new CancellationTokenSource();
            var progress = new Progress<FlowProgressEvent>(OnProgress); // marshals to this (UI) thread

            try
            {
                var result = await _runner.RunAsync(flow, CurrentShell(), progress, PromptPrerequisiteAsync, _cts.Token);
                if (result.Success)       { Append($"✓ {flow.DisplayName} completed successfully."); LiveStatus = $"{flow.DisplayName} completed successfully."; }
                else if (result.Cancelled){ Append("■ Cancelled.");                                    LiveStatus = "Flow cancelled."; }
                else                      { Append($"✗ Failed: {result.Error}");                        LiveStatus = $"Flow failed. {result.Error}"; }
            }
            catch (Exception ex)
            {
                Append("✗ " + ex.Message);
                LiveStatus = "Flow failed. " + ex.Message;
            }
            finally
            {
                IsRunning = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void OnProgress(FlowProgressEvent e)
        {
            switch (e.Type)
            {
                case FlowEventType.FlowStarted:    Append($"▶ {e.Message}");                          LiveStatus = $"Starting {e.Message}."; break;
                case FlowEventType.StepStarted:    Append($"• {e.StepTitle}…");                        LiveStatus = e.StepTitle ?? ""; break;
                case FlowEventType.StepOutput:     Append("    " + e.Message);                         break;
                case FlowEventType.StepSkipped:    Append($"  ⤼ {e.StepTitle} — {e.Message}");          LiveStatus = $"{e.StepTitle} skipped. {e.Message}"; break;
                case FlowEventType.StepSucceeded:  Append($"  ✓ {e.StepTitle}");                        LiveStatus = $"{e.StepTitle} done."; break;
                case FlowEventType.StepFailed:     Append($"  ✗ {e.StepTitle} — {e.Message}");          LiveStatus = $"{e.StepTitle} failed. {e.Message}"; break;
                case FlowEventType.Info:           Append("  " + e.Message);                            LiveStatus = e.Message ?? ""; break;
                default:                           if (!string.IsNullOrEmpty(e.Message)) Append(e.Message); break;
            }
        }

        private Task<bool> PromptPrerequisiteAsync(FlowPrerequisite pre)
        {
            return Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var r = MessageBox.Show(
                    $"This flow needs {pre.ToolName}, which is not installed.\n\nInstall {pre.ToolName} now, then continue?",
                    "Prerequisite required", MessageBoxButton.YesNo, MessageBoxImage.Question);
                return r == MessageBoxResult.Yes;
            }).Task;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────
        private ShellKind CurrentShell()
        {
            var cmd = _mainViewModel.SelectedProfile?.Command?.ToLowerInvariant() ?? "";
            if (cmd.Contains("cmd")) return ShellKind.Cmd;
            if (cmd.Contains("wsl")) return ShellKind.Wsl;
            return ShellKind.PowerShell;
        }

        private const int MaxLogLines = 600;
        private void Append(string line)
        {
            Log.Add(line);
            while (Log.Count > MaxLogLines) Log.RemoveAt(0);
        }

        private void PreviewSelected()
        {
            var flow = SelectedFlow;
            if (flow == null) return;
            var shell = CurrentShell();
            Log.Clear();
            Append($"Preview — {flow.DisplayName}  [{shell}]");
            if (flow.Prerequisites.Count > 0)
                Append("Prerequisites: " + string.Join(", ", flow.Prerequisites.Select(p => p.ToolName)));
            foreach (var step in flow.Steps)
            {
                Append($"• {step.Title}");
                if (!string.IsNullOrWhiteSpace(step.DetectCommand)) Append($"    (skip if: {step.DetectCommand})");
                Append("    $ " + (step.CommandFor(shell) ?? "(no command for this shell)"));
            }
            LiveStatus = $"Preview of {flow.DisplayName}, {flow.Steps.Count} steps.";
        }

        private void DeleteSelected()
        {
            if (SelectedFlow is not { IsBuiltIn: false } flow) return;
            Flows.Remove(flow);
            _library.Save(Flows.ToList());
        }

        private void ExportFlows()
        {
            var path = _dialogService.ShowSaveFileDialog("flows.json", "JSON Files (*.json)|*.json|All files (*.*)|*.*");
            if (string.IsNullOrEmpty(path)) return;
            try { File.WriteAllText(path, JsonSerializer.Serialize(Flows.ToList(), JsonOpts)); }
            catch (Exception ex) { MessageBox.Show($"Export failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ImportFlows()
        {
            var path = _dialogService.ShowOpenFileDialog("JSON Files (*.json)|*.json|All files (*.*)|*.*");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var imported = JsonSerializer.Deserialize<List<Flow>>(File.ReadAllText(path), JsonOpts);
                if (imported == null) return;
                var existing = new HashSet<string>(Flows.Select(f => f.Id), StringComparer.OrdinalIgnoreCase);
                foreach (var f in imported.Where(f => !string.IsNullOrWhiteSpace(f.Id) && !existing.Contains(f.Id)))
                {
                    f.IsBuiltIn = false;
                    Flows.Add(f);
                }
                _library.Save(Flows.ToList());
                LoadFlows();
            }
            catch (Exception ex) { MessageBox.Show($"Import failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }
}
