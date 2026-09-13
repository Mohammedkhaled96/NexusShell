using NexusShell.App.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Linq;

namespace NexusShell.App.Views
{
    public partial class SettingsWindow : Window
    {
        private SettingsViewModel? _viewModel;

        public SettingsWindow()
        {
            InitializeComponent();
            DataContextChanged += (s, e) =>
            {
                _viewModel = DataContext as SettingsViewModel;
            };

            Loaded += (s, e) => {
                if (ApiKeyBox.Password != _viewModel?.AIApiKey)
                    ApiKeyBox.Password = _viewModel?.AIApiKey;
                
                // Initial focus logic is now handled by WindowService to distinguish
                // between opening from menu vs internal navigation.
            };
        }

        /// <summary>
        /// Public method to jump focus into the settings of the currently selected category.
        /// This is called when opening from the main menu.
        /// </summary>
        public void FocusActiveCategoryContent()
        {
            // Give the UI a moment to switch visibility
            Dispatcher.InvokeAsync(() =>
            {
                StackPanel? activePanel = null;
                switch (_viewModel?.SelectedCategory)
                {
                    case "General":     activePanel = GeneralPanel;     break;
                    case "Appearance":  activePanel = AppearancePanel;  break;
                    case "Integration": activePanel = IntegrationPanel; break;
                    case "AI":          activePanel = AIPanel;          break;
                }

                if (activePanel != null)
                {
                    var element = FindFirstFocusable(activePanel);
                    element?.Focus();
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private UIElement? FindFirstFocusable(DependencyObject parent)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is UIElement element && element.Focusable && element.IsVisible && 
                    !(child is TextBlock) && !(child is Label) && !(child is ScrollViewer))
                {
                    return element;
                }

                var found = FindFirstFocusable(child);
                if (found != null) return found;
            }
            return null;
        }

        private void ShowKeyToggle_Checked(object sender, RoutedEventArgs e)
        {
            ApiKeyVisible.Text = ApiKeyBox.Password;
            ApiKeyVisible.Visibility = Visibility.Visible;
            ApiKeyBox.Visibility = Visibility.Collapsed;
        }

        private void ShowKeyToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            ApiKeyBox.Password = ApiKeyVisible.Text;
            ApiKeyBox.Visibility = Visibility.Visible;
            ApiKeyVisible.Visibility = Visibility.Collapsed;
        }

        private void ApiKeyVisible_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ApiKeyBox != null && ApiKeyVisible.Visibility == Visibility.Visible)
            {
                if (ApiKeyBox.Password != ApiKeyVisible.Text)
                    ApiKeyBox.Password = ApiKeyVisible.Text;
            }
        }

        private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.AIApiKey = ApiKeyBox.Password;
            }
        }
    }
}
