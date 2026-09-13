using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using NexusShell.App.Commands;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;

namespace NexusShell.App.ViewModels
{
    public class ThemeManagerViewModel : ViewModelBase
    {
        private readonly IConfigManager _configManager;
        private readonly MainViewModel _mainViewModel;
        private AppSettings _settings;

        public ObservableCollection<TerminalTheme> Themes { get; }

        private TerminalTheme? _selectedTheme;
        public TerminalTheme? SelectedTheme
        {
            get => _selectedTheme;
            set
            {
                if (SetProperty(ref _selectedTheme, value) && value != null)
                {
                    ApplyTheme(value);
                }
            }
        }

        public ThemeManagerViewModel(IConfigManager configManager, MainViewModel mainViewModel)
        {
            _configManager = configManager;
            _mainViewModel = mainViewModel;
            _settings = _configManager.Load();
            Themes = new ObservableCollection<TerminalTheme>(_settings.Themes);
            _selectedTheme = Themes.FirstOrDefault(t => t.Name == _settings.CurrentThemeName);
        }

        private void ApplyTheme(TerminalTheme theme)
        {
            _mainViewModel.BackgroundColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Background));
            _mainViewModel.TextColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Foreground));
            
            _settings.CurrentThemeName = theme.Name;
            _settings.BackgroundColor = theme.Background;
            _settings.TextColor = theme.Foreground;
            _configManager.Save(_settings);
        }
    }
}
