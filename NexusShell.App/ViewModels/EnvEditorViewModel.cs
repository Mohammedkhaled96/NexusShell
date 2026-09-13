using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using NexusShell.App.Commands;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;

namespace NexusShell.App.ViewModels
{
    public class EnvEditorViewModel : ViewModelBase
    {
        private readonly IWindowService _windowService;

        public ObservableCollection<EnvVariable> Variables { get; } = new();

        private EnvVariable? _selectedVariable;
        public EnvVariable? SelectedVariable
        {
            get => _selectedVariable;
            set => SetProperty(ref _selectedVariable, value);
        }

        public ICommand RefreshCommand { get; }
        public ICommand AddVariableCommand { get; }
        public ICommand EditVariableCommand { get; }
        public ICommand DeleteVariableCommand { get; }

        public EnvEditorViewModel(IWindowService windowService)
        {
            _windowService = windowService;

            RefreshCommand = new RelayCommand(_ => LoadVariables());
            
            AddVariableCommand = new RelayCommand(_ => 
            {
                var newVar = _windowService.ShowAddEditEnvWindow();
                if (newVar != null)
                {
                    try
                    {
                        Environment.SetEnvironmentVariable(newVar.Name, newVar.Value, EnvironmentVariableTarget.Process);
                        Variables.Add(newVar);
                        SelectedVariable = newVar;
                    }
                    catch (Exception ex)
                    {
                        System.Windows.MessageBox.Show($"Error setting variable: {ex.Message}", "Error");
                    }
                }
            });

            EditVariableCommand = new RelayCommand(_ => 
            {
                if (SelectedVariable != null)
                {
                    var editedVar = _windowService.ShowAddEditEnvWindow(SelectedVariable);
                    if (editedVar != null)
                    {
                        try
                        {
                            // If name changed, we should delete the old one
                            if (editedVar.Name != SelectedVariable.Name)
                            {
                                Environment.SetEnvironmentVariable(SelectedVariable.Name, null, EnvironmentVariableTarget.Process);
                            }
                            
                            Environment.SetEnvironmentVariable(editedVar.Name, editedVar.Value, EnvironmentVariableTarget.Process);
                            
                            SelectedVariable.Name = editedVar.Name;
                            SelectedVariable.Value = editedVar.Value;
                            
                            // Refresh list to show changes if necessary, or just rely on property notification if we were using a different model
                            // Since EnvVariable in Models doesn't have SetProperty (it's a simple POCO), 
                            // we should probably replace the item in the collection.
                            int index = Variables.IndexOf(SelectedVariable);
                            if (index >= 0)
                            {
                                Variables[index] = editedVar;
                                SelectedVariable = editedVar;
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Windows.MessageBox.Show($"Error updating variable: {ex.Message}", "Error");
                        }
                    }
                }
            }, _ => SelectedVariable != null);

            DeleteVariableCommand = new RelayCommand(_ => 
            {
                if (SelectedVariable != null)
                {
                    var result = System.Windows.MessageBox.Show($"Are you sure you want to delete '{SelectedVariable.Name}' from this session?", "Confirm Delete", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                    if (result == System.Windows.MessageBoxResult.Yes)
                    {
                        try
                        {
                            Environment.SetEnvironmentVariable(SelectedVariable.Name, null, EnvironmentVariableTarget.Process);
                            Variables.Remove(SelectedVariable);
                        }
                        catch (Exception ex)
                        {
                            System.Windows.MessageBox.Show($"Error deleting variable: {ex.Message}", "Error");
                        }
                    }
                }
            }, _ => SelectedVariable != null);

            LoadVariables();
        }

        private void LoadVariables()
        {
            var currentSelectionName = SelectedVariable?.Name;
            Variables.Clear();
            var envVars = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process);
            
            var sortedVars = envVars.Cast<DictionaryEntry>()
                .Select(de => new EnvVariable(de.Key.ToString() ?? "", de.Value?.ToString() ?? ""))
                .OrderBy(v => v.Name);

            foreach (var v in sortedVars)
            {
                Variables.Add(v);
            }
            
            if (currentSelectionName != null)
            {
                SelectedVariable = Variables.FirstOrDefault(v => v.Name == currentSelectionName);
            }
        }
    }
}
