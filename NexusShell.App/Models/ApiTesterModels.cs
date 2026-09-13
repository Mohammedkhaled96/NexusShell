using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;

namespace NexusShell.App.Models
{
    /// <summary>Lightweight INPC base so DataGrid cell edits propagate to the bound object.</summary>
    public abstract class ObservableModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public enum HttpMethodType { GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS }
    public enum ApiBodyType    { None, Raw, FormData, UrlEncoded }
    public enum ApiRawType     { JSON, Text, XML, HTML }

    public class ApiVariable : ObservableModel
    {
        private string _name    = string.Empty;
        private string _value   = string.Empty;
        private bool   _isSecret;
        private bool   _isEnabled = true;

        public string Name      { get => _name;      set => SetProperty(ref _name,      value); }
        public string Value     { get => _value;     set => SetProperty(ref _value,     value); }
        public bool   IsSecret  { get => _isSecret;  set => SetProperty(ref _isSecret,  value); }
        public bool   IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    }

    public class ApiEnvironment
    {
        public Guid   Id        { get; set; } = Guid.NewGuid();
        public string Name      { get; set; } = "New Environment";
        public ObservableCollection<ApiVariable> Variables { get; set; } = new();
    }

    public class ApiHeader : ObservableModel
    {
        private string _key     = string.Empty;
        private string _value   = string.Empty;
        private bool   _isEnabled = true;

        public string Key       { get => _key;       set => SetProperty(ref _key,       value); }
        public string Value     { get => _value;     set => SetProperty(ref _value,     value); }
        public bool   IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    }

    public class ApiParameter : ObservableModel
    {
        private string _key     = string.Empty;
        private string _value   = string.Empty;
        private bool   _isEnabled = true;

        public string Key       { get => _key;       set => SetProperty(ref _key,       value); }
        public string Value     { get => _value;     set => SetProperty(ref _value,     value); }
        public bool   IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    }

    public class ApiAuth
    {
        public string Type   { get; set; } = "None";
        public Dictionary<string, string> Config { get; set; } = new();
    }

    public class ApiRequest
    {
        public Guid             Id               { get; set; } = Guid.NewGuid();
        public string           Name             { get; set; } = "New Request";
        public HttpMethodType   Method           { get; set; } = HttpMethodType.GET;
        public string           Url              { get; set; } = string.Empty;
        public ObservableCollection<ApiParameter> Parameters  { get; set; } = new() { new ApiParameter() };
        public ObservableCollection<ApiHeader>    Headers     { get; set; } = new() { new ApiHeader() };
        public ApiAuth          Auth             { get; set; } = new();
        public ApiBodyType      BodyType         { get; set; } = ApiBodyType.None;
        public ApiRawType       RawType          { get; set; } = ApiRawType.JSON;
        public string           BodyContent      { get; set; } = string.Empty;
        public string           PreRequestScript { get; set; } = string.Empty;
        public string           TestScript       { get; set; } = string.Empty;
    }

    public class ApiFolder : ObservableModel
    {
        private string _name       = "New Folder";
        private bool   _isExpanded = true;

        public Guid   Id         { get; set; } = Guid.NewGuid();
        public string Name       { get => _name;       set => SetProperty(ref _name,       value); }
        public bool   IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
        public ObservableCollection<ApiFolder>  Folders  { get; set; } = new();
        public ObservableCollection<ApiRequest> Requests { get; set; } = new();

        /// <summary>
        /// Combined observable children for the TreeView HierarchicalDataTemplate.
        /// IMPORTANT: this instance is cached so WPF's binding engine can track changes
        /// to the underlying Folders/Requests ObservableCollections via CollectionContainer.
        /// Creating a new CompositeCollection on every access would lose those hooks.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        private CompositeCollection? _items;

        [Newtonsoft.Json.JsonIgnore]
        public CompositeCollection Items
        {
            get
            {
                if (_items == null)
                {
                    _items = new CompositeCollection();
                    _items.Add(new CollectionContainer { Collection = Folders });
                    _items.Add(new CollectionContainer { Collection = Requests });
                }
                return _items;
            }
        }
    }

    public class ApiCollection : ObservableModel
    {
        private string _name       = "New Collection";
        private bool   _isExpanded = true;

        public Guid    Id         { get; set; } = Guid.NewGuid();
        public string  Name       { get => _name;       set => SetProperty(ref _name,       value); }
        public bool    IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
        public ObservableCollection<ApiFolder>  Folders  { get; set; } = new();
        public ObservableCollection<ApiRequest> Requests { get; set; } = new();
        public ApiAuth? Auth { get; set; }

        /// <summary>
        /// Combined observable children for the TreeView HierarchicalDataTemplate.
        /// Cached so WPF binding keeps its change-listener attached to the underlying
        /// Folders/Requests ObservableCollections through the CollectionContainer wrappers.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        private CompositeCollection? _items;

        [Newtonsoft.Json.JsonIgnore]
        public CompositeCollection Items
        {
            get
            {
                if (_items == null)
                {
                    _items = new CompositeCollection();
                    _items.Add(new CollectionContainer { Collection = Folders });
                    _items.Add(new CollectionContainer { Collection = Requests });
                }
                return _items;
            }
        }
    }

    public class ApiResponse
    {
        public int    StatusCode       { get; set; }
        public string StatusDescription { get; set; } = string.Empty;
        public long   ResponseTimeMs   { get; set; }
        public long   ContentLength    { get; set; }
        public string Body             { get; set; } = string.Empty;
        public Dictionary<string, string> Headers { get; set; } = new();
        public Dictionary<string, string> Cookies { get; set; } = new();
        public bool   IsSuccess        => StatusCode >= 200 && StatusCode < 300;

        public string FormattedSize =>
            ContentLength < 1024
                ? $"{ContentLength} B"
                : ContentLength < 1_048_576
                    ? $"{ContentLength / 1024.0:F1} KB"
                    : $"{ContentLength / 1_048_576.0:F1} MB";
    }

    public class ApiHistoryEntry
    {
        public Guid           Id              { get; set; } = Guid.NewGuid();
        public DateTime       Timestamp       { get; set; } = DateTime.Now;
        public HttpMethodType Method          { get; set; }
        public string         Url             { get; set; } = string.Empty;
        public int            StatusCode      { get; set; }
        public long           ResponseTimeMs  { get; set; }
        public ApiRequest     RequestSnapshot { get; set; } = new();
    }
}
