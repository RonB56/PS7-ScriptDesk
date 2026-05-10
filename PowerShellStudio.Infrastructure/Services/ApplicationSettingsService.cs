using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using PowerShellStudio.Application.Diagnostics;
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

        public string SettingsFilePath => _settingsFilePath;

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
            DeveloperDiagnostics.LogOperationStart(
                "Settings",
                "LoadSettings",
                $"Loading application settings from '{_settingsFilePath}'.",
                additionalProperties: new Dictionary<string, object?> { ["settingsPath"] = _settingsFilePath });

            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    var defaults = new ApplicationSettings();
                    DeveloperDiagnostics.ConfigureFromSettings(defaults, "Settings file missing; defaults applied");
                    DeveloperDiagnostics.LogDecision(
                        "Settings",
                        "LoadSettings",
                        "Settings file was missing; defaults were used.",
                        decision: "DefaultsUsed",
                        additionalProperties: new Dictionary<string, object?> { ["settingsPath"] = _settingsFilePath });
                    DeveloperDiagnostics.LogOperationStop("Settings", "LoadSettings", "Application settings loaded from defaults.");
                    return defaults;
                }

                var json = File.ReadAllText(_settingsFilePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    var defaults = new ApplicationSettings();
                    DeveloperDiagnostics.ConfigureFromSettings(defaults, "Settings file empty; defaults applied");
                    DeveloperDiagnostics.LogDecision(
                        "Settings",
                        "LoadSettings",
                        "Settings file was empty; defaults were used.",
                        decision: "DefaultsUsed",
                        additionalProperties: new Dictionary<string, object?> { ["settingsPath"] = _settingsFilePath });
                    DeveloperDiagnostics.LogOperationStop("Settings", "LoadSettings", "Application settings loaded from defaults.");
                    return defaults;
                }

                var settings = JsonSerializer.Deserialize<ApplicationSettings>(json, SerializerOptions) ?? new ApplicationSettings();
                DeveloperDiagnostics.ConfigureFromSettings(settings, "Settings loaded from disk");
                DeveloperDiagnostics.LogOperationStop(
                    "Settings",
                    "LoadSettings",
                    "Application settings loaded from disk.",
                    additionalProperties: new Dictionary<string, object?>
                    {
                        ["settingsPath"] = _settingsFilePath,
                        ["developerDiagnosticsEnabled"] = settings.IsDeveloperDiagnosticsEnabled
                    });
                return settings;
            }
            catch (Exception ex)
            {
                var defaults = new ApplicationSettings();
                DeveloperDiagnostics.ConfigureFromSettings(defaults, "Settings load failed; defaults applied");
                DeveloperDiagnostics.LogOperationFailure(
                    "Settings",
                    "LoadSettings",
                    $"Application settings load failed for '{_settingsFilePath}'.",
                    ex,
                    additionalProperties: new Dictionary<string, object?> { ["settingsPath"] = _settingsFilePath });
                return defaults;
            }
        }

        public void SaveSettings(ApplicationSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            DeveloperDiagnostics.LogOperationStart(
                "Settings",
                "SaveSettings",
                $"Saving application settings to '{_settingsFilePath}'.",
                additionalProperties: new Dictionary<string, object?> { ["settingsPath"] = _settingsFilePath });

            var directoryPath = Path.GetDirectoryName(_settingsFilePath);
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                throw new InvalidOperationException("Unable to resolve the settings storage directory.");
            }

            try
            {
                Directory.CreateDirectory(directoryPath);

                var json = JsonSerializer.Serialize(settings, SerializerOptions);
                var temporaryFilePath = _settingsFilePath + ".tmp";

                File.WriteAllText(temporaryFilePath, json);

                if (File.Exists(_settingsFilePath))
                {
                    File.Copy(temporaryFilePath, _settingsFilePath, overwrite: true);
                    File.Delete(temporaryFilePath);
                }
                else
                {
                    File.Move(temporaryFilePath, _settingsFilePath);
                }

                DeveloperDiagnostics.ConfigureFromSettings(settings, "Settings saved");
                DeveloperDiagnostics.LogOperationStop(
                    "Settings",
                    "SaveSettings",
                    "Application settings saved.",
                    additionalProperties: new Dictionary<string, object?>
                    {
                        ["settingsPath"] = _settingsFilePath,
                        ["developerDiagnosticsEnabled"] = settings.IsDeveloperDiagnosticsEnabled
                    });
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogOperationFailure(
                    "Settings",
                    "SaveSettings",
                    $"Application settings save failed for '{_settingsFilePath}'.",
                    ex,
                    additionalProperties: new Dictionary<string, object?> { ["settingsPath"] = _settingsFilePath });
                throw;
            }
        }
    }
}
