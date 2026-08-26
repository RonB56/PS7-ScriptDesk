using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IApiLocalTestHostService : IAsyncDisposable
{
    event EventHandler<ApiLocalTestHostStatus>? StatusChanged;

    ApiLocalTestHostStatus CurrentStatus { get; }

    Task<ApiLocalTestHostStartResult> StartAsync(
        ApiLocalTestHostRequest request,
        CancellationToken cancellationToken = default);

    Task<ApiLocalTestHostStartResult> RestartAsync(
        ApiLocalTestHostRequest request,
        CancellationToken cancellationToken = default);

    Task<ApiLocalTestHostStatus> StopAsync(CancellationToken cancellationToken = default);
}
