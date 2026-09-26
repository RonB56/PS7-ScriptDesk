using System.Reflection;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using Xunit;

namespace PS7ScriptDesk.Tests;

[Collection("DiagnosticReliability")]
public sealed class DeveloperDiagnosticsReliabilityTests
{
    [Fact]
    public async Task CategoryFileFailure_PreservesPrimaryEventStorage()
    {
        await AssertSecondaryArtifactFailureAsync(
            "Category file",
            path => path.EndsWith("ui-events.ndjson", StringComparison.OrdinalIgnoreCase),
            () => DeveloperDiagnostics.LogInfo("UI", "Injected category artifact failure."));
    }

    [Fact]
    public async Task ErrorsNdjsonFailure_PreservesPrimaryEventStorage()
    {
        await AssertSecondaryArtifactFailureAsync(
            "Errors NDJSON",
            path => path.EndsWith("errors.ndjson", StringComparison.OrdinalIgnoreCase),
            () => DeveloperDiagnostics.LogException("Reliability", new InvalidOperationException("Injected errors artifact failure."), "Exception should still reach primary storage."));
    }

    [Fact]
    public async Task ReadableLogFailure_PreservesPrimaryEventStorage()
    {
        await AssertSecondaryArtifactFailureAsync(
            "Readable log",
            path => path.EndsWith("developer-diagnostics-readable.log", StringComparison.OrdinalIgnoreCase),
            () => DeveloperDiagnostics.LogInfo("Reliability", "Injected readable artifact failure."));
    }

