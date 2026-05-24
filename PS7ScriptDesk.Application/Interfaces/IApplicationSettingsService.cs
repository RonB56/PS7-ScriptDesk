using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces
{
    public interface IApplicationSettingsService
    {
        string SettingsFilePath { get; }

        ApplicationSettings LoadSettings();

        void SaveSettings(ApplicationSettings settings);
    }
}
