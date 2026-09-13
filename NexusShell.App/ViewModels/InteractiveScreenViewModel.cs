using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusShell.App.Models;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace NexusShell.App.ViewModels
{
    /// <summary>
    /// ViewModel for the Interactive Screen overlay.
    /// Managed exclusively by <see cref="Services.InteractiveScreenService"/>;
    /// the View binds to this object while the overlay is visible.
    /// </summary>
    public sealed partial class InteractiveScreenViewModel : ObservableObject
    {
        // ── Visibility ─────────────────────────────────────────────────────────

        [ObservableProperty]
        private bool _isVisible;

        // ── Content ────────────────────────────────────────────────────────────

        [ObservableProperty]
        private string _title = string.Empty;

        [ObservableProperty]
        private string _description = string.Empty;

        [ObservableProperty]
        private string _platformName = string.Empty;

        [ObservableProperty]
        private bool _hasPlatformName;

        [ObservableProperty]
        private string _cancelText  = "Cancel  [Esc]";

        [ObservableProperty]
        private string _confirmText = "Confirm  [Enter]";

        // ── Options ────────────────────────────────────────────────────────────

        public ObservableCollection<InteractiveOption> Options { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        private int _selectedIndex = -1;

        [ObservableProperty]
        private bool _isMultiSelect;

        [ObservableProperty]
        private bool _allowCancel = true;

        // ── Accessibility live-region text ─────────────────────────────────────

        /// <summary>
        /// Updated on every navigation step so that screen-reader live regions
        /// announce the current position without requiring explicit focus moves.
        /// </summary>
        public string StatusText
        {
            get
            {
                if (Options.Count == 0) return Title;

                int idx = SelectedIndex;
                if (idx < 0 || idx >= Options.Count)
                    return $"{Title}. {Options.Count} options available.";

                var opt = Options[idx];
                string pos  = $"Option {idx + 1} of {Options.Count}";
                string name = opt.Text;
                string desc = string.IsNullOrWhiteSpace(opt.Description)
                              ? string.Empty
                              : $"  —  {opt.Description}";
                string shortcut = string.IsNullOrWhiteSpace(opt.ShortcutKey)
                              ? string.Empty
                              : $"  Shortcut: {opt.ShortcutKey}";
                string sel  = (IsMultiSelect && opt.IsSelected) ? "  (selected)" : string.Empty;

                return $"{pos}: {name}{desc}{shortcut}{sel}";
            }
        }

        // ── Commands ───────────────────────────────────────────────────────────

        /// <summary>Raised when the user confirms their selection.</summary>
        public event Action? ConfirmRequested;

        /// <summary>Raised when the user cancels (Escape / Cancel button).</summary>
        public event Action? CancelRequested;

        [RelayCommand]
        private void Confirm() => ConfirmRequested?.Invoke();

        [RelayCommand]
        private void Cancel()
        {
            if (AllowCancel) CancelRequested?.Invoke();
        }

        // ── Navigation helpers (called from View's PreviewKeyDown) ─────────────

        public void MoveUp()
        {
            if (Options.Count == 0) return;
            SelectedIndex = SelectedIndex <= 0
                ? Options.Count - 1
                : SelectedIndex - 1;
        }

        public void MoveDown()
        {
            if (Options.Count == 0) return;
            SelectedIndex = SelectedIndex >= Options.Count - 1
                ? 0
                : SelectedIndex + 1;
        }

        public void ToggleCurrentSelection()
        {
            if (!IsMultiSelect) return;
            if (SelectedIndex < 0 || SelectedIndex >= Options.Count) return;
            var opt = Options[SelectedIndex];
            if (opt.IsEnabled) opt.IsSelected = !opt.IsSelected;
            OnPropertyChanged(nameof(StatusText));
        }

        // ── Selected option accessors ──────────────────────────────────────────

        public InteractiveOption? CurrentOption
            => (SelectedIndex >= 0 && SelectedIndex < Options.Count)
               ? Options[SelectedIndex]
               : null;

        public IReadOnlyList<InteractiveOption> CheckedOptions
            => Options.Where(o => o.IsSelected).ToList();

        // ── Initialisation ─────────────────────────────────────────────────────

        internal void LoadConfig(InteractiveScreenConfig config)
        {
            Title         = config.Title;
            Description   = config.Description;
            PlatformName  = config.PlatformName;
            HasPlatformName = !string.IsNullOrWhiteSpace(config.PlatformName);
            CancelText    = config.CancelText;
            ConfirmText   = config.ConfirmText;
            IsMultiSelect = config.AllowMultiSelect;
            AllowCancel   = config.AllowCancel;

            Options.Clear();
            foreach (var opt in config.Options)
                Options.Add(opt);

            SelectedIndex = Options.Count > 0 ? config.InitialSelectedIndex : -1;
        }
    }
}
