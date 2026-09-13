using System.Windows.Input;
using NexusShell.App.Commands;
using NexusShell.App.Models;

namespace NexusShell.App.ViewModels
{
    public class AddEditEnvViewModel : ViewModelBase
    {
        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        private string _value = string.Empty;
        public string Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }

        public bool? DialogResult { get; private set; }

        public AddEditEnvViewModel(EnvVariable? variable = null)
        {
            if (variable != null)
            {
                Name = variable.Name;
                Value = variable.Value;
            }

            SaveCommand = new RelayCommand(obj => 
            {
                if (string.IsNullOrWhiteSpace(Name))
                {
                    System.Windows.MessageBox.Show("Variable name cannot be empty.", "Validation Error");
                    return;
                }
                DialogResult = true;
                CloseWindow(obj);
            });

            CancelCommand = new RelayCommand(obj => 
            {
                DialogResult = false;
                CloseWindow(obj);
            });
        }

        private void CloseWindow(object? parameter)
        {
            if (parameter is System.Windows.Window window)
            {
                window.DialogResult = DialogResult;
                window.Close();
            }
        }

        public EnvVariable GetEnvVariable()
        {
            return new EnvVariable(Name, Value);
        }
    }
}
