namespace PS7ScriptDesk.Application.Services;

/// <summary>
/// Production switch for the PSES-owned interactive session.
/// The authoritative PSES path is enabled unless explicitly disabled with
/// PS7SCRIPTDESK_PSES_OWNED_SESSION=0, false, or no.
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
        var disabled = string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);
        return new PsesOwnedInteractiveSessionFeatureGate(!disabled);
    }
}
