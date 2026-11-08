namespace PS7ScriptDesk.UI.ViewModels;

/// <summary>Provides immutable document text for consumers that need a full string at a defined boundary.</summary>
public interface IEditorTextSnapshot
{
    long Revision { get; }
    int TextLength { get; }
    string MaterializeText();
}
