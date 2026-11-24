using System.Text;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

[Collection("DiagnosticReliability")]
public sealed class TerminalCriticalTraceTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk.TerminalCriticalTraceTests-{Guid.NewGuid():N}");
    private readonly string _tracePath;

    public TerminalCriticalTraceTests()
    {
        Directory.CreateDirectory(_testDirectory);
        _tracePath = Path.Combine(_testDirectory, "TerminalCriticalTrace.log");
        TerminalCriticalTrace.ConfigureForTests(
            _tracePath,
            uiThreadSnapshotProvider: () => new TerminalCriticalUiThreadSnapshot(false, 1234));
    }

    public void Dispose()
    {
        TerminalCriticalTrace.ResetForTests();
        try
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void LogException_PersistsExceptionToStringInnerExceptionThreadAndStage()
    {
        var exception = CaptureNestedException();

        TerminalCriticalTrace.LogException(
            "ConPTY.Reader.FatalException",
            exception,
            new Dictionary<string, object?>
            {
                ["terminalSessionGeneration"] = 7,
                ["rendererGeneration"] = 11,
                ["brokerSessionGeneration"] = 13,
                ["sequence"] = 17,
                ["pipelineStage"] = "reader-outer-catch"
            });

        Assert.True(TerminalCriticalTrace.FlushForTests());
        var trace = File.ReadAllText(_tracePath);
        Assert.Contains("stage=ConPTY.Reader.FatalException", trace, StringComparison.Ordinal);
        Assert.Contains("managedThreadId=", trace, StringComparison.Ordinal);
        Assert.Contains("apartmentState=", trace, StringComparison.Ordinal);
        Assert.Contains("uiDispatcherAccess=False", trace, StringComparison.Ordinal);
        Assert.Contains("uiDispatcherThreadId=1234", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.terminalSessionGeneration=7", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.rendererGeneration=11", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.brokerSessionGeneration=13", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.sequence=17", trace, StringComparison.Ordinal);
        Assert.Contains("exception.type=System.InvalidOperationException", trace, StringComparison.Ordinal);
        Assert.Contains("outer diagnostic exception", trace, StringComparison.Ordinal);
        Assert.Contains("inner diagnostic exception", trace, StringComparison.Ordinal);
        Assert.Contains(nameof(CaptureNestedException), trace, StringComparison.Ordinal);
    }

    [Fact]
    public void LogStage_RedactsUserContentMetadataButKeepsShapeFields()
    {
        TerminalCriticalTrace.LogStage(
            "LiveConsole.PublishTerminalChunkForSession.Filtered",
            new Dictionary<string, object?>
            {
                ["rawOutput"] = "secret terminal output",
                ["scriptText"] = "secret script text",
                ["outputCharacterLength"] = 22,
                ["appDataRoot"] = @"C:\Users\example\AppData\Local\PS7ScriptDesk"
            });

        Assert.True(TerminalCriticalTrace.FlushForTests());
        var trace = File.ReadAllText(_tracePath);
        Assert.DoesNotContain("secret terminal output", trace, StringComparison.Ordinal);
        Assert.DoesNotContain("secret script text", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.rawOutput=[omitted]", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.scriptText=[omitted]", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.outputCharacterLength=22", trace, StringComparison.Ordinal);
        Assert.Contains(@"metadata.appDataRoot=C:\Users\example\AppData\Local\PS7ScriptDesk", trace, StringComparison.Ordinal);
    }

    [Fact]
    public void LogStage_BoundsTraceEntryAndRotatesTraceFile()
    {
        var captured = new List<string>();
        var moved = false;
        TerminalCriticalTrace.ConfigureForTests(
            _tracePath,
            createDirectory: _ => { },
            appendAllText: (_, text, _) => captured.Add(text),
            fileExists: _ => true,
            fileLength: _ => 2 * 1024 * 1024,
            deleteFile: _ => { },
            moveFile: (_, _) => moved = true);

        TerminalCriticalTrace.LogStage(
            "Diagnostic.Bounded",
            new Dictionary<string, object?>
            {
                ["diagnosticNote"] = new string('x', 100_000)
            });

        Assert.True(TerminalCriticalTrace.FlushForTests());
        var entry = Assert.Single(captured);
        Assert.True(moved);
        Assert.True(entry.Length < 70_000);
        Assert.Contains("Terminal critical trace entry truncated", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void LoggingFailure_DoesNotEscapeToTerminalReaderSubscriberDispatch()
    {
        TerminalCriticalTrace.ConfigureForTests(
            _tracePath,
            createDirectory: _ => { },
            appendAllText: (_, _, _) => throw new IOException("injected trace sink failure"),
            fileExists: _ => false,
            fileLength: _ => 0);
        using var service = new LiveConsoleService();
        var laterSubscriberCalled = false;
        service.RawOutputReceived += ThrowingRawOutputSubscriber;
        service.RawOutputReceived += (_, _) => laterSubscriberCalled = true;

        var exception = Record.Exception(() => Publish(service, "PS C:\\> "));

        Assert.Null(exception);
        Assert.True(laterSubscriberCalled);
        Assert.True(TerminalCriticalTrace.FlushForTests());
        Assert.True(TerminalCriticalTrace.Health.WriteFailures > 0);
    }

    [Fact]
    public void RawOutputSubscriberFailure_CapturesSubscriberIdentityAndPreDispatcherStage()
    {
        using var service = new LiveConsoleService();
        service.RawOutputReceived += ThrowingRawOutputSubscriber;

        Publish(service, "PS C:\\> ");

        Assert.True(TerminalCriticalTrace.FlushForTests());
        var trace = File.ReadAllText(_tracePath);
        Assert.Contains("stage=LiveConsole.RawOutputReceivedSubscriber.Exception", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.subscriberDeclaringType=PS7ScriptDesk.Tests.TerminalCriticalTraceTests", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.subscriberMethod=ThrowingRawOutputSubscriber", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.terminalSessionGeneration=", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.outputCharacterLength=", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.uiDispatcherTransition=before-dispatcher-marshal", trace, StringComparison.Ordinal);
        Assert.Contains("subscriber thread-affinity failure", trace, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupIdentity_CapturesBinaryIdentityFields()
    {
        TerminalCriticalTrace.LogStartupIdentity(
            new Dictionary<string, object?>());

        Assert.True(TerminalCriticalTrace.FlushForTests());
        var trace = File.ReadAllText(_tracePath);
        Assert.Contains("stage=Startup.BinaryIdentity", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.executablePath=", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.applicationAssemblyVersion=", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.applicationInformationalVersion=", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.processId=", trace, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentProducers_PersistEveryAcceptedRecordInAdmissionOrder()
    {
        var observed = new List<string>();
        TerminalCriticalTrace.ConfigureForTests(_tracePath,
            appendAllText: (_, entry, _) => observed.Add(entry));
        Parallel.For(0, 200, index =>
            TerminalCriticalTrace.LogStage("Concurrent.Record", new Dictionary<string, object?>
            {
                ["index"] = index
            }));

        Assert.True(TerminalCriticalTrace.FlushForTests());
        Assert.Equal(200, observed.Count);
        Assert.Equal(200, TerminalCriticalTrace.Health.Accepted);
        Assert.Equal(0, TerminalCriticalTrace.Health.DroppedVerbose);
        var persistedSequences = observed.Select(entry =>
        {
            var marker = "traceSequence=";
            var start = entry.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var end = entry.IndexOf(';', start);
            return long.Parse(entry[start..end], System.Globalization.CultureInfo.InvariantCulture);
        }).ToArray();
        Assert.Equal(Enumerable.Range(1, 200).Select(static value => (long)value), persistedSequences);
    }

    [Fact]
    public void FullOptionalQueue_CountsRejectedRecordsWhilePreservingBoundaryCapacity()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        TerminalCriticalTrace.ConfigureForTests(_tracePath,
            appendAllText: (_, _, _) =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            });
        TerminalCriticalTrace.LogStage("Optional.First");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        for (var index = 0; index < 3200; index++) TerminalCriticalTrace.LogStage("Optional.Burst");
        TerminalCriticalTrace.LogException("Boundary.Exception", new IOException("test failure"));
        Assert.True(TerminalCriticalTrace.Health.DroppedVerbose > 0);
        Assert.Equal(0, TerminalCriticalTrace.Health.DroppedBoundary);
        release.Set();
        Assert.True(TerminalCriticalTrace.FlushForTests(5000));
    }

    [Fact]
    public void WriterFailure_ExposesHealthAndCanRestartForTests()
    {
        TerminalCriticalTrace.ConfigureForTests(_tracePath,
            appendAllText: (_, _, _) => throw new IOException("locked log"));
        TerminalCriticalTrace.LogStage("Boundary.Exception");
        Assert.True(TerminalCriticalTrace.FlushForTests());
        Assert.True(TerminalCriticalTrace.Health.WriteFailures > 0);
        Assert.Equal(nameof(IOException), TerminalCriticalTrace.Health.LastFailureType);

        TerminalCriticalTrace.ResetForTests();
        TerminalCriticalTrace.ConfigureForTests(_tracePath);
        TerminalCriticalTrace.LogStage("Recovery.AfterWriterFailure");
        Assert.True(TerminalCriticalTrace.FlushForTests());
        Assert.Contains("Recovery.AfterWriterFailure", File.ReadAllText(_tracePath), StringComparison.Ordinal);
    }

    [Fact]
    public void LockedTraceFile_CountsFailureAndRecoversAfterRelease()
    {
        using (var locked = new FileStream(_tracePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            TerminalCriticalTrace.LogStage("Boundary.Exception.Locked");
            Assert.True(TerminalCriticalTrace.FlushForTests());
            Assert.True(TerminalCriticalTrace.Health.WriteFailures > 0);
        }

        TerminalCriticalTrace.LogStage("Boundary.Exception.Recovered");
        Assert.True(TerminalCriticalTrace.FlushForTests());
        Assert.Contains("Boundary.Exception.Recovered", File.ReadAllText(_tracePath), StringComparison.Ordinal);
    }

    [Fact]
    public void TimingSamples_AggregateOnWriterAndRetainGenerationSequenceWithoutPayload()
    {
        var observed = new List<string>();
        TerminalCriticalTrace.ConfigureForTests(_tracePath,
            appendAllText: (_, entry, _) => observed.Add(entry));
        for (var index = 0; index < 128; index++)
            TerminalCriticalTrace.RecordDurationMicroseconds("WpfCallbackTotal", 500 + index,
                sessionGeneration: 7, rendererGeneration: 11, sequence: index + 1, queueDepth: 3,
                envelopeCount: 1);

        Assert.True(TerminalCriticalTrace.FlushForTests());
        var summary = Assert.Single(observed);
        Assert.Contains("stage=TerminalTiming.WpfCallbackTotal", summary, StringComparison.Ordinal);
        Assert.Contains("count=128", summary, StringComparison.Ordinal);
        Assert.Contains("p50Us=", summary, StringComparison.Ordinal);
        Assert.Contains("p95Us=", summary, StringComparison.Ordinal);
        Assert.Contains("p99Us=", summary, StringComparison.Ordinal);
        Assert.Contains("terminalSessionGeneration=7", summary, StringComparison.Ordinal);
        Assert.Contains("rendererGeneration=11", summary, StringComparison.Ordinal);
        Assert.Contains("sequence=128", summary, StringComparison.Ordinal);
        Assert.Contains("contentOmitted=true", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SlowWriter_DoesNotBlockAdmissionAndStopIsBounded()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        TerminalCriticalTrace.ConfigureForTests(_tracePath,
            appendAllText: (_, _, _) =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                Thread.Sleep(60);
            });
        TerminalCriticalTrace.LogStage("Slow.Writer.First");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

        var started = System.Diagnostics.Stopwatch.StartNew();
        TerminalCriticalTrace.LogStage("Slow.Writer.Second");
        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(500));

        release.Set();
        Assert.True(TerminalCriticalTrace.FlushForTests());
        Assert.True(TerminalCriticalTrace.Health.SlowWrites > 0);
    }

    [Fact]
    public void Stop_ReturnsWithinBoundWhenStorageIsBlocked()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        TerminalCriticalTrace.ConfigureForTests(_tracePath,
            appendAllText: (_, _, _) =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            });
        TerminalCriticalTrace.LogStage("Shutdown.BlockedStorage");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

        var started = System.Diagnostics.Stopwatch.StartNew();
        TerminalCriticalTrace.Stop();
        Assert.InRange(started.ElapsedMilliseconds, 0, 3000);
        release.Set();
        Assert.True(SpinWait.SpinUntil(() => TerminalCriticalTrace.Health.Pending == 0,
            TimeSpan.FromSeconds(3)));
    }

    private static Exception CaptureNestedException()
    {
        try
        {
            throw new InvalidDataException("inner diagnostic exception");
        }
        catch (Exception ex)
        {
            return new InvalidOperationException("outer diagnostic exception", ex);
        }
    }

    private static void ThrowingRawOutputSubscriber(int generation, string text)
    {
        throw new InvalidOperationException("subscriber thread-affinity failure");
    }

    private static void Publish(LiveConsoleService service, string text)
    {
        var method = typeof(LiveConsoleService).GetMethod(
            "PublishTerminalChunk",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(
            service,
            new object[]
            {
                text,
                ExecutionOutputStreamKind.StandardOutput,
                new Action<ExecutionOutputRecord>(_ => { })
            });
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
