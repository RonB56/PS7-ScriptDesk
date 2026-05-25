using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace PS7ScriptDesk.UI.ViewModels
{
    public class EditorTabViewModel : INotifyPropertyChanged
    {
        private string _title;
        private string _content;
        private string? _filePath;
        private bool _isDirty;
        private string _lineNumbersText;
        private int _lineCount;
        private int _caretLine;
        private int _caretColumn;
        private int _selectionLength;
        private readonly SortedDictionary<int, bool> _breakpoints = new();
        private int _enabledBreakpointCount;
        private readonly ObservableCollection<SyntaxErrorViewModel> _syntaxErrors = new();
        private readonly ObservableCollection<EditorDiagnosticSpanViewModel> _syntaxDiagnosticSpans = new();
        private string _syntaxDiagnosticsStatusText = "Syntax checking is waiting for a PowerShell runtime";

        public EditorTabViewModel(string title, string content, string? filePath = null)
        {
            _title = title;
            _content = content;
            _filePath = filePath;
            _isDirty = false;
            _lineNumbersText = "1";
            _lineCount = 1;
            _caretLine = 1;
            _caretColumn = 1;
            _selectionLength = 0;
            SyntaxErrors = new ReadOnlyObservableCollection<SyntaxErrorViewModel>(_syntaxErrors);
            SyntaxDiagnosticSpans = new ReadOnlyObservableCollection<EditorDiagnosticSpanViewModel>(_syntaxDiagnosticSpans);

            RecalculateEditorMetrics();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayTitle));
                }
            }
        }

        public string? FilePath
        {
            get => _filePath;
            private set
            {
                if (_filePath != value)
                {
                    _filePath = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Content
        {
            get => _content;
            set
            {
                if (_content != value)
                {
                    _content = value;
                    OnPropertyChanged();

                    RecalculateEditorMetrics();

                    if (!IsDirty)
                    {
                        IsDirty = true;
                    }
                }
            }
        }

        /// <summary>
        /// Updates the backing text after an AvalonEdit edit without forcing another
        /// full-document line-number rebuild. AvalonEdit already owns the visual line
        /// number margin, so the view-model only needs the current line count for the
        /// status bar and breakpoint validation.
        /// </summary>
        public void UpdateContentFromEditor(string content, int editorLineCount)
        {
            var normalizedContent = content ?? string.Empty;
            var contentChanged = !string.Equals(_content, normalizedContent, StringComparison.Ordinal);

            if (contentChanged)
            {
                _content = normalizedContent;
                OnPropertyChanged(nameof(Content));

                if (!IsDirty)
                {
                    IsDirty = true;
                }
            }

            UpdateLineCount(editorLineCount);
        }

        public bool IsDirty
        {
            get => _isDirty;
            private set
            {
                if (_isDirty != value)
                {
                    _isDirty = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayTitle));
                }
            }
        }

        public int LineCount => _lineCount;

        public string LineNumbersText => _lineNumbersText;

        public int CaretLine => _caretLine;

        public int CaretColumn => _caretColumn;

        public int SelectionLength => _selectionLength;

        public string CaretDisplayText => $"Ln {_caretLine}, Col {_caretColumn}";

        public string EditorMetricsText => $"Lines: {_lineCount}";

        public string SelectionDisplayText => _selectionLength > 0
            ? $"Selection: {_selectionLength} char(s)"
            : "Selection: None";

        /// <summary>
        /// 1-based line number where execution is currently paused in the debugger, or -1 if not paused.
        /// The background renderer uses this to draw the yellow execution-arrow highlight.
        /// </summary>
        public int CurrentDebugLine { get; private set; } = -1;

        public void SetCurrentDebugLine(int lineNumber)
        {
            var normalized = lineNumber > 0 ? lineNumber : -1;
            if (CurrentDebugLine == normalized)
            {
                return;
            }

            CurrentDebugLine = normalized;
            OnPropertyChanged(nameof(CurrentDebugLine));
        }

        public void ClearCurrentDebugLine()
        {
            if (CurrentDebugLine == -1)
            {
                return;
            }

            CurrentDebugLine = -1;
            OnPropertyChanged(nameof(CurrentDebugLine));
        }

        public int BreakpointCount => _breakpoints.Count;

        public int EnabledBreakpointCount => _enabledBreakpointCount;

        public ReadOnlyObservableCollection<SyntaxErrorViewModel> SyntaxErrors { get; }

        public ReadOnlyObservableCollection<EditorDiagnosticSpanViewModel> SyntaxDiagnosticSpans { get; }

        public bool HasSyntaxErrors => DiagnosticErrorCount > 0;

        public bool HasDiagnosticWarnings => DiagnosticWarningCount > 0;

        public bool HasDiagnostics => _syntaxErrors.Count > 0;

        public int DiagnosticErrorCount => _syntaxErrors.Count(static diagnostic => diagnostic.IsError);

        public int DiagnosticWarningCount => _syntaxErrors.Count(static diagnostic => diagnostic.IsWarning);

        public bool HasSyntaxDiagnosticsStatusMessage =>
            !HasDiagnostics &&
            !string.IsNullOrWhiteSpace(_syntaxDiagnosticsStatusText) &&
            !string.Equals(_syntaxDiagnosticsStatusText, "Syntax: OK", StringComparison.Ordinal) &&
            !string.Equals(_syntaxDiagnosticsStatusText, "Diagnostics: OK", StringComparison.Ordinal) &&
            !string.Equals(_syntaxDiagnosticsStatusText, "Syntax checking…", StringComparison.Ordinal);

        public bool ShowSyntaxDiagnosticsPanel => HasSyntaxErrors || HasSyntaxDiagnosticsStatusMessage;

        public string SyntaxDiagnosticsStatusText => _syntaxDiagnosticsStatusText;

        public string BreakpointDisplayText => BreakpointCount == 0
            ? "Breakpoints: None"
            : $"Breakpoints: {string.Join(", ", _breakpoints.Select(pair => pair.Value ? pair.Key.ToString() : $"{pair.Key} (off)"))}";

        public string SyntaxErrorSummaryText => HasDiagnostics
            ? BuildDiagnosticsSummaryText(prefix: "Editor diagnostics")
            : _syntaxDiagnosticsStatusText;

        public string DiagnosticsTabHeaderText => HasDiagnostics
            ? BuildDiagnosticsSummaryText(prefix: "Diagnostics")
            : "Diagnostics";

        public string DiagnosticsBadgeText => HasDiagnostics
            ? BuildDiagnosticsSummaryText(prefix: string.Empty)
            : string.Empty;

        /// <summary>The set of 1-based line numbers that have a breakpoint, enabled or disabled.</summary>
        public IReadOnlyCollection<int> BreakpointLineNumbers => _breakpoints.Keys.ToArray();

        public int BreakpointVersion { get; private set; }

        public string DisplayTitle => IsDirty ? $"{Title}*" : Title;

        public void SetFilePath(string filePath)
        {
            FilePath = filePath;
            Title = Path.GetFileName(filePath);
        }

        public void MarkSaved()
        {
            IsDirty = false;
        }

        /// <summary>
        /// Marks this tab as modified without changing the visible editor text.
        /// Used when a clean restored tab no longer matches the current file on disk;
        /// the visible buffer must be treated as the source of truth for Run/Debug.
        /// </summary>
        public void MarkExternallyStale()
        {
            IsDirty = true;
        }

        public bool HasBreakpoint(int lineNumber)
        {
            return _breakpoints.ContainsKey(Math.Max(1, lineNumber));
        }

        public bool IsBreakpointEnabled(int lineNumber)
        {
            return _breakpoints.TryGetValue(Math.Max(1, lineNumber), out var isEnabled) && isEnabled;
        }

        public IEnumerable<int> GetAllBreakpointLines()
        {
            return _breakpoints.Keys.ToArray();
        }

        public IEnumerable<int> GetEnabledBreakpointLines()
        {
            return _breakpoints.Where(pair => pair.Value).Select(pair => pair.Key).ToArray();
        }

        public bool ToggleBreakpoint(int lineNumber)
        {
            var normalizedLine = Math.Max(1, lineNumber);
            if (_breakpoints.ContainsKey(normalizedLine))
            {
                _breakpoints.Remove(normalizedLine);
                NotifyBreakpointsChanged();
                return false;
            }

            _breakpoints[normalizedLine] = true;
            NotifyBreakpointsChanged();
            return true;
        }

        public void SetBreakpointEnabled(int lineNumber, bool isEnabled)
        {
            var normalizedLine = Math.Max(1, lineNumber);
            if (!_breakpoints.TryGetValue(normalizedLine, out var currentValue) || currentValue == isEnabled)
            {
                return;
            }

            _breakpoints[normalizedLine] = isEnabled;
            NotifyBreakpointsChanged();
        }

        public bool RemoveBreakpoint(int lineNumber)
        {
            var removed = _breakpoints.Remove(Math.Max(1, lineNumber));
            if (removed)
            {
                NotifyBreakpointsChanged();
            }

            return removed;
        }

        public void ClearInvalidBreakpoints()
        {
            var invalidLines = _breakpoints.Keys.Where(line => line < 1 || line > _lineCount).ToList();
            if (invalidLines.Count == 0)
            {
                return;
            }

            foreach (var line in invalidLines)
            {
                _breakpoints.Remove(line);
            }

            NotifyBreakpointsChanged();
        }

        public bool SetSyntaxDiagnostics(IEnumerable<EditorDiagnosticSpanViewModel> diagnostics, string? successStatusText = null)
        {
            var incomingDiagnostics = (diagnostics ?? Enumerable.Empty<EditorDiagnosticSpanViewModel>()).ToList();
            var errorCount = incomingDiagnostics.Count(static diagnostic => diagnostic.IsError);
            var warningCount = incomingDiagnostics.Count(static diagnostic => diagnostic.IsWarning);
            var nextStatusText = incomingDiagnostics.Count > 0
                ? BuildDiagnosticsSummaryText(prefix: "Editor diagnostics", errorCount: errorCount, warningCount: warningCount)
                : (string.IsNullOrWhiteSpace(successStatusText) ? "Syntax: OK" : successStatusText);

            if (DiagnosticsAreEquivalent(_syntaxDiagnosticSpans, incomingDiagnostics) &&
                string.Equals(_syntaxDiagnosticsStatusText, nextStatusText, StringComparison.Ordinal))
            {
                return false;
            }

            _syntaxDiagnosticSpans.Clear();
            _syntaxErrors.Clear();

            foreach (var diagnostic in incomingDiagnostics)
            {
                _syntaxDiagnosticSpans.Add(diagnostic);
                _syntaxErrors.Add(new SyntaxErrorViewModel(diagnostic.LineNumber, diagnostic.ColumnNumber, diagnostic.Message, diagnostic.StartOffset, diagnostic.EndOffset, diagnostic.Severity));
            }

            _syntaxDiagnosticsStatusText = nextStatusText;

            OnPropertyChanged(nameof(SyntaxDiagnosticSpans));
            OnPropertyChanged(nameof(SyntaxErrors));
            NotifyDiagnosticSummaryChanged();
            return true;
        }

        public bool SetSyntaxDiagnosticsStatus(string statusText, bool clearErrors = false)
        {
            var diagnosticsChanged = false;
            if (clearErrors)
            {
                diagnosticsChanged = _syntaxDiagnosticSpans.Count > 0 || _syntaxErrors.Count > 0;
                _syntaxDiagnosticSpans.Clear();
                _syntaxErrors.Clear();
                if (diagnosticsChanged)
                {
                    OnPropertyChanged(nameof(SyntaxDiagnosticSpans));
                    OnPropertyChanged(nameof(SyntaxErrors));
                }
            }

            var nextStatusText = string.IsNullOrWhiteSpace(statusText)
                ? "Syntax checking status unavailable"
                : statusText;

            if (!diagnosticsChanged &&
                string.Equals(_syntaxDiagnosticsStatusText, nextStatusText, StringComparison.Ordinal))
            {
                return false;
            }

            _syntaxDiagnosticsStatusText = nextStatusText;

            NotifyDiagnosticSummaryChanged();
            return true;
        }


        private string BuildDiagnosticsSummaryText(string prefix)
        {
            return BuildDiagnosticsSummaryText(prefix, DiagnosticErrorCount, DiagnosticWarningCount);
        }

        private static string BuildDiagnosticsSummaryText(string prefix, int errorCount, int warningCount)
        {
            string detail;
            if (errorCount > 0 && warningCount > 0)
            {
                detail = $"{errorCount} error{(errorCount == 1 ? string.Empty : "s")}, {warningCount} warning{(warningCount == 1 ? string.Empty : "s")}";
            }
            else if (errorCount > 0)
            {
                detail = $"{errorCount} error{(errorCount == 1 ? string.Empty : "s")}";
            }
            else if (warningCount > 0)
            {
                detail = $"{warningCount} warning{(warningCount == 1 ? string.Empty : "s")}";
            }
            else
            {
                detail = "0";
            }

            return string.IsNullOrWhiteSpace(prefix)
                ? detail
                : $"{prefix}: {detail}";
        }

        private static bool DiagnosticsAreEquivalent(
            IReadOnlyList<EditorDiagnosticSpanViewModel> existingDiagnostics,
            IReadOnlyList<EditorDiagnosticSpanViewModel> incomingDiagnostics)
        {
            if (existingDiagnostics.Count != incomingDiagnostics.Count)
            {
                return false;
            }

            for (var index = 0; index < existingDiagnostics.Count; index++)
            {
                var existing = existingDiagnostics[index];
                var incoming = incomingDiagnostics[index];
                if (existing.LineNumber != incoming.LineNumber ||
                    existing.ColumnNumber != incoming.ColumnNumber ||
                    existing.StartOffset != incoming.StartOffset ||
                    existing.EndOffset != incoming.EndOffset ||
                    !string.Equals(existing.Message, incoming.Message, StringComparison.Ordinal) ||
                    !string.Equals(existing.Severity, incoming.Severity, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private void NotifyDiagnosticSummaryChanged()
        {
            OnPropertyChanged(nameof(HasSyntaxErrors));
            OnPropertyChanged(nameof(HasDiagnosticWarnings));
            OnPropertyChanged(nameof(HasDiagnostics));
            OnPropertyChanged(nameof(DiagnosticErrorCount));
            OnPropertyChanged(nameof(DiagnosticWarningCount));
            OnPropertyChanged(nameof(SyntaxDiagnosticsStatusText));
            OnPropertyChanged(nameof(SyntaxErrorSummaryText));
            OnPropertyChanged(nameof(DiagnosticsTabHeaderText));
            OnPropertyChanged(nameof(DiagnosticsBadgeText));
            OnPropertyChanged(nameof(HasSyntaxDiagnosticsStatusMessage));
            OnPropertyChanged(nameof(ShowSyntaxDiagnosticsPanel));
        }

        public void UpdateCaretPosition(int line, int column, int selectionLength)
        {
            var normalizedLine = Math.Max(1, line);
            var normalizedColumn = Math.Max(1, column);
            var normalizedSelectionLength = Math.Max(0, selectionLength);
            var hasChanged = false;

            if (_caretLine != normalizedLine)
            {
                _caretLine = normalizedLine;
                OnPropertyChanged(nameof(CaretLine));
                hasChanged = true;
            }

            if (_caretColumn != normalizedColumn)
            {
                _caretColumn = normalizedColumn;
                OnPropertyChanged(nameof(CaretColumn));
                hasChanged = true;
            }

            if (_selectionLength != normalizedSelectionLength)
            {
                _selectionLength = normalizedSelectionLength;
                OnPropertyChanged(nameof(SelectionLength));
                OnPropertyChanged(nameof(SelectionDisplayText));
            }

            if (hasChanged)
            {
                OnPropertyChanged(nameof(CaretDisplayText));
            }
        }

        private void RecalculateEditorMetrics()
        {
            UpdateLineCount(CalculateLineCount(_content));
        }

        private void UpdateLineCount(int lineCount)
        {
            var normalizedLineCount = Math.Max(1, lineCount);
            if (_lineCount == normalizedLineCount)
            {
                return;
            }

            _lineCount = normalizedLineCount;
            OnPropertyChanged(nameof(LineCount));
            OnPropertyChanged(nameof(EditorMetricsText));

            if (_caretLine > _lineCount)
            {
                _caretLine = _lineCount;
                OnPropertyChanged(nameof(CaretLine));
                OnPropertyChanged(nameof(CaretDisplayText));
            }

            ClearInvalidBreakpoints();
        }

        private static int CalculateLineCount(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 1;
            }

            var lineCount = 1;
            foreach (var character in text)
            {
                if (character == '\n')
                {
                    lineCount++;
                }
            }

            return lineCount;
        }

        private void NotifyBreakpointsChanged()
        {
            _enabledBreakpointCount = 0;
            foreach (var pair in _breakpoints)
            {
                if (pair.Value) _enabledBreakpointCount++;
            }

            BreakpointVersion++;
            OnPropertyChanged(nameof(BreakpointCount));
            OnPropertyChanged(nameof(EnabledBreakpointCount));
            OnPropertyChanged(nameof(BreakpointDisplayText));
            OnPropertyChanged(nameof(BreakpointVersion));
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
