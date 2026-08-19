using System.ComponentModel;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

[Collection("DiagnosticReliability")]
public sealed class ExeExportServiceDiagnosticsTests
{
    private static readonly SemaphoreSlim DiagnosticTestGate = new(1, 1);

    [Fact]
    public async Task TemporaryDirectoryFailure_IsServiceOwnedAndPreservesOriginalException()
    {
        using var source = new TemporaryScript();
        using var tempFile = new TemporaryFile();

        await AssertServiceFailureAsync(
            () => new TemporaryDirectoryScope(tempFile.Path),
            source.Path,
            Path.Combine(source.DirectoryPath, "Exported.exe"),
            "CreateTemporaryDirectories",
            expectedExceptionType: nameof(IOException));
    }

    [Fact]
    public async Task DotNetStartupFailure_PreservesWin32ExceptionAndSingleOwnership()
    {
        using var source = new TemporaryScript();
        using var fakeDotNet = FakeDotNet.CreateInvalidExecutable();

        await AssertServiceFailureAsync(
            () => new PathScope(fakeDotNet.DirectoryPath),
            source.Path,
            Path.Combine(source.DirectoryPath, "Exported.exe"),
            "RunDotNetPublish",
            expectedExceptionType: nameof(Win32Exception));
    }

    [Fact]
    public async Task NonzeroPublishExit_UsesActualProcessAndPrivatePublishMetadata()
    {
        using var source = new TemporaryScript();
        using var fakeDotNet = FakeDotNet.CopySystemExecutable("find.exe");

        await AssertServiceFailureAsync(
            () => new PathScope(fakeDotNet.DirectoryPath),
            source.Path,
            Path.Combine(source.DirectoryPath, "Exported.exe"),
            "RunDotNetPublish",
            expectedExitCode: 2);
    }

    [Fact]
    public async Task SuccessfulPublishWithoutExecutable_IsServiceOwned()
    {
        using var source = new TemporaryScript();
        using var fakeDotNet = FakeDotNet.CopySystemExecutable(Path.GetFileName(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"));

        await AssertServiceFailureAsync(
            () => new PathScope(fakeDotNet.DirectoryPath),
            source.Path,
            Path.Combine(source.DirectoryPath, "Exported.exe"),
            "LocatePublishedExecutable");
    }

    [Fact]
    public async Task Cancellation_PreservesExistingFailureResult()
    {
        using var source = new TemporaryScript();
        using var fakeDotNet = FakeDotNet.CopySystemExecutable(Path.GetFileName(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"));
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await DiagnosticTestGate.WaitAsync();
        try
        {
            using var pathScope = new PathScope(fakeDotNet.DirectoryPath);
            var result = await new ExeExportService().ExportScriptAsExeAsync(
                CreateRequest(source.Path, Path.Combine(source.DirectoryPath, "Canceled.exe")),
                cancellationSource.Token);

            Assert.False(result.Succeeded);
            Assert.Equal("Export as EXE failed unexpectedly.", result.SummaryMessage);
        }
        finally
        {
            DiagnosticTestGate.Release();
        }
    }

    private static async Task AssertServiceFailureAsync(
        Func<IDisposable> environmentScopeFactory,
        string sourcePath,
        string outputPath,
        string expectedStage,
        string? expectedExceptionType = null,
        int? expectedExitCode = null)
    {
        await DiagnosticTestGate.WaitAsync();
        DeveloperDiagnostics.ConfigureFromSettings(
            new ApplicationSettings
            {
                IsDeveloperDiagnosticsEnabled = true,
                DeveloperDiagnosticsWriteJsonLines = true
            },
            "Exe export diagnostic reliability test");

        try
        {
            var diagnosticsPath = Path.Combine(DeveloperDiagnostics.CurrentSessionDirectory!, "developer-diagnostics.ndjson");
            var diagnosticsBeforeLength = File.Exists(diagnosticsPath)
                ? (await ReadAllTextWithRetryAsync(diagnosticsPath)).Length
                : 0;
            using var environmentScope = environmentScopeFactory();
            using var appLogCapture = new AppLogCapture();

            var result = await new ExeExportService().ExportScriptAsExeAsync(CreateRequest(sourcePath, outputPath));
            DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings(), "Exe export diagnostic reliability cleanup");
            await WaitForAsync(
                () => appLogCapture.Entries.Any(entry => entry.Contains("[ERROR] [ExeExport]", StringComparison.Ordinal)),
                timeoutMilliseconds: 5000);

            var diagnosticText = await ReadAllTextWithRetryAsync(diagnosticsPath);
            var diagnostics = diagnosticText[Math.Min(diagnosticsBeforeLength, diagnosticText.Length)..];
            var appLog = string.Concat(appLogCapture.Entries);

            Assert.False(result.Succeeded);
            Assert.Contains(expectedStage, diagnostics, StringComparison.Ordinal);
            Assert.Single(
                appLog.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries),
                line => line.Contains("[ERROR] [ExeExport]", StringComparison.Ordinal));
            Assert.Equal(1, CountOccurrences(diagnostics, "\"Category\": \"ExeExport\""));
            Assert.DoesNotContain(ScriptSentinel, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(ScriptSentinel, appLog, StringComparison.Ordinal);
            if (expectedExceptionType is not null)
            {
                Assert.Contains(expectedExceptionType, diagnostics, StringComparison.Ordinal);
            }

            if (expectedExitCode is not null)
            {
                Assert.Contains(expectedExitCode.Value.ToString(), diagnostics, StringComparison.Ordinal);
            }
        }
        finally
        {
            DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings(), "Exe export diagnostic reliability cleanup");
            DiagnosticTestGate.Release();
        }
    }

    private static ExeExportRequest CreateRequest(string source, string output)
        => new(
            source,
            $"Write-Output '{ScriptSentinel}'",
            output,
            new PowerShellRuntimeInfo(
                "PowerShell 7 test",
                "Core",
                "7.0.0",
                new Version(7, 0),
                "x64",
                Environment.ProcessPath!,
                "test",
                true,
                false,
                false));

    private static async Task<string> ReadAllTextWithRetryAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 20)
            {
                await Task.Delay(25);
            }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("Timed out waiting for the exporter application log.");
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

    private const string ScriptSentinel = "PHASE3_SCRIPT_SECRET_8B65";

    private sealed class PathScope : IDisposable
    {
        private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");

        public PathScope(string pathPrefix)
        {
            Environment.SetEnvironmentVariable("PATH", pathPrefix);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _originalPath);
        }
    }

