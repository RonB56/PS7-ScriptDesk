using PowerShellStudio.Domain.Models;

namespace PowerShellStudio.Application.Interfaces
{
    public interface IApplicationSettingsService
    {
        string SettingsFilePath { get; }

        ApplicationSettings LoadSettings();

        void SaveSettings(ApplicationSettings settings);
    }
}
