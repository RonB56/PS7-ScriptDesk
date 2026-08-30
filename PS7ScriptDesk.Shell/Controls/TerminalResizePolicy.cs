namespace PS7ScriptDesk.Shell.Controls;

internal readonly record struct TerminalResizeDecision(
    bool Accepted,
    int Columns,
    int Rows,
    long ResizeGeneration,
    string Reason);

/// <summary>
/// Rejects unusable and duplicate geometry notifications for one renderer instance.
/// Renderer recreation starts a fresh geometry generation so the same dimensions can
/// be applied to the replacement without being mistaken for a duplicate.
/// </summary>
internal sealed class TerminalResizePolicy
{
    private int _rendererGeneration = -1;
    private int _lastColumns;
    private int _lastRows;
    private long _resizeGeneration;

    public long ResizeGeneration => _resizeGeneration;

    public void Reset(int rendererGeneration)
    {
        _rendererGeneration = rendererGeneration;
        _lastColumns = 0;
        _lastRows = 0;
        _resizeGeneration = 0;
    }

    public TerminalResizeDecision Evaluate(int columns, int rows, int rendererGeneration)
    {
        if (columns <= 0 || rows <= 0)
        {
            return new TerminalResizeDecision(
                Accepted: false,
                Columns: columns,
                Rows: rows,
                ResizeGeneration: _resizeGeneration,
                Reason: "invalid-geometry");
        }

        if (_rendererGeneration != rendererGeneration)
        {
            Reset(rendererGeneration);
        }

        if (_lastColumns == columns && _lastRows == rows)
        {
            return new TerminalResizeDecision(
                Accepted: false,
                Columns: columns,
                Rows: rows,
                ResizeGeneration: _resizeGeneration,
                Reason: "duplicate-geometry");
        }

        _lastColumns = columns;
        _lastRows = rows;
        _resizeGeneration++;
        return new TerminalResizeDecision(
            Accepted: true,
            Columns: columns,
            Rows: rows,
            ResizeGeneration: _resizeGeneration,
            Reason: "accepted");
    }
}
