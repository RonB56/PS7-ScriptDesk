namespace PS7ScriptDesk.Application.Services;

/// <summary>Startup-stable, default-off switch for the direct PSES Run File experiment.</summary>
public sealed class ExperimentalRunFileBridgeFeatureGate
{
    public const string EnvironmentVariableName = "PS7SCRIPTDESK_EXPERIMENTAL_DIRECT_RUNFILE";

    public ExperimentalRunFileBridgeFeatureGate(bool isEnabled = false) => IsEnabled = isEnabled;

    public bool IsEnabled { get; }

    public bool ShouldUseDirectBridge(bool executeInCurrentScope)
        => IsEnabled && !executeInCurrentScope;

    public static ExperimentalRunFileBridgeFeatureGate FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        var enabled = string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        return new ExperimentalRunFileBridgeFeatureGate(enabled);
    }
}
