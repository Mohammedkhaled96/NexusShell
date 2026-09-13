using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace NexusShell.App.Helpers
{
    public static class RichTextBoxHelper
    {
        public static readonly DependencyProperty DocumentProperty =
            DependencyProperty.RegisterAttached("Document", typeof(FlowDocument), typeof(RichTextBoxHelper), new FrameworkPropertyMetadata(null, OnDocumentChanged));

        public static FlowDocument GetDocument(DependencyObject obj)
        {
            return (FlowDocument)obj.GetValue(DocumentProperty);
        }

        public static void SetDocument(DependencyObject obj, FlowDocument value)
        {
            obj.SetValue(DocumentProperty, value);
        }

        private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RichTextBox richTextBox)
            {
                if (e.NewValue is FlowDocument doc)
                {
                    try
                    {
                        richTextBox.Document = doc;
                    }
                    catch
                    {
                        // Ignore if document belongs to another RichTextBox.
                        // Ideally, we should clone it or handle it better, 
                        // but this prevents the crash.
                        richTextBox.Document = new FlowDocument();
                    }
                }
                else
                {
                    richTextBox.Document = new FlowDocument();
                }
            }
        }
    }
}