    [Fact]
    public async Task SummaryFailure_PreservesPrimaryEventStorage()
    {
        var diagnosticsType = typeof(DeveloperDiagnostics);
        var writeField = diagnosticsType.GetField("_writeAllText", BindingFlags.NonPublic | BindingFlags.Static)!;
        var originalWrite = writeField.GetValue(null);

        try
        {
            StartDiagnostics("Summary failure test");
            writeField.SetValue(null, (Action<string, string, System.Text.Encoding>)((path, text, encoding) =>
            {
                if (path.EndsWith("diagnostics-summary.txt", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Injected summary failure.");
                }

                File.WriteAllText(path, text, encoding);
            }));

            DeveloperDiagnostics.LogError("Reliability", "An Error forces a summary refresh.");
            await WaitForAsync(() => DeveloperDiagnostics.BuildSummaryText().Contains("Diagnostics Storage State: PrimaryWritableWithSecondaryFailures", StringComparison.Ordinal));

            Assert.Contains("Diagnostics Storage State: PrimaryWritableWithSecondaryFailures", DeveloperDiagnostics.BuildSummaryText(), StringComparison.Ordinal);
        }
        finally
        {
            writeField.SetValue(null, originalWrite);
            StopDiagnostics();
        }
    }

    [Fact]
    public void ManifestFailure_MarksOnlyTheManifestSidecarUnavailable()
    {
        var diagnosticsType = typeof(DeveloperDiagnostics);
        var writeField = diagnosticsType.GetField("_writeAllText", BindingFlags.NonPublic | BindingFlags.Static)!;
        var originalWrite = writeField.GetValue(null);

        try
        {
            writeField.SetValue(null, (Action<string, string, System.Text.Encoding>)((path, text, encoding) =>
            {
                if (path.EndsWith("session-manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Injected manifest failure.");
                }

                File.WriteAllText(path, text, encoding);
            }));

            StartDiagnostics("Manifest failure test");

            Assert.Contains("Diagnostics Storage State: PrimaryWritableWithSecondaryFailures", DeveloperDiagnostics.BuildSummaryText(), StringComparison.Ordinal);
        }
        finally
        {
            writeField.SetValue(null, originalWrite);
            StopDiagnostics();
        }
    }

    [Fact]
    public void LatestSessionPointerFailure_MarksOnlyThePointerSidecarUnavailable()
    {
        var diagnosticsType = typeof(DeveloperDiagnostics);
        var writeField = diagnosticsType.GetField("_writeAllText", BindingFlags.NonPublic | BindingFlags.Static)!;
        var originalWrite = writeField.GetValue(null);

        try
        {
            writeField.SetValue(null, (Action<string, string, System.Text.Encoding>)((path, text, encoding) =>
            {
                if (path.EndsWith("latest-session.txt", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Injected latest-session pointer failure.");
                }

                File.WriteAllText(path, text, encoding);
            }));

            StartDiagnostics("Pointer failure test");

            Assert.Contains("Diagnostics Storage State: PrimaryWritableWithSecondaryFailures", DeveloperDiagnostics.BuildSummaryText(), StringComparison.Ordinal);
        }
        finally
        {
            writeField.SetValue(null, originalWrite);
            StopDiagnostics();
        }
    }

    [Fact]
    public async Task PrimaryNdjsonFailure_DisablesOnlyCoreStorage_WithoutThrowingToCaller()
    {
        var diagnosticsType = typeof(DeveloperDiagnostics);
        var appendField = diagnosticsType.GetField("_appendLine", BindingFlags.NonPublic | BindingFlags.Static)!;
        var originalAppend = appendField.GetValue(null);

        try
        {
            DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings { IsDeveloperDiagnosticsEnabled = true }, "Reliability test");
            appendField.SetValue(null, (Action<string, string>)((path, line) =>
            {
                if (path.EndsWith("developer-diagnostics.ndjson", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Injected primary NDJSON failure.");
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }));

            var exception = Record.Exception(() => DeveloperDiagnostics.LogInfo("Reliability", "Primary persistence failure must remain nonthrowing."));
            await Task.Delay(250);
            var summary = DeveloperDiagnostics.BuildSummaryText();

            Assert.Null(exception);
            Assert.Contains("Diagnostics Storage State: StorageDisabled", summary, StringComparison.Ordinal);
        }
        finally
        {
            appendField.SetValue(null, originalAppend);
            DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings(), "Reliability test cleanup");
        }
    }

    [Fact]
    public async Task HighRateUiForensics_ArePersistedWithoutQueueDrops()
    {
        StartDiagnostics("High-rate UI forensics persistence test");
        var sessionDirectory = DeveloperDiagnostics.CurrentSessionDirectory!;

        try
        {
            for (var index = 0; index < 600; index++)
            {
                DeveloperDiagnostics.LogInfo(
                    "UI",
                    "Synthetic debug splitter target telemetry.",
                    new Dictionary<string, object?>
                    {
                        ["horizontalChange"] = index + 1,
                        ["previousTargetWidth"] = 637d,
                        ["nextTargetWidth"] = 322d,
                        ["debugSplitterIsDragging"] = true
                    });
            }
        }
        finally
        {
            StopDiagnostics();
        }

        var json = await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "ui-events.ndjson"));
        var persistedCount = CountOccurrences(json, "Synthetic debug splitter target telemetry.");

        Assert.Equal(600, persistedCount);
        Assert.Contains("Dropped Diagnostic Events: 0", DeveloperDiagnostics.BuildSummaryText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CriticalForensicsRemainLosslessDuringConcurrentGeneralEventFlood()
    {
        DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings
        {
            IsDeveloperDiagnosticsEnabled = true,
            DeveloperDiagnosticsWriteJsonLines = true,
            DeveloperDiagnosticsWriteReadableLog = false
        }, "Critical forensic stress test");
        var sessionDirectory = DeveloperDiagnostics.CurrentSessionDirectory!;

        try
        {
            for (var index = 0; index < 10_000; index++)
            {
                DeveloperDiagnostics.LogInfo("Noise", "Synthetic high-rate input noise.", new Dictionary<string, object?> { ["index"] = index });
                if (index < 500)
                {
                    DeveloperDiagnostics.LogCriticalForensic(
                        "UI",
                        "Synthetic.DebugSplitterDragDelta",
                        "Synthetic critical debug splitter target telemetry.",
                        new Dictionary<string, object?>
                        {
                            ["phase"] = index % 3 == 0 ? "Entry" : index % 3 == 1 ? "AfterHandler" : "AfterDispatcher",
                            ["horizontalChange"] = index + 1,
                            ["verticalChange"] = 0d,
                            ["targetPreviousIdentity"] = 1001,
                            ["targetNextIdentity"] = 1002,
                            ["previousTargetWidthValue"] = 637d,
                            ["nextTargetWidthValue"] = 322d,
                            ["previousTargetActualWidth"] = 637d,
                            ["nextTargetActualWidth"] = 322d,
                            ["debugSplitterIsDragging"] = true
                        });
                }
            }
        }
        finally
        {
            DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings(), "Critical forensic stress test cleanup");
        }

        var criticalPath = Path.Combine(sessionDirectory, "critical-ui-forensics.ndjson");
        var criticalJson = await File.ReadAllTextAsync(criticalPath);
        var summary = await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "diagnostics-summary.txt"));

        Assert.Equal(500, CountOccurrences(criticalJson, "Synthetic critical debug splitter target telemetry."));
        Assert.Contains("Critical Forensic Events Accepted: 500", summary, StringComparison.Ordinal);
        Assert.Contains("Critical Forensic Events Written: 500", summary, StringComparison.Ordinal);
        Assert.Contains("Critical Forensic Events Dropped: 0", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CriticalForensics_SanitizeNonFiniteMeasurementValues()
    {
        StartDiagnostics("Critical non-finite measurement test");
        var sessionDirectory = DeveloperDiagnostics.CurrentSessionDirectory!;

        try
        {
            DeveloperDiagnostics.LogCriticalForensic(
                "UI",
                "Synthetic.NonFiniteMeasurement",
                "Synthetic critical measurement.",
                new Dictionary<string, object?>
                {
                    ["actualWidth"] = double.NaN,
                    ["actualHeight"] = double.PositiveInfinity
                });
        }
        finally
        {
            StopDiagnostics();
        }

        var criticalJson = await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "critical-ui-forensics.ndjson"));
        var summary = await File.ReadAllTextAsync(Path.Combine(sessionDirectory, "diagnostics-summary.txt"));
        Assert.Contains("Synthetic critical measurement.", criticalJson, StringComparison.Ordinal);
        Assert.Contains("NaN", criticalJson, StringComparison.Ordinal);
        Assert.Contains("Infinity", criticalJson, StringComparison.Ordinal);
        Assert.Contains("Critical Forensic Events Written: 1", summary, StringComparison.Ordinal);
        Assert.Contains("Critical Forensic Events Dropped: 0", summary, StringComparison.Ordinal);
    }

    private static async Task AssertSecondaryArtifactFailureAsync(string artifactName, Func<string, bool> shouldFail, Action writeEvent)
    {
        var diagnosticsType = typeof(DeveloperDiagnostics);
        var appendField = diagnosticsType.GetField("_appendLine", BindingFlags.NonPublic | BindingFlags.Static)!;
        var originalAppend = appendField.GetValue(null);

        try
        {
            StartDiagnostics($"{artifactName} failure test");
            appendField.SetValue(null, (Action<string, string>)((path, line) =>
            {
                if (shouldFail(path))
                {
                    throw new IOException($"Injected {artifactName} failure.");
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }));

            writeEvent();
            await WaitForAsync(() => DeveloperDiagnostics.BuildSummaryText().Contains("Diagnostics Storage State: PrimaryWritableWithSecondaryFailures", StringComparison.Ordinal));

            Assert.Contains("Diagnostics Storage State: PrimaryWritableWithSecondaryFailures", DeveloperDiagnostics.BuildSummaryText(), StringComparison.Ordinal);
        }
        finally
        {
            appendField.SetValue(null, originalAppend);
            StopDiagnostics();
        }
    }

    private static void StartDiagnostics(string reason)
        => DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings
        {
            IsDeveloperDiagnosticsEnabled = true,
            DeveloperDiagnosticsWriteJsonLines = true,
            DeveloperDiagnosticsWriteReadableLog = true
        }, reason);

    private static void StopDiagnostics()
        => DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings(), "Reliability test cleanup");

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.True(condition(), "Expected diagnostics state transition was not observed within one second.");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
