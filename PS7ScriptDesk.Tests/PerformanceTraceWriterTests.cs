using System.Text.Json;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Tests;

public sealed class PerformanceTraceWriterTests
{
    [Fact]
    public void EnabledTraceWritesNonEmptyJsonlAndMatchingSessionIdentity()
    {
        var previous = Environment.GetEnvironmentVariable(PerformanceTrace.EnvironmentVariableName);
        var previousWorkload = Environment.GetEnvironmentVariable(PerformanceTrace.WorkloadEnvironmentVariableName);
        var previousRun = Environment.GetEnvironmentVariable(PerformanceTrace.RunIdEnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(PerformanceTrace.EnvironmentVariableName, "1");
            Environment.SetEnvironmentVariable(PerformanceTrace.WorkloadEnvironmentVariableName, "LocalSmoke");
            Environment.SetEnvironmentVariable(PerformanceTrace.RunIdEnvironmentVariableName, "LocalSmoke-01");
            PerformanceTrace.Record("instant", "Smoke", "WriterSmoke", properties: new Dictionary<string, object?> { ["contentOmitted"] = true });
            var tracePath = PerformanceTrace.TracePath;
            var sessionId = PerformanceTrace.SessionId;
            PerformanceTrace.Stop();

            Assert.False(string.IsNullOrWhiteSpace(tracePath));
            Assert.True(File.Exists(tracePath));
            Assert.True(new FileInfo(tracePath).Length > 0);
            using var document = JsonDocument.Parse(File.ReadAllLines(tracePath!)[0]);
            Assert.Equal(sessionId, document.RootElement.GetProperty("sessionId").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PerformanceTrace.EnvironmentVariableName, previous);
            Environment.SetEnvironmentVariable(PerformanceTrace.WorkloadEnvironmentVariableName, previousWorkload);
            Environment.SetEnvironmentVariable(PerformanceTrace.RunIdEnvironmentVariableName, previousRun);
        }
    }
}
