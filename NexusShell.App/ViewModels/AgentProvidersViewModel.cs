using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusShell.Agents.Application.Abstractions.Configuration;
using NexusShell.Agents.Application.Abstractions.Models;
using NexusShell.Agents.Application.Abstractions.Secrets;
using NexusShell.Agents.Application.Conversations;
using NexusShell.Agents.Domain.Providers;
using NexusShell.Agents.Domain.Secrets;

namespace NexusShell.App.ViewModels
{
    /// <summary>One configured provider as the list shows and speaks it. Never holds a key value.</summary>
    public sealed class ProviderRow
    {
        public required ProviderProfile Profile { get; init; }

        public required bool IsAvailable { get; init; }

        public required string StatusDetail { get; init; }

        public required bool KeyIsSet { get; init; }

        public required bool IsPreferredFirst { get; init; }

        public string Id => Profile.Id;

        public string Text =>
            $"{Profile.Id}  -  {(Profile.IsLocal ? "local" : "online")}, {(Profile.Enabled ? IsAvailable ? "ready" : "not ready" : "turned off")}";

        public string AutomationName
        {
            get
            {
                string where = Profile.IsLocal ? "local server" : "online";
                string state = !Profile.Enabled ? "turned off" : IsAvailable ? "ready" : "not ready: " + StatusDetail;
                string key = Profile.ApiKey is null ? string.Empty : $", key variable {Profile.ApiKey.Name} {(KeyIsSet ? "is set" : "is not set")}";
                string model = Profile.DefaultModel is null ? string.Empty : $", default model {Profile.DefaultModel}";
                return $"{Profile.Id}, {where}, {state}{key}{model}{(IsPreferredFirst ? ", used first" : string.Empty)}";
            }
        }
    }

    /// <summary>
    /// "Providers and models": add online providers (OpenAI, Anthropic, Gemini, Groq, OpenRouter, Mistral,
    /// DeepSeek, xAI, Together, Fireworks, Cerebras, GitHub Models, Hugging Face, NVIDIA, Perplexity, Azure, or
    /// any OpenAI-compatible address) and local servers (Ollama, LM Studio, llama.cpp, vLLM, Jan), find their
    /// models and store their key. Settings go to the user's agent.json; keys go to user environment variables.
    /// </summary>
    public sealed class AgentProvidersViewModel : ViewModelBase
    {
        private readonly IAgentConfigurationProvider _configuration;
        private readonly INexusAgentService _agent;
        private readonly IModelDiscovery _discovery;
        private readonly IAgentSettingsStore _store;
        private readonly ISecretStore _secrets;

        public AgentProvidersViewModel(
            IAgentConfigurationProvider configuration,
            INexusAgentService agent,
            IModelDiscovery discovery,
            IAgentSettingsStore store,
            ISecretStore secrets)
        {
            _configuration = configuration;
            _agent = agent;
            _discovery = discovery;
            _store = store;
            _secrets = secrets;

            foreach (var preset in ProviderPresets.All) Presets.Add(preset);
            Kinds.Add(new ChoiceItem("openai-compatible", "OpenAI-compatible (most providers and local servers)", "OpenAI-compatible: most online providers and local servers"));
            Kinds.Add(new ChoiceItem("openai", "OpenAI", "OpenAI"));
            Kinds.Add(new ChoiceItem("anthropic", "Anthropic (Claude)", "Anthropic, Claude"));
            Kinds.Add(new ChoiceItem("ollama", "Ollama", "Ollama"));
            Kinds.Add(new ChoiceItem("azure-openai", "Azure OpenAI", "Azure OpenAI"));
            _selectedKind = Kinds[0];
        }

        public ObservableCollection<ProviderRow> Providers { get; } = new();

        public ObservableCollection<ProviderPreset> Presets { get; } = new();

        public ObservableCollection<ChoiceItem> Kinds { get; } = new();

        public ObservableCollection<string> FoundModels { get; } = new();

