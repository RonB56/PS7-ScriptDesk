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
    public void IfElseLoopAndTryBranchesProduceIndependentNestedRanges()
    {
        const string script = "if ($true) {\n  foreach ($item in 1) {\n    try {\n      $item\n    } catch {\n      0\n    } finally {\n      $null = $null\n    }\n  }\n} else {\n  0\n}";
        var ranges = _strategy.CreateFoldings(script);

        Assert.Contains(ranges, range => range.Name == "if branch");
        Assert.Contains(ranges, range => range.Name == "else branch");
        Assert.Contains(ranges, range => range.Name == "foreach loop");
        Assert.Contains(ranges, range => range.Name == "try block");
        Assert.Contains(ranges, range => range.Name == "catch block");
        Assert.Contains(ranges, range => range.Name == "finally block");
        AssertValidNestedRanges(script, ranges);
    }

    [Fact]
    public void ClassMethodsSwitchClausesAndNestedScriptBlocksAreFoldable()
    {
        const string script = "class Example {\n  [void] Run() {\n    $action = {\n      switch (1) {\n        1 {\n          'one'\n        }\n        default {\n          'other'\n        }\n      }\n    }\n  }\n}";
        var ranges = _strategy.CreateFoldings(script);

        Assert.Contains(ranges, range => range.Name == "class Example");
        Assert.Contains(ranges, range => range.Name == "method Run");
        Assert.Contains(ranges, range => range.Name == "switch");
        Assert.Contains(ranges, range => range.Name == "switch clause");
        Assert.Contains(ranges, range => range.Name == "switch default");
        Assert.Contains(ranges, range => range.Name == "script block");
        AssertValidNestedRanges(script, ranges);
    }

    [Fact]
    public void SingleLineSwitchClausesDoNotProduceIndividualFoldingRanges()
    {
        const string script = "switch (1) { 1 { 'one' } default { 'other' } }";
        var ranges = _strategy.CreateFoldings(script);

        Assert.DoesNotContain(ranges, range => range.Name == "switch clause");
        Assert.DoesNotContain(ranges, range => range.Name == "switch default");
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
    public void DeepNestingUsesPowerShellAstAndParserTokenExtents()
    {
        const int nesting = 160;
        var script = string.Concat(Enumerable.Repeat("{\n", nesting)) + "value" + string.Concat(Enumerable.Repeat("\n}", nesting));
        Assert.Equal(nesting, _strategy.CreateFoldings(script).Count);
    }

    [Fact]
    public void IncompleteStructuresDoNotInventClosingBoundaries()
    {
        Assert.Empty(_strategy.CreateFoldings("function Broken {\n  if ($true) {\n    'still typing'"));
        Assert.Empty(_strategy.CreateFoldings("if ($true)\n"));
    }

    [Fact]
    public void CanceledCalculationStopsBeforeParsing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => _strategy.CreateFoldings("{\n}", cts.Token));
    }

    [Fact]
    public void PerformanceWorkloadsReportLatencyDistributionWithoutTimingAssertions()
    {
        foreach (var lineCount in new[] { 100, 2_000, 10_000 })
        {
            var script = CreateScript(lineCount);
            var measurements = new List<double>(7);
            IReadOnlyList<FoldingRange> ranges = Array.Empty<FoldingRange>();
            for (var iteration = 0; iteration < 7; iteration++)
            {
                var stopwatch = Stopwatch.StartNew();
                ranges = _strategy.CreateFoldings(script);
                stopwatch.Stop();
                measurements.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
            measurements.Sort();
            Assert.NotEmpty(ranges);
            Console.WriteLine($"folding-lines={lineCount}; chars={script.Length}; ranges={ranges.Count}; iterations=7; median-ms={measurements[3]:F3}; p95-ms={measurements[6]:F3}; worst-ms={measurements[^1]:F3}");
        }
    }

    [Fact]
    public void EditorPipelineUsesSnapshotWorkerAndValidatesBeforeDispatcherApply()
    {
        var main = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var controller = TestRepositoryPaths.ReadFile("PS7ScriptDesk.Shell", "Editor", "EditorFoldingController.cs");
        var registrationStart = main.IndexOf("private void RegisterEditor(TextEditor editorTextEditor)", StringComparison.Ordinal);
        var registrationEnd = main.IndexOf("private int IncrementEditorRegistrationVersion", registrationStart, StringComparison.Ordinal);
        var registration = main[registrationStart..registrationEnd];
        Assert.Contains("_editorFoldingController.ScheduleFolding(editorTextEditor, debounceMilliseconds: 0)", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateFoldings(", registration, StringComparison.Ordinal);
        Assert.Contains("Task.Run(", main, StringComparison.Ordinal);
        Assert.Contains("session.CaptureSnapshot()", controller, StringComparison.Ordinal);
        Assert.Contains("session.IsRegistrationCurrent(snapshot)", controller, StringComparison.Ordinal);
        Assert.Contains("manager.UpdateFoldings(ranges.Select", controller, StringComparison.Ordinal);
        Assert.Contains("CancelPendingFolding(editor)", controller, StringComparison.Ordinal);
    }

    private static string CreateScript(int lineCount)
    {
        var builder = new StringBuilder(lineCount * 14);
        builder.AppendLine("function Measure-Folding {");
        for (var i = 0; i < lineCount; i++) builder.Append("  $v").Append(i).Append(" = ").Append(i).AppendLine();
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void AssertValidNestedRanges(string script, IReadOnlyList<FoldingRange> ranges)
    {
        Assert.All(ranges, range =>
        {
            Assert.InRange(range.StartOffset, 0, script.Length - 1);
            Assert.InRange(range.EndOffset, range.StartOffset + 1, script.Length);
            Assert.Contains('\n', script[range.StartOffset..range.EndOffset]);
        });

        for (var leftIndex = 0; leftIndex < ranges.Count; leftIndex++)
        for (var rightIndex = leftIndex + 1; rightIndex < ranges.Count; rightIndex++)
        {
            var left = ranges[leftIndex];
            var right = ranges[rightIndex];
            var overlaps = left.StartOffset < right.EndOffset && right.StartOffset < left.EndOffset;
            var nested = left.StartOffset <= right.StartOffset && right.EndOffset <= left.EndOffset ||
                         right.StartOffset <= left.StartOffset && left.EndOffset <= right.EndOffset;
            Assert.True(!overlaps || nested, $"Ranges cross: {left} and {right}.");
        }
    }
}
