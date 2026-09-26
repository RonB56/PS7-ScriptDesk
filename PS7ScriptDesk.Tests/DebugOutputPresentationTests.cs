using PS7ScriptDesk.Shell.Debug;
using System.Diagnostics;

namespace PS7ScriptDesk.Tests;

public sealed class DebugOutputPresentationTests
{
    [Fact]
    public void Collection_EvictsOldestByCountAndTextFootprint()
    {
        var model = new DebugOutputPresentationModel(maximumItemCount: 3, maximumTextFootprint: 320);
        var sessionId = Guid.NewGuid();

        for (var index = 1; index <= 5; index++)
        {
            model.Append(Event(sessionId, index, $"message-{index}-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"));
        }

        Assert.True(model.Items.Count <= 3);
        Assert.True(model.RetainedTextFootprint <= 320);
        Assert.DoesNotContain(model.Items, item => item.Event?.Sequence == 1);
        Assert.Contains(model.Items, item => item.Event?.Sequence == 5);
    }

    [Fact]
    public void Filtering_HidesScriptOutputWithoutDiscardingIt()
    {
        var model = new DebugOutputPresentationModel();
        var sessionId = Guid.NewGuid();
        model.Append(Event(sessionId, 1, "lifecycle", DebuggerEventCategory.DebuggerLifecycle));
        model.Append(Event(sessionId, 2, "output", DebuggerEventCategory.NativeStdout));

        model.IncludeScriptOutput = false;

        Assert.Equal(2, model.Items.Count);
        Assert.Single(model.GetVisibleItems());
        Assert.Equal(DebuggerEventCategory.DebuggerLifecycle, model.GetVisibleItems()[0].Event!.Category);
    }

    [Fact]
    public void Export_UsesVisibleOrderAndNeverIncludesProtocolMarkers()
    {
        var model = new DebugOutputPresentationModel();
        var sessionId = Guid.NewGuid();
        model.Append(Event(sessionId, 1, "first", DebuggerEventCategory.DebuggerLifecycle));
        model.Append(Event(sessionId, 2, "__PSS_DEBUG_READY__", DebuggerEventCategory.Protocol));
        model.Append(Event(sessionId, 3, "second", DebuggerEventCategory.Error));

        var text = model.FormatVisibleItems();

        Assert.Contains("[DebuggerLifecycle]", text, StringComparison.Ordinal);
        Assert.Contains("[Error]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("__PSS_DEBUG_READY__", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("first", StringComparison.Ordinal) < text.IndexOf("second", StringComparison.Ordinal));
    }

    [Fact]
    public void Clear_RemovesPresentationRecordsAndAllowsLaterEvents()
    {
        var model = new DebugOutputPresentationModel();
        var sessionId = Guid.NewGuid();
        model.Append(Event(sessionId, 1, "before clear"));

        model.Clear();
        model.Append(Event(sessionId, 2, "after clear"));

        Assert.Single(model.Items);
        Assert.Equal("after clear", model.Items[0].DisplayText);
    }

    [Fact]
    public void PresentationItem_ExposesSafeNavigationAndDetails()
    {
        var item = DebugOutputItem.ForEvent(new DebuggerEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            1,
            DebuggerEventCategory.Breakpoint,
            DebuggerEventSeverity.Information,
            "Breakpoint",
            "Paused",
            filePath: @"C:\temp\script.ps1",
            lineNumber: 42,
            details: "frame details",
            isNavigable: true,
            isExpandable: true));

        Assert.True(item.IsNavigable);
        Assert.True(item.IsExpandable);
        Assert.Equal("script.ps1:42", item.LocationLabel);
        Assert.Equal("frame details", item.Details);
    }

    [Fact]
    public void SessionBoundary_IsRetainedAsFirstClassPresentationRecord()
    {
        var model = new DebugOutputPresentationModel();
        var sessionId = Guid.NewGuid();

        model.BeginSession(sessionId, @"C:\temp\script.ps1");

        Assert.Single(model.Items);
        Assert.True(model.Items[0].IsSessionBoundary);
        Assert.Contains(sessionId.ToString("N")[..8], model.Items[0].DisplayText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DebuggerEventCategory.NativeStdout, "Theme.Debugger.Output.Foreground")]
    [InlineData(DebuggerEventCategory.DebuggerLifecycle, "Theme.Debugger.Debugger.Foreground")]
    [InlineData(DebuggerEventCategory.Step, "Theme.Debugger.Debugger.Foreground")]
    [InlineData(DebuggerEventCategory.Breakpoint, "Theme.Debugger.Breakpoint.Foreground")]
    [InlineData(DebuggerEventCategory.Warning, "Theme.Debugger.Warning.Foreground")]
    [InlineData(DebuggerEventCategory.Error, "Theme.Debugger.Error.Foreground")]
    [InlineData(DebuggerEventCategory.Exception, "Theme.Debugger.Error.Foreground")]
    [InlineData(DebuggerEventCategory.Verbose, "Theme.Debugger.Verbose.Foreground")]
    [InlineData(DebuggerEventCategory.Debug, "Theme.Debugger.Debug.Foreground")]
    [InlineData(DebuggerEventCategory.Information, "Theme.Debugger.Information.Foreground")]
    public void CategoryBrushMapping_UsesCentralizedSemanticResource(
        DebuggerEventCategory category,
        string resourceKey)
    {
        Assert.Equal(resourceKey, DebugOutputCategoryBrushConverter.GetResourceKey(category));
    }

    [Fact]
    public void Stress_TenThousandEventsRemainBounded()
    {
        var model = new DebugOutputPresentationModel();
        var sessionId = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();

        for (var sequence = 1; sequence <= 10_000; sequence++)
        {
            model.Append(Event(sessionId, sequence, $"output-{sequence}"));
        }

        stopwatch.Stop();

        Assert.InRange(model.Items.Count, 1, 5000);
        Assert.True(model.RetainedTextFootprint <= DebugOutputPresentationModel.DefaultMaximumTextFootprint);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"10,000 event append took {stopwatch.Elapsed}.");
    }

    private static DebuggerEvent Event(
        Guid sessionId,
        long sequence,
        string text,
        DebuggerEventCategory category = DebuggerEventCategory.Information) =>
        new(
            sessionId,
            DateTimeOffset.UtcNow,
            sequence,
            category,
            DebuggerEventSeverity.Information,
            "Test",
            text);
}
