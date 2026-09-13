using NexusShell.App.Commands;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;
using NexusShell.App.Services;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Input;

namespace NexusShell.App.ViewModels
{
    public class SettingsViewModel : ViewModelBase
    {
        private readonly IConfigManager _configManager;
        private readonly IWindowService _windowService;
        private readonly IRegistryManager _registryManager;
        private readonly ISoundService _soundService;

        private AppSettings _originalSettings;

        // ── General ──────────────────────────────────────────────────────────

        private string _shellArgs = "";
        public string ShellArgs { get => _shellArgs; set => SetProperty(ref _shellArgs, value); }

        private string _fontFamily = "Consolas";
        public string FontFamily { get => _fontFamily; set => SetProperty(ref _fontFamily, value); }

        private double _fontSize;
        public double FontSize { get => _fontSize; set => SetProperty(ref _fontSize, value); }

        private OutputDisplayMode _selectedDisplayMode;
        public OutputDisplayMode SelectedDisplayMode { get => _selectedDisplayMode; set => SetProperty(ref _selectedDisplayMode, value); }

        private ShellProfile _selectedProfile = null!;
        public ShellProfile SelectedProfile { get => _selectedProfile; set => SetProperty(ref _selectedProfile, value); }

        private bool _isRegistered;
        public bool IsRegistered { get => _isRegistered; set => SetProperty(ref _isRegistered, value); }

        // NOTE: these checkboxes are ButtonBase, so the app-wide ButtonBase.Click
        // sound handler already ticks on toggle — no explicit Play here (would double).
        private bool _autoCheckUpdates;
        public bool AutoCheckUpdates { get => _autoCheckUpdates; set => SetProperty(ref _autoCheckUpdates, value); }

        private bool _enableSuggestions;
        public bool EnableSuggestions { get => _enableSuggestions; set => SetProperty(ref _enableSuggestions, value); }

        private bool _showShellHeader;
        public bool ShowShellHeader { get => _showShellHeader; set => SetProperty(ref _showShellHeader, value); }

        private bool _runOnStartup;
        public bool RunOnStartup { get => _runOnStartup; set => SetProperty(ref _runOnStartup, value); }

        private bool _enableSoundEffects;
        public bool EnableSoundEffects
        {
            get => _enableSoundEffects;
            set
            {
                if (SetProperty(ref _enableSoundEffects, value))
                {
                    // Apply live. When turning OFF this runs before the global click
                    // handler fires, so the disabling click itself stays silent.
                    _soundService.Enabled = value;
                }
            }
        }

        private double _soundEffectsVolume;
        public double SoundEffectsVolume
        {
            get => _soundEffectsVolume;
            set
            {
                if (SetProperty(ref _soundEffectsVolume, value))
                {
                    _soundService.Volume = value;
                    _soundService.Play(AppSound.Output); // quiet tick so the user hears the new level
                }
            }
        }

        private bool _enableVisualEffects;
        public bool EnableVisualEffects
        {
            get => _enableVisualEffects;
            set
            {
                if (SetProperty(ref _enableVisualEffects, value))
                    VisualEffects.SetUserPreference(value); // apply live (code-driven animations)
            }
        }

        // ── Navigation ───────────────────────────────────────────────────────

        private string _selectedCategory = "General";
        public string SelectedCategory
        {
            get => _selectedCategory;
            set { if (SetProperty(ref _selectedCategory, value)) _soundService.Play(AppSound.Toggle); }
        }

        public List<string> AvailableCategories { get; } = new List<string> { "General", "Appearance", "Integration", "AI" };

        // ── AI Settings ──────────────────────────────────────────────────────

        private string _aiApiKey = "";
        /// <summary>
        /// The Groq API key. Set and read via code-behind because PasswordBox
        /// does not support two-way data binding.
        /// </summary>
        public string AIApiKey
        {
            get => _aiApiKey;
            set
            {
                if (SetProperty(ref _aiApiKey, value))
                {
                    OnPropertyChanged(nameof(AIConfigured));
                    OnPropertyChanged(nameof(AIStatusText));
                }
            }
        }

        /// <summary>True when an API key has been entered — used to drive the status dot in XAML.</summary>
        public bool AIConfigured => !string.IsNullOrWhiteSpace(AIApiKey);

        public string AIStatusText => AIConfigured
            ? "AI is configured and ready"
            : "Enter your Groq API key to enable all AI features";

        private string _aiModel = "auto";
        public string AIModel { get => _aiModel; set => SetProperty(ref _aiModel, value); }

        private string _aiLanguage = "English";
        public string AILanguage { get => _aiLanguage; set => SetProperty(ref _aiLanguage, value); }

        public List<string> AvailableModels { get; } = new List<string>
        {
            "auto",
            "llama-3.3-70b-versatile",
            "llama-3.1-8b-instant",
            "mixtral-8x7b-32768",
            "gemma2-9b-it"
        };

        public List<string> AvailableLanguages { get; } = new List<string>
        {
            "English",
            "Arabic",
            "French",
            "Spanish",
            "German",
            "Italian",
            "Japanese",
            "Chinese"
        };

        // ── Collections ──────────────────────────────────────────────────────

