using NexusShell.App.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace NexusShell.App.Views
{
    public partial class AddEditShortcutWindow : Window
    {
        public AddEditShortcutWindow(AddEditShortcutViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            
            // Set initial focus
            Loaded += (s, e) => CommandCombo.Focus();
        }

        private void KeyInputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Tab) return;
            e.Handled = true;

            var key = (e.Key == Key.System ? e.SystemKey : e.Key);

            if (DataContext is AddEditShortcutViewModel vm)
            {
                if (IsModifierKey(key))
                {
                    // Only update modifiers if we don't have a main key assigned yet,
                    // or if the user is pressing modifiers while the main key is None.
                    // This allows live feedback for "Ctrl + ..."
                    if (vm.Key == Key.None)
                    {
                        UpdateModifiers(vm);
                    }
                }
                else
                {
                    // A non-modifier key was pressed. Capture it AND the current modifiers.
                    UpdateModifiers(vm);
                    vm.Key = key;
                }
            }
        }

        private void KeyInputBox_PreviewKeyUp(object sender, KeyEventArgs e)
        {
             if (DataContext is AddEditShortcutViewModel vm)
             {
                 // Only clear modifier feedback if we haven't assigned a main key yet.
                 // Once a key like "R" is assigned, we want "Ctrl + R" to stay even if Ctrl is released.
                 if (vm.Key == Key.None)
                 {
                     UpdateModifiers(vm);
                 }
             }
        }

        private void UpdateModifiers(AddEditShortcutViewModel vm)
        {
            vm.IsCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            vm.IsShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            vm.IsAlt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        }

        private bool IsModifierKey(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl ||
                   key == Key.LeftShift || key == Key.RightShift ||
                   key == Key.LeftAlt || key == Key.RightAlt ||
                   key == Key.LWin || key == Key.RWin ||
                   key == Key.System;
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is AddEditShortcutViewModel vm)
            {
                var shortcut = vm.GetShortcut();
                if (shortcut == null)
                {
                    MessageBox.Show(vm.StatusMessage, "Invalid Shortcut", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}