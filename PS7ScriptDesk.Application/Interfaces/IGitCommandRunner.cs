using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IGitCommandRunner
{
    Task<GitEnvironmentInfo> GetEnvironmentAsync(CancellationToken cancellationToken = default);

    Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
