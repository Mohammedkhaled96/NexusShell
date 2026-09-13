using Microsoft.Extensions.Logging;
using NexusShell.App.Commands;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;
using NexusShell.App.Services; // Added using
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Text.Json;
using NexusShell.App.Views;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.InteropServices;

namespace NexusShell.App.ViewModels
{
    public class MainViewModel : ViewModelBase, ICommandProvider
    {
        private readonly IConfigManager _configManager;
        private readonly IWindowService _windowService;
        private readonly IDialogService _dialogService;
        private readonly IOutputHistoryService _outputHistory;
        private readonly ISuggestionService _suggestionService;
        private readonly IAIService _aiService;
        private readonly IInteractiveScreenService _interactiveScreenService;
        private readonly ILogger<MainViewModel> _logger;
        private readonly IServiceProvider _serviceProvider;
        private AppSettings _settings = new();
        private static readonly HttpClient HttpClient = new HttpClient();

        // Resolved lazily — MainViewModel is a singleton built early; this avoids
        // expanding its constructor just for fire-and-forget UI sounds.
        private ISoundService? _soundServiceCache;
        private ISoundService SoundService => _soundServiceCache ??= _serviceProvider.GetRequiredService<ISoundService>();

        public ICommand AskAICommand { get; }

        public List<ShellProfile> Profiles => _settings.Profiles;

        private ShellProfile _selectedProfile = null!;
        public ShellProfile SelectedProfile
        {
            get => _selectedProfile;
            set => SetProperty(ref _selectedProfile, value);
        }

        // Suggestions
        private string _currentSuggestionInput = string.Empty;
        private int _loadedSuggestionCount;

        public ObservableCollection<string> Suggestions { get; } = new ObservableCollection<string>();

        private string _selectedSuggestion = string.Empty;
        public string SelectedSuggestion
        {
            get => _selectedSuggestion;
            set
            {
                if (SetProperty(ref _selectedSuggestion, value))
                {
                    if (_selectedSuggestion != null && Suggestions.Count > 0 && _selectedSuggestion == Suggestions.Last())
                    {
                        LoadMoreSuggestions();
                    }
                }
            }
        }

        private bool _isSuggestionsOpen;
        public bool IsSuggestionsOpen
        {
            get => _isSuggestionsOpen;
            set
            {
                if (SetProperty(ref _isSuggestionsOpen, value) && value)
                    SoundService.Play(AppSound.Toggle); // suggestions popped open
            }
        }

        private void LoadMoreSuggestions()
        {
             if (string.IsNullOrEmpty(_currentSuggestionInput)) return;

             var moreResults = _suggestionService.GetSuggestions(_currentSuggestionInput, _loadedSuggestionCount, 10).ToList();
             if (moreResults.Any())
             {
                 foreach(var res in moreResults)
                 {
                     Suggestions.Add(res);
                 }
                 _loadedSuggestionCount += moreResults.Count;
             }
        }

        public void ApplySuggestion(string suggestion)
        {
            if (string.IsNullOrEmpty(suggestion)) return;
            CurrentCommand = suggestion + " "; // Add space for convenience
            IsSuggestionsOpen = false;
            SoundService.Play(AppSound.Notify); // a suggestion was accepted
            // Focus logic should handle moving caret to end, usually handled by View
        }

        public ObservableCollection<TerminalTabViewModel> Tabs { get; } = new ObservableCollection<TerminalTabViewModel>();

        private TerminalTabViewModel _selectedTab = null!;
        public TerminalTabViewModel SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (_selectedTab != null)
                {
                    _selectedTab.PropertyChanged -= SelectedTab_PropertyChanged;
                }
                
