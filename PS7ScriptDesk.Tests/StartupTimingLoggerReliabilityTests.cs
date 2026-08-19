using System.Reflection;
using PS7ScriptDesk.Application.Utilities;
using Xunit;

namespace PS7ScriptDesk.Tests;

[Collection("DiagnosticReliability")]
public sealed class StartupTimingLoggerReliabilityTests
{
    [Fact]
    public void DirectoryFailure_DisablesOptionalTimingStorage_AndDoesNotEscape()
    {
        var loggerType = typeof(StartupTimingLogger);
        var directoryCreate = loggerType.GetField("_directoryCreate", BindingFlags.NonPublic | BindingFlags.Static)!;
        var storageDisabled = loggerType.GetField("_timingStorageDisabled", BindingFlags.NonPublic | BindingFlags.Static)!;
        var failureReported = loggerType.GetField("_timingStorageFailureReported", BindingFlags.NonPublic | BindingFlags.Static)!;
        var originalDirectoryCreate = directoryCreate.GetValue(null);
        var originalStorageDisabled = storageDisabled.GetValue(null);
        var originalFailureReported = failureReported.GetValue(null);
        var attempts = 0;

        try
        {
            storageDisabled.SetValue(null, 0);
            failureReported.SetValue(null, 0);
            directoryCreate.SetValue(null, (Action<string>)(_ =>
            {
                attempts++;
                throw new IOException("Injected timing directory failure.");
            }));

            var exception = Record.Exception(() => StartupTimingLogger.Log("Test", "Timing I/O must remain optional."));
            StartupTimingLogger.Log("Test", "Known failing storage must not be retried.");

            Assert.Null(exception);
            Assert.Equal(1, attempts);
            Assert.Equal(1, (int)storageDisabled.GetValue(null)!);
        }
        finally
        {
            directoryCreate.SetValue(null, originalDirectoryCreate);
            storageDisabled.SetValue(null, originalStorageDisabled);
            failureReported.SetValue(null, originalFailureReported);
        }
    }
}
