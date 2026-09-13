using NexusShell.App.Models;
using System.Collections.Generic;
using System.Windows.Input;

namespace NexusShell.App.ViewModels
{
    public class AddEditShortcutViewModel : ViewModelBase
    {
        private string _selectedCommand;
        public string SelectedCommand
        {
            get => _selectedCommand;
            set => SetProperty(ref _selectedCommand, value);
        }

        private Key _key;
        public Key Key
        {
            get => _key;
            set
            {
                if (SetProperty(ref _key, value))
                {
                    OnPropertyChanged(nameof(KeyDisplayString));
                    StatusMessage = $"Current Shortcut: {KeyDisplayString}";
                }
            }
        }

        private bool _isCtrl;
        public bool IsCtrl
        {
            get => _isCtrl;
            set
            {
                if (SetProperty(ref _isCtrl, value))
                {
                    OnPropertyChanged(nameof(KeyDisplayString));
                    StatusMessage = $"Current Shortcut: {KeyDisplayString}";
                }
            }
        }

        private bool _isShift;
        public bool IsShift
        {
            get => _isShift;
            set
            {
                if (SetProperty(ref _isShift, value))
                {
                    OnPropertyChanged(nameof(KeyDisplayString));
                    StatusMessage = $"Current Shortcut: {KeyDisplayString}";
                }
            }
        }

        private bool _isAlt;
        public bool IsAlt
        {
            get => _isAlt;
            set
            {
                if (SetProperty(ref _isAlt, value))
                {
                    OnPropertyChanged(nameof(KeyDisplayString));
                    StatusMessage = $"Current Shortcut: {KeyDisplayString}";
                }
            }
        }

        private string _statusMessage;
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public string KeyDisplayString
        {
            get
            {
                var parts = new List<string>();
                if (IsCtrl) parts.Add("Ctrl");
                if (IsShift) parts.Add("Shift");
                if (IsAlt) parts.Add("Alt");

                if (Key != Key.None)
                {
                    parts.Add(Key.ToString());
                }
                else if (parts.Count > 0)
                {
                    // Modifiers are held but no key yet
                    parts.Add("...");
                }
                else
                {
                    return "Press a key combination...";
                }

                return string.Join(" + ", parts);
            }
        }

        public List<string> AvailableCommands { get; }

        public AddEditShortcutViewModel(List<string> availableCommands, Shortcut? shortcut = null)
        {
            AvailableCommands = availableCommands;
            _selectedCommand = string.Empty;
            _statusMessage = "Select a command and press a key combination.";

            if (shortcut != null)
            {
                SelectedCommand = shortcut.CommandName;
                Key = shortcut.Key;
                IsCtrl = shortcut.Modifiers.HasFlag(ModifierKeys.Control);
                IsShift = shortcut.Modifiers.HasFlag(ModifierKeys.Shift);
                IsAlt = shortcut.Modifiers.HasFlag(ModifierKeys.Alt);
                StatusMessage = "Editing existing shortcut.";
            }

            // Force update display string
            OnPropertyChanged(nameof(KeyDisplayString));
        }

        public Shortcut? GetShortcut()
        {
            if (string.IsNullOrEmpty(SelectedCommand) || Key == Key.None)
            {
                StatusMessage = "Please select a command and a valid key.";
                return null;
            }

            var modifiers = ModifierKeys.None;
            if (IsCtrl) modifiers |= ModifierKeys.Control;
            if (IsShift) modifiers |= ModifierKeys.Shift;
            if (IsAlt) modifiers |= ModifierKeys.Alt;

            // Validation: Prevent single letter shortcuts (e.g. "C") which block typing and cause crashes
            if (modifiers == ModifierKeys.None && !IsFunctionKey(Key))
            {
                StatusMessage = "Error: Text keys require a modifier (Ctrl/Alt/Shift).";
                return null;
            }

            return new Shortcut(Key, modifiers, SelectedCommand);
        }

        private bool IsFunctionKey(Key key)
        {
            return (key >= Key.F1 && key <= Key.F24) ||
                   key == Key.PageUp || key == Key.PageDown ||
                   key == Key.Home || key == Key.End ||
                   key == Key.Insert || key == Key.Delete ||
                   key == Key.PrintScreen || key == Key.Pause || 
                   key == Key.Escape; // Esc is usually reserved but allowed as non-modifier
        }
    }
}