using ICSharpCode.AvalonEdit.Document;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Shell.Editor;

/// <summary>
/// Keeps logical editor breakpoints attached to source positions as AvalonEdit
/// applies document edits. Runtime debugger breakpoints remain immutable snapshots.
/// </summary>
public sealed class EditorBreakpointTracker : IDisposable
{
    private readonly TextDocument _document;
    private readonly EditorTabViewModel _tab;
    private readonly Dictionary<int, (TextAnchor Anchor, bool IsEnabled)> _anchors = new();
    private bool _disposed;

    public EditorBreakpointTracker(TextDocument document, EditorTabViewModel tab)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _tab = tab ?? throw new ArgumentNullException(nameof(tab));
        _document.TextChanged += Document_TextChanged;
        SynchronizeFromModel();
    }

    public IReadOnlyList<int> CurrentLineNumbers => _anchors.Values
        .Where(entry => !entry.Anchor.IsDeleted)
        .Select(entry => entry.Anchor.Line)
        .Distinct()
        .OrderBy(line => line)
        .ToArray();

    public void SynchronizeFromModel()
    {
        ThrowIfDisposed();
        _anchors.Clear();

        foreach (var line in _tab.GetAllBreakpointLines())
        {
            if (line < 1 || line > _document.LineCount)
            {
                continue;
            }

            var anchor = _document.CreateAnchor(_document.GetLineByNumber(line).Offset);
            anchor.MovementType = AnchorMovementType.AfterInsertion;
            anchor.SurviveDeletion = true;
            _anchors[line] = (anchor, _tab.IsBreakpointEnabled(line));
        }
    }

    private void Document_TextChanged(object? sender, EventArgs e)
    {
        if (_disposed || _anchors.Count == 0)
        {
            return;
        }

        var trackedBreakpoints = _anchors.Values
            .Where(entry => !entry.Anchor.IsDeleted)
            .Select(entry => (LineNumber: Math.Clamp(entry.Anchor.Line, 1, _document.LineCount), entry.IsEnabled))
            .ToArray();

        var changed = _tab.ReplaceBreakpointLines(trackedBreakpoints);
        SynchronizeFromModel();

        if (changed)
        {
            DeveloperDiagnostics.LogDecision(
                "Debugger",
                "EditorBreakpointTracking",
                "Editor breakpoints followed AvalonEdit document anchors after a text mutation.",
                "Tracked",
                new Dictionary<string, object?>
                {
                    ["documentLength"] = _document.TextLength,
                    ["editorBreakpointLines"] = CurrentLineNumbers.ToArray(),
                    ["editorRevision"] = _tab.DiagnosticDocument.Revision
                });
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _document.TextChanged -= Document_TextChanged;
        _anchors.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EditorBreakpointTracker));
        }
    }
}
