using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Tests;

[Collection("DiagnosticReliability")]
public sealed class AppLoggerReliabilityTests
{
    private const string IsolatedChildEnvironmentVariable = "PS7SCRIPTDESK_APPLOGGER_TEST_CHILD";

    [Fact]
    public async Task InitialWriterDirectoryFailure_DegradesAndRoutesFailureEvidenceToEmergencySpool()
    {
        if (!IsIsolatedChild())
        {
            await RunInIsolatedProcessAsync();
            return;
        }

        var loggerType = typeof(AppLogger);
        var primaryDirectoryCreate = GetStaticField(loggerType, "_primaryDirectoryCreate");
        var emergencyDirectoryCreate = GetStaticField(loggerType, "_emergencyDirectoryCreate");
        var emergencyAppend = GetStaticField(loggerType, "_emergencyAppend");
        var loggerState = GetStaticField(loggerType, "_loggerState");
        var originalPrimaryDirectoryCreate = primaryDirectoryCreate.GetValue(null);
        var originalEmergencyDirectoryCreate = emergencyDirectoryCreate.GetValue(null);
        var originalEmergencyAppend = emergencyAppend.GetValue(null);
        var capturedEmergencyRecords = new ConcurrentQueue<string>();

        try
        {
            primaryDirectoryCreate.SetValue(null, (Action<string>)(_ => throw new IOException("Injected initial log-directory failure.")));
            emergencyDirectoryCreate.SetValue(null, (Action<string>)(_ => { }));
            emergencyAppend.SetValue(null, (Func<string, string, System.Text.Encoding, Task>)((_, text, _) =>
            {
                capturedEmergencyRecords.Enqueue(text);
                return Task.CompletedTask;
            }));

            var writerLoop = loggerType.GetMethod("WriterLoopAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            var exception = Record.Exception(() => Assert.IsAssignableFrom<Task>(writerLoop.Invoke(null, null)).GetAwaiter().GetResult());

            await WaitForAsync(() => capturedEmergencyRecords.Any(record => record.Contains("Injected initial log-directory failure.", StringComparison.Ordinal)));

            Assert.Null(exception);
            var loggerStateType = loggerType.GetNestedType("LoggerState", BindingFlags.NonPublic)!;
            var stateValue = Assert.IsType<int>(loggerState.GetValue(null));
            Assert.Equal("Degraded", Enum.GetName(loggerStateType, stateValue));
            Assert.Contains(capturedEmergencyRecords, record => record.Contains("Primary application log writer failed", StringComparison.Ordinal));
        }
        finally
        {
            primaryDirectoryCreate.SetValue(null, originalPrimaryDirectoryCreate);
            emergencyDirectoryCreate.SetValue(null, originalEmergencyDirectoryCreate);
            emergencyAppend.SetValue(null, originalEmergencyAppend);
        }
    }

    [Fact]
    public async Task EmergencySpoolWriterFailure_IsCountedWithoutThrowingOrRecursing()
    {
        if (!IsIsolatedChild())
        {
            await RunInIsolatedProcessAsync();
            return;
        }

        var loggerType = typeof(AppLogger);
        var emergencyDirectoryCreate = GetStaticField(loggerType, "_emergencyDirectoryCreate");
        var emergencyAppend = GetStaticField(loggerType, "_emergencyAppend");
        var persistenceFailures = GetStaticField(loggerType, "_emergencyPersistenceFailureCount");
        var originalEmergencyDirectoryCreate = emergencyDirectoryCreate.GetValue(null);
        var originalEmergencyAppend = emergencyAppend.GetValue(null);
        var startingFailures = (long)persistenceFailures.GetValue(null)!;

        try
        {
            emergencyDirectoryCreate.SetValue(null, (Action<string>)(_ => { }));
            emergencyAppend.SetValue(null, (Func<string, string, System.Text.Encoding, Task>)((_, _, _) =>
                Task.FromException(new IOException("Injected emergency spool append failure."))));

            var queueEmergency = loggerType.GetMethod("QueueEmergency", BindingFlags.NonPublic | BindingFlags.Static)!;
            var exception = Record.Exception(() => queueEmergency.Invoke(null, ["Injected emergency spool writer failure."]));

            await WaitForAsync(() => (long)persistenceFailures.GetValue(null)! > startingFailures);

            Assert.Null(exception);
            Assert.Equal(startingFailures + 1, (long)persistenceFailures.GetValue(null)!);
        }
        finally
        {
            emergencyDirectoryCreate.SetValue(null, originalEmergencyDirectoryCreate);
            emergencyAppend.SetValue(null, originalEmergencyAppend);
        }
    }

    [Fact]
    public async Task ConcurrentRejectedErrors_ArePersistedAsCompleteNoninterleavedEmergencyRecords()
    {
        if (!IsIsolatedChild())
        {
            await RunInIsolatedProcessAsync();
            return;
        }

        var loggerType = typeof(AppLogger);
        var emergencyDirectoryCreate = GetStaticField(loggerType, "_emergencyDirectoryCreate");
        var emergencyAppend = GetStaticField(loggerType, "_emergencyAppend");
        var originalEmergencyDirectoryCreate = emergencyDirectoryCreate.GetValue(null);
        var originalEmergencyAppend = emergencyAppend.GetValue(null);
        var persistedRecords = new ConcurrentQueue<string>();

        try
        {
            emergencyDirectoryCreate.SetValue(null, (Action<string>)(_ => { }));
            emergencyAppend.SetValue(null, (Func<string, string, System.Text.Encoding, Task>)((_, text, _) =>
            {
                persistedRecords.Enqueue(text);
                return Task.CompletedTask;
            }));

            var markers = Enumerable.Range(0, 24).Select(index => $"concurrent-emergency-record-{index:D2}").ToArray();
            var producerStopwatch = Stopwatch.StartNew();
            await Task.WhenAll(markers.Select(marker => Task.Run(() => RejectErrorThroughNormalSaturationPath(loggerType, marker))));
            producerStopwatch.Stop();
            await WaitForAsync(() => persistedRecords.Count >= markers.Length);

            Assert.True(producerStopwatch.Elapsed < TimeSpan.FromSeconds(1), "Concurrent producer calls blocked unexpectedly.");
            Assert.Equal(markers.Length, persistedRecords.Count);
            Assert.All(persistedRecords, record => Assert.Single(markers, marker => string.Equals(record, marker + Environment.NewLine, StringComparison.Ordinal)));
        }
        finally
        {
            emergencyDirectoryCreate.SetValue(null, originalEmergencyDirectoryCreate);
            emergencyAppend.SetValue(null, originalEmergencyAppend);
        }
    }

    [Fact]
    public async Task EmergencySpoolSaturation_IsBoundedAndCountsRejectedRecords()
    {
        if (!IsIsolatedChild())
        {
            await RunInIsolatedProcessAsync();
            return;
        }

        const int emergencyQueueCapacity = 32; // Provisional production bound; this test must not change it.
        var loggerType = typeof(AppLogger);
        var emergencyDirectoryCreate = GetStaticField(loggerType, "_emergencyDirectoryCreate");
        var emergencyAppend = GetStaticField(loggerType, "_emergencyAppend");
        var emergencyRejections = GetStaticField(loggerType, "_emergencyRejectionCount");
        var originalEmergencyDirectoryCreate = emergencyDirectoryCreate.GetValue(null);
        var originalEmergencyAppend = emergencyAppend.GetValue(null);
        var startingRejections = (long)emergencyRejections.GetValue(null)!;
        var appendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAppend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            emergencyDirectoryCreate.SetValue(null, (Action<string>)(_ => { }));
            emergencyAppend.SetValue(null, (Func<string, string, System.Text.Encoding, Task>)((_, _, _) =>
            {
                appendStarted.TrySetResult();
                return releaseAppend.Task;
            }));

            var queueEmergency = loggerType.GetMethod("QueueEmergency", BindingFlags.NonPublic | BindingFlags.Static)!;
            queueEmergency.Invoke(null, ["stall emergency writer"]);
            await appendStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var producerStopwatch = Stopwatch.StartNew();
            for (var index = 0; index < emergencyQueueCapacity + 5; index++)
            {
                queueEmergency.Invoke(null, [$"saturated-emergency-record-{index:D2}"]);
            }
            producerStopwatch.Stop();

            Assert.True(producerStopwatch.Elapsed < TimeSpan.FromSeconds(1), "Emergency saturation performed synchronous work on producer calls.");
            Assert.Equal(startingRejections + 5, (long)emergencyRejections.GetValue(null)!);
        }
        finally
        {
            releaseAppend.TrySetResult();
            emergencyDirectoryCreate.SetValue(null, originalEmergencyDirectoryCreate);
            emergencyAppend.SetValue(null, originalEmergencyAppend);
        }
    }

