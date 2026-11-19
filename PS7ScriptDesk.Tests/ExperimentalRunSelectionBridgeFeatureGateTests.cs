using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EditorExecutionFeatureGateEnvironmentCollection
{
    public const string Name = "Editor execution feature gate environment";
}

[Collection(EditorExecutionFeatureGateEnvironmentCollection.Name)]
public sealed class ExperimentalRunSelectionBridgeFeatureGateTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    public void ExplicitOptOutKeepsLegacyRunSelection(string value)
    {
        var previous = Environment.GetEnvironmentVariable(ExperimentalRunSelectionBridgeFeatureGate.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(ExperimentalRunSelectionBridgeFeatureGate.EnvironmentVariableName, value);
            var gate = ExperimentalRunSelectionBridgeFeatureGate.FromEnvironment();

            Assert.False(gate.IsEnabled);
            Assert.False(gate.ShouldUseDirectBridge(EditorExecutionMode.RunSelection, executeInCurrentScope: true, coordinatorApproved: true));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalRunSelectionBridgeFeatureGate.EnvironmentVariableName, previous);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unexpected")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    public void DefaultAndEnablementRequireCoordinatorApproval(string? value)
    {
        var previous = Environment.GetEnvironmentVariable(ExperimentalRunSelectionBridgeFeatureGate.EnvironmentVariableName);
        var previousFileGate = Environment.GetEnvironmentVariable(ExperimentalRunFileBridgeFeatureGate.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(ExperimentalRunSelectionBridgeFeatureGate.EnvironmentVariableName, value);
            Environment.SetEnvironmentVariable(ExperimentalRunFileBridgeFeatureGate.EnvironmentVariableName, "0");
            var gate = ExperimentalRunSelectionBridgeFeatureGate.FromEnvironment();

            Assert.True(gate.ShouldUseDirectBridge(EditorExecutionMode.RunSelection, executeInCurrentScope: true, coordinatorApproved: true));
            Assert.False(gate.ShouldUseDirectBridge(EditorExecutionMode.RunSelection, executeInCurrentScope: true, coordinatorApproved: false));
            Assert.False(gate.ShouldUseDirectBridge(EditorExecutionMode.ScriptCall, executeInCurrentScope: false, coordinatorApproved: true));
            Assert.False(gate.ShouldUseDirectBridge(EditorExecutionMode.RunSelection, executeInCurrentScope: false, coordinatorApproved: true));
            Assert.False(ExperimentalRunFileBridgeFeatureGate.FromEnvironment().ShouldUseDirectBridge(executeInCurrentScope: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalRunSelectionBridgeFeatureGate.EnvironmentVariableName, previous);
            Environment.SetEnvironmentVariable(ExperimentalRunFileBridgeFeatureGate.EnvironmentVariableName, previousFileGate);
        }
    }

    [Fact]
    public void SelectionGateIsStartupStableAndIndependentFromRunFileGate()
    {
        var previous = Environment.GetEnvironmentVariable(ExperimentalRunSelectionBridgeFeatureGate.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(ExperimentalRunSelectionBridgeFeatureGate.EnvironmentVariableName, "yes");
            var selectionGate = ExperimentalRunSelectionBridgeFeatureGate.FromEnvironment();
            Environment.SetEnvironmentVariable(ExperimentalRunSelectionBridgeFeatureGate.EnvironmentVariableName, "0");

            Assert.True(selectionGate.ShouldUseDirectBridge(EditorExecutionMode.RunSelection, executeInCurrentScope: true, coordinatorApproved: true));
            Assert.False(ExperimentalRunFileBridgeFeatureGate.FromEnvironment().ShouldUseDirectBridge(executeInCurrentScope: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExperimentalRunSelectionBridgeFeatureGate.EnvironmentVariableName, previous);
        }
    }

}
