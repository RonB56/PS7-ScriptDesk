namespace PS7ScriptDesk.Application.Services;

/// <summary>
/// Opt-in switch for the Phase 2C PSES-owned interactive session foundation.
/// The legacy ConPTY path remains the default until the new path passes UAT.
/// </summary>
public sealed class PsesOwnedInteractiveSessionFeatureGate
{
    public const string EnvironmentVariableName = "PS7SCRIPTDESK_PSES_OWNED_SESSION";

    public PsesOwnedInteractiveSessionFeatureGate(bool enabled = false)
    {
        IsEnabled = enabled;
    }

    public bool IsEnabled { get; }

    public static PsesOwnedInteractiveSessionFeatureGate FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        return new PsesOwnedInteractiveSessionFeatureGate(
            string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase));
    }
}
