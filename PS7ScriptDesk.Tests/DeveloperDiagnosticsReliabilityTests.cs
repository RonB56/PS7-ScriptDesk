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
}
