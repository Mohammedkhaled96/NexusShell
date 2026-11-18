using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NexusShell.App.Helpers
{
    /// <summary>
    /// Screen-reader friendly combo boxes: "browse first, then choose".
    /// <list type="bullet">
    /// <item>Enter, Space, F4, Alt+Down, Up or Down on a CLOSED box opens the list (WPF would otherwise
    /// change the choice on every arrow press, and Enter would press the window's default button).</item>
    /// <item>While the list is open, arrows only move through the choices and the screen reader reads them.</item>
    /// <item>Enter chooses the item under the cursor; Escape closes the list and keeps the previous choice.</item>
    /// <item>Tab / Shift+Tab close the list (keeping the previous choice) and move to the next / previous
    /// control, instead of cycling through the items and trapping the user inside the list.</item>
    /// <item>An Enter pressed right after choosing is ignored, so it does not reopen the list.</item>
    /// </list>
    /// Usage: <c>helpers:ComboBoxKeyboard.BrowseBeforeChoosing="True"</c>.
    /// </summary>
    public static class ComboBoxKeyboard
    {
        private static readonly TimeSpan RepeatEnterGuard = TimeSpan.FromMilliseconds(1200);

        public static readonly DependencyProperty BrowseBeforeChoosingProperty =
            DependencyProperty.RegisterAttached(
                "BrowseBeforeChoosing",
                typeof(bool),
                typeof(ComboBoxKeyboard),
                new PropertyMetadata(false, OnChanged));

        /// <summary>When Enter last closed the list (to ignore a second Enter right after choosing).</summary>
        private static readonly DependencyProperty LastChosenAtProperty =
            DependencyProperty.RegisterAttached("LastChosenAt", typeof(DateTime), typeof(ComboBoxKeyboard), new PropertyMetadata(DateTime.MinValue));

        public static bool GetBrowseBeforeChoosing(DependencyObject element) => (bool)element.GetValue(BrowseBeforeChoosingProperty);

        public static void SetBrowseBeforeChoosing(DependencyObject element, bool value) => element.SetValue(BrowseBeforeChoosingProperty, value);

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ComboBox combo) return;
            combo.PreviewKeyDown -= OnPreviewKeyDown;
            if (e.NewValue is true) combo.PreviewKeyDown += OnPreviewKeyDown;
        }

        // Keys pressed on the items of the open list also tunnel through the combo box first.
        private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not ComboBox combo) return;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (combo.IsDropDownOpen)
            {
                if (key == Key.Tab)
                {
                    bool back = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                    combo.IsDropDownOpen = false; // closes without choosing the highlighted item
                    combo.Focus();
                    combo.MoveFocus(new TraversalRequest(back ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
                    e.Handled = true;
                }
                else if (key == Key.Enter)
                {
                    combo.SetValue(LastChosenAtProperty, DateTime.UtcNow); // WPF chooses the item and closes the list
                }
                return; // arrows, Enter and Escape: WPF's own handling
            }

            if (Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Alt)) return;

            if (key == Key.Enter && DateTime.UtcNow - (DateTime)combo.GetValue(LastChosenAtProperty) < RepeatEnterGuard)
            {
                e.Handled = true; // the Enter that just chose an item was pressed twice: stay closed, do not press Send
                return;
            }

            bool opens = key switch
            {
                Key.Enter or Key.F4 => true,
                Key.Space => !combo.IsEditable,          // in an editable box Space types a space
                Key.Up or Key.Down => !combo.IsEditable, // editable boxes keep their usual behaviour
                _ => false,
            };
            if (!opens) return;

            combo.IsDropDownOpen = true;
            e.Handled = true;
        }
    }
}
