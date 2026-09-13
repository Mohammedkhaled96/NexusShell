using System.Windows;
using System.Windows.Input;

namespace NexusShell.App.Views
{
    public partial class ApiResponseWindow : Window
    {
        public ApiResponseWindow()
        {
            InitializeComponent();
        }

        public ApiResponseWindow(object dataContext) : this()
        {
            DataContext = dataContext;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void CopyBody_Click(object sender, RoutedEventArgs e)
        {
            var text = ResponseBodyTxt.Text;
            if (!string.IsNullOrEmpty(text))
                Clipboard.SetText(text);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }
    }
}
