using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

[Collection(PsesOwnedSessionEnvironmentCollection.Name)]
public sealed class PsesOwnedInteractiveSessionFoundationTests
{
    [Fact]
    public void FeatureGate_IsDisabledByDefaultWhenEnvironmentIsUnset()
    {
        var original = Environment.GetEnvironmentVariable(PsesOwnedInteractiveSessionFeatureGate.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(PsesOwnedInteractiveSessionFeatureGate.EnvironmentVariableName, null);
            Assert.False(PsesOwnedInteractiveSessionFeatureGate.FromEnvironment().IsEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PsesOwnedInteractiveSessionFeatureGate.EnvironmentVariableName, original);
        }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    public void FeatureGate_RecognizesExplicitOptIn(string value)
    {
        var original = Environment.GetEnvironmentVariable(PsesOwnedInteractiveSessionFeatureGate.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(PsesOwnedInteractiveSessionFeatureGate.EnvironmentVariableName, value);
            Assert.True(PsesOwnedInteractiveSessionFeatureGate.FromEnvironment().IsEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PsesOwnedInteractiveSessionFeatureGate.EnvironmentVariableName, original);
        }
    }

    [Fact]
    public void PsesOwnedProvider_ImplementsTheExistingTerminalContract()
    {
        using var provider = new PsesOwnedInteractiveSessionService();
        Assert.False(provider.IsSessionRunning);
        Assert.False(provider.IsInputWritable);
        Assert.Null(provider.ProcessId);
        Assert.IsAssignableFrom<PS7ScriptDesk.Application.Interfaces.ILiveConsoleService>(provider);
    }

    [Fact]
    public async Task PsesOwnedProvider_ShutdownWithoutSessionIsIdempotent()
    {
        using var provider = new PsesOwnedInteractiveSessionService();

        Assert.True(await provider.ShutdownAsync());
        Assert.True(await provider.ShutdownAsync());
        Assert.False(provider.IsSessionRunning);
        Assert.Null(provider.ProcessId);
    }

    [Fact]
    public void PsesArguments_UseValidHostVersionAndDirectoryLogPath()
    {
        var arguments = PsesOwnedInteractiveSessionService.BuildArguments(
            @"C:\PSES",
            @"C:\PSES\PowerShellEditorServices\Start-EditorServices.ps1",
            @"C:\Session\session.json",
            @"C:\Session\Logs",
            PsesOwnedInteractiveSessionService.ResolvePsesHostVersion());

        var hostVersion = GetValue(arguments, "-HostVersion");
        var logPath = GetValue(arguments, "-LogPath");

        Assert.True(Version.TryParse(hostVersion, out _));
        Assert.NotEqual("2C", hostVersion);
        Assert.Equal(@"C:\Session\Logs", logPath);
        Assert.DoesNotContain("@()", arguments);
        Assert.DoesNotContain("-FeatureFlags", arguments);
        Assert.DoesNotContain("-AdditionalModules", arguments);
    }

    [Fact]
    public void PsesArguments_ContainRequiredStartupContract()
    {
        var arguments = PsesOwnedInteractiveSessionService.BuildArguments(
            @"C:\PSES",
            @"C:\PSES\PowerShellEditorServices\Start-EditorServices.ps1",
            @"C:\Session\session.json",
            @"C:\Session\Logs",
            "5.0.1");

        Assert.Contains("-EnableConsoleRepl", arguments);
        Assert.DoesNotContain("-NonInteractive", arguments);
        Assert.Equal("C:\\Session\\session.json", GetValue(arguments, "-SessionDetailsPath"));
        Assert.Equal("5.0.1", GetValue(arguments, "-HostVersion"));
    }

    private static string GetValue(IReadOnlyList<string> arguments, string parameter)
    {
        var index = -1;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (string.Equals(arguments[i], parameter, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }
        Assert.True(index >= 0 && index + 1 < arguments.Count);
        return arguments[index + 1];
    }
}
