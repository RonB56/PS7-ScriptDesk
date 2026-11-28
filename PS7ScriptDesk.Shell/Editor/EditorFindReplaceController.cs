using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using ICSharpCode.AvalonEdit;
using PS7ScriptDesk.Shell;

namespace PS7ScriptDesk.Shell.Editor
{
    internal sealed class EditorFindReplaceController
    {
        private readonly Window _owner;
        private readonly Func<TextEditor?> _resolveActiveEditor;
        private readonly Action<string> _publishStatus;
        private FindReplaceWindow? _findReplaceWindow;
        private string _lastFindText = string.Empty;
        private string _lastReplaceText = string.Empty;
        private bool _lastFindMatchCase;
        private bool _lastFindWholeWord;
        private bool _lastFindUseRegex;

        internal EditorFindReplaceController(Window owner, Func<TextEditor?> resolveActiveEditor, Action<string> publishStatus)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _resolveActiveEditor = resolveActiveEditor ?? throw new ArgumentNullException(nameof(resolveActiveEditor));
            _publishStatus = publishStatus ?? throw new ArgumentNullException(nameof(publishStatus));
        }

        internal void RepeatFind(bool backwards)
        {
            if (backwards)
                ExecuteFindPrev(_lastFindText, _lastFindMatchCase, _lastFindWholeWord, _lastFindUseRegex);
            else
                ExecuteFindNext(_lastFindText, _lastFindMatchCase, _lastFindWholeWord, _lastFindUseRegex);
        }
        internal void ExecuteFindNext(string findText, bool matchCase, bool wholeWord = false, bool useRegex = false)
        {
            _lastFindText = findText ?? string.Empty;
            _lastFindMatchCase = matchCase;
            _lastFindWholeWord = wholeWord;
            _lastFindUseRegex = useRegex;

            if (_resolveActiveEditor() is not TextEditor editorTextEditor)
                return;

            if (string.IsNullOrWhiteSpace(findText))
            {
                _findReplaceWindow?.ShowStatus("Enter text to find");
                return;
            }

            try
            {
                if (!TryFindNext(editorTextEditor, findText, matchCase, wholeWord, useRegex, forward: true))
                    _findReplaceWindow?.ShowStatus("The search text was not found");
                else
                    _findReplaceWindow?.ShowStatus(null);
            }
            catch (ArgumentException ex)
            {
                _findReplaceWindow?.ShowStatus($"Invalid regex: {ex.Message}");
            }
        }

        internal void ExecuteFindPrev(string findText, bool matchCase, bool wholeWord = false, bool useRegex = false)
        {
            _lastFindText = findText ?? string.Empty;
            _lastFindMatchCase = matchCase;
            _lastFindWholeWord = wholeWord;
            _lastFindUseRegex = useRegex;

            if (_resolveActiveEditor() is not TextEditor editorTextEditor)
                return;

            if (string.IsNullOrWhiteSpace(findText))
            {
                _findReplaceWindow?.ShowStatus("Enter text to find");
                return;
            }

            try
            {
                if (!TryFindNext(editorTextEditor, findText, matchCase, wholeWord, useRegex, forward: false))
                    _findReplaceWindow?.ShowStatus("The search text was not found");
                else
                    _findReplaceWindow?.ShowStatus(null);
            }
            catch (ArgumentException ex)
            {
                _findReplaceWindow?.ShowStatus($"Invalid regex: {ex.Message}");
            }
        }

        internal void ExecuteReplace(string findText, string replaceText, bool matchCase, bool wholeWord = false, bool useRegex = false)
        {
            _lastFindText = findText ?? string.Empty;
            _lastReplaceText = replaceText ?? string.Empty;
            _lastFindMatchCase = matchCase;
            _lastFindWholeWord = wholeWord;
            _lastFindUseRegex = useRegex;

            if (_resolveActiveEditor() is not TextEditor editorTextEditor)
                return;

            if (string.IsNullOrWhiteSpace(findText))
            {
                _findReplaceWindow?.ShowStatus("Enter text to replace");
                return;
            }

            var sanitizedReplaceText = replaceText ?? string.Empty;
            string? statusMsg = null;
            try
            {
                if (!TryReplaceCurrent(editorTextEditor, findText, sanitizedReplaceText, matchCase, wholeWord, useRegex))
                    TryFindNext(editorTextEditor, findText, matchCase, wholeWord, useRegex, forward: true);
            }
            catch (ArgumentException ex)
            {
                statusMsg = $"Invalid regex: {ex.Message}";
            }
            _findReplaceWindow?.ShowStatus(statusMsg);
        }

