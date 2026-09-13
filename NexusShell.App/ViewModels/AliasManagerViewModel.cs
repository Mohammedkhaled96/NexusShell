using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using NexusShell.App.Commands;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;

namespace NexusShell.App.ViewModels
{
    public class AliasManagerViewModel : ViewModelBase
    {
        private readonly IConfigManager _configManager;
        private AppSettings _settings;

        public ObservableCollection<AliasConfig> Aliases { get; }

        private AliasConfig? _selectedAlias;
        public AliasConfig? SelectedAlias
        {
            get => _selectedAlias;
            set => SetProperty(ref _selectedAlias, value);
        }

        public ICommand AddAliasCommand { get; }
        public ICommand DeleteAliasCommand { get; }
        public ICommand SaveCommand { get; }

        public AliasManagerViewModel(IConfigManager configManager)
        {
            _configManager = configManager;
            _settings = _configManager.Load();
            Aliases = new ObservableCollection<AliasConfig>(_settings.Aliases);

            AddAliasCommand = new RelayCommand(_ => 
            {
                var newAlias = new AliasConfig { Trigger = "new", Command = "echo Hello" };
                Aliases.Add(newAlias);
                SelectedAlias = newAlias;
            });

            DeleteAliasCommand = new RelayCommand(_ => 
            {
                if (SelectedAlias != null) Aliases.Remove(SelectedAlias);
            }, _ => SelectedAlias != null);

            SaveCommand = new RelayCommand(_ => 
            {
                _settings.Aliases = Aliases.ToList();
                _configManager.Save(_settings);
            });
        }
    }
}
