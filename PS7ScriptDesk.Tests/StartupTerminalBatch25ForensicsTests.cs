using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class StartupTerminalBatch25ForensicsTests
{
    [Fact]
    public async Task RealConptyBackendCreatesPwshAndStartsReaderWhenRuntimeIsAvailable()
    {
        var runtimeService = new RuntimeService();
        var runtime = runtimeService.DiscoverRuntimes(requireLaunchValidation: true)
            .DetectedRuntimes
            .FirstOrDefault(item => item.IsPowerShell7OrLater && File.Exists(item.LaunchExecutablePath));

        if (runtime is null)
        {
            return;
        }

        using var service = new LiveConsoleService(preferRedirectedTerminalSession: false);
        var output = new List<ExecutionOutputRecord>();
        await service.StartSessionAsync(runtime, output.Add, Environment.CurrentDirectory).WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            Assert.True(service.IsSessionRunning);
            Assert.Equal(runtime.LaunchExecutablePath, service.ActiveRuntime?.LaunchExecutablePath, ignoreCase: true);
            Assert.NotNull(service.CurrentWorkingDirectory);
        }
        finally
        {
            await service.StopConsoleAsync(output.Add).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
