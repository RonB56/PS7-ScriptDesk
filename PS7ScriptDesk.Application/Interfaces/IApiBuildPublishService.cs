using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IApiBuildPublishService
{
    Task<ApiBuildPublishResult> GenerateProjectAsync(
        ApiBuildPublishRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ApiBuildPublishProgressUpdate>? progress = null);

    Task<ApiBuildPublishResult> BuildAsync(
        ApiBuildPublishRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ApiBuildPublishProgressUpdate>? progress = null);

    Task<ApiBuildPublishResult> PublishAsync(
        ApiBuildPublishRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ApiBuildPublishProgressUpdate>? progress = null);
}
