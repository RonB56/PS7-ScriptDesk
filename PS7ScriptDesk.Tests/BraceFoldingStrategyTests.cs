using System.Diagnostics;
using System.Text;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class BraceFoldingStrategyTests
{
    private readonly BraceFoldingStrategy _strategy = new();

    [Fact]
    public void FunctionAndNestedControlBlocksProduceNestedRanges()
    {
        const string script = "function Get-Value {\n if ($true) {\n  1\n }\n}";
        var ranges = _strategy.CreateFoldings(script);
        Assert.Equal(2, ranges.Count);
        Assert.Equal(script.IndexOf('{'), ranges[0].StartOffset);
        Assert.Equal(script.LastIndexOf('}') + 1, ranges[0].EndOffset);
        Assert.True(ranges[1].StartOffset > ranges[0].StartOffset);
    }

    [Fact]
    public void BracesInStringsAndCommentsDoNotCreateFolds()
    {
        const string script = "function Real {\n  $single = '{' # }\n  $double = \"}\"\n  $escaped = \"a`\"}b\"\n  <# { ignored } #>\n  # { ignored\n}";
        var ranges = _strategy.CreateFoldings(script);
        Assert.Single(ranges);
        Assert.Equal(script.IndexOf('{'), ranges[0].StartOffset);
    }

    [Fact]
    public void HereStringsDoNotCreateFoldsAndNamedRegionsRemainFoldable()
    {
        const string script = "@'\n{\n'@\n#region Demo\nbody\n#endregion\n";
        var ranges = _strategy.CreateFoldings(script);
        var region = Assert.Single(ranges);
        Assert.Equal("#region Demo", region.Name);
        Assert.Equal(script.IndexOf("#region", StringComparison.Ordinal), region.StartOffset);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void MixedSupportedNewlineFormsAreRecognized(string newline)
    {
        var script = $"function F {{{newline}  1{newline}}}";
        Assert.Single(_strategy.CreateFoldings(script));
    }

    [Fact]
    public void IncompleteBracesAndIndependentRegionsAreHandled()
    {
        Assert.Empty(_strategy.CreateFoldings("function Broken {\n  1"));
        const string script = "function A {\n 1\n}\nfunction B {\n 2\n}";
        Assert.Equal(2, _strategy.CreateFoldings(script).Count);
    }

    [Fact]
    public void DeepNestingUsesStackBasedPairing()
    {
        const int nesting = 800;
        var script = string.Concat(Enumerable.Repeat("{\n", nesting)) + "value" + string.Concat(Enumerable.Repeat("\n}", nesting));
        Assert.Equal(nesting, _strategy.CreateFoldings(script).Count);
    }

    [Fact]
    public void CanceledCalculationStopsBeforeParsing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => _strategy.CreateFoldings("{\n}", cts.Token));
    }

    [Fact]
    public void PerformanceWorkloadsReportMeasurementsWithoutTimingAssertions()
    {
        foreach (var lineCount in new[] { 1_000, 10_000, 100_000 })
        {
            var script = CreateScript(lineCount);
            var measurements = new List<double>(3);
            IReadOnlyList<FoldingRange> ranges = Array.Empty<FoldingRange>();
            for (var iteration = 0; iteration < 3; iteration++)
            {
                var stopwatch = Stopwatch.StartNew();
                ranges = _strategy.CreateFoldings(script);
                stopwatch.Stop();
                measurements.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
            measurements.Sort();
            Assert.NotEmpty(ranges);
            Console.WriteLine($"folding-lines={lineCount}; chars={script.Length}; ranges={ranges.Count}; iterations=3; median-ms={measurements[1]:F3}; max-ms={measurements[2]:F3}");
        }
    }

    [Fact]
    public void EditorPipelineUsesSnapshotWorkerAndValidatesBeforeDispatcherApply()
    {
        var main = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var registrationStart = main.IndexOf("private void RegisterEditor(TextEditor editorTextEditor)", StringComparison.Ordinal);
        var registrationEnd = main.IndexOf("private int IncrementEditorRegistrationVersion", registrationStart, StringComparison.Ordinal);
        var registration = main[registrationStart..registrationEnd];
        Assert.Contains("ScheduleFolding(editorTextEditor)", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateFoldings(", registration, StringComparison.Ordinal);
        Assert.Contains("Task.Run(", main, StringComparison.Ordinal);
        Assert.Contains("session.CaptureSnapshot()", main, StringComparison.Ordinal);
        Assert.Contains("session.IsRegistrationCurrent(snapshot)", main, StringComparison.Ordinal);
        Assert.Contains("manager.UpdateFoldings(ranges.Select", main, StringComparison.Ordinal);
        Assert.Contains("CancelPendingFolding(editor)", main, StringComparison.Ordinal);
    }

    private static string CreateScript(int lineCount)
    {
        var builder = new StringBuilder(lineCount * 14);
        builder.AppendLine("function Measure-Folding {");
        for (var i = 0; i < lineCount; i++) builder.Append("  $v").Append(i).Append(" = ").Append(i).AppendLine();
        builder.AppendLine("}");
        return builder.ToString();
    }
}
