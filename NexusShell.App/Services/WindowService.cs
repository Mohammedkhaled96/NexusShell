using Microsoft.Extensions.DependencyInjection;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;
using NexusShell.App.ViewModels;
using NexusShell.App.Views;
using System;
using System.Collections.Generic;
using System.Windows;

namespace NexusShell.App.Services
{
    public class WindowService : IWindowService
    {
        private readonly IServiceProvider _serviceProvider;
        private SettingsWindow? _settingsWindow;
        private AboutWindow? _aboutWindow;
        private HelpWindow? _helpWindow;
        private ShortcutManagerWindow? _shortcutManagerWindow;

        public WindowService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        // Resolved lazily so window-open/close effects don't add a hard ctor
        // dependency (and so a missing sound service never breaks window opening).
        private void PlaySound(AppSound sound)
        {
            try { _serviceProvider.GetService<ISoundService>()?.Play(sound); } catch { }
        }

        public void ShowSettingsWindow(string? category = null)
        {
            PlaySound(AppSound.OpenSettings);
            if (_settingsWindow == null)
            {
                var viewModel = _serviceProvider.GetRequiredService<SettingsViewModel>();
                if (!string.IsNullOrEmpty(category))
                {
                    viewModel.SelectedCategory = category;
                }

                _settingsWindow = new SettingsWindow
                {
                    DataContext = viewModel
                };
                _settingsWindow.Closed += (s, e) => { _settingsWindow = null; PlaySound(AppSound.CloseWindow); };
                VisualEffects.FadeInWindow(_settingsWindow);
                _settingsWindow.Show();

                // If a category was specifically requested, jump focus to it
                if (!string.IsNullOrEmpty(category))
                {
                    _settingsWindow.FocusActiveCategoryContent();
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(category) && _settingsWindow.DataContext is SettingsViewModel vm)
                {
                    vm.SelectedCategory = category;
                    _settingsWindow.FocusActiveCategoryContent();
                }
                _settingsWindow.Activate();
            }
        }

        public void CloseSettingsWindow()
        {
            _settingsWindow?.Close();
        }

        public void ShowAboutWindow()
        {
            PlaySound(AppSound.OpenManager);
            if (_aboutWindow == null)
            {
                _aboutWindow = new AboutWindow
                {
                    DataContext = _serviceProvider.GetService<AboutViewModel>()
                };
                _aboutWindow.Closed += (s, e) => { _aboutWindow = null; PlaySound(AppSound.CloseWindow); };
                VisualEffects.FadeInWindow(_aboutWindow);
                _aboutWindow.Show();
            }
            else
            {
                _aboutWindow.Activate();
            }
        }

        public void ShowHelpWindow()
        {
            PlaySound(AppSound.OpenManager);
            if (_helpWindow == null)
            {
                _helpWindow = new HelpWindow
                {
                    DataContext = _serviceProvider.GetService<HelpViewModel>()
                };
                _helpWindow.Closed += (s, e) => { _helpWindow = null; PlaySound(AppSound.CloseWindow); };
                VisualEffects.FadeInWindow(_helpWindow);
                _helpWindow.Show();
            }
            else
            {
                _helpWindow.Activate();
            }
        }

        public void ShowShortcutManagerWindow(Action? onClosed = null)
        {
            PlaySound(AppSound.OpenManager);
            if (_shortcutManagerWindow == null)
            {
                var configManager = _serviceProvider.GetRequiredService<IConfigManager>();
                var commandProvider = _serviceProvider.GetRequiredService<ICommandProvider>();
                var dialogService = _serviceProvider.GetRequiredService<IDialogService>();

                var viewModel = new ShortcutManagerViewModel(configManager, commandProvider, this, dialogService);

                _shortcutManagerWindow = new ShortcutManagerWindow
                {
                    DataContext = viewModel
                };
                _shortcutManagerWindow.Closed += (s, e) =>
                {
                    _shortcutManagerWindow = null;
                    PlaySound(AppSound.CloseWindow);
                    onClosed?.Invoke();
                };
                VisualEffects.FadeInWindow(_shortcutManagerWindow);
                _shortcutManagerWindow.Show();
            }
            else
            {
                _shortcutManagerWindow.Activate();
            }
        }

