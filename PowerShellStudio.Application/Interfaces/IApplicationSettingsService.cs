using PowerShellStudio.Domain.Models;

namespace PowerShellStudio.Application.Interfaces
{
    public interface IApplicationSettingsService
    {
        ApplicationSettings LoadSettings();

        void SaveSettings(ApplicationSettings settings);
    }
}
