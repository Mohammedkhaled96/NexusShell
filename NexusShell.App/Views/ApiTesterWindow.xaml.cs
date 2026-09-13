using NexusShell.App.Models;
using NexusShell.App.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;

namespace NexusShell.App.Views
{
    public partial class ApiTesterWindow : Window
    {
        public ApiTesterWindow()
        {
            InitializeComponent();
        }

        public ApiTesterWindow(object dataContext) : this()
        {
            DataContext = dataContext;
            Loaded += (_, _) =>
            {
                UrlInput.Focus();
                // Sync the masked PasswordBoxes from the ViewModel after binding.
                // PasswordBox.Password is intentionally NOT a DependencyProperty
                // (so secrets never sit in the binding system / VS visualizer);
                // we push the value in once at load and keep it in sync via the
                // PasswordChanged handlers below.
                SyncPasswordBoxesFromViewModel();
            };
        }

        private void SyncPasswordBoxesFromViewModel()
        {
            if (DataContext is not ApiTesterViewModel vm) return;
            // Only set if different to avoid PasswordChanged feedback loops
            if (BearerPasswordBox.Password != vm.BearerToken)
                BearerPasswordBox.Password = vm.BearerToken;
            if (BasicPasswordBox.Password  != vm.BasicPassword)
                BasicPasswordBox.Password  = vm.BasicPassword;
            if (ApiKeyPasswordBox.Password != vm.ApiKeyValue)
                ApiKeyPasswordBox.Password = vm.ApiKeyValue;
        }

        private void BearerPasswordBox_Changed(object sender, RoutedEventArgs e)
        {
            if (DataContext is ApiTesterViewModel vm && sender is PasswordBox pb)
                vm.BearerToken = pb.Password;
        }

        private void BasicPasswordBox_Changed(object sender, RoutedEventArgs e)
        {
            if (DataContext is ApiTesterViewModel vm && sender is PasswordBox pb)
                vm.BasicPassword = pb.Password;
        }

        private void ApiKeyPasswordBox_Changed(object sender, RoutedEventArgs e)
        {
            if (DataContext is ApiTesterViewModel vm && sender is PasswordBox pb)
                vm.ApiKeyValue = pb.Password;
        }

        // ── Click / selection handlers ───────────────────────────────────────────

        /// <summary>
        /// Fires whenever the TreeView's selected item changes.
        /// If the newly selected item is an ApiRequest, load it into the editor.
        /// Using code-behind rather than MouseBinding inside a DataTemplate because
        /// ElementName bindings are unreliable for items inside a CompositeCollection.
        /// </summary>
        private void CollectionsTree_SelectedItemChanged(object sender,
            RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is ApiRequest req && DataContext is ApiTesterViewModel vm)
                vm.SelectRequestCommand.Execute(req);
        }

        // ── Context menu helpers ─────────────────────────────────────────────────

        /// <summary>
        /// PreviewMouseRightButtonDown (tunnelling phase) on the TreeView.
        /// Using Preview* ensures this fires BEFORE TreeViewItem's own
        /// OnMouseRightButtonDown handler which marks the event as Handled and
        /// would suppress the normal MouseRightButtonUp from ever bubbling up.
        /// We walk up the visual tree from the click source to find the owning
        /// TreeViewItem, select it, then show the appropriate context menu.
        /// </summary>
        private void CollectionsTree_RightClick(object sender, MouseButtonEventArgs e)
        {
            var tvi = FindTreeViewItem(e.OriginalSource as DependencyObject);
            if (tvi == null) return;

            tvi.IsSelected = true;
            tvi.Focus();
            ShowContextMenu(tvi, tvi.DataContext);
            e.Handled = true;  // prevent TreeViewItem from re-processing
        }

        /// <summary>
        /// Application / Menu key (and Shift+F10): open the context menu for the
        /// currently focused / selected TreeViewItem.
        /// </summary>
        private void CollectionsTree_KeyDown(object sender, KeyEventArgs e)
        {
            bool isMenuKey  = e.Key == Key.Apps;
            bool isShiftF10 = e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift;
            if (!isMenuKey && !isShiftF10) return;

            // Walk from the currently focused element upward
            var tvi = FindTreeViewItem(Keyboard.FocusedElement as DependencyObject);
            if (tvi == null) return;

            ShowContextMenu(tvi, tvi.DataContext);
            e.Handled = true;
        }