        public void ShowSupportWindow()
        {
            PlaySound(AppSound.OpenManager);
            var supportWindow = new SupportWindow
            {
                DataContext = new SupportViewModel() // Or resolve via DI if registered
            };
            VisualEffects.FadeInWindow(supportWindow);
            supportWindow.ShowDialog();
        }

        private SnippetManagerWindow? _snippetManagerWindow;
        private EnvEditorWindow? _envEditorWindow;
        private SSHManagerWindow? _sshManagerWindow;
        private AliasManagerWindow? _aliasManagerWindow;
        private ThemeManagerWindow? _themeManagerWindow;
        private FlowsManagerWindow? _flowsManagerWindow;

        public void ShowSnippetManagerWindow()
        {
            PlaySound(AppSound.OpenManager);
            if (_snippetManagerWindow == null)
            {
                var configManager = _serviceProvider.GetRequiredService<IConfigManager>();
                var viewModel = new SnippetManagerViewModel(configManager);
                _snippetManagerWindow = new SnippetManagerWindow(viewModel);
                _snippetManagerWindow.Owner = Application.Current.MainWindow;
                _snippetManagerWindow.Closed += (s, e) => { _snippetManagerWindow = null; PlaySound(AppSound.CloseWindow); };
                VisualEffects.FadeInWindow(_snippetManagerWindow);
                _snippetManagerWindow.Show();
            }
            else
            {
                _snippetManagerWindow.Activate();
            }
        }

        public void ShowEnvEditorWindow()
        {
            PlaySound(AppSound.OpenManager);
            if (_envEditorWindow == null)
            {
                var viewModel = new EnvEditorViewModel(this);
                _envEditorWindow = new EnvEditorWindow(viewModel);
                _envEditorWindow.Owner = Application.Current.MainWindow;
                _envEditorWindow.Closed += (s, e) => { _envEditorWindow = null; PlaySound(AppSound.CloseWindow); };
                VisualEffects.FadeInWindow(_envEditorWindow);
                _envEditorWindow.Show();
            }
            else
            {
                _envEditorWindow.Activate();
            }
        }

        public void ShowSSHManagerWindow()
        {
            PlaySound(AppSound.OpenManager);
            if (_sshManagerWindow == null)
            {
                var configManager = _serviceProvider.GetRequiredService<IConfigManager>();
                var commandProvider = _serviceProvider.GetRequiredService<ICommandProvider>();
                var viewModel = new SSHManagerViewModel(configManager, commandProvider);
                _sshManagerWindow = new SSHManagerWindow(viewModel);
                _sshManagerWindow.Owner = Application.Current.MainWindow;
                _sshManagerWindow.Closed += (s, e) => { _sshManagerWindow = null; PlaySound(AppSound.CloseWindow); };
                VisualEffects.FadeInWindow(_sshManagerWindow);
                _sshManagerWindow.Show();
            }
            else
            {
                _sshManagerWindow.Activate();
            }
        }

        public void ShowAliasManagerWindow()
        {
            PlaySound(AppSound.OpenManager);
            if (_aliasManagerWindow == null)
            {
                var configManager = _serviceProvider.GetRequiredService<IConfigManager>();
                var viewModel = new AliasManagerViewModel(configManager);
                _aliasManagerWindow = new AliasManagerWindow(viewModel);
                _aliasManagerWindow.Owner = Application.Current.MainWindow;
                _aliasManagerWindow.Closed += (s, e) => { _aliasManagerWindow = null; PlaySound(AppSound.CloseWindow); };
                VisualEffects.FadeInWindow(_aliasManagerWindow);
                _aliasManagerWindow.Show();
            }
            else
            {
                _aliasManagerWindow.Activate();
            }
        }

