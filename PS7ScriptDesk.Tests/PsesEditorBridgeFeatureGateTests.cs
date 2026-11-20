using PS7ScriptDesk.Application.Services;

namespace PS7ScriptDesk.Tests;

public sealed class PsesEditorBridgeFeatureGateTests
{
    [Fact]
    public void BridgeIsEnabledByDefaultAndCanBeExplicitlyDisabled()
    {
        var previous = Environment.GetEnvironmentVariable(PsesEditorBridgeFeatureGate.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(PsesEditorBridgeFeatureGate.EnvironmentVariableName, null);
            Assert.True(PsesEditorBridgeFeatureGate.FromEnvironment().IsEnabled);

            Environment.SetEnvironmentVariable(PsesEditorBridgeFeatureGate.EnvironmentVariableName, "0");
            Assert.False(PsesEditorBridgeFeatureGate.FromEnvironment().IsEnabled);

            Environment.SetEnvironmentVariable(PsesEditorBridgeFeatureGate.EnvironmentVariableName, "1");
            Assert.True(PsesEditorBridgeFeatureGate.FromEnvironment().IsEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PsesEditorBridgeFeatureGate.EnvironmentVariableName, previous);
        }
    }
}
