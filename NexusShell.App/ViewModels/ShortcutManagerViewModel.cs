using NexusShell.App.Commands;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace NexusShell.App.ViewModels
{
    public class ShortcutManagerViewModel : ViewModelBase
    {
        private readonly IConfigManager _configManager;
        private readonly ICommandProvider _commandProvider;
        private readonly IWindowService _windowService;
        private readonly IDialogService _dialogService;

        public ObservableCollection<Shortcut> Shortcuts { get; set; }
        public List<string> AvailableCommands { get; set; }

        private Shortcut? _selectedShortcut;
        public Shortcut? SelectedShortcut
        {
            get => _selectedShortcut;
            set
            {
                SetProperty(ref _selectedShortcut, value);
                // CommandManager.InvalidateRequerySuggested() is handled automatically by WPF for built-in binding,
                // but since we rely on selected item for CanExecute, we rely on the command system to poll.
            }
        }

        public ICommand AddShortcutCommand { get; }
        public ICommand EditShortcutCommand { get; }
        public ICommand DeleteShortcutCommand { get; }
        public ICommand ResetDefaultsCommand { get; }
        public ICommand ImportShortcutsCommand { get; }
        public ICommand ExportShortcutsCommand { get; }

        public ShortcutManagerViewModel(IConfigManager configManager, ICommandProvider commandProvider, IWindowService windowService, IDialogService dialogService)
        {
            _configManager = configManager;
            _commandProvider = commandProvider;
            _windowService = windowService;
            _dialogService = dialogService;

            var settings = _configManager.Load();
            Shortcuts = new ObservableCollection<Shortcut>(settings.Shortcuts);
            AvailableCommands = _commandProvider.GetAvailableCommands().ToList();

            AddShortcutCommand = new RelayCommand(ExecuteAddShortcut);
            EditShortcutCommand = new RelayCommand(ExecuteEditShortcut, CanExecuteEditOrDeleteShortcut);
            DeleteShortcutCommand = new RelayCommand(ExecuteDeleteShortcut, CanExecuteEditOrDeleteShortcut);
            ResetDefaultsCommand = new RelayCommand(ExecuteResetDefaults);
            ImportShortcutsCommand = new RelayCommand(ExecuteImportShortcuts);
            ExportShortcutsCommand = new RelayCommand(ExecuteExportShortcuts);
        }

        private void ExecuteImportShortcuts(object? obj)
        {
            var filePath = _dialogService.ShowOpenFileDialog("JSON Files (*.json)|*.json|All files (*.*)|*.*");
            if (string.IsNullOrEmpty(filePath))
                return;

            try
            {
                var json = File.ReadAllText(filePath);
                var importedShortcuts = JsonSerializer.Deserialize<List<Shortcut>>(json);
                if (importedShortcuts != null)
                {
                    foreach (var shortcut in importedShortcuts)
                    {
                        // Avoid adding duplicate commands
                        if (!Shortcuts.Any(s => s.CommandName == shortcut.CommandName))
                        {
                            Shortcuts.Add(shortcut);
                        }
                    }
                    SaveChanges();
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Failed to import shortcuts: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExecuteExportShortcuts(object? obj)
        {
            var filePath = _dialogService.ShowSaveFileDialog("shortcuts.json", "JSON Files (*.json)|*.json|All files (*.*)|*.*");
            if (string.IsNullOrEmpty(filePath))
                return;

            try
            {
                var json = JsonSerializer.Serialize(Shortcuts, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
                MessageBox.Show("Shortcuts exported successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Failed to export shortcuts: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExecuteAddShortcut(object? obj)
        {
            // Open a new window to add a shortcut
            var newShortcut = _windowService.ShowAddEditShortcutWindow(AvailableCommands);
            if (newShortcut != null)
            {
                Shortcuts.Add(newShortcut);
                SaveChanges();
            }
        }

        private void ExecuteEditShortcut(object? selectedItem)
        {
            var shortcutToEdit = selectedItem as Shortcut ?? SelectedShortcut;

            if (shortcutToEdit != null)
            {
                // Open a new window to edit the shortcut
                var editedShortcut = _windowService.ShowAddEditShortcutWindow(AvailableCommands, shortcutToEdit);
                if (editedShortcut != null)
                {
                    shortcutToEdit.Key = editedShortcut.Key;
                    shortcutToEdit.Modifiers = editedShortcut.Modifiers;
                    shortcutToEdit.CommandName = editedShortcut.CommandName;
                    
                    // Force refresh list view if needed (ObservableCollection handles add/remove, but property updates might need manual notification or replacement)
                    // Simplest way: replace the item
                    var index = Shortcuts.IndexOf(shortcutToEdit);
                    if (index >= 0)
                    {
                        Shortcuts[index] = editedShortcut;
                    }
                    
                    SaveChanges();
                }
            }
        }

        private void ExecuteDeleteShortcut(object? selectedItem)
        {
            var shortcutToDelete = selectedItem as Shortcut ?? SelectedShortcut;

            if (shortcutToDelete != null)
            {
                Shortcuts.Remove(shortcutToDelete);
                SaveChanges();
            }
        }

        private void ExecuteResetDefaults(object? obj)
        {
            var defaults = new List<Shortcut>
            {
                new Shortcut(Key.F1, ModifierKeys.None, "ViewHelpCommand"),
                new Shortcut(Key.F2, ModifierKeys.None, "OpenSettingsCommand"),
                new Shortcut(Key.C, ModifierKeys.Control | ModifierKeys.Shift, "CopyOutputCommand"),
                new Shortcut(Key.Left, ModifierKeys.Control, "PreviousOutputCommand"),
                new Shortcut(Key.Right, ModifierKeys.Control, "NextOutputCommand"),
                new Shortcut(Key.P, ModifierKeys.Control, "PreviousCommand"),
                new Shortcut(Key.N, ModifierKeys.Control, "NextCommand"),
                new Shortcut(Key.S, ModifierKeys.Control | ModifierKeys.Shift, "StopCommand"),
                new Shortcut(Key.R, ModifierKeys.Control | ModifierKeys.Shift, "ReadScreenCommand")
            };

            Shortcuts.Clear();
            foreach (var s in defaults) Shortcuts.Add(s);
            SaveChanges();
        }

        private bool CanExecuteEditOrDeleteShortcut(object? selectedItem)
        {
            return (selectedItem is Shortcut) || (SelectedShortcut != null);
        }

        private void SaveChanges()
        {
            var settings = _configManager.Load();
            settings.Shortcuts = Shortcuts.ToList();
            _configManager.Save(settings);
        }
    }
}