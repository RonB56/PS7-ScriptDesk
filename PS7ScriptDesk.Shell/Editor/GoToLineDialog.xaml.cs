using System;
using System.Windows;
using System.Windows.Input;
using PS7ScriptDesk.Shell.Help;

namespace PS7ScriptDesk.Shell.Editor
{
    public partial class GoToLineDialog : Window
    {
        private readonly int _maxLine;

        public GoToLineDialog(Window owner, int currentLine, int maxLine)
        {
            InitializeComponent();
            Owner   = owner;
            _maxLine = Math.Max(1, maxLine);

            RangeHintText.Text = $"(1 – {_maxLine})";
            LineNumberBox.Text = currentLine.ToString();
            LineNumberBox.SelectAll();
        }

        /// <summary>The validated 1-based line number chosen by the user, or -1 if canceled.</summary>
        public int SelectedLine { get; private set; } = -1;

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ContextHelp.ValidateWindowTopics(this);
            LineNumberBox.Focus();
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.F1)
            {
                e.Handled = true;
                ContextHelp.OpenForFocusedElement(this);
            }
        }

        private void LineNumberBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            OkButton.IsEnabled = TryParseLineNumber(out _);
        }

        private void LineNumberBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter && TryParseLineNumber(out var line))
            {
                SelectedLine = line;
                DialogResult = true;
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (TryParseLineNumber(out var line))
            {
                SelectedLine = line;
                DialogResult = true;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private bool TryParseLineNumber(out int line)
        {
            line = -1;
            if (!int.TryParse(LineNumberBox.Text?.Trim(), out var parsed))
            {
                return false;
            }

            if (parsed < 1 || parsed > _maxLine)
            {
                return false;
            }

            line = parsed;
            return true;
        }
    }
}
