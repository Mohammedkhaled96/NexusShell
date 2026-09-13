using NexusShell.App.Commands;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;
using NexusShell.App.Views;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NexusShell.App.ViewModels
{
    public class ApiTesterViewModel : ViewModelBase
    {
        private readonly IApiTesterService _apiService;
        private readonly IDialogService    _dialogService;
        private CancellationTokenSource?   _cts;

        // ── Collections & Environments ──────────────────────────────────────────
        public ObservableCollection<ApiCollection>  Collections  { get; } = new();
        public ObservableCollection<ApiEnvironment> Environments { get; } = new();

        private ApiEnvironment? _activeEnvironment;
        public ApiEnvironment? ActiveEnvironment
        {
            get => _activeEnvironment;
            set { SetProperty(ref _activeEnvironment, value); OnPropertyChanged(nameof(ResolvedUrl)); }
        }

        // ── History ─────────────────────────────────────────────────────────────
        public ObservableCollection<ApiHistoryEntry> History { get; } = new();

        // ── Active Request ───────────────────────────────────────────────────────
        private ApiRequest _activeRequest = new();
        public ApiRequest ActiveRequest
        {
            get => _activeRequest;
            set
            {
                SetProperty(ref _activeRequest, value);
                SyncAuthFromRequest(value);
                SyncBodyTypeFromRequest(value);
                ClearJsonValidation();
                OnPropertyChanged(nameof(ResolvedUrl));
            }
        }

        public string ResolvedUrl => _apiService.ResolveVariables(ActiveRequest.Url, ActiveEnvironment);

        // ── Auth UI bindings ─────────────────────────────────────────────────────
        public static string[] AuthTypes       { get; } = { "None", "Bearer", "Basic", "ApiKey" };
        public static string[] ApiKeyLocations { get; } = { "Header", "Query" };

        private string _authType = "None";
        public string AuthType
        {
            get => _authType;
            set
            {
                SetProperty(ref _authType, value);
                ActiveRequest.Auth.Type = value;
                OnPropertyChanged(nameof(IsBearerAuth));
                OnPropertyChanged(nameof(IsBasicAuth));
                OnPropertyChanged(nameof(IsApiKeyAuth));
            }
        }
        public bool IsBearerAuth => _authType == "Bearer";
        public bool IsBasicAuth  => _authType == "Basic";
        public bool IsApiKeyAuth => _authType == "ApiKey";

        private string _bearerToken = string.Empty;
        public string BearerToken
        {
            get => _bearerToken;
            set { SetProperty(ref _bearerToken, value); ActiveRequest.Auth.Config["token"] = value; }
        }

        private string _basicUsername = string.Empty;
        public string BasicUsername
        {
            get => _basicUsername;
            set { SetProperty(ref _basicUsername, value); ActiveRequest.Auth.Config["username"] = value; }
        }

        private string _basicPassword = string.Empty;
        public string BasicPassword
        {
            get => _basicPassword;
            set { SetProperty(ref _basicPassword, value); ActiveRequest.Auth.Config["password"] = value; }
        }

        private string _apiKeyName = string.Empty;
        public string ApiKeyName
        {
            get => _apiKeyName;
            set { SetProperty(ref _apiKeyName, value); ActiveRequest.Auth.Config["key"] = value; }
        }

        private string _apiKeyValue = string.Empty;
        public string ApiKeyValue
        {
            get => _apiKeyValue;
            set { SetProperty(ref _apiKeyValue, value); ActiveRequest.Auth.Config["value"] = value; }
        }

        private string _apiKeyLocation = "Header";
        public string ApiKeyLocation
        {
            get => _apiKeyLocation;
            set { SetProperty(ref _apiKeyLocation, value); ActiveRequest.Auth.Config["location"] = value; }
        }

        // ── Body type UI bindings ────────────────────────────────────────────────
        // JSON is exposed as a top-level shortcut radio that selects Raw + RawType=JSON.
        // The "raw" radio represents Raw + any non-JSON format (Text/XML/HTML).
        public bool IsBodyNone
        {
            get => ActiveRequest.BodyType == ApiBodyType.None;
            set { if (value) SetBodyType(ApiBodyType.None); }
        }
        public bool IsBodyJson
        {
            get => ActiveRequest.BodyType == ApiBodyType.Raw
                && ActiveRequest.RawType  == ApiRawType.JSON;
            set { if (value) SetBodyType(ApiBodyType.Raw, ApiRawType.JSON); }
        }
        public bool IsBodyRaw
        {
            get => ActiveRequest.BodyType == ApiBodyType.Raw
                && ActiveRequest.RawType  != ApiRawType.JSON;
            // Switching to "raw" from JSON drops to Text so the radios stay mutually exclusive
            set { if (value) SetBodyType(ApiBodyType.Raw,
                                         ActiveRequest.RawType == ApiRawType.JSON
                                             ? ApiRawType.Text
                                             : ActiveRequest.RawType); }
        }
        public bool IsBodyFormData
        {
            get => ActiveRequest.BodyType == ApiBodyType.FormData;
            set { if (value) SetBodyType(ApiBodyType.FormData); }
        }
        public bool IsBodyUrlEncoded
        {
            get => ActiveRequest.BodyType == ApiBodyType.UrlEncoded;
            set { if (value) SetBodyType(ApiBodyType.UrlEncoded); }
        }
        public bool IsBodyForm =>
            ActiveRequest.BodyType is ApiBodyType.FormData or ApiBodyType.UrlEncoded;
        // True for both "raw" and "JSON" — controls visibility of the editor + format strip
        public bool IsBodyRawAny => ActiveRequest.BodyType == ApiBodyType.Raw;

        private void SetBodyType(ApiBodyType type, ApiRawType? rawType = null)
        {
            ActiveRequest.BodyType = type;
            if (rawType.HasValue) ActiveRequest.RawType = rawType.Value;
            AutoSuggestContentType(type);
            ClearJsonValidation();
            OnPropertyChanged(nameof(IsBodyNone));
            OnPropertyChanged(nameof(IsBodyJson));
            OnPropertyChanged(nameof(IsBodyRaw));
            OnPropertyChanged(nameof(IsBodyFormData));
            OnPropertyChanged(nameof(IsBodyUrlEncoded));
            OnPropertyChanged(nameof(IsBodyForm));
            OnPropertyChanged(nameof(IsBodyRawAny));
            OnPropertyChanged(nameof(ActiveRequest));
        }

        /// <summary>
        /// When the user switches body type, add the appropriate Content-Type header
        /// if no Content-Type header currently exists in the request.
        /// Never overwrites an existing value the user set intentionally.
        /// </summary>
        private void AutoSuggestContentType(ApiBodyType type)
        {
            var suggested = type switch
            {
                ApiBodyType.UrlEncoded => "application/x-www-form-urlencoded",
                ApiBodyType.Raw        => ActiveRequest.RawType == ApiRawType.JSON ? "application/json"
                                       : ActiveRequest.RawType == ApiRawType.XML  ? "application/xml"
                                       : (string?)null,
                _                      => null
            };
            if (suggested == null) return;

            // Check if a Content-Type header already exists (non-empty key)
            bool alreadySet = ActiveRequest.Headers.Any(h =>
                h.IsEnabled &&
                !string.IsNullOrWhiteSpace(h.Key) &&
                string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase));

            if (alreadySet) return;

            // Replace the first empty-key slot, or append a new row
            var emptySlot = ActiveRequest.Headers.FirstOrDefault(h => string.IsNullOrWhiteSpace(h.Key));
            if (emptySlot != null)
            {
                emptySlot.Key      = "Content-Type";
                emptySlot.Value    = suggested;
                emptySlot.IsEnabled = true;
            }
            else
            {
                ActiveRequest.Headers.Add(new ApiHeader
                {
                    Key       = "Content-Type",
                    Value     = suggested,
                    IsEnabled = true
                });
            }
            StatusMessage = $"Content-Type auto-set to '{suggested}' — adjust in Headers tab if needed.";
        }

        // ── Code generation ──────────────────────────────────────────────────────
        public static string[] CodeLanguages { get; } = { "cURL", "C#", "JavaScript", "Python", "TypeScript" };

        private string _selectedCodeLanguage = "cURL";
        public string SelectedCodeLanguage
        {
            get => _selectedCodeLanguage;
            set { SetProperty(ref _selectedCodeLanguage, value); RefreshCodeSnippet(); }
        }

        private string _codeSnippet = string.Empty;
        public string CodeSnippet
        {
            get => _codeSnippet;
            set => SetProperty(ref _codeSnippet, value);
        }

        // ── UI state ─────────────────────────────────────────────────────────────
        private bool _isSending;
        public bool IsSending
        {
            get => _isSending;
            set => SetProperty(ref _isSending, value);
        }

        private string _statusMessage = "Ready — Ctrl+Enter to send";
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        // ── Inline response state ─────────────────────────────────────────────────
        private ApiResponse? _lastResponse;

        private bool _hasResponse;
        public bool HasResponse
        {
            get => _hasResponse;
            set => SetProperty(ref _hasResponse, value);
        }

        private int _responseStatusCode;
        public int ResponseStatusCode
        {
            get => _responseStatusCode;
            set => SetProperty(ref _responseStatusCode, value);
        }

        private string _responseStatusDesc = string.Empty;
        public string ResponseStatusDesc
        {
            get => _responseStatusDesc;
            set => SetProperty(ref _responseStatusDesc, value);
        }

        private long _responseTimeMs;
        public long ResponseTimeMs
        {
            get => _responseTimeMs;
            set => SetProperty(ref _responseTimeMs, value);
        }

        private string _responseFormattedSize = string.Empty;
        public string ResponseFormattedSize
        {
            get => _responseFormattedSize;
            set => SetProperty(ref _responseFormattedSize, value);
        }

        private string _responseBodyText = string.Empty;
        public string ResponseBodyText
        {
            get => _responseBodyText;
            set => SetProperty(ref _responseBodyText, value);
        }

        private string _responseHeadersText = string.Empty;
        public string ResponseHeadersText
        {
            get => _responseHeadersText;
            set => SetProperty(ref _responseHeadersText, value);
        }

        private string _responseCookiesText = string.Empty;
        public string ResponseCookiesText
        {
            get => _responseCookiesText;
            set => SetProperty(ref _responseCookiesText, value);
        }

        // ── JSON body validation (live, driven from code-behind TextChanged) ─────
        private string _jsonValidationStatus = string.Empty;
        public string JsonValidationStatus
        {
            get => _jsonValidationStatus;
            set => SetProperty(ref _jsonValidationStatus, value);
        }

        private bool _isJsonBodyValid = true;
        public bool IsJsonBodyValid
        {
            get => _isJsonBodyValid;
            set
            {
                SetProperty(ref _isJsonBodyValid, value);
                OnPropertyChanged(nameof(JsonValidationBrush));
            }
        }

        /// <summary>Green when JSON is valid, red when invalid, transparent when empty/not JSON.</summary>
        public Brush JsonValidationBrush =>
            string.IsNullOrEmpty(_jsonValidationStatus)
                ? Brushes.Transparent
                : _isJsonBodyValid
                    ? new SolidColorBrush(Color.FromRgb(78, 201, 176))   // teal-green
                    : new SolidColorBrush(Color.FromRgb(244, 71,  71));   // red

        /// <summary>Called by code-behind on every keystroke in the raw body editor.</summary>
        public void ValidateJsonBody(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                ClearJsonValidation();
                return;
            }
            try
            {
                using var _ = System.Text.Json.JsonDocument.Parse(text);
                JsonValidationStatus = "✓ Valid JSON";
                IsJsonBodyValid      = true;
            }
            catch
            {
                JsonValidationStatus = "✗ Invalid JSON";
                IsJsonBodyValid      = false;
            }
        }

        public void ClearJsonValidation()
        {
            JsonValidationStatus = string.Empty;
            IsJsonBodyValid      = true;
        }

        // ── Commands: request ────────────────────────────────────────────────────
        public ICommand SendRequestCommand      { get; }
        public ICommand CancelRequestCommand    { get; }
        public ICommand AddParamCommand         { get; }
        public ICommand RemoveParamCommand      { get; }
        public ICommand AddHeaderCommand        { get; }
        public ICommand RemoveHeaderCommand     { get; }

        // ── Commands: collections ────────────────────────────────────────────────
        public ICommand NewCollectionCommand            { get; }
        public ICommand ImportCollectionCommand         { get; }
        public ICommand RenameCollectionCommand         { get; }
        public ICommand ExportCollectionCommand         { get; }
        public ICommand DeleteCollectionCommand         { get; }
        public ICommand AddFolderToCollectionCommand    { get; }
        public ICommand AddRequestToCollectionCommand   { get; }

        // ── Commands: folders ────────────────────────────────────────────────────
        public ICommand RenameFolderCommand             { get; }
        public ICommand DeleteFolderCommand             { get; }
        public ICommand AddFolderToFolderCommand        { get; }
        public ICommand AddRequestToFolderCommand       { get; }

        // ── Commands: requests ───────────────────────────────────────────────────
        public ICommand SelectRequestCommand            { get; }
        public ICommand RenameRequestCommand            { get; }
        public ICommand DeleteRequestCommand            { get; }
        public ICommand DuplicateRequestCommand         { get; }
        public ICommand SaveActiveRequestCommand        { get; }
        public ICommand BeautifyBodyCommand             { get; }
        public ICommand RefreshCodeSnippetCommand       { get; }

        // ── Commands: history / environment ─────────────────────────────────────
        public ICommand LoadFromHistoryCommand      { get; }
        public ICommand ClearHistoryCommand         { get; }
        public ICommand NewEnvironmentCommand       { get; }
        public ICommand DeleteEnvironmentCommand    { get; }
        public ICommand CopyCodeSnippetCommand      { get; }
        public ICommand CopyResponseBodyCommand     { get; }
        public ICommand ViewResponseDetailCommand   { get; }
        public ICommand ClearResponseCommand        { get; }

        // ── Constructor ──────────────────────────────────────────────────────────
        public ApiTesterViewModel(IApiTesterService apiService, IDialogService dialogService)
        {
            _apiService    = apiService;
            _dialogService = dialogService;

            // Request commands
            SendRequestCommand      = new RelayCommand(async _ => await ExecuteSendRequest(), _ => !IsSending);
            CancelRequestCommand    = new RelayCommand(_ => _cts?.Cancel(), _ => IsSending);
            AddParamCommand         = new RelayCommand(_ => ActiveRequest.Parameters.Add(new ApiParameter()));
            RemoveParamCommand      = new RelayCommand(p => { if (p is ApiParameter x) ActiveRequest.Parameters.Remove(x); });
            AddHeaderCommand        = new RelayCommand(_ => ActiveRequest.Headers.Add(new ApiHeader()));
            RemoveHeaderCommand     = new RelayCommand(h => { if (h is ApiHeader x) ActiveRequest.Headers.Remove(x); });

            // Collection commands
            NewCollectionCommand            = new RelayCommand(_ => CreateNewCollection());
            ImportCollectionCommand         = new RelayCommand(async _ => await ImportCollection());
            RenameCollectionCommand         = new RelayCommand(c => RenameCollection(c as ApiCollection));
            ExportCollectionCommand         = new RelayCommand(c => ExportCollection(c as ApiCollection));
            DeleteCollectionCommand         = new RelayCommand(c => DeleteCollection(c as ApiCollection));
            AddFolderToCollectionCommand    = new RelayCommand(c => AddFolderToCollection(c as ApiCollection));
            AddRequestToCollectionCommand   = new RelayCommand(c => AddRequestToCollection(c as ApiCollection));

            // Folder commands
            RenameFolderCommand             = new RelayCommand(f => RenameFolder(f as ApiFolder));
            DeleteFolderCommand             = new RelayCommand(f => DeleteFolder(f as ApiFolder));
            AddFolderToFolderCommand        = new RelayCommand(f => AddFolderToFolder(f as ApiFolder));
            AddRequestToFolderCommand       = new RelayCommand(f => AddRequestToFolder(f as ApiFolder));

            // Request commands
            SelectRequestCommand            = new RelayCommand(r => { if (r is ApiRequest x) LoadRequest(x); });
            RenameRequestCommand            = new RelayCommand(r => RenameRequest(r as ApiRequest));
            DeleteRequestCommand            = new RelayCommand(r => DeleteRequest(r as ApiRequest));
            DuplicateRequestCommand         = new RelayCommand(r => DuplicateRequest(r as ApiRequest));
            SaveActiveRequestCommand        = new RelayCommand(_ => SaveActiveRequest());
            BeautifyBodyCommand             = new RelayCommand(_ => BeautifyBody(), _ => IsBodyRawAny);
            RefreshCodeSnippetCommand       = new RelayCommand(_ => RefreshCodeSnippet());

            // History / env commands
            LoadFromHistoryCommand          = new RelayCommand(h => { if (h is ApiHistoryEntry e) LoadRequest(e.RequestSnapshot); });
            ClearHistoryCommand             = new RelayCommand(_ => ClearHistory());
            NewEnvironmentCommand           = new RelayCommand(_ => CreateNewEnvironment());
            DeleteEnvironmentCommand        = new RelayCommand(e => DeleteEnvironment(e as ApiEnvironment));
            CopyCodeSnippetCommand          = new RelayCommand(_ => CopyToClipboard(CodeSnippet));
            CopyResponseBodyCommand         = new RelayCommand(_ => CopyToClipboard(ResponseBodyText), _ => HasResponse);
            ViewResponseDetailCommand       = new RelayCommand(_ => OpenResponseDetail(),              _ => HasResponse);
            ClearResponseCommand            = new RelayCommand(_ => ClearResponse(),                  _ => HasResponse);

            LoadPersistedData();
        }

        // ── Startup data load ────────────────────────────────────────────────────
        private void LoadPersistedData()
        {
            foreach (var c in _apiService.LoadCollections())  Collections.Add(c);
            foreach (var e in _apiService.LoadEnvironments()) Environments.Add(e);
            foreach (var h in _apiService.LoadHistory())      History.Add(h);

            var defaultReq = new ApiRequest();
            defaultReq.Headers.Clear();
            defaultReq.Headers.Add(new ApiHeader { Key = "Content-Type", Value = "application/json" });
            defaultReq.Headers.Add(new ApiHeader { Key = "Accept",       Value = "application/json" });
            ActiveRequest = defaultReq;
        }

        // ── Send request ─────────────────────────────────────────────────────────
        private async Task ExecuteSendRequest()
        {
            if (string.IsNullOrWhiteSpace(ActiveRequest.Url))
            {
                StatusMessage = "⚠ Please enter a URL before sending.";
                return;
            }

            // Validate JSON body before sending
            if (IsBodyJson && !string.IsNullOrWhiteSpace(ActiveRequest.BodyContent))
            {
                try { using var _ = System.Text.Json.JsonDocument.Parse(ActiveRequest.BodyContent); }
                catch (System.Text.Json.JsonException ex)
                {
                    var proceed = MessageBox.Show(
                        $"Request body contains invalid JSON:\n\n{ex.Message}\n\nSend anyway?",
                        "JSON Validation Warning",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (proceed != MessageBoxResult.Yes) return;
                }
            }

            // Dispose any leftover CTS from a previous send before allocating a new one
            _cts?.Dispose();
            _cts          = new CancellationTokenSource();
            IsSending     = true;
            HasResponse   = false;
            StatusMessage = "Sending request…";

            try
            {
                var response = await _apiService.SendRequestAsync(ActiveRequest, ActiveEnvironment, _cts.Token);

                // Pretty-print JSON body
                if (!string.IsNullOrEmpty(response.Body))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(response.Body);
                        response.Body = System.Text.Json.JsonSerializer.Serialize(
                            doc.RootElement,
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    }
                    catch { /* not JSON — show raw */ }
                }

                // Populate inline response panel
                _lastResponse         = response;
                HasResponse           = true;
                ResponseStatusCode    = response.StatusCode;
                ResponseStatusDesc    = response.StatusDescription;
                ResponseTimeMs        = response.ResponseTimeMs;
                ResponseFormattedSize = response.FormattedSize;
                ResponseBodyText      = response.Body;
                ResponseHeadersText   = string.Join("\n", response.Headers.Select(kvp => $"{kvp.Key}: {kvp.Value}"));
                ResponseCookiesText   = string.Join("\n", response.Cookies.Select(kvp => $"{kvp.Key}: {kvp.Value}"));

                StatusMessage = response.StatusCode == 0
                    ? $"✗ {response.StatusDescription}"
                    : $"✓ {response.StatusCode} {response.StatusDescription}  ·  {response.ResponseTimeMs} ms  ·  {response.FormattedSize}";

                AddToHistory(response);
                RefreshCodeSnippet();
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Request cancelled.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"✗ Error: {ex.Message}";
            }
            finally
            {
                IsSending = false;
                _cts?.Dispose();
                _cts      = null;
            }
        }

        // Opens the ApiResponseWindow (maximized) for a full-screen detail view
        private void OpenResponseDetail()
        {
            if (_lastResponse == null) return;
            Application.Current.Dispatcher.Invoke(() =>
            {
                var owner  = GetMainWindow();
                var resWin = new ApiResponseWindow(_lastResponse) { Owner = owner };
                resWin.Show();
            });
        }

        // ── Clear response panel ─────────────────────────────────────────────────
        private void ClearResponse()
        {
            _lastResponse         = null;
            HasResponse           = false;
            ResponseStatusCode    = 0;
            ResponseStatusDesc    = string.Empty;
            ResponseTimeMs        = 0;
            ResponseFormattedSize = string.Empty;
            ResponseBodyText      = string.Empty;
            ResponseHeadersText   = string.Empty;
            ResponseCookiesText   = string.Empty;
            StatusMessage         = "Response cleared.";
        }

        // ── History ──────────────────────────────────────────────────────────────
        // SECURITY: history is persisted to disk in plaintext. Strip auth credentials
        // and any header that looks sensitive (Authorization / X-API-Key / Cookie / …)
        // BEFORE we snapshot the request, so secrets do not bleed into history.json.
        private void AddToHistory(ApiResponse response)
        {
            var snapshot = CloneRequest(ActiveRequest);

            // Drop the full Auth.Config (tokens, passwords, api-key values) — keep
            // only the auth Type so the user can see WHICH method was used.
            snapshot.Auth = new ApiAuth { Type = snapshot.Auth.Type };

            // Mask values of headers commonly used to carry secrets
            foreach (var h in snapshot.Headers)
            {
                if (IsSensitiveHeader(h.Key)) h.Value = "***";
            }

            var entry = new ApiHistoryEntry
            {
                Method          = ActiveRequest.Method,
                Url             = ActiveRequest.Url,
                StatusCode      = response.StatusCode,
                ResponseTimeMs  = response.ResponseTimeMs,
                RequestSnapshot = snapshot
            };
            History.Insert(0, entry);
            while (History.Count > 100) History.RemoveAt(100);
            _apiService.SaveHistory(History);
        }

        private static bool IsSensitiveHeader(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            var k = key.Trim();
            return k.Equals("Authorization",   StringComparison.OrdinalIgnoreCase)
                || k.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                || k.Equals("Cookie",           StringComparison.OrdinalIgnoreCase)
                || k.Equals("Set-Cookie",       StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("X-API",        StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("X-Auth",       StringComparison.OrdinalIgnoreCase)
                || k.IndexOf("token",  StringComparison.OrdinalIgnoreCase) >= 0
                || k.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0
                || k.IndexOf("apikey", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ClearHistory()
        {
            History.Clear();
            _apiService.SaveHistory(History);
            StatusMessage = "History cleared.";
        }

        // ── Collection CRUD ───────────────────────────────────────────────────────
        private void CreateNewCollection()
        {
            var name = SimpleInputDialog.Show("New Collection", "Collection name:", "New Collection",
                           GetMainWindow());
            if (string.IsNullOrWhiteSpace(name)) return;

            var col = new ApiCollection { Name = name };
            Collections.Add(col);
            Save();
            StatusMessage = $"Created collection '{name}'.";
        }

        private void RenameCollection(ApiCollection? col)
        {
            if (col == null) return;
            var name = SimpleInputDialog.Show("Rename Collection", "New name:", col.Name, GetMainWindow());
            if (string.IsNullOrWhiteSpace(name)) return;
            col.Name = name;
            Save();
            StatusMessage = $"Renamed to '{name}'.";
        }

        private async Task ImportCollection()
        {
            var file = _dialogService.ShowOpenFileDialog(
                "Postman & OpenAPI files|*.json;*.yaml;*.yml|JSON|*.json|YAML|*.yaml;*.yml|All|*.*");
            if (string.IsNullOrEmpty(file)) return;

            try
            {
                var content = await File.ReadAllTextAsync(file);
                ApiCollection collection;

                if (file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".yml",  StringComparison.OrdinalIgnoreCase))
                {
                    collection = _apiService.ImportOpenApi(content);
                }
                else
                {
                    try   { collection = _apiService.ImportPostmanCollection(content); }
                    catch { collection = _apiService.ImportOpenApi(content); }
                }

                Collections.Add(collection);
                Save();
                var total = CountRequests(collection);
                StatusMessage = $"Imported '{collection.Name}' — {total} request(s).";
            }
            catch (Exception ex)
            {
                StatusMessage = $"✗ Import failed: {ex.Message}";
            }
        }

        private void ExportCollection(ApiCollection? col)
        {
            if (col == null) return;
            var file = _dialogService.ShowSaveFileDialog($"{col.Name}.json", "JSON|*.json");
            if (string.IsNullOrEmpty(file)) return;

            try
            {
                File.WriteAllText(file, Newtonsoft.Json.JsonConvert.SerializeObject(col, Newtonsoft.Json.Formatting.Indented));
                StatusMessage = $"Exported '{col.Name}'.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"✗ Export failed: {ex.Message}";
            }
        }

        private void DeleteCollection(ApiCollection? col)
        {
            if (col == null) return;
            var ok = MessageBox.Show($"Delete collection '{col.Name}'?", "Confirm Delete",
                         MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.Yes) return;

            // If the active request lives anywhere inside this collection, reset
            // the editor so a subsequent Save can't resurrect a deleted item.
            if (CollectionContainsRequest(col, ActiveRequest.Id))
                ResetActiveRequest();

            if (Collections.Remove(col))
            {
                Save();
                StatusMessage = $"Deleted '{col.Name}'.";
            }
        }

        // True if any request inside the collection (incl. nested folders) has the given Id.
        private static bool CollectionContainsRequest(ApiCollection col, Guid id)
            => col.Requests.Any(r => r.Id == id) || col.Folders.Any(f => FolderContainsRequest(f, id));

        private static bool FolderContainsRequest(ApiFolder folder, Guid id)
            => folder.Requests.Any(r => r.Id == id) || folder.Folders.Any(sub => FolderContainsRequest(sub, id));

        // Replace ActiveRequest with a fresh blank request so the editor
        // is no longer bound to a deleted item.
        private void ResetActiveRequest()
        {
            var blank = new ApiRequest();
            blank.Headers.Clear();
            blank.Headers.Add(new ApiHeader { Key = "Content-Type", Value = "application/json" });
            blank.Headers.Add(new ApiHeader { Key = "Accept",       Value = "application/json" });
            ActiveRequest = blank;
        }

        private void AddFolderToCollection(ApiCollection? col)
        {
            if (col == null) return;
            var name = SimpleInputDialog.Show("New Folder", "Folder name:", "New Folder", GetMainWindow());
            if (string.IsNullOrWhiteSpace(name)) return;
            col.Folders.Add(new ApiFolder { Name = name });
            Save();
            StatusMessage = $"Added folder '{name}' to '{col.Name}'.";
        }

        private void AddRequestToCollection(ApiCollection? col)
        {
            if (col == null) return;
            var req = new ApiRequest { Name = "New Request" };
            col.Requests.Add(req);
            Save();
            LoadRequest(req);
            StatusMessage = $"New request added to '{col.Name}'.";
        }

        // ── Folder CRUD ───────────────────────────────────────────────────────────
        private void RenameFolder(ApiFolder? folder)
        {
            if (folder == null) return;
            var name = SimpleInputDialog.Show("Rename Folder", "New name:", folder.Name, GetMainWindow());
            if (string.IsNullOrWhiteSpace(name)) return;
            folder.Name = name;
            Save();
        }

        private void DeleteFolder(ApiFolder? folder)
        {
            if (folder == null) return;
            var ok = MessageBox.Show($"Delete folder '{folder.Name}' and all its requests?",
                         "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.Yes) return;

            // If the active request is inside this folder, clear the editor first
            if (FolderContainsRequest(folder, ActiveRequest.Id))
                ResetActiveRequest();

            foreach (var col in Collections)
            {
                if (col.Folders.Remove(folder)) { Save(); return; }
                if (RemoveFolderFromFolder(col.Folders, folder)) { Save(); return; }
            }
        }

        private static bool RemoveFolderFromFolder(ObservableCollection<ApiFolder> folders, ApiFolder target)
        {
            foreach (var f in folders)
            {
                if (f.Folders.Remove(target)) return true;
                if (RemoveFolderFromFolder(f.Folders, target)) return true;
            }
            return false;
        }

        private void AddFolderToFolder(ApiFolder? folder)
        {
            if (folder == null) return;
            var name = SimpleInputDialog.Show("New Folder", "Folder name:", "New Folder", GetMainWindow());
            if (string.IsNullOrWhiteSpace(name)) return;
            folder.Folders.Add(new ApiFolder { Name = name });
            Save();
        }

        private void AddRequestToFolder(ApiFolder? folder)
        {
            if (folder == null) return;
            var req = new ApiRequest { Name = "New Request" };
            folder.Requests.Add(req);
            Save();
            LoadRequest(req);
        }

        // ── Request CRUD ──────────────────────────────────────────────────────────
        private void RenameRequest(ApiRequest? req)
        {
            if (req == null) return;
            var name = SimpleInputDialog.Show("Rename Request", "New name:", req.Name, GetMainWindow());
            if (string.IsNullOrWhiteSpace(name)) return;
            req.Name = name;
            Save();
        }

        private void DeleteRequest(ApiRequest? req)
        {
            if (req == null) return;

            // If we're deleting the request currently loaded in the editor,
            // reset it so subsequent Ctrl+S can't resurrect it.
            if (ActiveRequest.Id == req.Id) ResetActiveRequest();

            foreach (var col in Collections)
            {
                if (col.Requests.Remove(req)) { Save(); return; }
                if (RemoveRequestFromFolders(col.Folders, req)) { Save(); return; }
            }
        }

        private static bool RemoveRequestFromFolders(ObservableCollection<ApiFolder> folders, ApiRequest req)
        {
            foreach (var f in folders)
            {
                if (f.Requests.Remove(req)) return true;
                if (RemoveRequestFromFolders(f.Folders, req)) return true;
            }
            return false;
        }

        // ── Duplicate request ─────────────────────────────────────────────────────
        private void DuplicateRequest(ApiRequest? req)
        {
            if (req == null) return;
            var clone = CloneRequest(req);
            clone.Id   = Guid.NewGuid();
            clone.Name = $"{req.Name} (copy)";

            foreach (var col in Collections)
            {
                if (col.Requests.Contains(req)) { col.Requests.Add(clone); Save(); StatusMessage = $"Duplicated '{req.Name}'."; return; }
                if (DuplicateInFolders(col.Folders, req, clone)) { Save(); StatusMessage = $"Duplicated '{req.Name}'."; return; }
            }
        }

        private static bool DuplicateInFolders(ObservableCollection<ApiFolder> folders, ApiRequest original, ApiRequest clone)
        {
            foreach (var f in folders)
            {
                if (f.Requests.Contains(original)) { f.Requests.Add(clone); return true; }
                if (DuplicateInFolders(f.Folders, original, clone)) return true;
            }
            return false;
        }

        // ── Save active request back to its collection ────────────────────────────
        private void SaveActiveRequest()
        {
            var id = ActiveRequest.Id;

            foreach (var col in Collections)
            {
                for (int i = 0; i < col.Requests.Count; i++)
                {
                    if (col.Requests[i].Id == id)
                    {
                        col.Requests[i] = ActiveRequest;
                        Save();
                        StatusMessage = $"✓ Saved '{ActiveRequest.Name}' to '{col.Name}'.";
                        return;
                    }
                }
                if (SaveInFolders(col.Folders, id)) return;
            }

            StatusMessage = "⚠ Request is not part of any collection — add it first via the sidebar.";
        }

        private bool SaveInFolders(ObservableCollection<ApiFolder> folders, Guid id)
        {
            foreach (var f in folders)
            {
                for (int i = 0; i < f.Requests.Count; i++)
                {
                    if (f.Requests[i].Id == id)
                    {
                        f.Requests[i] = ActiveRequest;
                        Save();
                        StatusMessage = $"✓ Saved '{ActiveRequest.Name}' to '{f.Name}'.";
                        return true;
                    }
                }
                if (SaveInFolders(f.Folders, id)) return true;
            }
            return false;
        }

        // ── Beautify / Format JSON body ───────────────────────────────────────────
        private void BeautifyBody()
        {
            if (!IsBodyRawAny || string.IsNullOrWhiteSpace(ActiveRequest.BodyContent)) return;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(ActiveRequest.BodyContent);
                var formatted = System.Text.Json.JsonSerializer.Serialize(
                    doc.RootElement,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                // Replace ActiveRequest with a clone carrying the formatted body
                // (forces the TextBox binding to refresh since ApiRequest lacks INPC)
                var copy         = CloneRequest(ActiveRequest);
                copy.BodyContent = formatted;
                ActiveRequest    = copy;
                StatusMessage    = "✓ JSON formatted.";
            }
            catch (System.Text.Json.JsonException)
            {
                StatusMessage = "⚠ Body is not valid JSON — cannot format.";
            }
        }

        // ── Environments ─────────────────────────────────────────────────────────
        private void CreateNewEnvironment()
        {
            var env = new ApiEnvironment { Name = "New Environment" };
            Environments.Add(env);
            _apiService.SaveEnvironments(Environments);
            StatusMessage = "New environment created.";
        }

        private void DeleteEnvironment(ApiEnvironment? env)
        {
            if (env == null) return;
            if (ActiveEnvironment == env) ActiveEnvironment = null;
            if (Environments.Remove(env))
            {
                _apiService.SaveEnvironments(Environments);
                StatusMessage = $"Deleted environment '{env.Name}'.";
            }
        }

        // ── Request load / clone ─────────────────────────────────────────────────
        private void LoadRequest(ApiRequest src)
        {
            ActiveRequest = CloneRequest(src);
            StatusMessage = $"Loaded: {src.Method} {src.Url}";
        }

        private static ApiRequest CloneRequest(ApiRequest src) => new()
        {
            Id               = src.Id,
            Name             = src.Name,
            Method           = src.Method,
            Url              = src.Url,
            Parameters       = new ObservableCollection<ApiParameter>(
                                   src.Parameters.Select(p => new ApiParameter
                                       { Key = p.Key, Value = p.Value, IsEnabled = p.IsEnabled })),
            Headers          = new ObservableCollection<ApiHeader>(
                                   src.Headers.Select(h => new ApiHeader
                                       { Key = h.Key, Value = h.Value, IsEnabled = h.IsEnabled })),
            Auth             = new ApiAuth
                                   { Type   = src.Auth.Type,
                                     Config = new System.Collections.Generic.Dictionary<string, string>(src.Auth.Config) },
            BodyType         = src.BodyType,
            RawType          = src.RawType,
            BodyContent      = src.BodyContent,
            PreRequestScript = src.PreRequestScript,
            TestScript       = src.TestScript
        };

        // ── Code snippet ─────────────────────────────────────────────────────────
        private void RefreshCodeSnippet()
        {
            CodeSnippet = SelectedCodeLanguage switch
            {
                "cURL"       => _apiService.GenerateCurl(ActiveRequest,       ActiveEnvironment),
                "C#"         => _apiService.GenerateCSharp(ActiveRequest,     ActiveEnvironment),
                "JavaScript" => _apiService.GenerateJavaScript(ActiveRequest, ActiveEnvironment),
                "Python"     => _apiService.GeneratePython(ActiveRequest,     ActiveEnvironment),
                "TypeScript" => _apiService.GenerateTypeScript(ActiveRequest, ActiveEnvironment),
                _            => string.Empty
            };
        }

        // ── Sync helpers ─────────────────────────────────────────────────────────
        private void SyncAuthFromRequest(ApiRequest req)
        {
            _authType       = req.Auth.Type;
            _bearerToken    = req.Auth.Config.GetValueOrDefault("token",    string.Empty);
            _basicUsername  = req.Auth.Config.GetValueOrDefault("username", string.Empty);
            _basicPassword  = req.Auth.Config.GetValueOrDefault("password", string.Empty);
            _apiKeyName     = req.Auth.Config.GetValueOrDefault("key",      string.Empty);
            _apiKeyValue    = req.Auth.Config.GetValueOrDefault("value",    string.Empty);
            _apiKeyLocation = req.Auth.Config.GetValueOrDefault("location", "Header");

            OnPropertyChanged(nameof(AuthType));
            OnPropertyChanged(nameof(BearerToken));
            OnPropertyChanged(nameof(BasicUsername));
            OnPropertyChanged(nameof(BasicPassword));
            OnPropertyChanged(nameof(ApiKeyName));
            OnPropertyChanged(nameof(ApiKeyValue));
            OnPropertyChanged(nameof(ApiKeyLocation));
            OnPropertyChanged(nameof(IsBearerAuth));
            OnPropertyChanged(nameof(IsBasicAuth));
            OnPropertyChanged(nameof(IsApiKeyAuth));
        }

        private void SyncBodyTypeFromRequest(ApiRequest req)
        {
            OnPropertyChanged(nameof(IsBodyNone));
            OnPropertyChanged(nameof(IsBodyJson));
            OnPropertyChanged(nameof(IsBodyRaw));
            OnPropertyChanged(nameof(IsBodyFormData));
            OnPropertyChanged(nameof(IsBodyUrlEncoded));
            OnPropertyChanged(nameof(IsBodyForm));
            OnPropertyChanged(nameof(IsBodyRawAny));
        }

        // ── Persistence ───────────────────────────────────────────────────────────
        private void Save() => _apiService.SaveCollections(Collections);

        // ── Helpers ───────────────────────────────────────────────────────────────
        private static void CopyToClipboard(string text)
        {
            if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
        }

        private static Window? GetMainWindow() =>
            Application.Current.Windows.OfType<ApiTesterWindow>().FirstOrDefault()
            ?? Application.Current.MainWindow;

        private static int CountRequests(ApiCollection col) =>
            col.Requests.Count + col.Folders.Sum(CountFolderRequests);

        private static int CountFolderRequests(ApiFolder f) =>
            f.Requests.Count + f.Folders.Sum(CountFolderRequests);
    }
}
