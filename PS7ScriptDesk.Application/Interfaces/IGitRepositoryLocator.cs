using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces;

public interface IGitRepositoryLocator
{
    Task<GitRepositoryInfo> DetectRepositoryAsync(
        string folderPath,
        CancellationToken cancellationToken = default);
}
