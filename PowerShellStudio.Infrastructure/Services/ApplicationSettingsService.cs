using System;
using System.IO;
using System.Text.Json;
using PowerShellStudio.Application.Interfaces;
using PowerShellStudio.Application.Utilities;
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
            var settingsDirectoryPath = Path.Combine(localApplicationDataPath, ApplicationBranding.InternalName);
            _settingsFilePath = Path.Combine(settingsDirectoryPath, "appsettings.json");
            TryMigrateLegacySettings(localApplicationDataPath, _settingsFilePath);
        }


        private static void TryMigrateLegacySettings(string localApplicationDataPath, string newSettingsFilePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(localApplicationDataPath) || File.Exists(newSettingsFilePath))
                {
                    return;
                }

                var legacySettingsFilePath = Path.Combine(
                    localApplicationDataPath,
                    ApplicationBranding.LegacyInternalName,
                    "appsettings.json");

                if (!File.Exists(legacySettingsFilePath))
                {
                    return;
                }

                var newSettingsDirectory = Path.GetDirectoryName(newSettingsFilePath);
                if (string.IsNullOrWhiteSpace(newSettingsDirectory))
                {
                    return;
                }

                Directory.CreateDirectory(newSettingsDirectory);
                File.Copy(legacySettingsFilePath, newSettingsFilePath, overwrite: false);
            }
            catch
            {
                // Branding migration should never prevent the app from starting.
            }
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
