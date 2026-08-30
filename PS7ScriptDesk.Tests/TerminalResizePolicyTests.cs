using PS7ScriptDesk.Shell.Controls;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalResizePolicyTests
{
    [Fact]
    public void InvalidGeometry_IsIgnoredWithoutAdvancingGeneration()
    {
        var policy = new TerminalResizePolicy();
        policy.Reset(rendererGeneration: 1);

        var zero = policy.Evaluate(0, 24, rendererGeneration: 1);
        var negative = policy.Evaluate(80, -1, rendererGeneration: 1);

        Assert.False(zero.Accepted);
        Assert.Equal("invalid-geometry", zero.Reason);
        Assert.False(negative.Accepted);
        Assert.Equal("invalid-geometry", negative.Reason);
        Assert.Equal(0, policy.ResizeGeneration);
    }

    [Fact]
    public void DuplicateGeometry_IsCoalescedAndChangedGeometryIsAccepted()
    {
        var policy = new TerminalResizePolicy();
        policy.Reset(rendererGeneration: 4);

        var first = policy.Evaluate(120, 30, rendererGeneration: 4);
        var duplicate = policy.Evaluate(120, 30, rendererGeneration: 4);
        var changed = policy.Evaluate(121, 30, rendererGeneration: 4);

        Assert.True(first.Accepted);
        Assert.Equal(1, first.ResizeGeneration);
        Assert.False(duplicate.Accepted);
        Assert.Equal("duplicate-geometry", duplicate.Reason);
        Assert.True(changed.Accepted);
        Assert.Equal(2, changed.ResizeGeneration);
    }

    [Fact]
    public void RendererRecreation_AllowsSameGeometryForReplacementRenderer()
    {
        var policy = new TerminalResizePolicy();
        policy.Reset(rendererGeneration: 1);
        Assert.True(policy.Evaluate(100, 25, rendererGeneration: 1).Accepted);

        var replacement = policy.Evaluate(100, 25, rendererGeneration: 2);

        Assert.True(replacement.Accepted);
        Assert.Equal(1, replacement.ResizeGeneration);
        Assert.Equal(100, replacement.Columns);
        Assert.Equal(25, replacement.Rows);
    }

    [Fact]
    public void GeometryChanges_DoNotFlushOrResetProtocolFilterCarry()
    {
        var policy = new TerminalResizePolicy();
        var filter = new TerminalProtocolOutputFilter();
        const string done = "##PSSTUDIO_EXEC_DONE_0123456789abcdef0123456789abcdef";

        var first = filter.Process("output\r\n" + done[..20]);
        var resize = policy.Evaluate(100, 25, rendererGeneration: 1);
        var second = filter.Process(done[20..] + "\r\nPS C:\\> ");

        Assert.True(resize.Accepted);
        Assert.Equal("output\r\n", first.VisibleText);
        Assert.Equal("PS C:\\> ", second.VisibleText);
        Assert.Equal(string.Empty, filter.Flush().VisibleText);
    }

    [Fact]
    public void ResizePath_OnlyReportsGeometryAndDoesNotSubmitOrFlushOutput()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");
        var resizeStart = source.IndexOf("case \"resize\":", StringComparison.Ordinal);
        var resizeEnd = source.IndexOf("break;", resizeStart, StringComparison.Ordinal);

        Assert.True(resizeStart >= 0);
        Assert.True(resizeEnd > resizeStart);
        var resizeBlock = source[resizeStart..resizeEnd];
        Assert.DoesNotContain("PostWebMessageAsString", resizeBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteRaw", resizeBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("_terminalProtocolOutputFilter.Flush", resizeBlock, StringComparison.Ordinal);
        Assert.Contains("outputSubmissionOccurred", resizeBlock, StringComparison.Ordinal);
        Assert.Contains("filterFlushOccurred", resizeBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserResizePath_CoalescesAnimationFramesAndIgnoresZeroBounds()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");

        Assert.Contains("var resizeFramePending = false", source, StringComparison.Ordinal);
        Assert.Contains("var resizeRequested = false", source, StringComparison.Ordinal);
        Assert.Contains("scheduleResizeFit", source, StringComparison.Ordinal);
        Assert.Contains("terminalElement.clientWidth <= 0 || terminalElement.clientHeight <= 0", source, StringComparison.Ordinal);
        Assert.Contains("if (e.cols === lastReportedCols && e.rows === lastReportedRows) return", source, StringComparison.Ordinal);
        Assert.Contains("if (!IsCurrentCoreWebView2(coreWebView2))", source, StringComparison.Ordinal);
        Assert.Contains("_terminalResizePolicy.Reset(_rendererInstanceGeneration)", source, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        var path = Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray());
        return File.ReadAllText(path);
    }
}
