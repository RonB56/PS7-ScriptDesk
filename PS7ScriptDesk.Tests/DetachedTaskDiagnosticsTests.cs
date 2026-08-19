using System.Reflection;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell;

namespace PS7ScriptDesk.Tests;

[Collection("DiagnosticReliability")]
public sealed class DetachedTaskDiagnosticsTests
{
    [Fact]
    public async Task FaultedDetachedTask_PreservesAggregateExceptionAndWritesOneDurableRecordPerSink()
    {
        var operationName = $"detached-task-{Guid.NewGuid():N}";
        StartDiagnostics();
        try
        {
            var aggregate = new AggregateException(
                new InvalidOperationException("first detached fault"),
                new ObjectDisposedException("editor", "second detached fault"));

            await ObserveAsync(Task.FromException(aggregate), operationName);

            var diagnosticLog = await WaitForDiagnosticLogAsync(operationName);
            await WaitForAsync(() => File.Exists(AppLogger.CurrentLogPath) &&
                                     File.ReadAllText(AppLogger.CurrentLogPath).Contains(operationName, StringComparison.Ordinal));
            var appLog = File.ReadAllText(AppLogger.CurrentLogPath);

            Assert.Contains("AggregateException", diagnosticLog, StringComparison.Ordinal);
            Assert.Contains("aggregateInnerExceptionCount", diagnosticLog, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException,ObjectDisposedException", diagnosticLog, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(diagnosticLog, operationName));
            Assert.Contains(operationName, appLog, StringComparison.Ordinal);

            var disposedOperationName = $"disposed-detached-task-{Guid.NewGuid():N}";
            await ObserveAsync(Task.FromException(new ObjectDisposedException("editor")), disposedOperationName);
            var disposedDiagnosticLog = await WaitForDiagnosticLogAsync(disposedOperationName);
            Assert.Contains("ObjectDisposedException", disposedDiagnosticLog, StringComparison.Ordinal);
            Assert.Contains(disposedOperationName, disposedDiagnosticLog, StringComparison.Ordinal);

            var canceledOperationName = $"canceled-detached-task-{Guid.NewGuid():N}";
            var canceledObserver = Observe(Task.FromCanceled(new CancellationToken(canceled: true)), canceledOperationName);
            Assert.True(canceledObserver.IsCanceled);
            await Task.Delay(100);
            Assert.DoesNotContain(canceledOperationName, File.ReadAllText(Path.Combine(
                DeveloperDiagnostics.CurrentSessionDirectory!,
                "developer-diagnostics.ndjson")), StringComparison.Ordinal);
        }
        finally
        {
            StopDiagnostics();
        }
    }

    private static Task Observe(Task task, string operationName)
    {
        var observer = typeof(MainWindow).GetMethod(
            "ObserveFireAndForget",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(observer);
        return Assert.IsAssignableFrom<Task>(observer.Invoke(null, [task, operationName, null]));
    }

    private static async Task ObserveAsync(Task task, string operationName)
        => await Observe(task, operationName);

    private static async Task<string> WaitForDiagnosticLogAsync(string operationName)
    {
        var path = Path.Combine(DeveloperDiagnostics.CurrentSessionDirectory!, "developer-diagnostics.ndjson");
        await WaitForAsync(() => File.Exists(path) && File.ReadAllText(path).Contains(operationName, StringComparison.Ordinal));
        return File.ReadAllText(path);
    }

    private static void StartDiagnostics()
    {
        // Session folders are timestamped to the second. Keep isolated test sessions from
        // reusing a just-closed folder in the same test process.
        Thread.Sleep(1100);
        DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings
        {
            IsDeveloperDiagnosticsEnabled = true,
            DeveloperDiagnosticsWriteJsonLines = true,
            DeveloperDiagnosticsWriteReadableLog = false
        }, "Detached task diagnostics test");
    }

    private static void StopDiagnostics()
        => DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings(), "Detached task diagnostics test cleanup");

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("Timed out waiting for durable diagnostics.");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
