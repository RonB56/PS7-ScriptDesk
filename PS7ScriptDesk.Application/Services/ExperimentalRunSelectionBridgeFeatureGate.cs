using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Services;

/// <summary>Startup-stable opt-out for classifier-approved direct PSES Run Selection.</summary>
public sealed class ExperimentalRunSelectionBridgeFeatureGate
{
    public const string EnvironmentVariableName = "PS7SCRIPTDESK_EXPERIMENTAL_DIRECT_RUNSELECTION";

    public ExperimentalRunSelectionBridgeFeatureGate(bool isEnabled = true) => IsEnabled = isEnabled;

    public bool IsEnabled { get; }

    public bool ShouldUseDirectBridge(EditorExecutionMode mode, bool executeInCurrentScope, bool coordinatorApproved)
        => IsEnabled && coordinatorApproved && mode == EditorExecutionMode.RunSelection && executeInCurrentScope;

    public static ExperimentalRunSelectionBridgeFeatureGate FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        var enabled = !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) &&
                      !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) &&
                      !string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);
        return new ExperimentalRunSelectionBridgeFeatureGate(enabled);
    }
}
