using System.Reflection;
using System.Runtime.ExceptionServices;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class Phase4ReliabilityPolicyTests
{
    [Fact]
    public void ClipboardFailurePolicy_UsesIndependentPrivacySafeWarningEpisodes()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.Shell",
            "Controls",
            "TerminalControl.xaml.cs");
        var policyStart = source.IndexOf("private void LogClipboardFailure", StringComparison.Ordinal);
        var policyEnd = source.IndexOf("private void FlushOutputQueue", policyStart, StringComparison.Ordinal);
        Assert.True(policyStart >= 0 && policyEnd > policyStart);
        var policySource = source[policyStart..policyEnd];

        Assert.Contains("ClipboardCopy", source, StringComparison.Ordinal);
        Assert.Contains("ClipboardPasteRead", source, StringComparison.Ordinal);
        Assert.Contains("_clipboardCopyFailureEpisodeActive", source, StringComparison.Ordinal);
        Assert.Contains("_clipboardPasteReadFailureEpisodeActive", source, StringComparison.Ordinal);
        Assert.Contains("ResetClipboardFailureEpisode(ClipboardCopyOperation)", source, StringComparison.Ordinal);
        Assert.Contains("ResetClipboardFailureEpisode(ClipboardPasteReadOperation)", source, StringComparison.Ordinal);
        Assert.Contains("AppLogger.Warning(\"Terminal\", message)", policySource, StringComparison.Ordinal);
        Assert.Contains("DeveloperDiagnostics.LogWarning(\"Terminal\", message, metadata)", policySource, StringComparison.Ordinal);
        Assert.Contains("[\"exceptionType\"]", policySource, StringComparison.Ordinal);
        Assert.Contains("[\"hResult\"]", policySource, StringComparison.Ordinal);
        Assert.Contains("[\"contentOmitted\"] = true", policySource, StringComparison.Ordinal);
        Assert.DoesNotContain("exception.Message", policySource, StringComparison.Ordinal);
        Assert.DoesNotContain("LogException", policySource, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox", policySource, StringComparison.Ordinal);
        Assert.DoesNotContain("Focus", policySource, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry", policySource, StringComparison.Ordinal);
    }

    [Fact]
    public void ResizeFailureEpisode_SuppressesRepeatsAndSuccessResetsTheEpisode()
    {
        var episode = CreateResizeFailureEpisode();
        const int failure = unchecked((int)0x80070006);

        Assert.False(RecordResizeResult(episode, 4, 0));
        Assert.True(RecordResizeResult(episode, 4, failure));
        Assert.False(RecordResizeResult(episode, 4, failure));
        Assert.False(RecordResizeResult(episode, 4, 0));
        Assert.True(RecordResizeResult(episode, 4, failure));
    }

    [Fact]
    public void ResizeFailureEpisode_IsSessionScopedAndBoundsDistinctHresultWarnings()
    {
        var episode = CreateResizeFailureEpisode();
        const int firstFailure = unchecked((int)0x80070006);
        const int secondFailure = unchecked((int)0x80070057);
        const int thirdFailure = unchecked((int)0x80004005);

        Assert.True(RecordResizeResult(episode, 9, firstFailure));
        Assert.True(RecordResizeResult(episode, 9, secondFailure));
        Assert.False(RecordResizeResult(episode, 9, thirdFailure));
        Assert.False(RecordResizeResult(episode, 9, firstFailure));
        Assert.True(RecordResizeResult(episode, 10, firstFailure));
    }

    [Fact]
    public void ResizeResultPolicy_RequiresCurrentActiveSessionAndUsesEffectiveDimensions()
    {
        var source = ReadRepositoryFile(
            "PS7ScriptDesk.PowerShell",
            "Services",
            "LiveConsoleService.cs");

        Assert.Contains("var hResult = ResizePseudoConsole", source, StringComparison.Ordinal);
        Assert.Contains("if (hResult == 0)", source, StringComparison.Ordinal);
        Assert.Contains("ObserveResizeResult(\"ResizeHost\"", source, StringComparison.Ordinal);
        Assert.Contains("ObserveResizeResult(\"ResizeConsole\"", source, StringComparison.Ordinal);
        Assert.Contains("_terminalSessionTeardownInProgress", source, StringComparison.Ordinal);
        Assert.Contains("_terminalSessionGeneration == resizeRequest.SessionGeneration", source, StringComparison.Ordinal);
        Assert.Contains("_pseudoConsoleHandle == resizeRequest.PseudoConsole", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_process, resizeRequest.Process)", source, StringComparison.Ordinal);
        Assert.Contains("IsProcessRunningNoThrow(resizeRequest.Process)", source, StringComparison.Ordinal);
        Assert.Contains("new COORD((short)resizeRequest.Columns, (short)resizeRequest.Rows)", source, StringComparison.Ordinal);
        Assert.Contains("[\"hResultHex\"]", source, StringComparison.Ordinal);
        Assert.Contains("[\"contentOmitted\"] = true", source, StringComparison.Ordinal);
        var resizePolicyStart = source.IndexOf("private void ObserveResizeResult", StringComparison.Ordinal);
        var resizePolicyEnd = source.IndexOf("private bool IsCurrentResizeRequestNoLock", resizePolicyStart, StringComparison.Ordinal);
        Assert.True(resizePolicyStart >= 0 && resizePolicyEnd > resizePolicyStart);
        Assert.DoesNotContain("Marshal.GetLastWin32Error", source[resizePolicyStart..resizePolicyEnd], StringComparison.Ordinal);
    }

    private static object CreateResizeFailureEpisode()
    {
        var type = typeof(LiveConsoleService).GetNestedType("ResizeFailureEpisode", BindingFlags.NonPublic);
        Assert.NotNull(type);
        return Activator.CreateInstance(type, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create the private resize failure policy.");
    }

    private static bool RecordResizeResult(object episode, int sessionGeneration, int hResult)
    {
        var method = episode.GetType().GetMethod("RecordResult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);

        try
        {
            return Assert.IsType<bool>(method.Invoke(episode, [sessionGeneration, hResult]));
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static string ReadRepositoryFile(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "PS7ScriptDesk.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine([directory.FullName, .. relativeSegments]);
        Assert.True(File.Exists(path), $"Expected repository file was not found: {path}");
        return File.ReadAllText(path);
    }
}
