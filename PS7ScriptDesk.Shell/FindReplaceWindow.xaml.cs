using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PS7ScriptDesk.Shell.Help;

namespace PS7ScriptDesk.Shell
{
    public partial class FindReplaceWindow : Window
    {
        private readonly MainWindow _ownerWindow;
        private readonly ObservableCollection<FindResultRow> _results = new();
        private bool _isRefreshingResults;

        public FindReplaceWindow(MainWindow ownerWindow, string initialFindText, string initialReplaceText, bool matchCase)
        {
            _ownerWindow = ownerWindow ?? throw new ArgumentNullException(nameof(ownerWindow));
            InitializeComponent();

            Owner = ownerWindow;
            ResultsListBox.ItemsSource = _results;
            FindTextBox.Text = initialFindText ?? string.Empty;
            ReplaceTextBox.Text = initialReplaceText ?? string.Empty;
            MatchCaseCheckBox.IsChecked = matchCase;

            Loaded += (_, _) =>
            {
                ContextHelp.ValidateWindowTopics(this);
                RefreshResults(showStatus: false);
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

        public void SetMode(bool showReplace)
        {
            Title = showReplace ? "Replace" : "Find";

            if (showReplace)
            {
                ReplaceTextBox.Focus();
                ReplaceTextBox.SelectAll();
            }
            else
            {
                FindTextBox.Focus();
                FindTextBox.SelectAll();
            }
        }

        public void RefreshResultList()
        {
            RefreshResults(showStatus: false);
        }

        public void ShowStatus(string? message)
        {
            StatusText.Text = message ?? string.Empty;
        }

        private void FindNext_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = string.Empty;
            _ownerWindow.ExecuteFindNext(FindText, MatchCase, WholeWord, UseRegex);
            RefreshResults(showStatus: false);
        }

        private void FindPrev_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = string.Empty;
            _ownerWindow.ExecuteFindPrev(FindText, MatchCase, WholeWord, UseRegex);
            RefreshResults(showStatus: false);
        }

        private void Replace_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = string.Empty;
            _ownerWindow.ExecuteReplace(FindText, ReplaceText, MatchCase, WholeWord, UseRegex);
            RefreshResults(showStatus: false);
        }

        private void ReplaceAll_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = string.Empty;
            _ownerWindow.ExecuteReplaceAll(FindText, ReplaceText, MatchCase, WholeWord, UseRegex);
            RefreshResults(showStatus: true);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshResults(showStatus: false);
        }

        private void SearchOption_Changed(object sender, RoutedEventArgs e)
        {
            RefreshResults(showStatus: false);
        }

        private void ResultsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRefreshingResults)
            {
                return;
            }

            JumpToSelectedResult();
        }

        private void ResultsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            JumpToSelectedResult();
        }

        private void ResultsListBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                JumpToSelectedResult();
            }
        }

        private void RefreshResults(bool showStatus)
        {
            if (!IsInitialized)
            {
                return;
            }

            _isRefreshingResults = true;
            try
            {
                _results.Clear();
                ResultsEmptyText.Text = "Type search text to see matching lines here.";
                ResultsEmptyText.Visibility = Visibility.Visible;

                if (string.IsNullOrWhiteSpace(FindText))
                {
                    CountText.Text = "0 matches";
                    if (showStatus)
                    {
                        ShowStatus("Enter text to find");
                    }
                    return;
                }

                IReadOnlyList<FindResultRow> matches;
                try
                {
                    matches = _ownerWindow.GetFindResults(FindText, MatchCase, WholeWord, UseRegex);
                }
                catch (ArgumentException ex)
                {
                    CountText.Text = "Invalid search";
                    ResultsEmptyText.Text = "The regular expression is not valid.";
                    ShowStatus($"Invalid regex: {ex.Message}");
                    return;
                }

                foreach (var match in matches)
                {
                    _results.Add(match);
                }

                CountText.Text = matches.Count == 1 ? "1 match" : $"{matches.Count} matches";
                ResultsEmptyText.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (matches.Count == 0)
                {
                    ResultsEmptyText.Text = "No matches were found.";
                }

                if (showStatus)
                {
                    ShowStatus(matches.Count == 0 ? "No matches were found" : $"Found {matches.Count} match(es)");
                }
                else if (StatusText.Text.StartsWith("Invalid regex:", StringComparison.OrdinalIgnoreCase))
                {
                    ShowStatus(null);
                }
            }
            finally
            {
                _isRefreshingResults = false;
            }
        }

        private void JumpToSelectedResult()
        {
            if (ResultsListBox.SelectedItem is not FindResultRow result)
            {
                return;
            }

            _ownerWindow.NavigateToFindResult(result.Offset, result.Length, result.Line, result.Column);
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

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.F)
            {
                e.Handled = true;
                SetMode(showReplace: false);
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.H)
            {
                e.Handled = true;
                SetMode(showReplace: true);
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

    public sealed class FindResultRow
    {
        public FindResultRow(int number, int line, int column, int offset, int length, string preview)
        {
            Number = number;
            Line = line;
            Column = column;
            Offset = offset;
            Length = length;
            Preview = preview ?? string.Empty;
        }

        public int Number { get; }

        public int Line { get; }

        public int Column { get; }

        public int Offset { get; }

        public int Length { get; }

        public string Preview { get; }

        public string Location => $"{Line}:{Column}";
    }
}
