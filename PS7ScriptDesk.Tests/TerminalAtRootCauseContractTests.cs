namespace PS7ScriptDesk.Tests;

public sealed class TerminalAtRootCauseContractTests
{
    [Fact]
    public void RendererOnlyClearUsesSingleXtermResetOperation()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.Shell", "Controls", "TerminalControl.xaml.cs");

        Assert.Contains("try { term.reset(); }", source, StringComparison.Ordinal);
        Assert.DoesNotContain("term.write('\\x1b[2J\\x1b[3J\\x1b[H')", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearPrefersAuthoritativeInputTransportOverRendererFallback()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("if (_liveConsoleService.IsInputWritable)", source, StringComparison.Ordinal);
        Assert.Contains("Authoritative terminal clear failed.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredCompletionWaitsForPostEvaluateIdleSynchronization()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.PowerShell", "Services", "PsesLanguageServiceClient.cs");

        Assert.Contains("WaitForPostEvaluateIdleAsync", source, StringComparison.Ordinal);
        Assert.Contains("_lastIdleNotificationSequence", source, StringComparison.Ordinal);
        Assert.Contains("evaluationStartNotificationSequence", source, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(segments).ToArray()));
    }
}