    private sealed class AppLogCapture : IDisposable
    {
        private readonly FieldInfo _primaryAppendField;
        private readonly object? _originalPrimaryAppend;

        public AppLogCapture()
        {
            _primaryAppendField = typeof(AppLogger).GetField("_primaryAppend", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("AppLogger primary append test seam was not found.");
            _originalPrimaryAppend = _primaryAppendField.GetValue(null);
            _primaryAppendField.SetValue(null, (Func<string, string, Encoding, Task>)((_, text, _) =>
            {
                Entries.Enqueue(text);
                return Task.CompletedTask;
            }));
        }

        public ConcurrentQueue<string> Entries { get; } = new();

        public void Dispose()
        {
            _primaryAppendField.SetValue(null, _originalPrimaryAppend);
        }
    }

    private sealed class TemporaryDirectoryScope : IDisposable
    {
        private readonly string? _originalTemp = Environment.GetEnvironmentVariable("TEMP");
        private readonly string? _originalTmp = Environment.GetEnvironmentVariable("TMP");

        public TemporaryDirectoryScope(string filePath)
        {
            Environment.SetEnvironmentVariable("TEMP", filePath);
            Environment.SetEnvironmentVariable("TMP", filePath);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TEMP", _originalTemp);
            Environment.SetEnvironmentVariable("TMP", _originalTmp);
        }
    }

    private sealed class FakeDotNet : IDisposable
    {
        private FakeDotNet(string directoryPath)
        {
            DirectoryPath = directoryPath;
        }

        public string DirectoryPath { get; }

        public static FakeDotNet CreateInvalidExecutable()
        {
            var fake = CreateDirectory();
            File.WriteAllText(Path.Combine(fake.DirectoryPath, "dotnet.exe"), "not an executable");
            return fake;
        }

        public static FakeDotNet CopySystemExecutable(string executableName)
        {
            var fake = CreateDirectory();
            File.Copy(Path.Combine(Environment.SystemDirectory, executableName), Path.Combine(fake.DirectoryPath, "dotnet.exe"));
            return fake;
        }

        private static FakeDotNet CreateDirectory()
        {
            var directoryPath = Path.Combine(Path.GetTempPath(), $"PS7-FakeDotNet-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directoryPath);
            return new FakeDotNet(directoryPath);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch
            {
                // Test cleanup is best-effort.
            }
        }
    }

    private sealed class TemporaryScript : IDisposable
    {
        public TemporaryScript()
        {
            DirectoryPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"PS7-Test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
            Path = System.IO.Path.Combine(DirectoryPath, "source.ps1");
            File.WriteAllText(Path, "Write-Output 'test'");
        }

        public string DirectoryPath { get; }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch
            {
                // Test cleanup is best-effort.
            }
        }
    }

    private sealed class TemporaryFile : IDisposable
    {
        public TemporaryFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"PS7-Test-{Guid.NewGuid():N}");
            File.WriteAllText(Path, "file");
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch
            {
                // Test cleanup is best-effort.
            }
        }
    }

}
