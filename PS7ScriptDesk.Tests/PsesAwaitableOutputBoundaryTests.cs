namespace PS7ScriptDesk.Tests;

public sealed class PsesAwaitableOutputBoundaryTests
{
    [Fact]
    public void AuthoritativeReader_AwaitsVisibleOutputAfterBackendPromptReadiness()
    {
        var source = ReadSource("PS7ScriptDesk.PowerShell", "Services", "PsesOwnedInteractiveSessionService.cs");
        var readerStart = source.IndexOf("private async Task ReadStreamAsync(", StringComparison.Ordinal);
        var readerEnd = source.IndexOf("private bool ObservePromptBoundary(", readerStart, StringComparison.Ordinal);
        Assert.True(readerStart >= 0 && readerEnd > readerStart);
        var reader = source[readerStart..readerEnd];

        var promptReady = reader.IndexOf("NotifyPromptReadyObserved(generation, text)", StringComparison.Ordinal);
        var publish = reader.IndexOf("await PublishOutputAsync(kind, generation, text", StringComparison.Ordinal);
        Assert.True(promptReady >= 0 && publish > promptReady);
        Assert.Contains("await PublishOutputAsync(kind, generation, trailingText)", reader, StringComparison.Ordinal);
    }

    [Fact]
    public void AwaitablePublisher_OwnsOneVisibleDeliveryAndDoesNotAlsoRaiseLegacyRawEvent()
    {
        var source = ReadSource("PS7ScriptDesk.PowerShell", "Services", "PsesOwnedInteractiveSessionService.cs");
        var start = source.IndexOf("private async ValueTask PublishVisibleOutputAsync(", StringComparison.Ordinal);
        var end = source.IndexOf("private static string FilterAndTraceEcho(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var publisher = source[start..end];

        Assert.Contains("await transportOwner(generation, kind, visibleText).ConfigureAwait(false)", publisher, StringComparison.Ordinal);
        Assert.Contains("PublishExecutionOutputRecord(kind, generation, visibleText)", publisher, StringComparison.Ordinal);
        Assert.Contains("if (transportOwner is not null)", publisher, StringComparison.Ordinal);
        Assert.Contains("return;", publisher, StringComparison.Ordinal);
        Assert.Contains("PublishVisibleOutput(kind, generation, visibleText)", publisher, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptReadinessIsObservedBeforeVisibleOutputFiltering()
    {
        var source = ReadSource("PS7ScriptDesk.PowerShell", "Services", "PsesOwnedInteractiveSessionService.cs");
        var readStream = source.IndexOf("private async Task ReadStreamAsync", StringComparison.Ordinal);
        var promptObserved = source.IndexOf("NotifyPromptReadyObserved(generation, text)", readStream, StringComparison.Ordinal);
        var publish = source.IndexOf("await PublishOutputAsync(kind, generation, text, performanceReadId).ConfigureAwait(false)", promptObserved, StringComparison.Ordinal);
        var publishMethod = source.IndexOf("private async ValueTask PublishOutputAsync(", StringComparison.Ordinal);
        var preFilterObservation = source.IndexOf("OutputObservedBeforeDisplayFiltering", publishMethod, StringComparison.Ordinal);
        var filtering = source.IndexOf("FilterAndTraceEcho", preFilterObservation, StringComparison.Ordinal);

        Assert.True(readStream >= 0 && promptObserved > readStream && publish > promptObserved);
        Assert.True(publishMethod > publish && preFilterObservation > publishMethod && filtering > preFilterObservation);
        Assert.Contains("native prompt/parser and request-readiness observation before", source[readStream..publish], StringComparison.Ordinal);
    }
    [Fact]
    public void AwaitableBoundary_IsOptionalAndImplementedByAuthoritativePsesServiceOnly()
    {
        var contract = ReadSource("PS7ScriptDesk.Application", "Interfaces", "IAwaitableTerminalOutputSource.cs");
        var pses = ReadSource("PS7ScriptDesk.PowerShell", "Services", "PsesOwnedInteractiveSessionService.cs");
        var legacy = ReadSource("PS7ScriptDesk.PowerShell", "Services", "LiveConsoleService.cs");

        Assert.Contains("event Func<int, ExecutionOutputStreamKind, string, ValueTask>? VisibleOutputPublishingAsync", contract, StringComparison.Ordinal);
        Assert.Contains("IAwaitableTerminalOutputSource", pses, StringComparison.Ordinal);
        Assert.DoesNotContain("IAwaitableTerminalOutputSource", legacy, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] pathParts)
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), Path.Combine(pathParts)));

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.slnx"))) return current.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the PS7ScriptDesk repository root.");
    }
}
