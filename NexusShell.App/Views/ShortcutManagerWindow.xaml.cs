using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NexusShell.App.Views
{
    public partial class ShortcutManagerWindow : Window
    {
        public ShortcutManagerWindow()
        {
            InitializeComponent();
            
            // Set initial focus to the list view for screen readers
            Loaded += (s, e) => 
            {
                if (ShortcutsListView.Items.Count > 0)
                {
                    // If there are items, focus the first one
                    var item = ShortcutsListView.ItemContainerGenerator.ContainerFromIndex(0) as ListViewItem;
                    item?.Focus();
                }
                else
                {
                    // Fallback to the list itself
                    ShortcutsListView.Focus();
                }
            };
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}