namespace PS7ScriptDesk.Tests;

public sealed class IseUsabilityRefinementCharacterizationTests
{
    [Fact]
    public void SyntaxColorizer_IsParserFirstWithRegexFallback()
    {
        var colorizerSource = ReadRepositoryFile("PS7ScriptDesk.Shell", "Editor", "PowerShellSyntaxColorizer.cs");
        var diagnosticsSource = ReadRepositoryFile("PS7ScriptDesk.Shell", "Editor", "InProcessPowerShellSyntaxDiagnosticsService.cs");
        var mainWindowSource = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("Parser.ParseInput(normalizedScriptText, out parserTokens, out parserErrors);", diagnosticsSource, StringComparison.Ordinal);
        Assert.Contains("colorizer.SetParserTokens(syntaxTokens);", mainWindowSource, StringComparison.Ordinal);
        Assert.True(
            colorizerSource.IndexOf("ApplyParserTokens(lineOffset, lineEnd, occupied);", StringComparison.Ordinal) <
            colorizerSource.IndexOf("ApplyRegexFallbackTokens(lineText, lineOffset, occupied);", StringComparison.Ordinal));
        Assert.Contains("Fallback mode: uses lightweight regex rules", colorizerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveConsole_DisplayPathPreservesRawConptyOutputForXterm()
    {
        var liveConsoleSource = ReadRepositoryFile("PS7ScriptDesk.PowerShell", "Services", "LiveConsoleService.cs");

        Assert.Contains("Strip only null bytes; preserve all ANSI/VT100 sequences", liveConsoleSource, StringComparison.Ordinal);
        Assert.Contains("rawHandler(observedSessionGeneration ?? _terminalSessionGeneration, raw);", liveConsoleSource, StringComparison.Ordinal);
        Assert.Contains("FilterInternalTerminalOutput(raw, out var hasSentinel, observedSessionGeneration)", liveConsoleSource, StringComparison.Ordinal);
        Assert.DoesNotContain("raw = raw.Replace(\"\\r\\n\\r\\n\", \"\\r\\n\", StringComparison.Ordinal);", liveConsoleSource, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments)
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), Path.Combine(segments)));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PowerShellStudio repository root.");
    }
}
