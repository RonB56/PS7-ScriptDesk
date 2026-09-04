namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>
/// Stable logical identity and monotonic content revision for one editor document.
/// </summary>
public sealed class ScriptDocumentIdentity
{
    private long _revision;

    public ScriptDocumentIdentity(Guid? documentId = null)
    {
        DocumentId = documentId.GetValueOrDefault(Guid.NewGuid());
        if (DocumentId == Guid.Empty)
        {
            throw new ArgumentException("Document identity cannot be empty.", nameof(documentId));
        }
    }

    public Guid DocumentId { get; }

    public long Revision => Interlocked.Read(ref _revision);

    public long AdvanceRevision()
    {
        return Interlocked.Increment(ref _revision);
    }

    public ScriptDocumentSnapshot Capture()
    {
        return new ScriptDocumentSnapshot(DocumentId, Revision);
    }
}

public readonly record struct ScriptDocumentSnapshot(Guid DocumentId, long DocumentRevision);
