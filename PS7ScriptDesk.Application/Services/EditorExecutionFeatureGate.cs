namespace PS7ScriptDesk.Application.Services;

public sealed class EditorExecutionFeatureGate
{
    public const string EnvironmentVariableName = "PS7SCRIPTDESK_STRUCTURED_EXECUTION";

    public EditorExecutionFeatureGate(bool structuredExecutionEnabled = true)
    {
        IsStructuredExecutionEnabled = structuredExecutionEnabled;
    }

    public bool IsStructuredExecutionEnabled { get; }

    public static EditorExecutionFeatureGate FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        // Structured execution is the production path. Keep an explicit opt-out
        // for controlled rollback and diagnostics; an unset variable must not
        // silently reactivate the source-injecting legacy editor path.
        var disabled = string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(value, "no", StringComparison.OrdinalIgnoreCase);
        var enabled = !disabled;
        return new EditorExecutionFeatureGate(enabled);
    }
}