        internal void ExecuteReplaceAll(string findText, string replaceText, bool matchCase, bool wholeWord = false, bool useRegex = false)
        {
            _lastFindText = findText ?? string.Empty;
            _lastReplaceText = replaceText ?? string.Empty;
            _lastFindMatchCase = matchCase;
            _lastFindWholeWord = wholeWord;
            _lastFindUseRegex = useRegex;

            if (_resolveActiveEditor() is not TextEditor editorTextEditor)
                return;

            if (string.IsNullOrWhiteSpace(findText))
            {
                _findReplaceWindow?.ShowStatus("Enter text to replace");
                return;
            }

            var sanitizedReplaceText = replaceText ?? string.Empty;
            string? statusMsg = null;
            try
            {
                var replacements = ReplaceAll(editorTextEditor, findText, sanitizedReplaceText, matchCase, wholeWord, useRegex);
                statusMsg = replacements == 0 ? "No matches were found to replace" : null;
                _publishStatus(replacements == 0
                        ? "No matches were found to replace"
                        : $"Replaced {replacements} occurrence(s)");
            }
            catch (ArgumentException ex)
            {
                statusMsg = $"Invalid regex: {ex.Message}";
            }
            _findReplaceWindow?.ShowStatus(statusMsg);
        }

        internal void OpenFindReplaceWindow(bool showReplace)
        {
            var selectedFindText = GetActiveEditorSelectedSearchText();
            if (!string.IsNullOrEmpty(selectedFindText))
            {
                _lastFindText = selectedFindText;
            }

            _findReplaceWindow ??= new FindReplaceWindow(_owner, this, _lastFindText, _lastReplaceText, _lastFindMatchCase);
            _findReplaceWindow.FindText = _lastFindText;
            _findReplaceWindow.ReplaceText = _lastReplaceText;
            _findReplaceWindow.MatchCase = _lastFindMatchCase;
            _findReplaceWindow.WholeWord = _lastFindWholeWord;
            _findReplaceWindow.UseRegex = _lastFindUseRegex;
            _findReplaceWindow.ShowStatus(null);
            _findReplaceWindow.RefreshResultList();
            _findReplaceWindow.Show();
            _findReplaceWindow.Activate();
            _findReplaceWindow.SetMode(showReplace);
        }

        private string GetActiveEditorSelectedSearchText()
        {
            if (_resolveActiveEditor() is not TextEditor editorTextEditor ||
                editorTextEditor.Document is null ||
                editorTextEditor.SelectionLength <= 0)
            {
                return string.Empty;
            }

            var selectedText = editorTextEditor.SelectedText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(selectedText))
            {
                return string.Empty;
            }

            // The Find/Replace input is intentionally a single-line field. Do not
            // push a multi-line selection into it because that makes the dialog hard
            // to read and can look broken to non-advanced users.
            if (selectedText.Contains('\r') || selectedText.Contains('\n'))
            {
                return string.Empty;
            }