    [Fact]
    public async Task MainWriterDegradation_RoutesLaterErrorToEmergencyAndRejectsNormalIntake()
    {
        if (!IsIsolatedChild())
        {
            await RunInIsolatedProcessAsync();
            return;
        }

        var loggerType = typeof(AppLogger);
        var primaryAppend = GetStaticField(loggerType, "_primaryAppend");
        var emergencyDirectoryCreate = GetStaticField(loggerType, "_emergencyDirectoryCreate");
        var emergencyAppend = GetStaticField(loggerType, "_emergencyAppend");
        var loggerState = GetStaticField(loggerType, "_loggerState");
        var debugDrops = GetStaticField(loggerType, "_debugDropCount");
        var infoDrops = GetStaticField(loggerType, "_infoDropCount");
        var warningDrops = GetStaticField(loggerType, "_warningDropCount");
        var originalPrimaryAppend = primaryAppend.GetValue(null);
        var originalEmergencyDirectoryCreate = emergencyDirectoryCreate.GetValue(null);
        var originalEmergencyAppend = emergencyAppend.GetValue(null);
        var capturedEmergencyRecords = new ConcurrentQueue<string>();

        try
        {
            primaryAppend.SetValue(null, (Func<string, string, System.Text.Encoding, Task>)((_, _, _) =>
                Task.FromException(new IOException("Injected primary append failure."))));
            emergencyDirectoryCreate.SetValue(null, (Action<string>)(_ => { }));
            emergencyAppend.SetValue(null, (Func<string, string, System.Text.Encoding, Task>)((_, text, _) =>
            {
                capturedEmergencyRecords.Enqueue(text);
                return Task.CompletedTask;
            }));

            AppLogger.Info("ReliabilityTest", "Trigger primary writer degradation.");
            await WaitForAsync(() => IsLoggerState(loggerType, loggerState, "Degraded"));

            var startingDebugDrops = (long)debugDrops.GetValue(null)!;
            var startingInfoDrops = (long)infoDrops.GetValue(null)!;
            var startingWarningDrops = (long)warningDrops.GetValue(null)!;
            AppLogger.Debug("ReliabilityTest", "Rejected after degradation.");
            AppLogger.Info("ReliabilityTest", "Rejected after degradation.");
            AppLogger.Warning("ReliabilityTest", "Rejected after degradation.");
            AppLogger.Error("ReliabilityTest", "later-error-after-primary-degradation", new InvalidOperationException("later failure evidence"));

            await WaitForAsync(() => capturedEmergencyRecords.Any(record => record.Contains("later-error-after-primary-degradation", StringComparison.Ordinal)));

            Assert.Equal(startingDebugDrops + 1, (long)debugDrops.GetValue(null)!);
            Assert.Equal(startingInfoDrops + 1, (long)infoDrops.GetValue(null)!);
            Assert.Equal(startingWarningDrops + 1, (long)warningDrops.GetValue(null)!);
            Assert.Contains(capturedEmergencyRecords, record => record.Contains("later failure evidence", StringComparison.Ordinal));
        }
        finally
        {
            primaryAppend.SetValue(null, originalPrimaryAppend);
            emergencyDirectoryCreate.SetValue(null, originalEmergencyDirectoryCreate);
            emergencyAppend.SetValue(null, originalEmergencyAppend);
        }
    }

