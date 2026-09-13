using System.Collections.ObjectModel;
using System.Windows.Input;
using NexusShell.App.Commands;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;
using System.Linq;

namespace NexusShell.App.ViewModels
{
    public class SnippetManagerViewModel : ViewModelBase
    {
        private readonly IConfigManager _configManager;
        private AppSettings _settings;

        public ObservableCollection<Snippet> Snippets { get; }

        private Snippet? _selectedSnippet;
        public Snippet? SelectedSnippet
        {
            get => _selectedSnippet;
            set => SetProperty(ref _selectedSnippet, value);
        }

        public ICommand AddSnippetCommand { get; }
        public ICommand DeleteSnippetCommand { get; }
        public ICommand SaveCommand { get; }

        public SnippetManagerViewModel(IConfigManager configManager)
        {
            _configManager = configManager;
            _settings = _configManager.Load();
            Snippets = new ObservableCollection<Snippet>(_settings.Snippets);

            AddSnippetCommand = new RelayCommand(_ => 
            {
                var newSnippet = new Snippet { Name = "New Snippet", Command = "echo Hello", Category = "General" };
                Snippets.Add(newSnippet);
                SelectedSnippet = newSnippet;
            });

            DeleteSnippetCommand = new RelayCommand(_ => 
            {
                if (SelectedSnippet != null)
                {
                    Snippets.Remove(SelectedSnippet);
                }
            }, _ => SelectedSnippet != null);

            SaveCommand = new RelayCommand(_ => 
            {
                _settings.Snippets = Snippets.ToList();
                _configManager.Save(_settings);
            });
        }
    }
}
