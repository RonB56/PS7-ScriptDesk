using PS7ScriptDesk.Shell.Debug;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class DebugSourceMapTests
{
    [Fact]
    public void SavedSourceMapsExactlyByCanonicalFullPath()
    {
        var documentId = Guid.NewGuid();
        var map = new DebugSourceMap(Guid.NewGuid());
        map.Register("C:\\Scripts\\demo.ps1", documentId, 4, "C:\\Scripts\\demo.ps1", "Write-Output one", isSnapshot: false);

        var result = map.Map("c:/scripts/DEMO.ps1", 3, _ => 4);

        Assert.Equal(DebugSourceMappingStatus.Exact, result.Status);
        Assert.True(result.CanNavigate);
        Assert.Equal(documentId, result.SourceDocumentId);
        Assert.Equal(3, result.EditorLine);
    }

    [Fact]
    public void SnapshotPathMapsToTheLogicalEditorDocument()
    {
        var documentId = Guid.NewGuid();
        var map = new DebugSourceMap(Guid.NewGuid());
        map.Register("C:\\Temp\\debug-123.ps1", documentId, 0, null, "a\nb", isSnapshot: true);

        var result = map.Map("C:\\Temp\\DEBUG-123.ps1", 2, _ => 0);

        Assert.Equal(DebugSourceMappingStatus.SnapshotMapped, result.Status);
        Assert.True(result.CanNavigate);
        Assert.Equal(documentId, result.SourceDocumentId);
        Assert.Equal(2, result.EditorLine);
    }

    [Fact]
    public void DuplicateFileNamesRemainDistinctByFullPath()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var map = new DebugSourceMap(Guid.NewGuid());
        map.Register("C:\\One\\same.ps1", firstId, 0, "C:\\One\\same.ps1", "one", false);
        map.Register("C:\\Two\\same.ps1", secondId, 0, "C:\\Two\\same.ps1", "two", false);

        Assert.Equal(firstId, map.Map("C:\\One\\same.ps1", 1, _ => 0).SourceDocumentId);
        Assert.Equal(secondId, map.Map("C:\\Two\\same.ps1", 1, _ => 0).SourceDocumentId);
    }

    [Fact]
    public void RevisionMismatchIsRejectedInsteadOfMappingIntoNewText()
    {
        var documentId = Guid.NewGuid();
        var map = new DebugSourceMap(Guid.NewGuid());
        map.Register("C:\\Scripts\\demo.ps1", documentId, 7, "C:\\Scripts\\demo.ps1", "old", false);

        var result = map.Map("C:\\Scripts\\demo.ps1", 50, _ => 8);

        Assert.Equal(DebugSourceMappingStatus.RevisionMismatch, result.Status);
        Assert.False(result.CanNavigate);
        Assert.Equal(7, result.CapturedRevision);
        Assert.Equal(8, result.CurrentRevision);
    }

    [Fact]
    public void UnknownAndInvalidatedRuntimeLocationsAreNeverNavigable()
    {
        var map = new DebugSourceMap(Guid.NewGuid());
        Assert.False(map.Map("C:\\Generated\\internal.ps1", 1).CanNavigate);
        Assert.Equal(DebugSourceMappingStatus.RuntimeGenerated, map.Map("C:\\Generated\\internal.ps1", 1).Status);

        map.Invalidate();

        Assert.Equal(DebugSourceMappingStatus.Invalidated, map.Map("C:\\Generated\\internal.ps1", 1).Status);
        Assert.False(map.Map("C:\\Generated\\internal.ps1", 1).CanNavigate);
    }

    [Fact]
    public void DocumentRevisionMismatchIsPerLogicalDocument()
    {
        var firstDocument = Guid.NewGuid();
        var secondDocument = Guid.NewGuid();
        var revisions = new Dictionary<Guid, long> { [firstDocument] = 2, [secondDocument] = 0 };
        var map = new DebugSourceMap(Guid.NewGuid());
        map.Register("C:\\Scripts\\first.ps1", firstDocument, 1, "C:\\Scripts\\first.ps1", "first", false);
        map.Register("C:\\Scripts\\second.ps1", secondDocument, 0, "C:\\Scripts\\second.ps1", "second", false);

        Assert.True(map.IsDocumentRevisionMismatch(firstDocument, id => revisions[id]));
        Assert.False(map.IsDocumentRevisionMismatch(secondDocument, id => revisions[id]));
    }
}