        public ObservableCollection<string> Models { get; } = new();

        private ProviderRow? _selectedProvider;
        public ProviderRow? SelectedProvider
        {
            get => _selectedProvider;
            set
            {
                if (!SetProperty(ref _selectedProvider, value) || value is null) return;
                var p = value.Profile;
                FillForm(p.Id, KindName(p.Kind), p.Endpoint?.ToString(), p.ApiKey?.Name, p.IsLocal, p.DefaultModel, p.Models);
                Prefer = value.IsPreferredFirst;
                IsExisting = true;
                StatusMessage = value.AutomationName;
            }
        }

        private ProviderPreset? _selectedPreset;
        public ProviderPreset? SelectedPreset
        {
            get => _selectedPreset;
            set
            {
                if (!SetProperty(ref _selectedPreset, value) || value is null) return;
                var existing = Providers.FirstOrDefault(r => string.Equals(r.Id, value.Id, StringComparison.OrdinalIgnoreCase));
                FillForm(value.Id, value.Kind, value.Endpoint, value.ApiKeyEnv, value.Local, existing?.Profile.DefaultModel ?? value.SuggestedModel, existing?.Profile.Models ?? []);
                IsExisting = existing is not null;
                StatusMessage = $"{value.Name}: {value.Notes} " +
                    (value.Local ? "No key needed. Press Find models, then Save provider." : $"Store your key, press Find models, then Save provider.");
            }
        }

        private string _providerId = string.Empty;
        public string ProviderId { get => _providerId; set => SetProperty(ref _providerId, value); }

        private ChoiceItem _selectedKind;
        public ChoiceItem SelectedKind { get => _selectedKind; set => SetProperty(ref _selectedKind, value ?? Kinds[0]); }

        private string _endpoint = string.Empty;
        public string Endpoint { get => _endpoint; set => SetProperty(ref _endpoint, value); }

        private bool _isLocal;
        public bool IsLocal { get => _isLocal; set => SetProperty(ref _isLocal, value); }

        private string _keyVariable = string.Empty;
        public string KeyVariable
        {
            get => _keyVariable;
            set
            {
                if (SetProperty(ref _keyVariable, value)) OnPropertyChanged(nameof(KeyStatus));
            }
        }

        public string KeyStatus
        {
            get
            {
                string name = KeyVariable.Trim();
                if (name.Length == 0) return IsLocal ? "Local servers need no key." : "Type the name of the variable that will hold the key, for example MYPROVIDER_API_KEY.";
                if (!SecretReference.IsValidName(name)) return "That is not a valid variable name (letters, digits and _ only).";
                return _secrets.HasSecret(new SecretReference(name)) ? $"{name} is set." : $"{name} is not set yet: paste the key and press Store key.";
            }
        }

        private string _defaultModel = string.Empty;
        public string DefaultModel { get => _defaultModel; set => SetProperty(ref _defaultModel, value); }

        private string? _selectedModelInList;
        public string? SelectedModelInList { get => _selectedModelInList; set => SetProperty(ref _selectedModelInList, value); }

        private bool _prefer;
        public bool Prefer { get => _prefer; set => SetProperty(ref _prefer, value); }

