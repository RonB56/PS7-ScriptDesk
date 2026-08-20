using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class AdvancedExeExportTests
{
    [Fact]
    public void PortablePreset_IsSelfContainedEmbeddedSingleFileX64()
    {
        var configuration = ExeExportConfiguration.CreatePreset(ExeExportPreset.PortableWindowsExe, "Network Tool");
        Assert.Equal("win-x64", configuration.RuntimeIdentifier);
        Assert.True(configuration.IsPortable);
        Assert.Equal(ExePackageFormat.SingleFile, configuration.PackageFormat);
    }

    [Fact]
    public void Arm64Preset_UsesArm64Rid()
    {
        var configuration = ExeExportConfiguration.CreatePreset(ExeExportPreset.Arm64PortableExe, "Network Tool");
        Assert.Equal("win-arm64", configuration.RuntimeIdentifier);
        Assert.True(configuration.IsPortable);
    }

    [Fact]
    public void FrameworkDependentInstalledPowerShell_ReportsBothDependencies()
    {
        var configuration = ExeExportConfiguration.CreatePreset(ExeExportPreset.SmallExe, "Small Tool");
        configuration.PowerShellRuntimeModel = ExePowerShellRuntimeModel.InstalledPowerShell;
        Assert.True(configuration.RequiresDotNetRuntime);
        Assert.True(configuration.RequiresInstalledPowerShell);
        Assert.False(configuration.IsPortable);
    }

    [Fact]
    public void Validator_RejectsInvalidOutputAndSilentGuiFailureMode()
    {
        var configuration = ExeExportConfiguration.CreatePreset(ExeExportPreset.WindowsGuiApplication, "Gui Tool");
        configuration.OutputExecutablePath = "bad?.exe";
        configuration.ShowFatalErrorDialog = false;
        configuration.WriteApplicationLog = false;
        var result = new ExeExportConfigurationValidator().Validate(configuration);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("invalid", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("must show fatal errors", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DependencyAnalyzer_ReportsModulesRelativeFilesAndExternalExecutables()
    {
        const string script = "Import-Module BurntToast\nGet-Content \"$PSScriptRoot\\config.json\"\nStart-Process ffmpeg.exe";
        var dependencies = new PowerShellDependencyAnalyzer().Analyze(script);
        Assert.Contains(dependencies, dependency => dependency.Kind == ExeExportDependencyKind.Module && dependency.Value == "BurntToast");
        Assert.Contains(dependencies, dependency => dependency.Kind == ExeExportDependencyKind.ScriptRelativePath);
        Assert.Contains(dependencies, dependency => dependency.Kind == ExeExportDependencyKind.Executable && dependency.Value.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase));
    }
}