        /// <summary>
        /// Builds and opens the correct context menu for a Collection, Folder or Request.
        /// All commands are wired directly — no XAML binding required.
        /// </summary>
        private void ShowContextMenu(FrameworkElement anchor, object? item)
        {
            if (DataContext is not ApiTesterViewModel vm) return;

            var menu = new ContextMenu
            {
                Background  = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground  = new SolidColorBrush(Color.FromRgb(204, 204, 204))
            };

            if (item is ApiCollection col)
            {
                menu.Items.Add(MakeItem("➕  Add Request",   vm.AddRequestToCollectionCommand,  col));
                menu.Items.Add(MakeItem("📂  Add Folder",    vm.AddFolderToCollectionCommand,    col));
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeItem("✏   Rename",        vm.RenameCollectionCommand,         col));
                menu.Items.Add(MakeItem("⬇   Export",        vm.ExportCollectionCommand,         col));
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeItem("🗑   Delete",        vm.DeleteCollectionCommand,         col));
            }
            else if (item is ApiFolder folder)
            {
                menu.Items.Add(MakeItem("➕  Add Request",   vm.AddRequestToFolderCommand,       folder));
                menu.Items.Add(MakeItem("📂  Add Subfolder", vm.AddFolderToFolderCommand,        folder));
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeItem("✏   Rename",        vm.RenameFolderCommand,             folder));
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeItem("🗑   Delete",        vm.DeleteFolderCommand,             folder));
            }
            else if (item is ApiRequest req)
            {
                menu.Items.Add(MakeItem("▶   Load Request",  vm.SelectRequestCommand,            req));
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeItem("✏   Rename",        vm.RenameRequestCommand,            req));
                menu.Items.Add(MakeItem("⧉   Duplicate",     vm.DuplicateRequestCommand,         req));
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeItem("🗑   Delete",        vm.DeleteRequestCommand,            req));
            }
            else
            {
                return; // nothing to show
            }

            menu.PlacementTarget = anchor;
            menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen          = true;
        }

        private static MenuItem MakeItem(string header, ICommand command, object parameter)
            => new()
            {
                Header           = header,
                Command          = command,
                CommandParameter = parameter,
                Padding          = new Thickness(8, 4, 12, 4),
                FontSize         = 13
            };

        // ── JSON body validation ──────────────────────────────────────────────────

        /// <summary>
        /// Fires on every keystroke in the raw body TextBox.
        /// Delegates to the ViewModel for live JSON validation feedback.
        /// Only validates when the JSON shortcut is selected (BodyType=Raw, RawType=JSON).
        /// </summary>
        private void JsonBody_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (DataContext is not ApiTesterViewModel vm) return;
            if (sender is not TextBox tb) return;

            // Only validate when the JSON body type is active
            if (!vm.IsBodyJson)
            {
                vm.ClearJsonValidation();
                return;
            }

            vm.ValidateJsonBody(tb.Text);
        }

        // ── Tree-row "⋯" application button → opens the standard context menu ─────

        /// <summary>
        /// Handler for the always-visible ⋯ menu button rendered on every collection,
        /// folder and request row. The button's Tag carries the bound DataContext (the
        /// ApiCollection / ApiFolder / ApiRequest) so we can route it through the same
        /// ShowContextMenu used by right-click and the Apps-key handlers — guaranteeing
        /// identical behaviour between mouse, keyboard and explicit-button invocation.
        /// </summary>
        private void ItemMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;

            // Make sure the row this button belongs to is selected first so the user
            // visually sees what they're acting on (matches right-click behaviour).
            var tvi = FindTreeViewItem(btn);
            if (tvi != null)
            {
                tvi.IsSelected = true;
                tvi.Focus();
            }

            // Tag is bound to the row's DataContext (ApiCollection / ApiFolder / ApiRequest)
            ShowContextMenu(btn, btn.Tag);
            e.Handled = true;
        }

        // ── Visual tree helper ───────────────────────────────────────────────────

        /// <summary>
        /// Walks the WPF visual tree upward from <paramref name="source"/> until
        /// a <see cref="TreeViewItem"/> is found, or returns null.
        /// </summary>
        private static TreeViewItem? FindTreeViewItem(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is TreeViewItem tvi) return tvi;
                source = VisualTreeHelper.GetParent(source);
            }
            return null;
        }
    }
}
