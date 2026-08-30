namespace PS7ScriptDesk.Application.Services;

public sealed class EditorExecutionFeatureGate
{
    public const string EnvironmentVariableName = "PS7SCRIPTDESK_STRUCTURED_EXECUTION";

    public EditorExecutionFeatureGate(bool structuredExecutionEnabled = false)
    {
        IsStructuredExecutionEnabled = structuredExecutionEnabled;
    }

    public bool IsStructuredExecutionEnabled { get; }

    public static EditorExecutionFeatureGate FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        var enabled = string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        return new EditorExecutionFeatureGate(enabled);
    }
}
