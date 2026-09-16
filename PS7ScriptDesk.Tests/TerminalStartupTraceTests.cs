using System.IO;
using System.Text.RegularExpressions;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalStartupTraceTests
{
    [Fact]
    public void Trace_PersistsCorrelatedMilestonesWithoutPayloadContent()
    {
        TerminalStartupTrace.Start("test startup");
        TerminalStartupTrace.Write("TEST_GUARD", "reason=already-running; contentOmitted=true");
        TerminalStartupTrace.FirstRead("chars=12");
        TerminalStartupTrace.FirstRead("chars=99");
        TerminalStartupTrace.FirstNonEmptyOutput("chars=12; contentOmitted=true");
        TerminalStartupTrace.FirstRendererOutput("chars=12; contentOmitted=true");

        var path = TerminalStartupTrace.CurrentTracePath;
        Assert.True(File.Exists(path), $"Expected startup trace at {path}.");
        var lines = File.ReadAllLines(path);
        Assert.Contains(lines, line => line.Contains("Stage=TEST_GUARD", StringComparison.Ordinal));
        Assert.Single(lines, line => line.Contains("Stage=CONPTY_FIRST_READ", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Stage=CONPTY_FIRST_NONEMPTY_OUTPUT", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Stage=FIRST_TERMINAL_OUTPUT_TO_RENDERER", StringComparison.Ordinal));

        var ids = lines
            .Select(line => Regex.Match(line, @"StartupAttemptId=(?<id>[0-9a-f]+)").Groups["id"].Value)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Single(ids);
        Assert.DoesNotContain(lines, line => line.Contains("test secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Trace_SourceDeclaresBoundedRetentionAndRequiredEvidenceHooks()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.Application", "Diagnostics", "TerminalStartupTrace.cs"));
        Assert.Contains("MaximumFileBytes = 512 * 1024", source, StringComparison.Ordinal);
        Assert.Contains("MaximumRetainedTraces = 10", source, StringComparison.Ordinal);
        Assert.Contains("File.AppendAllText", source, StringComparison.Ordinal);
        Assert.Contains("Replace('\\r', ' ').Replace('\\n', ' ')", source, StringComparison.Ordinal);
        Assert.Contains("StartupAttemptId=", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherCheckAccess=", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.slnx"))) return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
