namespace PS7ScriptDesk.Application.Services;

/// <summary>Startup-stable production switch for the independently packaged PSES editor control channel.</summary>
public sealed class PsesEditorBridgeFeatureGate
{
    public const string EnvironmentVariableName = "PS7SCRIPTDESK_PSES_EDITOR_BRIDGE";

    public PsesEditorBridgeFeatureGate(bool isEnabled = false) => IsEnabled = isEnabled;

    public bool IsEnabled { get; }

    public static PsesEditorBridgeFeatureGate FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        var enabled = !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) &&
                      !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) &&
                      !string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);
        return new PsesEditorBridgeFeatureGate(enabled);
    }
}
