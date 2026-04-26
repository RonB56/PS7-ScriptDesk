using System;
using System.IO;
using System.Text.Json;
using PowerShellStudio.Application.Interfaces;
using PowerShellStudio.Domain.Models;

namespace PowerShellStudio.Infrastructure.Services
{
    public class ApplicationSettingsService : IApplicationSettingsService
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly string _settingsFilePath;

        public ApplicationSettingsService()
        {
            var localApplicationDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var settingsDirectoryPath = Path.Combine(localApplicationDataPath, "PowerShellStudio");
            _settingsFilePath = Path.Combine(settingsDirectoryPath, "appsettings.json");
        }

        public ApplicationSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    return new ApplicationSettings();
                }

                var json = File.ReadAllText(_settingsFilePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new ApplicationSettings();
                }

                return JsonSerializer.Deserialize<ApplicationSettings>(json, SerializerOptions) ?? new ApplicationSettings();
            }
            catch
            {
                return new ApplicationSettings();
            }
        }

        public void SaveSettings(ApplicationSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            var directoryPath = Path.GetDirectoryName(_settingsFilePath);
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                throw new InvalidOperationException("Unable to resolve the settings storage directory.");
            }

            Directory.CreateDirectory(directoryPath);

            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            var temporaryFilePath = _settingsFilePath + ".tmp";

            File.WriteAllText(temporaryFilePath, json);

            if (File.Exists(_settingsFilePath))
            {
                File.Copy(temporaryFilePath, _settingsFilePath, overwrite: true);
                File.Delete(temporaryFilePath);
                return;
            }

            File.Move(temporaryFilePath, _settingsFilePath);
        }
    }
}
