using System.Windows;
using ICSharpCode.AvalonEdit;

namespace PS7ScriptDesk.Shell.Editor
{
    public class BindableTextEditor : TextEditor
    {
        private bool _isUpdatingBoundText;

        public static readonly DependencyProperty BoundTextProperty = DependencyProperty.Register(
            nameof(BoundText),
            typeof(string),
            typeof(BindableTextEditor),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBoundTextChanged));

        public string BoundText
        {
            get => (string)GetValue(BoundTextProperty);
            set => SetValue(BoundTextProperty, value);
        }

        protected override void OnTextChanged(System.EventArgs e)
        {
            base.OnTextChanged(e);

            if (_isUpdatingBoundText)
            {
                return;
            }

            try
            {
                _isUpdatingBoundText = true;
                SetCurrentValue(BoundTextProperty, Text ?? string.Empty);
            }
            finally
            {
                _isUpdatingBoundText = false;
            }
        }

        private static void OnBoundTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
        {
            if (dependencyObject is not BindableTextEditor editor)
            {
                return;
            }

            if (editor._isUpdatingBoundText)
            {
                return;
            }

            var newText = e.NewValue as string ?? string.Empty;
            if (string.Equals(editor.Text, newText, System.StringComparison.Ordinal))
            {
                return;
            }

            var currentOffset = editor.CaretOffset;

            try
            {
                editor._isUpdatingBoundText = true;
                editor.Text = newText;
                editor.CaretOffset = System.Math.Min(currentOffset, editor.Text.Length);
            }
            finally
            {
                editor._isUpdatingBoundText = false;
            }
        }
    }
}
