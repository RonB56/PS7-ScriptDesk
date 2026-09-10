namespace PS7ScriptDesk.Domain.Models;

public sealed record GitEnvironmentInfo(
    bool IsAvailable,
    string? ExecutablePath,
    string? Version,
    string? Error,
    string? FailureKind)
{
    public static GitEnvironmentInfo Unavailable(string? error, string failureKind = "Unavailable")
        => new(false, null, null, error, failureKind);
}
