using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using NexusShell.App.Commands;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;

namespace NexusShell.App.ViewModels
{
    public class SSHManagerViewModel : ViewModelBase
    {
        private readonly IConfigManager _configManager;
        private readonly ICommandProvider _commandProvider;
        private AppSettings _settings;

        public ObservableCollection<SSHConnection> Connections { get; }

        private SSHConnection? _selectedConnection;
        public SSHConnection? SelectedConnection
        {
            get => _selectedConnection;
            set => SetProperty(ref _selectedConnection, value);
        }

        public ICommand AddConnectionCommand { get; }
        public ICommand DeleteConnectionCommand { get; }
        public ICommand ConnectCommand { get; }
        public ICommand SaveCommand { get; }

        public SSHManagerViewModel(IConfigManager configManager, ICommandProvider commandProvider)
        {
            _configManager = configManager;
            _commandProvider = commandProvider;
            _settings = _configManager.Load();
            Connections = new ObservableCollection<SSHConnection>(_settings.SSHConnections);

            AddConnectionCommand = new RelayCommand(_ => 
            {
                var conn = new SSHConnection { Name = "My Server", Host = "127.0.0.1", User = "admin" };
                Connections.Add(conn);
                SelectedConnection = conn;
            });

            DeleteConnectionCommand = new RelayCommand(_ => 
            {
                if (SelectedConnection != null) Connections.Remove(SelectedConnection);
            }, _ => SelectedConnection != null);

            ConnectCommand = new RelayCommand(_ =>
            {
                if (SelectedConnection == null) return;

                var user = (SelectedConnection.User ?? string.Empty).Trim();
                var host = (SelectedConnection.Host ?? string.Empty).Trim();

                // This command is auto-executed in the shell, so the stored fields must
                // not be able to inject extra commands (e.g. host "h;calc"). Port is an int.
                if (string.IsNullOrEmpty(host) || !IsSafeSshToken(user) || !IsSafeSshToken(host))
                {
                    System.Windows.MessageBox.Show(
                        "Invalid SSH user or host. They must not be empty or contain spaces or shell "
                        + "metacharacters such as & | ; < > $ ( ) ` \" ' \\ { }.",
                        "SSH Connection",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }

                // Start SSH process: ssh user@host -p port
                string sshCmd = string.IsNullOrEmpty(user)
                    ? $"ssh {host} -p {SelectedConnection.Port}"
                    : $"ssh {user}@{host} -p {SelectedConnection.Port}";

                // Assuming commandProvider can inject command into active tab or create new tab
                if (_commandProvider is MainViewModel mainVm)
                {
                    mainVm.CurrentCommand = sshCmd;
                    mainVm.SendCommand.Execute(null);
                }

                // Close manager window? Logic usually in WindowService
            }, _ => SelectedConnection != null);

            SaveCommand = new RelayCommand(_ =>
            {
                _settings.SSHConnections = Connections.ToList();
                _configManager.Save(_settings);
            });
        }

        // The user/host strings are interpolated into a shell command line that is
        // auto-executed in an elevated terminal. Reject anything that could break out
        // of the ssh argument and inject extra commands. An empty user is allowed
        // (host-only form). ':' '%' '[' ']' are permitted so IPv6 / zone-id hosts work.
        private static bool IsSafeSshToken(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;
            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c)) return false;
                if ("&|;<>$()`\"'\\{}".IndexOf(c) >= 0) return false;
            }
            return true;
        }
    }
}
