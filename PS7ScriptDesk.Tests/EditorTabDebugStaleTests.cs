using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class EditorTabDebugStaleTests
{
    [Fact]
    public void NormalTitleAndTooltipRemainUnchangedWithoutDebuggerState()
    {
        var tab = new EditorTabViewModel("Phase7-Unsaved-Snapshot.ps1", "Write-Host test");

        Assert.Equal("Phase7-Unsaved-Snapshot.ps1", tab.DisplayTitle);
        Assert.Equal("Unsaved document", tab.EditorTabToolTip);
        Assert.False(tab.IsDebugSourceStale);
    }

    [Fact]
    public void StaleCuePreservesFilenameAndDirtyIndicatorWithDescriptiveTooltip()
    {
        var tab = new EditorTabViewModel("Phase7-Unsaved-Snapshot.ps1", "old");
        tab.Content = "edited";

        Assert.True(tab.SetDebugSourceStale(true));
        Assert.Equal("Phase7-Unsaved-Snapshot.ps1*  ⚠ Debug Stale", tab.DisplayTitle);
        Assert.Contains("Debug Stale", tab.DisplayTitle, StringComparison.Ordinal);
        Assert.Contains("older source snapshot", tab.EditorTabToolTip, StringComparison.Ordinal);
        Assert.Contains("Restart debugging", tab.EditorTabToolTip, StringComparison.Ordinal);
        Assert.Equal("Phase7-Unsaved-Snapshot.ps1", tab.Title);
        Assert.Null(tab.FilePath);
    }

    [Fact]
    public void ClearingStaleCueRestoresNormalTitleAndDoesNotChangePath()
    {
        var tab = new EditorTabViewModel("script.ps1", "text", "C:\\Scripts\\script.ps1");
        var pathBefore = tab.FilePath;
        tab.SetDebugSourceStale(true);
        tab.SetDebugSourceStale(false);

        Assert.Equal("script.ps1", tab.DisplayTitle);
        Assert.Equal(pathBefore, tab.FilePath);
        Assert.DoesNotContain("Debug Stale", tab.EditorTabToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleCueIsIndependentPerDocument()
    {
        var first = new EditorTabViewModel("first.ps1", "one");
        var second = new EditorTabViewModel("second.ps1", "two");

        first.SetDebugSourceStale(true);

        Assert.Contains("Debug Stale", first.DisplayTitle, StringComparison.Ordinal);
        Assert.DoesNotContain("Debug Stale", second.DisplayTitle, StringComparison.Ordinal);
        Assert.False(second.IsDebugSourceStale);
    }

    [Fact]
    public void SavingClearsDirtyMarkerButPreservesDebuggerStaleCue()
    {
        var tab = new EditorTabViewModel("script.ps1", "old");
        tab.Content = "edited";
        tab.SetDebugSourceStale(true);

        tab.MarkSaved();

        Assert.False(tab.IsDirty);
        Assert.Equal("script.ps1  ⚠ Debug Stale", tab.DisplayTitle);
        Assert.True(tab.IsDebugSourceStale);
        Assert.Contains("older source snapshot", tab.EditorTabToolTip, StringComparison.Ordinal);
    }
}
