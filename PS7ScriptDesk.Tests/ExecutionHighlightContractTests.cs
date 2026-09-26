using PS7ScriptDesk.Shell.Debug;

namespace PS7ScriptDesk.Tests;

public sealed class ExecutionHighlightContractTests
{
    [Fact]
    public void RevisionMismatchRemainsNonNavigableAfterRuntimePauseMoves()
    {
        var map = new DebugSourceMap(Guid.NewGuid());
        var documentId = Guid.NewGuid();
        map.Register("C:\\Temp\\snapshot.ps1", documentId, 4, "C:\\Temp\\editor.ps1", "old", isSnapshot: true);

        var firstPause = map.Map("C:\\Temp\\snapshot.ps1", 6, _ => 4);
        var editedPause = map.Map("C:\\Temp\\snapshot.ps1", 7, _ => 5);

        Assert.True(firstPause.CanNavigate);
        Assert.Equal(DebugSourceMappingStatus.RevisionMismatch, editedPause.Status);
        Assert.False(editedPause.CanNavigate);
        Assert.Equal(7, editedPause.RuntimeLine);
        Assert.Equal(7, editedPause.EditorLine);
    }
}