    [Fact]
    public async Task Shutdown_DrainsAcceptedEmergencyRecordsWithinBoundAndDoesNotReopenIntake()
    {
        if (!IsIsolatedChild())
        {
            await RunInIsolatedProcessAsync();
            return;
        }

        var loggerType = typeof(AppLogger);
        var emergencyDirectoryCreate = GetStaticField(loggerType, "_emergencyDirectoryCreate");
        var emergencyAppend = GetStaticField(loggerType, "_emergencyAppend");
        var emergencyRejections = GetStaticField(loggerType, "_emergencyRejectionCount");
        var originalEmergencyDirectoryCreate = emergencyDirectoryCreate.GetValue(null);
        var originalEmergencyAppend = emergencyAppend.GetValue(null);
        var releaseAppend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistedRecords = new ConcurrentQueue<string>();

        try
        {
            emergencyDirectoryCreate.SetValue(null, (Action<string>)(_ => { }));
            emergencyAppend.SetValue(null, (Func<string, string, System.Text.Encoding, Task>)(async (_, text, _) =>
            {
                await releaseAppend.Task;
                persistedRecords.Enqueue(text);
            }));

            var queueEmergency = loggerType.GetMethod("QueueEmergency", BindingFlags.NonPublic | BindingFlags.Static)!;
            queueEmergency.Invoke(null, ["shutdown-emergency-1"]);
            queueEmergency.Invoke(null, ["shutdown-emergency-2"]);
            queueEmergency.Invoke(null, ["shutdown-emergency-3"]);
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                releaseAppend.TrySetResult();
            });

