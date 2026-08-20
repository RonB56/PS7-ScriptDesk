using System;
using System.IO;
using System.Text.RegularExpressions;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Services;

public sealed class ExeExportConfigurationValidator : IExeExportConfigurationValidator
{
    private static readonly Regex VersionPattern = new(@"^\d+(\.\d+){1,3}$", RegexOptions.CultureInvariant);

    public ExeExportValidationResult Validate(ExeExportConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var result = new ExeExportValidationResult();

        if (string.IsNullOrWhiteSpace(configuration.OutputExecutablePath))
            result.Errors.Add("Choose an output executable path.");
        else
        {
            var fileName = Path.GetFileName(configuration.OutputExecutablePath);
            if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                result.Errors.Add("The output filename contains invalid Windows filename characters.");
            if (!string.Equals(Path.GetExtension(fileName), ".exe", StringComparison.OrdinalIgnoreCase))
                result.Errors.Add("The output filename must end in .exe.");
        }

        if (string.IsNullOrWhiteSpace(configuration.ApplicationName) || configuration.ApplicationName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            result.Errors.Add("Enter a valid application name.");
        if (!string.Equals(configuration.DotNetTarget, "net10.0", StringComparison.OrdinalIgnoreCase))
            result.Errors.Add("The selected .NET target is not supported by this installed ScriptDesk export host.");
        if (!VersionPattern.IsMatch(configuration.FileVersion ?? string.Empty))
            result.Errors.Add("File version must contain two to four numeric components, such as 1.0.0.0.");
        if (!VersionPattern.IsMatch(configuration.ProductVersion ?? string.Empty))
            result.Errors.Add("Product version must contain two to four numeric components, such as 1.0.0.");
        if (!string.IsNullOrWhiteSpace(configuration.IconPath) &&
            (!File.Exists(configuration.IconPath) || !string.Equals(Path.GetExtension(configuration.IconPath), ".ico", StringComparison.OrdinalIgnoreCase)))
            result.Errors.Add("The selected application icon must be an existing .ico file.");

        if (configuration.DeploymentModel == ExeDeploymentModel.FrameworkDependent)
            result.Warnings.Add("Framework-dependent output requires a compatible .NET runtime on the destination computer.");
        if (configuration.PowerShellRuntimeModel == ExePowerShellRuntimeModel.InstalledPowerShell)
            result.Warnings.Add("This output requires a compatible installed PowerShell 7 runtime on the destination computer.");
        if (configuration.ApplicationType == ExeApplicationType.WindowsGui && !configuration.ShowFatalErrorDialog && !configuration.WriteApplicationLog)
            result.Errors.Add("A Windows GUI export must show fatal errors or write an application log so failures are not silent.");
        if (configuration.OptimizationProfile == ExeOptimizationProfile.FastStartup && configuration.DeploymentModel == ExeDeploymentModel.FrameworkDependent)
            result.Warnings.Add("Fast Startup may increase package size and depends on target runtime support.");

        return result;
    }
}