        public List<string> Fonts { get; }
        public List<OutputDisplayMode> DisplayModes { get; }
        public List<ShellProfile> Profiles { get; }

        // ── Commands ─────────────────────────────────────────────────────────

        public ICommand SaveCommand              { get; }
        public ICommand CancelCommand            { get; }
        public ICommand ToggleRegistrationCommand { get; }
        public ICommand OpenGetApiKeyCommand     { get; }

        // ── Constructor ──────────────────────────────────────────────────────

        public SettingsViewModel(IConfigManager configManager, IWindowService windowService, IRegistryManager registryManager, ISoundService soundService)
        {
            _configManager   = configManager;
            _windowService   = windowService;
            _registryManager = registryManager;
            _soundService    = soundService;

            _originalSettings = _configManager.Load();

            // General
            _shellArgs          = _originalSettings.ShellArgs;
            _fontFamily         = _originalSettings.FontFamily;
            _fontSize           = _originalSettings.FontSize;
            _selectedDisplayMode = _originalSettings.DisplayMode;
            _autoCheckUpdates   = _originalSettings.AutoCheckUpdates;
            _enableSuggestions  = _originalSettings.EnableSuggestions;
            _showShellHeader    = _originalSettings.ShowShellHeader;
            _runOnStartup       = _originalSettings.RunOnStartup;
            _enableSoundEffects = _originalSettings.EnableSoundEffects;
            _soundEffectsVolume = _originalSettings.SoundEffectsVolume;
            _enableVisualEffects = _originalSettings.EnableVisualEffects;

            Profiles        = _originalSettings.Profiles;
            SelectedProfile = Profiles.FirstOrDefault(p => p.Name == _originalSettings.DefaultProfileName)
                              ?? Profiles.FirstOrDefault()!;

            _isRegistered = _registryManager.IsDirectoryContextRegistered();

            // AI
            _aiApiKey = _originalSettings.AI?.ApiKey ?? "";
            _aiModel = _originalSettings.AI?.Model ?? "auto";
            _aiLanguage = _originalSettings.AI?.Language ?? "English";

            // Collections
            Fonts        = System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(f => f).ToList();
            DisplayModes = System.Enum.GetValues(typeof(OutputDisplayMode)).Cast<OutputDisplayMode>().ToList();

            // Commands
            SaveCommand               = new RelayCommand(ExecuteSave);
            CancelCommand             = new RelayCommand(ExecuteCancel);
            ToggleRegistrationCommand = new RelayCommand(ExecuteToggleRegistration);
            OpenGetApiKeyCommand      = new RelayCommand(_ =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = "https://console.groq.com/keys",
                        UseShellExecute = true
                    });
                }
                catch { }
            });
        }

        // ── Save ─────────────────────────────────────────────────────────────

        private void ExecuteSave(object? obj)
        {
            // IMPORTANT: Load from disk first to preserve every field not managed
            // by this UI (Aliases, Snippets, Themes, Shortcuts, SSHConnections, etc.).
            // Never start from "new AppSettings {}" — that wipes all other data.
            var settings = _configManager.Load();

            // General
            settings.ShellArgs          = ShellArgs;
            settings.FontFamily         = FontFamily;
            settings.FontSize           = (int)FontSize;
            settings.DisplayMode        = SelectedDisplayMode;
            settings.AutoCheckUpdates   = AutoCheckUpdates;
            settings.EnableSuggestions  = EnableSuggestions;
            settings.ShowShellHeader    = ShowShellHeader;
            settings.RunOnStartup       = RunOnStartup;
            settings.EnableSoundEffects = EnableSoundEffects;
            settings.SoundEffectsVolume = SoundEffectsVolume;
            settings.EnableVisualEffects = EnableVisualEffects;
            settings.Profiles           = Profiles;
            settings.DefaultProfileName = SelectedProfile?.Name ?? "PowerShell";

            // AI
            if (settings.AI == null) settings.AI = new AIConfig();
            settings.AI.ApiKey = AIApiKey.Trim();
            settings.AI.Model = AIModel;
            settings.AI.Language = AILanguage;

            // Startup registry
            if (_originalSettings.RunOnStartup != RunOnStartup)
                _registryManager.SetStartup(RunOnStartup);

            settings.EnableDirectoryContext = IsRegistered;

            _configManager.Save(settings);
            // Sync the live player to exactly what was persisted.
            _soundService.ApplySettings(settings);
            _windowService.CloseSettingsWindow();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private void ExecuteToggleRegistration(object? obj)
        {
            try
            {
                if (IsRegistered) { _registryManager.UnregisterDirectoryContext(); IsRegistered = false; }
                else              { _registryManager.RegisterDirectoryContext();   IsRegistered = true;  }
            }
            catch { }
        }

        private void ExecuteCancel(object? obj)
        {
            // Live enable/volume may have been changed by the sliders/checkbox above;
            // revert the player to the on-disk state since the user discarded changes.
            _soundService.ApplySettings(_originalSettings);
            VisualEffects.SetUserPreference(_originalSettings.EnableVisualEffects);
            _windowService.CloseSettingsWindow();
        }
    }
}
