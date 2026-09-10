namespace PS7ScriptDesk.Domain.Models;

public sealed record GitCommandResult(
    bool Success,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool WasCancelled,
    bool TimedOut,
    string? FailureKind)
{
    public static GitCommandResult Failed(string failureKind, string error, TimeSpan duration)
        => new(false, null, string.Empty, error, duration, false, false, failureKind);
}
