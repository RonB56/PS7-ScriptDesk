namespace PS7ScriptDesk.RestApiProofHost.PowerShell;

public sealed class ApiInvocationRequest
{
    public string FunctionName { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } = new Dictionary<string, object?>();
    public TimeSpan? Timeout { get; init; }
}