        private bool _isExisting;
        public bool IsExisting { get => _isExisting; private set => SetProperty(ref _isExisting, value); }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle));
            }
        }

        public bool IsIdle => !IsBusy;

        private string _statusMessage = "Choose a provider to edit it, or start from a template to add one. Keys are stored as environment variables of your Windows account, never in files.";
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

        public async Task LoadAsync()
        {
            _configuration.Invalidate();
            var config = await _configuration.GetConfigurationAsync();
            var health = await _agent.GetHealthAsync();
            var statuses = health.Providers.ToDictionary(s => s.ProviderId, StringComparer.OrdinalIgnoreCase);
            string? first = config.ModelPreference.Select(p => p.Split('/')[0].Trim()).FirstOrDefault(id =>
                config.Providers.TryGetValue(id, out var p) && p.Enabled);

            string? selected = SelectedProvider?.Id;
            Providers.Clear();
            foreach (var profile in config.Providers.Values.OrderBy(p => p.IsLocal).ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
            {
                statuses.TryGetValue(profile.Id, out var status);
                Providers.Add(new ProviderRow
                {
                    Profile = profile,
                    IsAvailable = status?.IsAvailable == true,
                    StatusDetail = status?.Detail ?? (profile.Enabled ? "unknown" : "turned off"),
                    KeyIsSet = profile.ApiKey is not null && _secrets.HasSecret(profile.ApiKey),
                    IsPreferredFirst = string.Equals(first, profile.Id, StringComparison.OrdinalIgnoreCase),
                });
            }
            if (selected is not null) _selectedProvider = Providers.FirstOrDefault(r => r.Id == selected);
            OnPropertyChanged(nameof(SelectedProvider));
        }

        public void NewProvider()
        {
            _selectedProvider = null;
            OnPropertyChanged(nameof(SelectedProvider));
            FillForm(string.Empty, "openai-compatible", "https://", string.Empty, false, string.Empty, []);
            IsExisting = false;
            StatusMessage = "New provider: type a name, its address and the key variable name, or choose a template.";
        }

        private void FillForm(string id, string kind, string? endpoint, string? keyVariable, bool local, string? defaultModel, IEnumerable<string> models)
        {
            ProviderId = id;
            SelectedKind = Kinds.FirstOrDefault(k => string.Equals(k.Value, kind, StringComparison.OrdinalIgnoreCase)) ?? Kinds[0];
            Endpoint = endpoint ?? string.Empty;
            IsLocal = local;
            KeyVariable = keyVariable ?? string.Empty;
            DefaultModel = defaultModel ?? string.Empty;
            Models.Clear();
            foreach (string model in models) Models.Add(model);
            FoundModels.Clear();
            Prefer = false;
            OnPropertyChanged(nameof(KeyStatus));
        }

        private ProviderSettings FormSettings() => new()
        {
            Id = ProviderId.Trim(),
            Kind = SelectedKind.Value ?? "openai-compatible",
            Endpoint = string.IsNullOrWhiteSpace(Endpoint) || Endpoint.Trim() == "https://" ? null : Endpoint.Trim(),
            ApiKeyEnv = string.IsNullOrWhiteSpace(KeyVariable) ? null : KeyVariable.Trim(),
            Local = IsLocal,
            DefaultModel = string.IsNullOrWhiteSpace(DefaultModel) ? null : DefaultModel.Trim(),
            Models = Models.ToList(),
            Prefer = Prefer,
        };

        /// <summary>Asks the provider which models it has (the key must be stored first for online providers).</summary>
        public async Task FindModelsAsync()
        {
            ProviderProfile profile;
            try
            {
                var settings = FormSettings();
                if (!ProviderKindParser.TryParse(settings.Kind, out var kind)) throw new ArgumentException("Choose a protocol.");
                Uri? endpoint = settings.Endpoint is null ? null : new Uri(settings.Endpoint);
                // Never send a key in clear text: online providers must use https (local servers may use http).
                if (endpoint is not null && !settings.Local && endpoint.Scheme != Uri.UriSchemeHttps)
                    throw new ArgumentException("online providers must use an https address. Tick Runs locally for a server on your computer or network.");
                profile = new ProviderProfile
                {
                    Id = string.IsNullOrWhiteSpace(settings.Id) ? "new-provider" : settings.Id,
                    Kind = kind,
                    Endpoint = endpoint,
                    ApiKey = SecretReference.FromNullable(settings.ApiKeyEnv),
                    IsLocal = settings.Local,
                };
            }
            catch (Exception ex) when (ex is ArgumentException or UriFormatException)
            {
                StatusMessage = "Cannot look for models yet: " + ex.Message;
                return;
            }

            if (profile.Kind == ProviderKind.AzureOpenAI)
            {
                StatusMessage = "Azure OpenAI uses deployment names: type your deployment name as the default model.";
                return;
            }

            IsBusy = true;
            StatusMessage = "Looking for models...";
            try
            {
                var models = await _discovery.ListModelsAsync(profile);
                FoundModels.Clear();
                foreach (string model in models) FoundModels.Add(model);
                StatusMessage = models.Count == 0
                    ? (profile.IsLocal
                        ? "No models found. Check that the local server is running at that address and has a model loaded (Ollama: ollama pull llama3.2)."
                        : "No models found. Check the address and that the key is stored, then try again.")
                    : $"Found {models.Count} models. Choose one in the Default model box, or add several to the model list.";
                if (models.Count > 0 && string.IsNullOrWhiteSpace(DefaultModel)) DefaultModel = models[0];
            }
            finally
            {
                IsBusy = false;
            }
        }

        public void AddModel()
        {
            string model = DefaultModel.Trim();
            if (model.Length == 0)
            {
                StatusMessage = "Type or choose a model in the Default model box first.";
                return;
            }
            if (Models.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                StatusMessage = $"{model} is already in the list.";
                return;
            }
            Models.Add(model);
            StatusMessage = $"Added {model}. It appears in the chat window's Model box after you save the provider.";
        }

        public void RemoveModel()
        {
            if (SelectedModelInList is not { } model) return;
            Models.Remove(model);
            StatusMessage = $"Removed {model} from the list.";
        }

        public async Task<bool> SaveAsync()
        {
            IsBusy = true;
            try
            {
                var settings = FormSettings();
                await _store.SaveProviderAsync(settings);
                await LoadAsync();
                _selectedProvider = Providers.FirstOrDefault(r => string.Equals(r.Id, settings.Id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(r.Id, settings.Id.ToLowerInvariant().Replace(' ', '-'), StringComparison.OrdinalIgnoreCase));
                OnPropertyChanged(nameof(SelectedProvider));
                IsExisting = true;

                var row = _selectedProvider;
                StatusMessage = row is null
                    ? "Saved."
                    : row.IsAvailable
                        ? $"Saved {row.Id}. It is ready and the agent can use it now."
                        : $"Saved {row.Id}, but it is not ready yet: {row.StatusDetail}";
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                StatusMessage = "Not saved: " + ex.Message;
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task RemoveAsync()
        {
            if (string.IsNullOrWhiteSpace(ProviderId)) return;
            IsBusy = true;
            try
            {
                await _store.RemoveProviderAsync(ProviderId.Trim());
                await LoadAsync();
                StatusMessage = $"{ProviderId} removed (built-in providers are turned off instead). Its key, if any, stays in your environment; remove it with Agent keys.";
                NewProvider();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                StatusMessage = "Not removed: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>Stores the key in the user's environment (the value is used once and never kept).</summary>
        public async Task<bool> StoreKeyAsync(string value)
        {
            string name = KeyVariable.Trim();
            if (!SecretReference.IsValidName(name))
            {
                StatusMessage = "Type a valid key variable name first, for example MYPROVIDER_API_KEY.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                StatusMessage = "Paste the key in the Key value box first.";
                return false;
            }

            IsBusy = true;
            try
            {
                await _secrets.SetSecretAsync(new SecretReference(name), value.Trim());
                OnPropertyChanged(nameof(KeyStatus));
                StatusMessage = $"Key stored in {name} (your Windows user environment). Press Find models to check it works.";
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not store the key: {ex.Message}";
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static string KindName(ProviderKind kind) => kind switch
        {
            ProviderKind.OpenAI => "openai",
            ProviderKind.AzureOpenAI => "azure-openai",
            ProviderKind.Anthropic => "anthropic",
            ProviderKind.Ollama => "ollama",
            _ => "openai-compatible",
        };
    }
}
