using PS7ScriptDesk.Application.Services;

namespace PS7ScriptDesk.Tests;

[Collection(EditorExecutionFeatureGateEnvironmentCollection.Name)]
public sealed class ExperimentalRunFileBridgeFeatureGateTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("unexpected")]
    public void DefaultAndUnknownValuesKeepCurrentBridge(string? value)
    {
        var previous = Environment.GetEnvironmentVariable(ExperimentalRunFileBridgeFeatureGate.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(ExperimentalRunFileBridgeFeatureGate.EnvironmentVariableName, value);
            var gate = ExperimentalRunFileBridgeFeatureGate.FromEnvironment();

            Assert.False(gate.IsEnabled);
            Assert.False(gate.ShouldUseDirectBridge(executeInCurrentScope: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalRunFileBridgeFeatureGate.EnvironmentVariableName, previous);
        }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    public void ExplicitEnablementSelectsOnlyRunFile(string value)
    {
        var previous = Environment.GetEnvironmentVariable(ExperimentalRunFileBridgeFeatureGate.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(ExperimentalRunFileBridgeFeatureGate.EnvironmentVariableName, value);
            var gate = ExperimentalRunFileBridgeFeatureGate.FromEnvironment();

            Assert.True(gate.IsEnabled);
            Assert.True(gate.ShouldUseDirectBridge(executeInCurrentScope: false));
            Assert.False(gate.ShouldUseDirectBridge(executeInCurrentScope: true));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalRunFileBridgeFeatureGate.EnvironmentVariableName, previous);
        }
    }

    [Fact]
    public void SelectionIsStableForLifetimeOfSessionGate()
    {
        var previous = Environment.GetEnvironmentVariable(ExperimentalRunFileBridgeFeatureGate.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(ExperimentalRunFileBridgeFeatureGate.EnvironmentVariableName, "1");
            var gate = ExperimentalRunFileBridgeFeatureGate.FromEnvironment();
            Environment.SetEnvironmentVariable(ExperimentalRunFileBridgeFeatureGate.EnvironmentVariableName, "0");

            Assert.True(gate.ShouldUseDirectBridge(executeInCurrentScope: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalRunFileBridgeFeatureGate.EnvironmentVariableName, previous);
        }
    }
}