                if (SetProperty(ref _selectedTab, value))
                {
                    if (_selectedTab != null)
                    {
                        _selectedTab.PropertyChanged += SelectedTab_PropertyChanged;
                    }
                    OnPropertyChanged(nameof(CurrentCommand));
                    OnPropertyChanged(nameof(RunOrStopHeader));
                }
            }
        }

        private void SelectedTab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TerminalTabViewModel.CurrentCommand))
                OnPropertyChanged(nameof(CurrentCommand));

            if (e.PropertyName == nameof(TerminalTabViewModel.IsCommandRunning))
                OnPropertyChanged(nameof(RunOrStopHeader));
        }

        private FontFamily _fontFamily = new("Consolas"); 
        public FontFamily FontFamily { get => _fontFamily; set => SetProperty(ref _fontFamily, value); }

        private double _fontSize;
        public double FontSize { get => _fontSize; set => SetProperty(ref _fontSize, value); }

        private Brush _backgroundColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E1E"));
        public Brush BackgroundColor { get => _backgroundColor; set => SetProperty(ref _backgroundColor, value); }

        private Brush _textColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC"));
        public Brush TextColor { get => _textColor; set => SetProperty(ref _textColor, value); }

        private bool _isAlwaysOnTop;
        public bool IsAlwaysOnTop
        {
            get => _isAlwaysOnTop;
            set
            {
                if (SetProperty(ref _isAlwaysOnTop, value))
                {
                    if (Application.Current.MainWindow != null)
                    {
                        Application.Current.MainWindow.Topmost = value;
                    }
                    // Save setting
                    _settings.IsAlwaysOnTop = value;
                    _configManager.Save(_settings);
                }
            }
        }

        public ICommand SendCommand { get; }
        public ICommand PreviousCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand AddTabCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand ExitCommand { get; }
        public ICommand ViewHelpCommand { get; }
        public ICommand OpenAboutCommand { get; }
        public ICommand OpenShortcutManagerCommand { get; }
        public ICommand OpenScriptCommand { get; }
        public ICommand ClearTerminalCommand { get; }
        public ICommand ExportCommandHistoryCommand { get; }
        public ICommand CheckForUpdatesCommand { get; }
        public ICommand GoToGitHubCommand { get; }
        public ICommand OpenSupportCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand RunOrStopCommand { get; }
        public ICommand RefreshTerminalCommand { get; }
        public ICommand ExplainAllOutputCommand { get; }
        public ICommand ExplainLatestOutputCommand { get; }
        public ICommand SummarizeAllOutputCommand { get; }
        public ICommand SummarizeLatestOutputCommand { get; }
        public ICommand CleanAllOutputCommand { get; }
        public ICommand CleanLatestOutputCommand { get; }
        public ICommand ChatAboutAllOutputCommand    { get; }
        public ICommand ChatAboutLatestOutputCommand { get; }
        public ICommand OpenAIChatCommand            { get; }
        public ICommand ToggleAlwaysOnTopCommand { get; }
        public ICommand OpenSearchCommand { get; }
        public ICommand OpenSnippetManagerCommand { get; }
        public ICommand OpenEnvEditorCommand { get; }
        public ICommand OpenSSHManagerCommand { get; }
        public ICommand OpenAliasManagerCommand { get; }
        public ICommand OpenThemeManagerCommand { get; }
        public ICommand OpenApiTesterCommand { get; }
        public ICommand OpenFlowsManagerCommand { get; }
        public ICommand ReadScreenCommand { get; }

        /// <summary>
        /// Raised when an accessibility re-read is requested. <c>MainWindow</c> handles
        /// this by raising a UI Automation notification so the screen reader speaks the text.
        /// </summary>
        public event Action<string>? AnnounceAccessibilityRequested;

        /// <summary>
        /// Header for the Run/Stop toggle menu item.
        /// Shows "Stop Command" while a command is executing, "Execute Command" when idle.
        /// </summary>
        public string RunOrStopHeader =>
            (SelectedTab?.IsCommandRunning == true) ? "_Stop Command" : "_Execute Command";

        // ── Interactive Screen overlay ──────────────────────────────────────────

        /// <summary>
        /// The ViewModel that drives the Interactive Screen overlay.
        /// Bound in MainWindow.xaml as the DataContext of InteractiveScreenView.
        /// </summary>
        public InteractiveScreenViewModel InteractiveScreen { get; }

        /// <summary>True while the Interactive Screen is displayed.</summary>
        public bool IsInteractiveScreenActive => _interactiveScreenService.IsActive;

        // ── Proxy properties ───────────────────────────────────────────────────

        // Proxy properties for existing bindings, forwarded to SelectedTab
        public string CurrentCommand
        {
            get => SelectedTab?.CurrentCommand ?? string.Empty;
            set
            {
                if (SelectedTab != null)
                {
                    if (SelectedTab.CurrentCommand != value)
                    {
                        SelectedTab.CurrentCommand = value;
                        OnPropertyChanged();
                        UpdateSuggestions(value);
                    }
                }
            }
        }

        private void UpdateSuggestions(string input)
        {
            if (!_settings.EnableSuggestions || string.IsNullOrWhiteSpace(input))
            {
                IsSuggestionsOpen = false;
                Suggestions.Clear();
                return;
            }

            _currentSuggestionInput = input;
            _loadedSuggestionCount = 0;

            var results = _suggestionService.GetSuggestions(input, 0, 10).ToList();
            if (results.Any())
            {
                Suggestions.Clear();
                foreach (var res in results) Suggestions.Add(res);
                _loadedSuggestionCount = results.Count;
                SelectedSuggestion = Suggestions.First();
                IsSuggestionsOpen = true;
            }
            else
            {
                IsSuggestionsOpen = false;
            }
        }

        public MainViewModel(
            IConfigManager configManager, 
            IWindowService windowService, 
            IOutputHistoryService outputHistory, 
            IDialogService dialogService,
            ISuggestionService suggestionService,
            IAIService aiService,
            IInteractiveScreenService interactiveScreenService,
            ILogger<MainViewModel> logger,
            IServiceProvider serviceProvider)
        {
            _configManager = configManager;
            _windowService = windowService;
            _outputHistory = outputHistory;
            _dialogService = dialogService;
            _suggestionService = suggestionService;
            _aiService = aiService;
            _interactiveScreenService = interactiveScreenService;
            _logger = logger;
            _serviceProvider = serviceProvider;

            // Expose the overlay VM so MainWindow can bind to it.
            InteractiveScreen = _serviceProvider
                .GetRequiredService<InteractiveScreenViewModel>();

            // Keep IsInteractiveScreenActive in sync with the service.
            _interactiveScreenService.ActiveStateChanged += (_, _)
                => OnPropertyChanged(nameof(IsInteractiveScreenActive));

            _logger.LogInformation("MainViewModel created (Multi-Tab Mode).");

            AddTabCommand = new RelayCommand(ExecuteAddTab);
            SendCommand = new RelayCommand(ExecuteSendCommand, CanExecuteSendCommand);
            AskAICommand = new RelayCommand(async (_) => await ExecuteAskAI());
            PreviousCommand = new RelayCommand(_ => SelectedTab?.PreviousCommand.Execute(null));
            NextCommand = new RelayCommand(_ => SelectedTab?.NextCommand.Execute(null));
            
            OpenSettingsCommand = new RelayCommand(ExecuteOpenSettings);
            ExitCommand = new RelayCommand(ExecuteExit);
            ViewHelpCommand = new RelayCommand(ExecuteViewHelp);
            OpenAboutCommand = new RelayCommand(ExecuteOpenAbout);
            
            OpenShortcutManagerCommand = new RelayCommand(ExecuteOpenShortcutManager);
            OpenScriptCommand = new RelayCommand(ExecuteOpenScript);
            ClearTerminalCommand = new RelayCommand(ExecuteClearTerminal);
            ExportCommandHistoryCommand = new RelayCommand(ExecuteExportCommandHistory);
            
            CheckForUpdatesCommand = new RelayCommand(async (o) => await ExecuteCheckForUpdates(o));
            GoToGitHubCommand = new RelayCommand(ExecuteGoToGitHub);
            OpenSupportCommand = new RelayCommand(ExecuteOpenSupport);
            StopCommand = new RelayCommand(_ => SelectedTab?.StopCommand.Execute(null), _ => SelectedTab != null);
            RunOrStopCommand = new RelayCommand(ExecuteRunOrStop, _ => SelectedTab != null);
            RefreshTerminalCommand = new RelayCommand(_ => SelectedTab?.RefreshCommand.Execute(null), _ => SelectedTab != null);
            ExplainAllOutputCommand    = new RelayCommand(_ => SelectedTab?.ExplainAllOutputCommand.Execute(null),    _ => SelectedTab != null);
            ExplainLatestOutputCommand = new RelayCommand(_ => SelectedTab?.ExplainLatestOutputCommand.Execute(null), _ => SelectedTab != null);
            SummarizeAllOutputCommand    = new RelayCommand(_ => SelectedTab?.SummarizeAllOutputCommand.Execute(null),    _ => SelectedTab != null);
            SummarizeLatestOutputCommand = new RelayCommand(_ => SelectedTab?.SummarizeLatestOutputCommand.Execute(null), _ => SelectedTab != null);
            CleanAllOutputCommand    = new RelayCommand(_ => SelectedTab?.CleanAllOutputCommand.Execute(null),    _ => SelectedTab != null);
            CleanLatestOutputCommand = new RelayCommand(_ => SelectedTab?.CleanLatestOutputCommand.Execute(null), _ => SelectedTab != null);
            ChatAboutAllOutputCommand    = new RelayCommand(_ => SelectedTab?.ChatAboutAllOutputCommand.Execute(null),    _ => SelectedTab != null);
            ChatAboutLatestOutputCommand = new RelayCommand(_ => SelectedTab?.ChatAboutLatestOutputCommand.Execute(null), _ => SelectedTab != null);
            OpenAIChatCommand            = new RelayCommand(_ => _windowService.ShowAIChatWindow());
            ToggleAlwaysOnTopCommand = new RelayCommand(_ => IsAlwaysOnTop = !IsAlwaysOnTop);
            OpenSearchCommand = new RelayCommand(_ => SelectedTab?.OpenSearchCommand.Execute(null), _ => SelectedTab != null);
            OpenSnippetManagerCommand = new RelayCommand(_ => _windowService.ShowSnippetManagerWindow());
            OpenEnvEditorCommand = new RelayCommand(_ => _windowService.ShowEnvEditorWindow());
            OpenSSHManagerCommand = new RelayCommand(_ => _windowService.ShowSSHManagerWindow());
            OpenAliasManagerCommand = new RelayCommand(_ => _windowService.ShowAliasManagerWindow());
            OpenThemeManagerCommand = new RelayCommand(_ => _windowService.ShowThemeManagerWindow());
            OpenApiTesterCommand = new RelayCommand(_ => _windowService.ShowApiTesterWindow());
            OpenFlowsManagerCommand = new RelayCommand(_ => _windowService.ShowFlowsManagerWindow());
            ReadScreenCommand = new RelayCommand(ExecuteReadScreen);

            if (HttpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
                HttpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NexusShell", version));
            }

            LoadAndApplySettings();
            
            // Add default tab only if we are not opening a specific path from command line
            if (System.Environment.GetCommandLineArgs().Length <= 1)
            {
                ExecuteAddTab(null);
            }

            // Trigger silent background update check and suggestions init
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            // Initialize suggestions in background
            await _suggestionService.InitializeAsync();
            
            // Wait a bit to let the app load fully
            await Task.Delay(3000);
            await ExecuteSilentUpdateCheck();
        }

        private async Task ExecuteSilentUpdateCheck()
        {
            try
            {
                var settings = _configManager.Load();
                if (!settings.AutoCheckUpdates) return;
                
                // Check if we are in a "Remind Later" period
                if (settings.NextUpdateCheck > DateTime.Now) return;

                _logger.LogInformation("Performing silent update check...");

                // Use /releases to get the absolute latest, including pre-releases, to avoid "latest" caching/filtering issues
                var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Mohammedkhaled96/NexusShell/releases");
                HttpResponseMessage response = await HttpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"Silent update check failed. Status: {response.StatusCode}");
                    return;
                }

                string content = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return;

                var latestRelease = root[0];
                string remoteVersionString = latestRelease.GetProperty("tag_name").GetString() ?? string.Empty;

                _logger.LogInformation($"Latest version found on GitHub: {remoteVersionString}");

                remoteVersionString = remoteVersionString.Replace("v", "").Replace("V", "");

                // Handle semantic versioning suffixes (e.g., 1.0.0-beta)
                if (remoteVersionString.Contains("-"))
                {
                    remoteVersionString = remoteVersionString.Split('-')[0];
                }

                if (!Version.TryParse(remoteVersionString, out var remoteVersion))
                {
                    _logger.LogWarning("Could not parse remote version '{Version}'. Skipping update check.", remoteVersionString);
                    return;
                }
                Version localVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version("0.0.0.0");

                if (remoteVersion > localVersion)
                {
                    string downloadUrl = TryGetFirstAssetUrl(latestRelease);
                    string changelog   = latestRelease.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? string.Empty : string.Empty;
                    string tagName     = latestRelease.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? string.Empty : string.Empty;

                    string updateInfoText = $"A new version of NexusShell is available!\n\n" +
                                            $"VERSION: {tagName}\n\n" +
                                            $"CHANGELOG:\n{changelog}";

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var updateWindow = new UpdateAvailableWindow(updateInfoText, downloadUrl);
                        updateWindow.ShowDialog();
                        
                        // Handle result
                        var result = updateWindow.Result;
                        var currentSettings = _configManager.Load(); // Reload in case it changed

                        switch (result)
                        {
                            case UpdateResult.RemindLater:
                                // Remind in 3 days
                                currentSettings.NextUpdateCheck = DateTime.Now.AddDays(3);
                                _configManager.Save(currentSettings);
                                break;
                            case UpdateResult.DoNotShowAgain:
                                // Disable auto updates
                                currentSettings.AutoCheckUpdates = false;
                                _configManager.Save(currentSettings);
                                break;
                            case UpdateResult.Update:
                                // Update initiated, no specific setting to save
                                break;
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Silent update check failed.");
            }
        }

        public void OpenInPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            // Remove quotes if present
            path = path.Trim('\"');

            // Handle Windows drive roots (e.g. C: -> C:\) when backslashes are stripped by shell parsing
            if (path.Length == 2 && path[1] == ':' && char.IsLetter(path[0]))
            {
                path += "\\";
            }

            if (Directory.Exists(path))
            {
                // It's a directory, open a new tab with this working directory
                ExecuteAddTabWithDir(path);
            }
            else if (File.Exists(path))
            {
                // It's a file, treat it as a script to execute
                ExecuteStartupScript(path);
            }
        }

        private void ExecuteAddTabWithDir(string workingDirectory)
        {
            try
            {
                // Close existing tabs (Single-view mode)
                foreach (var tab in Tabs.ToList())
                {
                    tab.Dispose();
                    Tabs.Remove(tab);
                }

                _settings = _configManager.Load();
                var profile = _settings.Profiles.FirstOrDefault(x => x.Name == _settings.DefaultProfileName) 
                              ?? _settings.Profiles.FirstOrDefault() 
                              ?? new ShellProfile();
                
                var terminal = _serviceProvider.GetRequiredService<ITerminalSession>();
                var history = _serviceProvider.GetRequiredService<ICommandHistory>();
                var tabLogger = _serviceProvider.GetRequiredService<ILogger<TerminalTabViewModel>>();

                var notificationService = _serviceProvider.GetRequiredService<INotificationService>();
                var soundService = _serviceProvider.GetRequiredService<ISoundService>();
                var newTab = new TerminalTabViewModel(profile.Name, profile, terminal, history, tabLogger, _configManager, _aiService, _windowService, notificationService, soundService, _interactiveScreenService, workingDirectory);
                newTab.CloseRequested += OnTabCloseRequested;

                Tabs.Add(newTab);
                SelectedTab = newTab;
                soundService.Play(AppSound.NewTab);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create new tab with working directory.");
            }
        }

        private void ExecuteAddTab(object? parameter)
        {
            try
            {
                // Close existing tabs to ensure a clean "switch" (Expert single-view mode)
                foreach (var tab in Tabs.ToList())
                {
                    tab.Dispose();
                    Tabs.Remove(tab);
                }

                ShellProfile profile;
                if (parameter is ShellProfile p)
                {
                    profile = p;
                }
                else
                {
                    // Reload settings to get the latest default if no specific profile passed
                    _settings = _configManager.Load();
                    profile = _settings.Profiles.FirstOrDefault(x => x.Name == _settings.DefaultProfileName) 
                              ?? _settings.Profiles.FirstOrDefault() 
                              ?? new ShellProfile();
                }
                
                var terminal = _serviceProvider.GetRequiredService<ITerminalSession>();
                var history = _serviceProvider.GetRequiredService<ICommandHistory>(); // Shared history for now
                var tabLogger = _serviceProvider.GetRequiredService<ILogger<TerminalTabViewModel>>();

                var notificationService = _serviceProvider.GetRequiredService<INotificationService>();
                var soundService = _serviceProvider.GetRequiredService<ISoundService>();
                var newTab = new TerminalTabViewModel(profile.Name, profile, terminal, history, tabLogger, _configManager, _aiService, _windowService, notificationService, soundService, _interactiveScreenService);
                newTab.CloseRequested += OnTabCloseRequested;

                Tabs.Add(newTab);
                SelectedTab = newTab;
                soundService.Play(AppSound.NewTab);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create new tab.");
                MessageBox.Show($"Failed to create new tab: {ex.Message}");
            }
        }

        private void OnTabCloseRequested(TerminalTabViewModel tab)
        {
            tab.Dispose();
            Tabs.Remove(tab);
            if (Tabs.Count == 0)
            {
                // If last tab closed, maybe add a default one or exit? 
                // Let's add default one for now to keep app alive
                ExecuteAddTab(null);
            }
        }

        // ... [Commands Implementation] ...
        
        private bool CanExecuteSendCommand(object? parameter) => SelectedTab != null && !string.IsNullOrWhiteSpace(SelectedTab.CurrentCommand);

        private void ExecuteSendCommand(object? parameter)
        {
            SelectedTab?.SendCommand.Execute(parameter);
            OnPropertyChanged(nameof(CurrentCommand)); // Clear input box
        }

        /// <summary>
        /// Toggle: executes the current command when idle, stops it when running.
        /// </summary>
        private void ExecuteRunOrStop(object? parameter)
        {
            if (SelectedTab == null) return;
            if (SelectedTab.IsCommandRunning)
                SelectedTab.StopCommand.Execute(null);
            else
                SelectedTab.SendCommand.Execute(null);
        }

        private async Task ExecuteAskAI()
        {
            if (!_aiService.IsConfigured)
            {
                MessageBox.Show("AI Service is not configured. Please check your settings.", "AI Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // ── Build system context ────────────────────────────────────────────
            var profile      = SelectedProfile;
            var profileName  = profile?.Name    ?? "Unknown";
            var shellExe     = profile?.Command ?? "Unknown";
            var shellArgs    = profile?.Arguments ?? "";

            // Determine shell type for the AI
            string shellType;
            if (shellExe.Contains("powershell", System.StringComparison.OrdinalIgnoreCase))
                shellType = "PowerShell";
            else if (shellExe.Contains("cmd", System.StringComparison.OrdinalIgnoreCase))
                shellType = "CMD (Command Prompt)";
            else if (shellExe.Contains("wsl", System.StringComparison.OrdinalIgnoreCase))
                shellType = "WSL (Windows Subsystem for Linux / bash)";
            else
                shellType = profileName;

            var osDescription  = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            var osArchitecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
            var machineName    = Environment.MachineName;
            var userName       = Environment.UserName;

            // Friendly Windows version (10 vs 11 detection via build number)
            string windowsVersion = "Windows";
            try
            {
                var build = Environment.OSVersion.Version.Build;
                windowsVersion = build >= 22000 ? "Windows 11" : "Windows 10";
                windowsVersion += $" (Build {build})";
            }
            catch { }

            var systemContext = $@"Device Name    : {machineName}
User Name      : {userName}
Operating System: {windowsVersion}
OS Full Info   : {osDescription}
Architecture   : {osArchitecture}
Active Profile : {profileName}
Shell Executable: {shellExe} {shellArgs}
Shell Type     : {shellType}";

            // ── Input dialog ────────────────────────────────────────────────────
            var inputDialog = new Window
            {
                Title = "Ask AI — Generate Command",
                Width = 460,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252526")),
                Owner = Application.Current.MainWindow
            };

            var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(14) };

            var lbl = new System.Windows.Controls.TextBlock
            {
                Text = $"Shell: {shellType}  |  Profile: {profileName}  |  OS: {windowsVersion}",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888")),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var prompt = new System.Windows.Controls.TextBlock
            {
                Text = "Describe what you want to do:",
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 6)
            };

            var txt = new System.Windows.Controls.TextBox
            {
                Height = 32,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E1E1E")),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555")),
                Padding = new Thickness(6, 4, 6, 4)
            };
            System.Windows.Automation.AutomationProperties.SetName(txt, "Describe what you want to do");

            var btn = new System.Windows.Controls.Button
            {
                Content = "Generate Command",
                Margin = new Thickness(0, 10, 0, 0),
                Height = 32,
                IsDefault = true,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007ACC")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };

            string userRequest = "";
            btn.Click += (s, e) => { userRequest = txt.Text; inputDialog.DialogResult = true; inputDialog.Close(); };

            stack.Children.Add(lbl);
            stack.Children.Add(prompt);
            stack.Children.Add(txt);
            stack.Children.Add(btn);
            inputDialog.Content = stack;

            // Focus the text box when dialog opens
            inputDialog.Loaded += (s, e) => txt.Focus();

            if (inputDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(userRequest))
            {
                AIAudioPlayer.Play();
                try
                {
                    var cmd = await _aiService.GetCommandFromNaturalLanguageAsync(userRequest, systemContext);
                    CurrentCommand = cmd;
                }
                finally
                {
                    AIAudioPlayer.Stop();
                }
            }
        }

        private void ExecuteOpenSupport(object? obj) => _windowService.ShowSupportWindow();

        private void ExecuteGoToGitHub(object? obj)
        {
            try
            {
                var url = "https://github.com/Mohammedkhaled96/NexusShell";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch { }
        }

        public void ApplyShortcuts(Window window)
        {
            window.InputBindings.Clear();
            int applied = 0;
            foreach (var shortcut in _settings.Shortcuts)
            {
                var command = this.GetType().GetProperty(shortcut.CommandName)?.GetValue(this) as ICommand;
                if (command != null)
                {
                    try { window.InputBindings.Add(new KeyBinding(command, shortcut.Key, shortcut.Modifiers)); applied++; } catch { }
                }
            }
            _logger.LogInformation("ApplyShortcuts: bound {Applied}/{Total}: {List}", applied, _settings.Shortcuts.Count,
                string.Join(", ", _settings.Shortcuts.Select(s => $"{s.CommandName}=[{s.DisplayString}]")));
        }

        // ── Accessibility: re-read screen content aloud (Ctrl+Shift+R) ──────────────

        /// <summary>
        /// Re-reads the interactive screen overlay aloud via UI Automation (its title,
        /// options, current selection and approve/reject keys). Targets the interactive
        /// screen ONLY — when no overlay is open it announces a short status so the
        /// screen-reader user gets feedback instead of silence.
        /// </summary>
        private void ExecuteReadScreen(object? _)
        {
            string text = IsInteractiveScreenActive
                ? ComposeInteractiveAnnouncement()
                : "No interactive screen is open.";

            _logger.LogInformation("ReadScreen shortcut fired. InteractiveActive={Active}, textLength={Len}.",
                IsInteractiveScreenActive, text.Length);
            AnnounceAccessibilityRequested?.Invoke(text);
        }

        private string ComposeInteractiveAnnouncement()
        {
            var vm = InteractiveScreen;
            var sb = new System.Text.StringBuilder();
            sb.Append("Interactive screen. ");
            if (!string.IsNullOrWhiteSpace(vm.Title))        sb.Append(vm.Title).Append(". ");
            if (vm.HasPlatformName)                          sb.Append("From ").Append(vm.PlatformName).Append(". ");
            if (!string.IsNullOrWhiteSpace(vm.Description))  sb.Append(vm.Description).Append(". ");

            if (vm.Options.Count > 0)
            {
                sb.Append(vm.Options.Count).Append(vm.Options.Count == 1 ? " option. " : " options. ");
                for (int i = 0; i < vm.Options.Count; i++)
                {
                    var o = vm.Options[i];
                    sb.Append(i + 1).Append(". ").Append(o.Text);
                    if (!string.IsNullOrWhiteSpace(o.ShortcutKey)) sb.Append(", key ").Append(o.ShortcutKey);
                    sb.Append(". ");
                }
                if (vm.SelectedIndex >= 0 && vm.SelectedIndex < vm.Options.Count)
                    sb.Append("Currently selected: ").Append(vm.Options[vm.SelectedIndex].Text).Append(". ");
            }

            sb.Append(vm.AllowCancel
                ? "Press Enter to approve, or Escape to reject."
                : "Press Enter to approve.");
            return sb.ToString();
        }

        public IEnumerable<string> GetAvailableCommands()
        {
            // All bindable commands exposed to the Shortcut Manager.
            // Names must exactly match the ICommand property names on this ViewModel.
            return new List<string>
            {
                // ── Terminal control ───────────────────────────────
                "SendCommand",
                "PreviousCommand",
                "NextCommand",
                "StopCommand",
                "RefreshTerminalCommand",
                "ClearTerminalCommand",
                "OpenScriptCommand",
                "ExportCommandHistoryCommand",
                "AddTabCommand",

                // ── Search ─────────────────────────────────────────
                "OpenSearchCommand",

                // ── AI features ────────────────────────────────────
                "AskAICommand",
                "ExplainAllOutputCommand",
                "ExplainLatestOutputCommand",
                "SummarizeAllOutputCommand",
                "SummarizeLatestOutputCommand",
                "CleanAllOutputCommand",
                "CleanLatestOutputCommand",
                "ChatAboutAllOutputCommand",
                "ChatAboutLatestOutputCommand",

                // ── Managers ───────────────────────────────────────
                "OpenShortcutManagerCommand",
                "OpenSnippetManagerCommand",
                "OpenAliasManagerCommand",
                "OpenThemeManagerCommand",
                "OpenSSHManagerCommand",
                "OpenEnvEditorCommand",
                "OpenFlowsManagerCommand",

                // ── Settings & App ─────────────────────────────────
                "OpenSettingsCommand",
                "ToggleAlwaysOnTopCommand",
                "ViewHelpCommand",
                "OpenAboutCommand",
                "CheckForUpdatesCommand",
                "GoToGitHubCommand",
                "OpenSupportCommand",
                "ExitCommand",

                // ── Accessibility ──────────────────────────────────
                "ReadScreenCommand",
            };
        }

        public void ResizeView(double width, double height)
        {
            // Propagate resize to selected tab (or all tabs?)
            // Usually only the visible tab needs resize, but background tabs might wrap weirdly if not resized.
            // For now, resize SelectedTab.
            if (SelectedTab != null)
            {
                // Logic needs to move to TabViewModel or be called here accessing SelectedTab._terminal
                // But _terminal is private in TabViewModel.
                // We should expose a Resize method on TabViewModel.
                // For this iteration, we skip dynamic resizing logic or implement it later.
            }
        }

        private void ExecuteOpenScript(object? obj)
        {
            var filter = "Script Files|*.ps1;*.bat;*.cmd|All files|*.*";
            var filePath = _dialogService.ShowOpenFileDialog(filter);

            if (!string.IsNullOrEmpty(filePath) && SelectedTab != null)
            {
                try
                {
                    _logger.LogInformation("Opening and executing script: {FilePath}", filePath);
                    var scriptContent = File.ReadAllText(filePath);
                    SelectedTab.WriteInput(scriptContent);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read or execute script file: {FilePath}", filePath);
                    MessageBox.Show($"Failed to execute script: {ex.Message}", "Error");
                }
            }
        }

        private void ExecuteClearTerminal(object? obj)
        {
            if (SelectedTab != null)
            {
                SelectedTab.Clear();
            }
        }

        private void ExecuteExportCommandHistory(object? obj)
        {
            _logger.LogInformation("Exporting command history.");
            var filter = "Text Files|*.txt|All files|*.*";
            var filePath = _dialogService.ShowSaveFileDialog("NexusShell_CommandHistory.txt", filter);

            if (!string.IsNullOrEmpty(filePath))
            {
                try
                {
                    // Accessing shared history for now, as it's a singleton in DI (based on App.xaml.cs)
                    // If history becomes per-tab, we'd access SelectedTab.History (if exposed)
                    // But currently we inject ICommandHistory into MainViewModel constructor only?
                    // Ah, MainViewModel constructor doesn't take ICommandHistory anymore in my latest write_file? 
                    // Wait, I removed it from constructor params in my last write_file of MainViewModel?
                    // Let's check the constructor signature in the previous read_file.
                    // Yes, I see "IConfigManager configManager, IWindowService windowService, IOutputHistoryService outputHistory, IDialogService dialogService, ILogger<MainViewModel> logger, IServiceProvider serviceProvider"
                    // ICommandHistory is MISSING from constructor.
                    // But I need it to export history.
                    // I should resolve it from _serviceProvider.
                    
                    var historyService = _serviceProvider.GetRequiredService<ICommandHistory>();
                    var history = historyService.GetAll();
                    
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("--- NexusShell Command History ---");
                    sb.AppendLine($"Exported on: {DateTime.Now}");
                    sb.AppendLine("------------------------------------ ");
                    int i = 1;
                    foreach (var command in history)
                    {
                        sb.AppendLine($"{i++}: {command}");
                    }
                    File.WriteAllText(filePath, sb.ToString());
                    MessageBox.Show($"Command history exported to {filePath}", "Success");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to export command history to: {FilePath}", filePath);
                    MessageBox.Show($"Failed to export command history: {ex.Message}", "Error");
                }
            }
        }

        private void ExecuteOpenSettings(object? obj)
        {
            _windowService.ShowSettingsWindow(obj as string);
            LoadAndApplySettings();
        }

        private void ExecuteExit(object? obj)
        {
            Application.Current.Shutdown();
        }

        private void ExecuteViewHelp(object? obj) => _windowService.ShowHelpWindow();
        private void ExecuteOpenAbout(object? obj) => _windowService.ShowAboutWindow();

        private void ExecuteOpenShortcutManager(object? obj)
        {
            _windowService.ShowShortcutManagerWindow(() =>
            {
                _settings = _configManager.Load();
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (Application.Current.MainWindow != null) ApplyShortcuts(Application.Current.MainWindow);
                });
            });
        }

        private async Task ExecuteCheckForUpdates(object? obj)
        {
            _logger.LogInformation("Checking for updates...");
            MessageBox.Show("Checking for updates...", "Update Check", MessageBoxButton.OK, MessageBoxImage.Information);

            try
            {
                // Use /releases to get the absolute latest, including pre-releases
                var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Mohammedkhaled96/NexusShell/releases");
                HttpResponseMessage response = await HttpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    MessageBox.Show($"Failed to check for updates. Status: {response.StatusCode}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                string content = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                {
                     MessageBox.Show("No releases found on the repository.", "Update Check", MessageBoxButton.OK, MessageBoxImage.Information);
                     return;
                }

                var latestRelease = root[0];
                string remoteVersionString = latestRelease.GetProperty("tag_name").GetString() ?? string.Empty;
                remoteVersionString = remoteVersionString.Replace("v", "").Replace("V", "");

                // Handle semantic versioning suffixes (e.g., 1.0.0-beta)
                if (remoteVersionString.Contains("-"))
                {
                    remoteVersionString = remoteVersionString.Split('-')[0];
                }

                if (!Version.TryParse(remoteVersionString, out var remoteVersion))
                {
                    MessageBox.Show("Could not determine the latest version (invalid version format).", "Update Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Version localVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version("0.0.0.0");

                if (remoteVersion > localVersion)
                {
                    string downloadUrl = TryGetFirstAssetUrl(latestRelease);
                    string changelog   = latestRelease.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? string.Empty : string.Empty;
                    string tagName     = latestRelease.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? string.Empty : string.Empty;

                    string updateInfoText = $"A new version of NexusShell is available!\n\n" +
                                            $"VERSION:\n{tagName}\n\n" +
                                            $"CHANGELOG:\n{changelog}";

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var updateWindow = new UpdateAvailableWindow(updateInfoText, downloadUrl);
                        updateWindow.ShowDialog();
                    });
                }
                else
                {
                    MessageBox.Show("You are currently running the latest version of NexusShell.", "No Update Available", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update check failed.");
                MessageBox.Show($"An error occurred while checking for updates: {ex.Message}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Safely extract the first release asset's download URL. The GitHub JSON may have
        // no "assets" array or an empty one; never index blindly. The returned value is
        // re-validated as an absolute https URL before it is ever launched (see
        // UpdateAvailableViewModel.ExecuteUpdate).
        private static string TryGetFirstAssetUrl(JsonElement release)
        {
            if (release.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array &&
                assets.GetArrayLength() > 0 &&
                assets[0].TryGetProperty("browser_download_url", out var urlEl))
            {
                return urlEl.GetString() ?? string.Empty;
            }
            return string.Empty;
        }

        private void LoadAndApplySettings()
        {
            try
            {
                _settings = _configManager.Load();
                
                // Initialize selection
                SelectedProfile = _settings.Profiles.FirstOrDefault(p => p.Name == _settings.DefaultProfileName) 
                                  ?? _settings.Profiles.FirstOrDefault()!;
                OnPropertyChanged(nameof(Profiles));

                FontFamily = new FontFamily(_settings.FontFamily ?? "Consolas");
                FontSize = _settings.FontSize;
                IsAlwaysOnTop = _settings.IsAlwaysOnTop;
                try
                {
                    BackgroundColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_settings.BackgroundColor));
                    TextColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_settings.TextColor));
                }
                catch { }
            }
            catch { }
        }

        public void ExecuteStartupScript(string filePath)
        {
             if (File.Exists(filePath) && SelectedTab != null)
             {
                 try
                 {
                     var scriptContent = File.ReadAllText(filePath);

                     // The file path can arrive from outside the app (command-line arg /
                     // "Open with" / file association), and its contents are fed verbatim
                     // into a terminal that runs elevated. Never run it silently — require
                     // explicit user confirmation, defaulting to "No".
                     var confirm = MessageBox.Show(
                         $"NexusShell was asked to run the contents of this file as shell commands:\n\n{filePath}\n\n"
                         + "This will execute in an elevated terminal. Run it?",
                         "Run startup script?",
                         MessageBoxButton.YesNo,
                         MessageBoxImage.Warning,
                         MessageBoxResult.No);

                     if (confirm != MessageBoxResult.Yes)
                     {
                         _logger.LogInformation("User declined to run startup script: {FilePath}", filePath);
                         return;
                     }

                     _logger.LogInformation("Executing startup script: {FilePath}", filePath);
                     // Delay to ensure terminal init
                     Task.Delay(500).ContinueWith(_ => SelectedTab.WriteInput(scriptContent));
                 }
                 catch (Exception ex)
                 {
                     _logger.LogError(ex, "Failed to execute startup script.");
                 }
             }
        }

        public void Cleanup()
        {
            foreach(var tab in Tabs) tab.Dispose();
            Tabs.Clear();
        }
    }
}
