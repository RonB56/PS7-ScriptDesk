using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class StructuredBuiltinModuleRegressionTests
{
    [Fact]
    public async Task SelectedRuntime_IncompatibleBuiltInModuleBundleIsRejectedBeforeRunspaceOpen()
    {
        var runtime = FindRuntime();
        Assert.NotNull(runtime);
        Assert.True(Directory.Exists(runtime!.PsHome), $"Selected runtime PSHOME does not exist: {runtime.PsHome}");

        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            PersistentPowerShellSessionBroker.CreateAsync("builtin-module-regression", runtime));

        Assert.Contains("byte-identical", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("System.Management.Automation.dll", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Write-Output 'TEST_OUTPUT'")]
    [InlineData("Write-Host 'TEST_HOST'")]
    [InlineData("Get-Location")]
    [InlineData("Set-Location $pwd")]
    [InlineData("Get-Command Write-Output")]
    [InlineData("Get-Command Set-Location")]
    [InlineData("Import-Module Microsoft.PowerShell.Utility; Import-Module Microsoft.PowerShell.Management")]
    public async Task SelectedRuntime_BuiltInCommandSmokeMatrixDoesNotBypassCompatibilityGuard(string script)
    {
        Assert.False(string.IsNullOrWhiteSpace(script));
        var runtime = FindRuntime();
        Assert.NotNull(runtime);

        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            PersistentPowerShellSessionBroker.CreateAsync("builtin-command-regression", runtime));

        Assert.Contains("byte-identical", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static PS7ScriptDesk.Domain.Models.PowerShellRuntimeInfo? FindRuntime()
    {
        var discovered = new RuntimeService().DiscoverRuntimes(requireLaunchValidation: false).DetectedRuntimes;
        return discovered.FirstOrDefault(runtime => Directory.Exists(runtime.PsHome));
    }
}
