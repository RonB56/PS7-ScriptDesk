using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Shell.Editor
{
    internal sealed class EditorFoldingController
    {
        private const int EditorFoldingDebounceMilliseconds = 350;
        private readonly Dictionary<TextEditor, FoldingManager> _foldingManagers = new();
        private readonly Dictionary<TextEditor, CancellationTokenSource> _foldingCancellationSources = new();
        // Inherited behavior: entries remain after editor unregistration (IFAA-SUP-001).
        private readonly Dictionary<TextEditor, long> _foldingRequestGenerations = new();
        private readonly Dispatcher _dispatcher;
        private readonly PowerShellParseCoordinator _powerShellParseCoordinator;
        private readonly Func<EditorTabViewModel, EditorDocumentSession?> _getSession;
        private readonly Func<EditorTabViewModel, TextEditor?> _getRegisteredEditor;
        private readonly Func<TextEditor, EditorTabViewModel?> _getTab;
        private readonly Func<TextEditor, int?> _getRegistrationVersion;
        private readonly Func<Task, string, IReadOnlyDictionary<string, object?>?, Task> _observeFireAndForget;

        internal EditorFoldingController(
            Dispatcher dispatcher,
            PowerShellParseCoordinator powerShellParseCoordinator,
            Func<EditorTabViewModel, EditorDocumentSession?> getSession,
            Func<EditorTabViewModel, TextEditor?> getRegisteredEditor,
            Func<TextEditor, EditorTabViewModel?> getTab,
            Func<TextEditor, int?> getRegistrationVersion,
            Func<Task, string, IReadOnlyDictionary<string, object?>?, Task> observeFireAndForget)
        {
            _dispatcher = dispatcher;
            _powerShellParseCoordinator = powerShellParseCoordinator;
            _getSession = getSession;
            _getRegisteredEditor = getRegisteredEditor;
            _getTab = getTab;
            _getRegistrationVersion = getRegistrationVersion;
            _observeFireAndForget = observeFireAndForget;
        }

        internal void InstallFoldingManagerIfAbsent(TextEditor editor)
        {
            if (!_foldingManagers.ContainsKey(editor))
                _foldingManagers[editor] = FoldingManager.Install(editor.TextArea);
        }

        internal bool HasFoldingManager(TextEditor editor) => _foldingManagers.ContainsKey(editor);

        internal bool CanFoldAll(TextEditor? editor) =>
            editor is not null && _foldingManagers.TryGetValue(editor, out var manager) &&
            editor.TextArea.MultiCaretCount == 1 && manager.AllFoldings.Any(folding => !folding.IsFolded);

        internal bool CanUnfoldAll(TextEditor? editor) =>
            editor is not null && _foldingManagers.TryGetValue(editor, out var manager) &&
            manager.AllFoldings.Any(folding => folding.IsFolded);

        internal void CancelAllPendingFolding()
        {
            foreach (var editor in _foldingCancellationSources.Keys.ToArray())
                CancelPendingFolding(editor);
        }

        internal void ToggleFoldAtCaret(TextEditor editor)
        {
            var commandStopwatch = Stopwatch.StartNew();
            if (!_foldingManagers.TryGetValue(editor, out var manager))
            {
                commandStopwatch.Stop();
                DeveloperDiagnostics.LogDebug("EditorFolding", "Ctrl+M was ignored because the active editor has no folding manager.",
                    new Dictionary<string, object?>
                    {
                        ["documentId"] = (editor.DataContext as EditorTabViewModel)?.DiagnosticDocument.DocumentId,
                        ["caretOffset"] = editor.CaretOffset,
                        ["commandMilliseconds"] = commandStopwatch.Elapsed.TotalMilliseconds
                    });
                return;
            }
            var section = FindFoldAtCaret(manager, editor);
            if (section is null)
            {
                commandStopwatch.Stop();
                DeveloperDiagnostics.LogDebug("EditorFolding", "Ctrl+M found no fold applicable to the active caret.",
                    new Dictionary<string, object?>
                    {
                        ["documentId"] = (editor.DataContext as EditorTabViewModel)?.DiagnosticDocument.DocumentId,
                        ["caretOffset"] = editor.CaretOffset,
                        ["foldCount"] = manager.AllFoldings.Count(),
                        ["commandMilliseconds"] = commandStopwatch.Elapsed.TotalMilliseconds
                    });
                return;
            }

            if (!section.IsFolded)
            {
                var hasMultipleCarets = editor.TextArea.MultiCaretCount != 1;
                var selectionIntersectsFold = !hasMultipleCarets &&
                    SelectionIntersectsFold(editor.SelectionStart, editor.SelectionLength, section);
                if (hasMultipleCarets || selectionIntersectsFold)
                {
                    commandStopwatch.Stop();
                    DeveloperDiagnostics.LogDebug("EditorFolding", "Ctrl+M collapse was skipped for a selection or multi-caret edit.",
                        new Dictionary<string, object?>
                        {
                            ["documentId"] = (editor.DataContext as EditorTabViewModel)?.DiagnosticDocument.DocumentId,
                            ["startOffset"] = section.StartOffset,
                            ["endOffset"] = section.EndOffset,
                            ["caretCount"] = editor.TextArea.MultiCaretCount,
                            ["selectionIntersectsFold"] = selectionIntersectsFold,
                            ["commandMilliseconds"] = commandStopwatch.Elapsed.TotalMilliseconds
                        });
                    return;
                }

                if (section.StartOffset < editor.CaretOffset && editor.CaretOffset < section.EndOffset)
                    editor.CaretOffset = section.StartOffset;
                section.IsFolded = true;
            }
            else
            {
                section.IsFolded = false;
            }
            commandStopwatch.Stop();

            DeveloperDiagnostics.LogUserAction("Editor", "ToggleFoldAtCaret", "Fold state toggled at the editor caret.",
                new Dictionary<string, object?>
                {
                    ["documentId"] = (editor.DataContext as EditorTabViewModel)?.DiagnosticDocument.DocumentId,
                    ["startOffset"] = section.StartOffset,
                    ["endOffset"] = section.EndOffset,
                    ["collapsed"] = section.IsFolded,
                    ["caretCount"] = editor.TextArea.MultiCaretCount,
                    ["commandMilliseconds"] = commandStopwatch.Elapsed.TotalMilliseconds
                });
        }

        internal void FoldAll(TextEditor editor)
        {
            if (!_foldingManagers.TryGetValue(editor, out var manager) || editor.TextArea.MultiCaretCount != 1) return;
            var expanded = manager.AllFoldings.Where(folding => !folding.IsFolded).ToArray();
            if (expanded.Length == 0) return;

            var selectionStart = editor.SelectionStart;
            var selectionLength = editor.SelectionLength;
            if (selectionLength == 0)
            {
                var containing = expanded
                    .Where(folding => folding.StartOffset < editor.CaretOffset && editor.CaretOffset < folding.EndOffset)
                    .OrderByDescending(folding => folding.EndOffset - folding.StartOffset)
                    .FirstOrDefault();
                if (containing is not null) editor.CaretOffset = containing.StartOffset;
            }

            var collapsedCount = 0;
            foreach (var folding in expanded)
            {
                if (SelectionIntersectsFold(selectionStart, selectionLength, folding)) continue;
                folding.IsFolded = true;
                collapsedCount++;
            }

            if (collapsedCount > 0)
                DeveloperDiagnostics.LogUserAction("Editor", "FoldAll", "All eligible folding regions were collapsed.",
                    new Dictionary<string, object?>
                    {
                        ["documentId"] = (editor.DataContext as EditorTabViewModel)?.DiagnosticDocument.DocumentId,
                        ["foldCount"] = collapsedCount,
                        ["selectionPreserved"] = selectionLength > 0
                    });
        }

        internal void UnfoldAll(TextEditor editor)
        {
            if (!_foldingManagers.TryGetValue(editor, out var manager)) return;
            var folded = manager.AllFoldings.Where(folding => folding.IsFolded).ToArray();
            foreach (var folding in folded) folding.IsFolded = false;
            if (folded.Length > 0)
                DeveloperDiagnostics.LogUserAction("Editor", "UnfoldAll", "All folding regions were expanded.",
                    new Dictionary<string, object?>
                    {
                        ["documentId"] = (editor.DataContext as EditorTabViewModel)?.DiagnosticDocument.DocumentId,
                        ["foldCount"] = folded.Length
                    });
        }

        private static FoldingSection? FindFoldAtCaret(FoldingManager manager, TextEditor editor)
        {
            var document = editor.Document;
            if (document is null || document.LineCount == 0) return null;

            var caretOffset = Math.Clamp(editor.CaretOffset, 0, document.TextLength);
            var caretLine = document.GetLineByOffset(caretOffset).LineNumber;
            return FoldRegionSelector.Select(
                manager.AllFoldings,
                caretOffset,
                caretLine,
                static folding => folding.StartOffset,
                static folding => folding.EndOffset,
                folding => GetFoldHeaderLineNumber(document, folding));
        }

        private static int GetFoldHeaderLineNumber(TextDocument document, FoldingSection folding)
        {
            var startOffset = Math.Clamp(folding.StartOffset, 0, document.TextLength);
            var startLine = document.GetLineByOffset(startOffset);
            if (startOffset >= document.TextLength || document.GetCharAt(startOffset) != '{')
                return startLine.LineNumber;

            var firstNonWhitespace = startLine.Offset;
            while (firstNonWhitespace < startOffset && char.IsWhiteSpace(document.GetCharAt(firstNonWhitespace)))
                firstNonWhitespace++;

            if (firstNonWhitespace < startOffset)
                return startLine.LineNumber;

            var previousLine = startLine.PreviousLine;
            while (previousLine is not null && IsDocumentLineWhitespace(document, previousLine))
                previousLine = previousLine.PreviousLine;
            return previousLine?.LineNumber ?? startLine.LineNumber;
        }

        private static bool IsDocumentLineWhitespace(TextDocument document, DocumentLine line)
        {
            for (var offset = line.Offset; offset < line.EndOffset; offset++)
                if (!char.IsWhiteSpace(document.GetCharAt(offset))) return false;
            return true;
        }

        private static bool SelectionIntersectsFold(int selectionStart, int selectionLength, FoldingSection folding)
            => selectionLength > 0 && selectionStart < folding.EndOffset &&
               selectionStart + selectionLength > folding.StartOffset;

        internal void CancelPendingFolding(TextEditor editorTextEditor)
        {
            if (_foldingCancellationSources.TryGetValue(editorTextEditor, out var cts))
            {
                _foldingCancellationSources.Remove(editorTextEditor);
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        internal void ScheduleFolding(TextEditor editorTextEditor, int debounceMilliseconds = EditorFoldingDebounceMilliseconds)
        {
            if (editorTextEditor.Document is null ||
                !_foldingManagers.TryGetValue(editorTextEditor, out var foldingManager) ||
                editorTextEditor.DataContext is not EditorTabViewModel tab ||
                _getSession(tab) is not { } session ||
                !session.IsRegistered)
            {
                return;
            }

            CancelPendingFolding(editorTextEditor);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(session.LifetimeToken);
            _foldingCancellationSources[editorTextEditor] = cts;
            var registrationVersion = _getRegistrationVersion(editorTextEditor) ?? 0;
            var requestGeneration = _foldingRequestGenerations.TryGetValue(editorTextEditor, out var generation)
                ? generation + 1
                : 1;
            _foldingRequestGenerations[editorTextEditor] = requestGeneration;
            var scheduleStopwatch = Stopwatch.StartNew();
            scheduleStopwatch.Stop();
            if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
            {
                DeveloperDiagnostics.LogDebug("EditorFolding", "Folding request scheduled.", new Dictionary<string, object?>
                {
                    ["documentId"] = tab.DiagnosticDocument.DocumentId,
                    ["revision"] = session.Revision,
                    ["registrationGeneration"] = session.RegistrationGeneration,
                    ["editorRegistrationVersion"] = registrationVersion,
                    ["requestGeneration"] = requestGeneration,
                    ["scheduleMicroseconds"] = scheduleStopwatch.ElapsedTicks * 1_000_000d / Stopwatch.Frequency
                });
            }

            _ = _observeFireAndForget(ProcessFoldingRequestAsync(
                editorTextEditor, tab, session, foldingManager, registrationVersion, requestGeneration, debounceMilliseconds, cts),
                "editor folding update",
                new Dictionary<string, object?>
                {
                    ["editorRegistrationVersion"] = registrationVersion,
                    ["requestGeneration"] = requestGeneration,
                    ["editorIdentity"] = RuntimeHelpers.GetHashCode(editorTextEditor)
                });
        }

        private async Task ProcessFoldingRequestAsync(
            TextEditor editor,
            EditorTabViewModel tab,
            EditorDocumentSession session,
            FoldingManager manager,
            int registrationVersion,
            long requestGeneration,
            int debounceMilliseconds,
            CancellationTokenSource cancellation)
        {
            var token = cancellation.Token;
            EditorDocumentSnapshot? snapshot = null;
            try
            {
                if (debounceMilliseconds > 0)
                    await Task.Delay(debounceMilliseconds, token).ConfigureAwait(false);
                await _dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested ||
                        !IsFoldingRequestCurrent(editor, tab, session, manager, registrationVersion, requestGeneration, cancellation))
                        return;
                    snapshot = session.CaptureSnapshot();
                }, DispatcherPriority.Background);

                if (snapshot is null || token.IsCancellationRequested) return;

                var calculationSnapshot = snapshot;
                var calculationStopwatch = Stopwatch.StartNew();
                var parseResult = await _powerShellParseCoordinator
                    .GetOrParseAsync(calculationSnapshot, token)
                    .ConfigureAwait(false);
                calculationStopwatch.Stop();

                if (!parseResult.IsFor(calculationSnapshot) || parseResult.ParseFailure is not null || parseResult.FoldingFailure is not null)
                {
                    if (DeveloperDiagnostics.IsEnabled)
                        DeveloperDiagnostics.LogWarning("EditorFolding", "Folding update was skipped because the shared parse or folding projection was unavailable.",
                            new Dictionary<string, object?>
                            {
                                ["documentId"] = calculationSnapshot.DocumentId,
                                ["revision"] = calculationSnapshot.Revision,
                                ["parseFailure"] = parseResult.ParseFailure,
                                ["foldingFailure"] = parseResult.FoldingFailure,
                                ["requestGeneration"] = requestGeneration
                            });
                    return;
                }

                var ranges = parseResult.FoldingRanges;

                await _dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested ||
                        !IsFoldingRequestCurrent(editor, tab, session, manager, registrationVersion, requestGeneration, cancellation) ||
                        !session.IsRegistrationCurrent(snapshot))
                    {
                        if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
                            DeveloperDiagnostics.LogDebug("EditorFolding", "Stale folding result rejected.", new Dictionary<string, object?>
                            {
                                ["documentId"] = snapshot.DocumentId,
                                ["revision"] = snapshot.Revision,
                                ["requestGeneration"] = requestGeneration
                            });
                        return;
                    }

                    var applyStopwatch = Stopwatch.StartNew();
                    manager.UpdateFoldings(ranges.Select(range =>
                    {
                        var folding = new NewFolding(range.StartOffset, range.EndOffset);
                        if (range.Name is not null) folding.Name = range.Name;
                        return folding;
                    }), parseResult.Errors.Count == 0 ? -1 : parseResult.Errors.Min(error => error.StartOffset));

                    var restoredFoldCount = 0;
                    var stateSnapshot = session.FoldedRegionStateSnapshot;
                    if (stateSnapshot is not null)
                    {
                        var foldedRanges = stateSnapshot.FindFoldedRanges(snapshot.TextSource, ranges);
                        foreach (var foldedRange in foldedRanges.OrderBy(range => range.EndOffset - range.StartOffset))
                        {
                            var section = manager.AllFoldings.FirstOrDefault(candidate =>
                                candidate.StartOffset == foldedRange.StartOffset &&
                                candidate.EndOffset == foldedRange.EndOffset);
                            if (section is null) continue;
                            section.IsFolded = true;
                            restoredFoldCount++;
                        }

                        session.ClearFoldedRegionState();
                    }
                    applyStopwatch.Stop();
                    if (DeveloperDiagnostics.IsEnabled)
                        DeveloperDiagnostics.LogInfo("EditorFolding", "Folding result applied.", new Dictionary<string, object?>
                        {
                            ["documentId"] = snapshot.DocumentId,
                            ["revision"] = snapshot.Revision,
                            ["foldCount"] = ranges.Count,
                            ["parserMilliseconds"] = parseResult.ParserMilliseconds,
                            ["foldingCalculationMilliseconds"] = parseResult.FoldingCalculationMilliseconds,
                            ["syntaxTokenCount"] = parseResult.SyntaxTokens.Count,
                            ["calculationMilliseconds"] = calculationStopwatch.Elapsed.TotalMilliseconds,
                            ["dispatcherApplyMilliseconds"] = applyStopwatch.Elapsed.TotalMilliseconds,
                            ["restoredFoldCount"] = restoredFoldCount,
                            ["parserPassSharedWithDiagnostics"] = true,
                            ["requestGeneration"] = requestGeneration
                        });
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
                    DeveloperDiagnostics.LogDebug("EditorFolding", "Folding request canceled.", new Dictionary<string, object?>
                    {
                        ["requestGeneration"] = requestGeneration,
                        ["documentId"] = tab.DiagnosticDocument.DocumentId
                    });
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogException("EditorFolding", ex, "Folding request failed.", new Dictionary<string, object?>
                {
                    ["documentId"] = tab.DiagnosticDocument.DocumentId,
                    ["revision"] = snapshot?.Revision,
                    ["requestGeneration"] = requestGeneration
                });
            }
            finally
            {
                try
                {
                    if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
                        await _dispatcher.InvokeAsync(() =>
                        {
                            if (_foldingCancellationSources.TryGetValue(editor, out var active) && ReferenceEquals(active, cancellation))
                                _foldingCancellationSources.Remove(editor);
                        }, DispatcherPriority.Background);
                }
                catch (InvalidOperationException) { }
                catch (TaskCanceledException) { }
                cancellation.Dispose();
            }
        }

        private bool IsFoldingRequestCurrent(
            TextEditor editor,
            EditorTabViewModel tab,
            EditorDocumentSession session,
            FoldingManager manager,
            int registrationVersion,
            long requestGeneration,
            CancellationTokenSource cancellation)
        {
            return !cancellation.IsCancellationRequested &&
                   !session.IsDisposed &&
                   session.IsRegistered &&
                   ReferenceEquals(editor.DataContext, tab) &&
                   ReferenceEquals(editor.Document, session.Document) &&
                   ReferenceEquals(_getSession(tab), session) &&
                   ReferenceEquals(_getRegisteredEditor(tab), editor) &&
                   _foldingManagers.TryGetValue(editor, out var currentManager) && ReferenceEquals(currentManager, manager) &&
                   _foldingCancellationSources.TryGetValue(editor, out var activeCancellation) && ReferenceEquals(activeCancellation, cancellation) &&
                   _getRegistrationVersion(editor) is int currentVersion && currentVersion == registrationVersion &&
                   _foldingRequestGenerations.TryGetValue(editor, out var currentGeneration) && currentGeneration == requestGeneration;
        }

        internal void UninstallFoldingManager(TextEditor editorTextEditor)
        {
            if (!_foldingManagers.Remove(editorTextEditor, out var foldingManager))
            {
                return;
            }

            FoldingManager.Uninstall(foldingManager);
        }

        internal void CaptureFoldedRegionState(TextEditor editorTextEditor)
        {
            if (!_foldingManagers.TryGetValue(editorTextEditor, out var manager) ||
                editorTextEditor.Document is null ||
                _getTab(editorTextEditor) is not { } tab ||
                _getSession(tab) is not { } session)
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var snapshot = FoldedRegionStateSnapshot.Capture(
                    editorTextEditor.Document,
                    manager.AllFoldings.Select(section =>
                        (new FoldingRange(section.StartOffset, section.EndOffset, section.Title), section.IsFolded)));
                session.CaptureFoldedRegionState(snapshot);
                stopwatch.Stop();
                if (DeveloperDiagnostics.IsEnabled)
                    DeveloperDiagnostics.LogInfo("EditorFolding", "Per-document folded state captured before folding manager removal.",
                        new Dictionary<string, object?>
                        {
                            ["documentId"] = session.DocumentId,
                            ["revision"] = session.Revision,
                            ["foldCount"] = manager.AllFoldings.Count(),
                            ["foldedCount"] = snapshot.Count,
                            ["documentLength"] = editorTextEditor.Document.TextLength,
                            ["elapsedMilliseconds"] = stopwatch.Elapsed.TotalMilliseconds
                        });
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogException("EditorFolding", ex, "Per-document folded state capture failed.",
                    new Dictionary<string, object?> { ["documentId"] = session.DocumentId });
            }
        }

    }
}
