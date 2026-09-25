using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LocalModelIntegrator.ToolWindows
{
    /// <summary>A selectable Markdown view whose source can be updated while streaming.</summary>
    public sealed class MarkdownContent : RichTextBox
    {
        public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
            nameof(Markdown), typeof(string), typeof(MarkdownContent),
            new PropertyMetadata(string.Empty, OnMarkdownChanged));

        private DispatcherOperation _pendingRender;

        public string Markdown
        {
            get => (string)GetValue(MarkdownProperty);
            set => SetValue(MarkdownProperty, value);
        }

        public MarkdownContent()
        {
            IsReadOnly = true;
            IsDocumentEnabled = true;
            IsUndoEnabled = false;
            BorderThickness = new Thickness(0);
            Padding = new Thickness(0);
            Background = System.Windows.Media.Brushes.Transparent;
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }

        private static void OnMarkdownChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var view = (MarkdownContent)sender;
            // Coalesce tokens queued in the same UI turn and let input take priority.
            if (view._pendingRender?.Status == DispatcherOperationStatus.Pending) return;
#pragma warning disable VSTHRD001 // Already on the UI thread; defer rendering to coalesce streaming updates.
            view._pendingRender = view.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new System.Action(() => view.Document = MarkdownDocumentRenderer.Render(view.Markdown)));
#pragma warning restore VSTHRD001
        }
    }
}