            return selectedText;
        }

        internal IReadOnlyList<FindResultRow> GetFindResults(string findText, bool matchCase, bool wholeWord = false, bool useRegex = false)
        {
            var results = new List<FindResultRow>();
            if (_resolveActiveEditor() is not TextEditor editorTextEditor ||
                editorTextEditor.Document is null ||
                string.IsNullOrWhiteSpace(findText))
            {
                return results;
            }

            var text = editorTextEditor.Text ?? string.Empty;
            if (text.Length == 0)
            {
                return results;
            }

            var rx = BuildFindRegex(findText, matchCase, wholeWord, useRegex);
            var matches = rx.Matches(text);
            var resultNumber = 1;

            foreach (Match match in matches)
            {
                if (!match.Success)
                {
                    continue;
                }

                var safeOffset = Math.Clamp(match.Index, 0, text.Length);
                var line = editorTextEditor.Document.GetLineByOffset(safeOffset);
                var column = safeOffset - line.Offset + 1;
                var lineText = editorTextEditor.Document.GetText(line.Offset, line.Length).Trim();
                if (string.IsNullOrEmpty(lineText))
                {
                    lineText = "(blank line)";
                }

                results.Add(new FindResultRow(
                    resultNumber++,
                    line.LineNumber,
                    Math.Max(1, column),
                    safeOffset,
                    match.Length,
                    lineText));
            }

            return results;
        }

        internal void NavigateToFindResult(int offset, int length, int lineNumber, int column)
        {
            if (_resolveActiveEditor() is not TextEditor editorTextEditor)
            {
                return;
            }

            var textLength = editorTextEditor.Text?.Length ?? 0;
            if (textLength == 0)
            {
                return;
            }

            var safeOffset = Math.Clamp(offset, 0, textLength);
            var safeLength = Math.Clamp(length, 0, textLength - safeOffset);
            editorTextEditor.Select(safeOffset, safeLength);
            editorTextEditor.ScrollTo(lineNumber, Math.Max(1, column));
            editorTextEditor.CaretOffset = Math.Min(textLength, safeOffset + safeLength);
            editorTextEditor.Focus();

            _publishStatus($"Jumped to search result at line {lineNumber}, column {column}");
        }

        // Throws ArgumentException for invalid regex patterns.
        private static Regex BuildFindRegex(string findText, bool matchCase, bool wholeWord, bool useRegex)
        {
            var pattern = useRegex ? findText : Regex.Escape(findText);
            if (wholeWord) pattern = $@"\b{pattern}\b";
            var options = RegexOptions.None;
            if (!matchCase) options |= RegexOptions.IgnoreCase;
            return new Regex(pattern, options);
        }

        private bool TryFindNext(TextEditor editorTextEditor, string findText, bool matchCase, bool wholeWord, bool useRegex, bool forward)
        {
            var text = editorTextEditor.Text ?? string.Empty;
            var rx = BuildFindRegex(findText, matchCase, wholeWord, useRegex);

            int searchFrom;
            if (forward)
            {
                searchFrom = editorTextEditor.SelectionLength > 0
                    ? editorTextEditor.SelectionStart + editorTextEditor.SelectionLength
                    : editorTextEditor.CaretOffset;
            }
            else
            {
                searchFrom = editorTextEditor.SelectionLength > 0
                    ? editorTextEditor.SelectionStart
                    : editorTextEditor.CaretOffset;
            }
            searchFrom = Math.Clamp(searchFrom, 0, text.Length);

            Match m;
            if (forward)
            {
                m = rx.Match(text, searchFrom);
                if (!m.Success && searchFrom > 0)
                    m = rx.Match(text, 0, searchFrom);
            }
            else
            {
                // Find last match before searchFrom — collect all matches up to that point.
                var allMatches = rx.Matches(text);
                m = Match.Empty;
                foreach (Match candidate in allMatches)
                {
                    if (candidate.Index < searchFrom)
                        m = candidate;
                }
                if (!m.Success)
                {
                    // Wrap: take the last match in the whole document.
                    foreach (Match candidate in allMatches)
                        m = candidate;
                }
            }

            if (!m.Success)
            {
                _publishStatus("Search text was not found");
                return false;
            }

            editorTextEditor.Select(m.Index, m.Length);
            editorTextEditor.ScrollTo(editorTextEditor.Document.GetLineByOffset(m.Index).LineNumber, 1);
            editorTextEditor.CaretOffset = forward ? m.Index + m.Length : m.Index;
            editorTextEditor.Focus();

            _publishStatus($"Found '{findText}'");

            return true;
        }

        private bool TryReplaceCurrent(TextEditor editorTextEditor, string findText, string replaceText, bool matchCase, bool wholeWord, bool useRegex)
        {
            var rx = BuildFindRegex(findText, matchCase, wholeWord, useRegex);
            var selectedText = editorTextEditor.SelectedText ?? string.Empty;
            var m = rx.Match(selectedText);
            if (!m.Success || m.Index != 0 || m.Length != selectedText.Length)
                return false;

            var selectionStart = editorTextEditor.SelectionStart;
            var replacement = useRegex ? m.Result(replaceText ?? string.Empty) : replaceText ?? string.Empty;
            editorTextEditor.Document.Replace(selectionStart, editorTextEditor.SelectionLength, replacement);
            editorTextEditor.Select(selectionStart, replacement.Length);
            editorTextEditor.CaretOffset = selectionStart + replacement.Length;
            editorTextEditor.Focus();

            _publishStatus($"Replaced '{findText}'");

            return true;
        }

        private int ReplaceAll(TextEditor editorTextEditor, string findText, string replaceText, bool matchCase, bool wholeWord, bool useRegex)
        {
            var originalText = editorTextEditor.Text ?? string.Empty;
            if (string.IsNullOrEmpty(originalText) || string.IsNullOrEmpty(findText))
                return 0;

            var rx = BuildFindRegex(findText, matchCase, wholeWord, useRegex);
            var replacement = replaceText ?? string.Empty;
            var replacements = 0;

            var newText = rx.Replace(originalText, m => { replacements++; return useRegex ? m.Result(replacement) : replacement; });

            if (replacements == 0)
                return 0;

            editorTextEditor.Text = newText;
            editorTextEditor.CaretOffset = Math.Min(editorTextEditor.Text.Length, editorTextEditor.CaretOffset);
            editorTextEditor.Focus();
            return replacements;
        }

    }
}