            var shutdownStopwatch = Stopwatch.StartNew();
            AppLogger.Shutdown(TimeSpan.FromMilliseconds(250));
            shutdownStopwatch.Stop();

            var startingRejections = (long)emergencyRejections.GetValue(null)!;
            AppLogger.Error("ReliabilityTest", "Error after shutdown cutoff.", new InvalidOperationException("shutdown"));

            Assert.True(shutdownStopwatch.Elapsed < TimeSpan.FromMilliseconds(500), "Shutdown exceeded its bounded wait allowance.");
            Assert.Equal(3, persistedRecords.Count);
            Assert.Equal(startingRejections + 1, (long)emergencyRejections.GetValue(null)!);
        }
        finally
        {
            releaseAppend.TrySetResult();
            emergencyDirectoryCreate.SetValue(null, originalEmergencyDirectoryCreate);
            emergencyAppend.SetValue(null, originalEmergencyAppend);
        }
    }

    [Fact]
    public void OversizedErrorEntry_IsExplicitlyBoundedAndRetainsExceptionContext()
    {
        var method = typeof(AppLogger).GetMethod("BuildEntry", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var exception = new InvalidOperationException("reliability-test-exception");
        var entry = Assert.IsType<string>(method.Invoke(null, [
            AppLogLevel.Error,
            "ReliabilityTest",
            new string('x', 20_000),
            exception]));

        Assert.True(entry.Length <= 16_384);
        Assert.Contains("[Log entry truncated by the bounded logging safety policy.]", entry, StringComparison.Ordinal);
        Assert.Contains("ReliabilityTest", entry, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorLogging_RemainsNonThrowingAtThePublicBoundary()
    {
        var exception = Record.Exception(() => AppLogger.Error("ReliabilityTest", "Error path must remain nonthrowing.", new InvalidOperationException("test")));

        Assert.Null(exception);
    }

    private static FieldInfo GetStaticField(Type type, string name)
        => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"Missing AppLogger test seam '{name}'.");

    private static void RejectErrorThroughNormalSaturationPath(Type loggerType, string marker)
    {
        var entryType = loggerType.GetNestedType("LogEntry", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(entryType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, [AppLogLevel.Error, marker], culture: null)!;
        var rejectNormalEntry = loggerType.GetMethod("RejectNormalEntry", BindingFlags.NonPublic | BindingFlags.Static)!;
        rejectNormalEntry.Invoke(null, [entry]);
    }

    private static bool IsIsolatedChild()
        => string.Equals(Environment.GetEnvironmentVariable(IsolatedChildEnvironmentVariable), "1", StringComparison.Ordinal);

    private static async Task RunInIsolatedProcessAsync([CallerMemberName] string? testName = null)
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(repositoryRoot, "PS7ScriptDesk.Tests", "PS7ScriptDesk.Tests.csproj");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add($"FullyQualifiedName=PS7ScriptDesk.Tests.AppLoggerReliabilityTests.{testName}");
        startInfo.Environment[IsolatedChildEnvironmentVariable] = "1";
        startInfo.Environment["PSSTUDIO_LOG_LEVEL"] = "Debug";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start isolated AppLogger test process.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        Assert.True(process.ExitCode == 0, $"Isolated AppLogger test failed.\n{output}\n{error}");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PS7ScriptDesk.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the PS7 ScriptDesk repository root for an isolated AppLogger test.");
    }

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

        Assert.True(condition(), "Timed out waiting for the emergency spool to receive the injected failure record.");
    }

    private static bool IsLoggerState(Type loggerType, FieldInfo stateField, string expectedState)
    {
        var loggerStateType = loggerType.GetNestedType("LoggerState", BindingFlags.NonPublic)!;
        var stateValue = (int)stateField.GetValue(null)!;
        return string.Equals(Enum.GetName(loggerStateType, stateValue), expectedState, StringComparison.Ordinal);
    }
}
