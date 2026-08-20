using System;
using System.Collections.Generic;
using System.IO;

namespace PS7ScriptDesk.Domain.Models;

public enum ExeExportPreset
{
    PortableWindowsExe,
    WindowsConsoleApplication,
    WindowsGuiApplication,
    Arm64PortableExe,
    SmallExe,
    Custom
}

public enum ExeApplicationType
{
    AutoDetect,
    Console,
    WindowsGui
}

public enum ExeTargetArchitecture
{
    X64,
    Arm64
}

public enum ExeDeploymentModel
{
    SelfContained,
    FrameworkDependent
}

public enum ExePowerShellRuntimeModel
{
    Embedded,
    InstalledPowerShell
}

public enum ExePackageFormat
{
    SingleFile,
    ApplicationFolder
}

public enum ExeOptimizationProfile
{
    MaximumCompatibility,
    Balanced,
    FastStartup
}

public enum ExeAdministratorMode
{
    NormalUser,
    RequestElevation,
    RequireAdministrator
}

public enum ExeApartmentState
{
    Default,
    Sta,
    Mta
}

/// <summary>
/// Stable user-selectable configuration for a generated PowerShell-hosted executable.
/// Source text is deliberately kept in <see cref="ExeExportRequest"/> rather than persisted here.
/// </summary>
public sealed class ExeExportConfiguration
{
    public ExeExportPreset Preset { get; set; } = ExeExportPreset.PortableWindowsExe;
    public ExeApplicationType ApplicationType { get; set; } = ExeApplicationType.AutoDetect;
    public ExeTargetArchitecture Architecture { get; set; } = ExeTargetArchitecture.X64;
    public ExeDeploymentModel DeploymentModel { get; set; } = ExeDeploymentModel.SelfContained;
    public ExePowerShellRuntimeModel PowerShellRuntimeModel { get; set; } = ExePowerShellRuntimeModel.Embedded;
    public ExePackageFormat PackageFormat { get; set; } = ExePackageFormat.SingleFile;
    public ExeOptimizationProfile OptimizationProfile { get; set; } = ExeOptimizationProfile.Balanced;
    public ExeAdministratorMode AdministratorMode { get; set; } = ExeAdministratorMode.NormalUser;
    public ExeApartmentState ApartmentState { get; set; } = ExeApartmentState.Default;

    /// <summary>Currently supported generated-host target. The wizard only offers targets validated by this application.</summary>
    public string DotNetTarget { get; set; } = "net10.0";
    public string ApplicationName { get; set; } = "ExportedPowerShellScript";
    public string ProductName { get; set; } = "Exported PowerShell Script";
    public string Description { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string Copyright { get; set; } = string.Empty;
    public string FileVersion { get; set; } = "1.0.0.0";
    public string ProductVersion { get; set; } = "1.0.0";
    public string? IconPath { get; set; }
    public string OutputExecutablePath { get; set; } = string.Empty;
    public bool LoadPowerShellProfile { get; set; }
    public bool ShowFatalErrorDialog { get; set; } = true;
    public bool WriteApplicationLog { get; set; } = true;
    public bool IncludePowerShellStackInformation { get; set; } = true;
    public string? WorkingDirectory { get; set; }
    public string? AdditionalModulePaths { get; set; }

    public string RuntimeIdentifier => Architecture switch
    {
        ExeTargetArchitecture.Arm64 => "win-arm64",
        _ => "win-x64"
    };

    public bool RequiresDotNetRuntime => DeploymentModel == ExeDeploymentModel.FrameworkDependent;
    public bool RequiresInstalledPowerShell => PowerShellRuntimeModel == ExePowerShellRuntimeModel.InstalledPowerShell;
    public bool IsPortable => !RequiresDotNetRuntime && !RequiresInstalledPowerShell;

    public ExeExportConfiguration Clone() => (ExeExportConfiguration)MemberwiseClone();

    public static ExeExportConfiguration CreatePreset(ExeExportPreset preset, string? suggestedApplicationName = null)
    {
        var safeName = string.IsNullOrWhiteSpace(suggestedApplicationName)
            ? "ExportedPowerShellScript"
            : Path.GetFileNameWithoutExtension(suggestedApplicationName);
        var configuration = new ExeExportConfiguration
        {
            Preset = preset,
            ApplicationName = safeName,
            ProductName = safeName
        };

        switch (preset)
        {
            case ExeExportPreset.WindowsConsoleApplication:
                configuration.ApplicationType = ExeApplicationType.Console;
                break;
            case ExeExportPreset.WindowsGuiApplication:
                configuration.ApplicationType = ExeApplicationType.WindowsGui;
                break;
            case ExeExportPreset.Arm64PortableExe:
                configuration.Architecture = ExeTargetArchitecture.Arm64;
                break;
            case ExeExportPreset.SmallExe:
                configuration.DeploymentModel = ExeDeploymentModel.FrameworkDependent;
                break;
            case ExeExportPreset.Custom:
                break;
        }

        return configuration;
    }

    public IReadOnlyDictionary<string, string> CreateSummary() => new Dictionary<string, string>
    {
        ["Application"] = $"{ApplicationName}.exe",
        ["Application type"] = ApplicationType == ExeApplicationType.AutoDetect ? "Auto Detect (Console if uncertain)" : ApplicationType.ToString(),
        ["Platform"] = Architecture == ExeTargetArchitecture.Arm64 ? "Windows ARM64" : "Windows x64",
        [".NET"] = DotNetTarget,
        ["Deployment"] = DeploymentModel == ExeDeploymentModel.SelfContained ? "Self-contained" : "Framework-dependent (.NET runtime required)",
        ["PowerShell"] = PowerShellRuntimeModel == ExePowerShellRuntimeModel.Embedded ? "Embedded PowerShell runtime" : "Installed PowerShell required",
        ["Package"] = PackageFormat == ExePackageFormat.SingleFile ? "Single EXE" : "Application folder",
        ["Optimization"] = OptimizationProfile.ToString(),
        ["Administrator"] = AdministratorMode.ToString(),
        ["Output"] = OutputExecutablePath
    };
}
