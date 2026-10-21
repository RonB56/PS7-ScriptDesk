using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

[Collection(PsesOwnedSessionEnvironmentCollection.Name)]
public sealed class PsesProductionAdapterContractTests
{
    [Fact]
    public void PsesLanguageServiceClientImplementsApplicationAdapterContract()
    {
        Assert.True(typeof(IPsesStructuredExecutionAdapter).IsAssignableFrom(typeof(PsesLanguageServiceClient)));
        Assert.Contains(typeof(PsesLanguageServiceClient).GetMethods(), method => method.Name == nameof(IPsesStructuredExecutionAdapter.InitializeAsync));
        Assert.Contains(typeof(PsesLanguageServiceClient).GetMethods(), method => method.Name == nameof(IPsesStructuredExecutionAdapter.EvaluateAsync));
        Assert.Contains(typeof(PsesLanguageServiceClient).GetMethods(), method => method.Name == nameof(IPsesStructuredExecutionAdapter.CancelRequestAsync));
    }

    [Fact]
    public void PsesOwnedSessionFeatureGateIsDisabledByDefault()
    {
        var previous = Environment.GetEnvironmentVariable(PsesOwnedInteractiveSessionFeatureGate.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(PsesOwnedInteractiveSessionFeatureGate.EnvironmentVariableName, null);
            Assert.False(PsesOwnedInteractiveSessionFeatureGate.FromEnvironment().IsEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PsesOwnedInteractiveSessionFeatureGate.EnvironmentVariableName, previous);
        }
    }

    [Fact]
    public void PsesOwnedArgumentsUsePinnedVsCodeHostIdentity()
    {
        var arguments = PsesOwnedInteractiveSessionService.BuildArguments(
            "C:\\PSES\\modules",
            "C:\\PSES\\Start-EditorServices.ps1",
            "C:\\Temp\\session.json",
            "C:\\Temp\\logs",
            "2025.4.0");

        Assert.Contains("Visual Studio Code Host", arguments);
        Assert.Contains("Microsoft.VSCode", arguments);
        Assert.Contains("-EnableConsoleRepl", arguments);
    }
}
