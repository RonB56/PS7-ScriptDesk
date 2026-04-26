using System;
using System.Windows;
using System.Windows.Input;
using PowerShellStudio.Shell.Help;

namespace PowerShellStudio.Shell
{
    public partial class FindReplaceWindow : Window
    {
        private readonly MainWindow _ownerWindow;

        public FindReplaceWindow(MainWindow ownerWindow, string initialFindText, string initialReplaceText, bool matchCase)
        {
            _ownerWindow = ownerWindow ?? throw new ArgumentNullException(nameof(ownerWindow));
            InitializeComponent();

            Owner = ownerWindow;
            FindTextBox.Text = initialFindText ?? string.Empty;
            ReplaceTextBox.Text = initialReplaceText ?? string.Empty;
            MatchCaseCheckBox.IsChecked = matchCase;

            Loaded += (_, _) =>
            {
                FindTextBox.Focus();
                FindTextBox.SelectAll();
            };
        }

        public string FindText
        {
            get => FindTextBox.Text;
            set => FindTextBox.Text = value ?? string.Empty;
        }

        public string ReplaceText
        {
            get => ReplaceTextBox.Text;
            set => ReplaceTextBox.Text = value ?? string.Empty;
        }

        public bool MatchCase
        {
            get => MatchCaseCheckBox.IsChecked == true;
            set => MatchCaseCheckBox.IsChecked = value;
        }

        public bool WholeWord
        {
            get => WholeWordCheckBox.IsChecked == true;
            set => WholeWordCheckBox.IsChecked = value;
        }

        public bool UseRegex
        {
            get => UseRegexCheckBox.IsChecked == true;
            set => UseRegexCheckBox.IsChecked = value;
        }

        public void ShowStatus(string? message)
        {
            StatusText.Text = message ?? string.Empty;
        }

        private void FindNext_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = string.Empty;
            _ownerWindow.ExecuteFindNext(FindText, MatchCase, WholeWord, UseRegex);
        }

        private void FindPrev_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = string.Empty;
            _ownerWindow.ExecuteFindPrev(FindText, MatchCase, WholeWord, UseRegex);
        }

        private void Replace_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = string.Empty;
            _ownerWindow.ExecuteReplace(FindText, ReplaceText, MatchCase, WholeWord, UseRegex);
        }

        private void ReplaceAll_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = string.Empty;
            _ownerWindow.ExecuteReplaceAll(FindText, ReplaceText, MatchCase, WholeWord, UseRegex);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Hide();
                return;
            }

            if (e.Key == Key.F3)
            {
                e.Handled = true;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                    FindPrev_Click(sender, new RoutedEventArgs());
                else
                    FindNext_Click(sender, new RoutedEventArgs());
                return;
            }

            if (e.Key == Key.F1)
            {
                e.Handled = true;
                ContextHelp.OpenForFocusedElement(this);
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
