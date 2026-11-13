using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusShell.Agents.Application.Abstractions.Configuration;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Domain.Secrets;

namespace NexusShell.App.ViewModels
{
    /// <summary>One secret the agent configuration refers to (by environment variable name).</summary>
    public sealed class AgentSecretRow : ObservableObject
    {
        public AgentSecretRow(string owner, string variable, bool isSet)
        {
            Owner = owner;
            Variable = variable;
            _isSet = isSet;
        }

        public string Owner { get; }

        public string Variable { get; }

        private bool _isSet;
        public bool IsSet
        {
            get => _isSet;
            set
            {
                if (SetProperty(ref _isSet, value))
                {
                    OnPropertyChanged(nameof(Status));
                    OnPropertyChanged(nameof(AutomationName));
                }
            }
        }

        public string Status => IsSet ? "Set" : "Not set";

        /// <summary>What a screen reader announces for the row. Never includes the value.</summary>
        public string AutomationName => $"{Owner}, variable {Variable}, {Status}";
    }

    /// <summary>
    /// Manages agent secrets. Values go to per-user environment variables through
    /// <see cref="ISecretStore"/>; this view model never keeps or displays a value.
    /// </summary>
    public sealed class AgentKeysViewModel : ViewModelBase
    {
        private readonly IAgentConfigurationProvider _configuration;
        private readonly ISecretStore _secrets;

        public AgentKeysViewModel(IAgentConfigurationProvider configuration, ISecretStore secrets)
        {
            _configuration = configuration;
            _secrets = secrets;
        }

        public ObservableCollection<AgentSecretRow> Secrets { get; } = new();

        private AgentSecretRow? _selectedSecret;
        public AgentSecretRow? SelectedSecret
        {
            get => _selectedSecret;
            set
            {
                if (SetProperty(ref _selectedSecret, value) && value is not null)
                    VariableName = value.Variable;
            }
        }

        private string _variableName = string.Empty;
        public string VariableName
        {
            get => _variableName;
            set => SetProperty(ref _variableName, value);
        }

        private string _statusMessage = "Keys are stored as environment variables of your Windows account, never in NexusShell files.";
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle));
            }
        }

        public bool IsIdle => !IsBusy;

        public async Task LoadAsync()
        {
            _configuration.Invalidate();
            var config = await _configuration.GetConfigurationAsync();

            var rows = new List<AgentSecretRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string owner, SecretReference? reference)
            {
                if (reference is null || !seen.Add(reference.Name)) return;
                rows.Add(new AgentSecretRow(owner, reference.Name, _secrets.HasSecret(reference)));
            }

            foreach (var provider in config.Providers.Values.OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                Add($"{provider.Id} API key", provider.ApiKey);
                Add($"{provider.Id} endpoint", provider.EndpointVariable);
            }

            foreach (var server in config.McpServers.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                var references = server.Environment.Values
                    .Concat(server.Headers.Values)
                    .Concat(server.Arguments)
                    .SelectMany(SecretTemplate.GetReferences);
                foreach (var reference in references)
                    Add($"MCP server {server.Name}", reference);
            }

            Secrets.Clear();
            foreach (var row in rows) Secrets.Add(row);
        }

        public async Task<bool> SaveAsync(string value)
        {
            string name = VariableName.Trim();
            if (!SecretReference.IsValidName(name))
            {
                StatusMessage = "Enter a valid variable name: letters, digits and underscore, not starting with a digit.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                StatusMessage = "Enter the key value first.";
                return false;
            }

            IsBusy = true;
            try
            {
                await _secrets.SetSecretAsync(new SecretReference(name), value);
                MarkRow(name, isSet: true);
                StatusMessage = $"Saved {name} to your user environment variables.";
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not save {name}: {ex.Message}";
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task RemoveAsync()
        {
            string name = VariableName.Trim();
            if (!SecretReference.IsValidName(name))
            {
                StatusMessage = "Select a key to remove.";
                return;
            }

            IsBusy = true;
            try
            {
                await _secrets.RemoveSecretAsync(new SecretReference(name));
                MarkRow(name, isSet: false);
                StatusMessage = $"Removed {name}.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not remove {name}: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void MarkRow(string name, bool isSet)
        {
            var row = Secrets.FirstOrDefault(r => string.Equals(r.Variable, name, StringComparison.OrdinalIgnoreCase));
            if (row is not null) row.IsSet = isSet;
            else Secrets.Add(new AgentSecretRow("Custom", name, isSet));
        }
    }
}
