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
    }

    [Fact]
    public void RawOutputSubscriberFailure_CapturesSubscriberIdentityAndPreDispatcherStage()
    {
        using var service = new LiveConsoleService();
        service.RawOutputReceived += ThrowingRawOutputSubscriber;

        Publish(service, "PS C:\\> ");

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
            new Dictionary<string, object?>
            {
                ["repairedOutputDispatchImplementationActive"] = true
            });

        var trace = File.ReadAllText(_tracePath);
        Assert.Contains("stage=Startup.BinaryIdentity", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.executablePath=", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.applicationAssemblyVersion=", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.applicationInformationalVersion=", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.processId=", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.terminalOutputDispatchImplementation=MainWindowDispatcherEnvelopeQueue", trace, StringComparison.Ordinal);
        Assert.Contains("metadata.repairedOutputDispatchImplementationActive=True", trace, StringComparison.Ordinal);
    }

    [Fact]
    public void RendererDrainExceptionInstrumentation_IsPresentAtUiBoundary()
    {
        var source = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("MainWindow.DispatcherBeginInvoke.Scheduled", source, StringComparison.Ordinal);
        Assert.Contains("MainWindow.DrainTerminalOutputForRenderer.EnvelopeException", source, StringComparison.Ordinal);
        Assert.Contains("uiDispatcherTransition", source, StringComparison.Ordinal);
        Assert.Contains("TerminalControl.WebView2.PostOutputException", ReadRepositoryFile("PS7ScriptDesk.Shell", "Controls", "TerminalControl.xaml.cs"), StringComparison.Ordinal);
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