        public void ShowThemeManagerWindow()
        {
            PlaySound(AppSound.OpenManager);
            if (_themeManagerWindow == null)
            {
                var configManager = _serviceProvider.GetRequiredService<IConfigManager>();
                var mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
                var viewModel = new ThemeManagerViewModel(configManager, mainViewModel);
                _themeManagerWindow = new ThemeManagerWindow(viewModel);
                _themeManagerWindow.Owner = Application.Current.MainWindow;
                _themeManagerWindow.Closed += (s, e) => { _themeManagerWindow = null; PlaySound(AppSound.CloseWindow); };
                VisualEffects.FadeInWindow(_themeManagerWindow);
                _themeManagerWindow.Show();
            }
            else
            {
                _themeManagerWindow.Activate();
            }
        }

        public void ShowFlowsManagerWindow()
        {
            PlaySound(AppSound.OpenManager);
            if (_flowsManagerWindow == null)
            {
                var library       = _serviceProvider.GetRequiredService<IFlowLibrary>();
                var runner        = _serviceProvider.GetRequiredService<IFlowRunner>();
                var mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
                var dialogService = _serviceProvider.GetRequiredService<IDialogService>();
                var viewModel     = new FlowsManagerViewModel(library, runner, mainViewModel, dialogService);

                _flowsManagerWindow = new FlowsManagerWindow(viewModel);
                _flowsManagerWindow.Owner = Application.Current.MainWindow;
                _flowsManagerWindow.Closed += (s, e) => { _flowsManagerWindow = null; PlaySound(AppSound.CloseWindow); };
                VisualEffects.FadeInWindow(_flowsManagerWindow);
                _flowsManagerWindow.Show();
            }
            else
            {
                _flowsManagerWindow.Activate();
            }
        }

        public void ShowAIResponseWindow(string title, string response)
        {
            PlaySound(AppSound.OpenAI);
            var viewModel = new AIResponseViewModel(title, response,
                openChatAction: () => ShowAIChatWindow(response, title));
            var window = new AIResponseWindow(viewModel);
            VisualEffects.FadeInWindow(window);
            window.Show();
        }

        public void ShowAIChatWindow(string initialContext = "", string contextLabel = "")
        {
            PlaySound(AppSound.OpenAI);
            var aiService = _serviceProvider.GetRequiredService<IAIService>();
            var configManager = _serviceProvider.GetRequiredService<IConfigManager>();
            var viewModel = new AIChatViewModel(aiService, configManager, initialContext, contextLabel);
            var window    = new AIChatWindow(viewModel);
            VisualEffects.FadeInWindow(window);
            window.Show();
        }

        private ApiTesterWindow? _apiTesterWindow;
        public void ShowApiTesterWindow()
        {
            PlaySound(AppSound.OpenManager);
            if (_apiTesterWindow == null)
            {
                var apiService = _serviceProvider.GetRequiredService<IApiTesterService>();
                var dialogService = _serviceProvider.GetRequiredService<IDialogService>();
                var viewModel = new ApiTesterViewModel(apiService, dialogService);

                _apiTesterWindow = new ApiTesterWindow(viewModel);
                _apiTesterWindow.Owner = Application.Current.MainWindow;
                _apiTesterWindow.Closed += (s, e) => { _apiTesterWindow = null; PlaySound(AppSound.CloseWindow); };
                VisualEffects.FadeInWindow(_apiTesterWindow);
                _apiTesterWindow.Show();
            }
            else
            {
                _apiTesterWindow.Activate();
            }
        }

        public Shortcut? ShowAddEditShortcutWindow(List<string> availableCommands, Shortcut? shortcut = null)
        {
            var viewModel = new AddEditShortcutViewModel(availableCommands, shortcut);
            var window = new AddEditShortcutWindow(viewModel);

            VisualEffects.FadeInWindow(window);
            if (window.ShowDialog() == true)
            {
                return viewModel.GetShortcut();
            }

            return null;
        }

        public EnvVariable? ShowAddEditEnvWindow(EnvVariable? variable = null)
        {
            var viewModel = new AddEditEnvViewModel(variable);
            var window = new AddEditEnvWindow(viewModel);

            VisualEffects.FadeInWindow(window);
            if (window.ShowDialog() == true)
            {
                return viewModel.GetEnvVariable();
            }

            return null;
        }
    }
}