using System;
using System.IO;
using System.Security;
using System.Text;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

internal sealed class ExeHostProjectGenerator
{
    private const string PowerShellSdkVersion = "7.6.1";

    public GeneratedExeHostProject Generate(ExeExportRequest request, string projectDirectory)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        var configuration = request.Configuration ?? CreateLegacyConfiguration(request);
        var assemblyName = MakeAssemblyName(configuration.ApplicationName);
        var scriptFileName = MakeScriptFileName(request.SourceScriptPath);
        var projectFilePath = Path.Combine(projectDirectory, $"{assemblyName}.csproj");
        var scriptFilePath = Path.Combine(projectDirectory, scriptFileName);
        var programFilePath = Path.Combine(projectDirectory, "Program.cs");
        var settingsFilePath = Path.Combine(projectDirectory, "ExportSettings.cs");

        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(scriptFilePath, request.ScriptContent, new UTF8Encoding(false));
        File.WriteAllText(settingsFilePath, BuildSettingsSource(configuration, request.RuntimeInfo.ExecutablePath, scriptFileName), new UTF8Encoding(false));
        File.WriteAllText(programFilePath,
            configuration.PowerShellRuntimeModel == ExePowerShellRuntimeModel.Embedded
                ? BuildEmbeddedHostSource()
                : BuildInstalledPowerShellHostSource(),
            new UTF8Encoding(false));

        string? iconFileName = null;
        if (!string.IsNullOrWhiteSpace(configuration.IconPath))
        {
            iconFileName = Path.GetFileName(configuration.IconPath);
            File.Copy(configuration.IconPath, Path.Combine(projectDirectory, iconFileName), overwrite: true);
        }

        var manifestFileName = "app.manifest";
        File.WriteAllText(Path.Combine(projectDirectory, manifestFileName), BuildManifest(configuration.AdministratorMode), new UTF8Encoding(false));
        File.WriteAllText(projectFilePath, BuildProjectFile(configuration, assemblyName, scriptFileName, manifestFileName, iconFileName), new UTF8Encoding(false));
        return new GeneratedExeHostProject(projectFilePath, assemblyName, configuration);
    }

    private static ExeExportConfiguration CreateLegacyConfiguration(ExeExportRequest request) => new()
    {
        ApplicationName = Path.GetFileNameWithoutExtension(request.OutputExecutablePath),
        ProductName = Path.GetFileNameWithoutExtension(request.OutputExecutablePath),
        OutputExecutablePath = request.OutputExecutablePath,
        DeploymentModel = ExeDeploymentModel.FrameworkDependent,
        PowerShellRuntimeModel = ExePowerShellRuntimeModel.InstalledPowerShell,
        ApplicationType = ExeApplicationType.WindowsGui
    };

    private static string BuildProjectFile(ExeExportConfiguration configuration, string assemblyName, string scriptFileName, string manifestFileName, string? iconFileName)
    {
        var outputType = configuration.ApplicationType == ExeApplicationType.WindowsGui ? "WinExe" : "Exe";
        var selfContained = configuration.DeploymentModel == ExeDeploymentModel.SelfContained ? "true" : "false";
        var singleFile = configuration.PackageFormat == ExePackageFormat.SingleFile ? "true" : "false";
        var readyToRun = configuration.OptimizationProfile == ExeOptimizationProfile.FastStartup ? "true" : "false";
        var iconProperty = string.IsNullOrWhiteSpace(iconFileName) ? string.Empty : $"\n    <ApplicationIcon>{Xml(iconFileName)}</ApplicationIcon>";
        var powerShellReference = configuration.PowerShellRuntimeModel == ExePowerShellRuntimeModel.Embedded
            ? $"\n  <ItemGroup>\n    <PackageReference Include=\"Microsoft.PowerShell.SDK\" Version=\"{PowerShellSdkVersion}\" />\n  </ItemGroup>"
            : string.Empty;

        return $"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>{outputType}</OutputType>
    <TargetFramework>{Xml(configuration.DotNetTarget)}</TargetFramework>
    <RuntimeIdentifier>{configuration.RuntimeIdentifier}</RuntimeIdentifier>
    <SelfContained>{selfContained}</SelfContained>
    <PublishSingleFile>{singleFile}</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <PublishTrimmed>false</PublishTrimmed>
    <PublishReadyToRun>{readyToRun}</PublishReadyToRun>
    <DebugType>None</DebugType>
    <DebugSymbols>false</DebugSymbols>
    <AssemblyName>{Xml(assemblyName)}</AssemblyName>
    <RootNamespace>{Xml(assemblyName)}</RootNamespace>
    <AssemblyTitle>{Xml(configuration.ApplicationName)}</AssemblyTitle>
    <Product>{Xml(configuration.ProductName)}</Product>
    <Description>{Xml(configuration.Description)}</Description>
    <Company>{Xml(configuration.Company)}</Company>
    <Copyright>{Xml(configuration.Copyright)}</Copyright>
    <FileVersion>{Xml(configuration.FileVersion)}</FileVersion>
    <Version>{Xml(configuration.ProductVersion)}</Version>
    <ApplicationManifest>{manifestFileName}</ApplicationManifest>{iconProperty}
  </PropertyGroup>
  <ItemGroup>
    <EmbeddedResource Include="{Xml(scriptFileName)}" LogicalName="PS7ScriptDesk.ExportedScript" />
  </ItemGroup>{powerShellReference}
</Project>
""";
    }

    private static string BuildSettingsSource(ExeExportConfiguration configuration, string runtimePath, string scriptFileName)
    {
        var isGui = configuration.ApplicationType == ExeApplicationType.WindowsGui ? "true" : "false";
        var showError = configuration.ShowFatalErrorDialog ? "true" : "false";
        var writeLog = configuration.WriteApplicationLog ? "true" : "false";
        var includeStack = configuration.IncludePowerShellStackInformation ? "true" : "false";
        var loadProfile = configuration.LoadPowerShellProfile ? "true" : "false";
        return string.Join(Environment.NewLine,
            "internal static class ExportSettings",
            "{",
            $"    public const bool IsGui = {isGui};",
            $"    public const bool ShowFatalErrorDialog = {showError};",
            $"    public const bool WriteApplicationLog = {writeLog};",
            $"    public const bool IncludePowerShellStackInformation = {includeStack};",
            $"    public const bool LoadProfile = {loadProfile};",
            $"    public const string ScriptFileName = {CSharp(scriptFileName)};",
            $"    public const string PreferredPowerShellPath = {CSharp(runtimePath)};",
            $"    public const string WorkingDirectory = {CSharp(configuration.WorkingDirectory ?? string.Empty)};",
            $"    public const string AdditionalModulePaths = {CSharp(configuration.AdditionalModulePaths ?? string.Empty)};",
            $"    public const string ApplicationName = {CSharp(configuration.ApplicationName)};",
            "}",
            string.Empty);
    }

    private static string BuildManifest(ExeAdministratorMode administratorMode)
    {
        var level = administratorMode switch
        {
            ExeAdministratorMode.RequestElevation => "highestAvailable",
            ExeAdministratorMode.RequireAdministrator => "requireAdministrator",
            _ => "asInvoker"
        };
        return $"""
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges>
        <requestedExecutionLevel level="{level}" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
</assembly>
""";
    }

    private static string BuildEmbeddedHostSource() => """
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? workspace = null;
        try
        {
            workspace = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk", "ExportedApps", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            var scriptPath = Path.Combine(workspace, ExportSettings.ScriptFileName);
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("PS7ScriptDesk.ExportedScript") ?? throw new InvalidOperationException("The embedded PowerShell script was not found."))
            using (var output = File.Create(scriptPath))
                input.CopyTo(output);

            if (!string.IsNullOrWhiteSpace(ExportSettings.WorkingDirectory) && Directory.Exists(ExportSettings.WorkingDirectory))
                Directory.SetCurrentDirectory(ExportSettings.WorkingDirectory);
            if (!string.IsNullOrWhiteSpace(ExportSettings.AdditionalModulePaths))
                Environment.SetEnvironmentVariable("PSModulePath", ExportSettings.AdditionalModulePaths + Path.PathSeparator + Environment.GetEnvironmentVariable("PSModulePath"));

            var initialSessionState = InitialSessionState.CreateDefault2();
            initialSessionState.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var powerShell = PowerShell.Create(initialSessionState);
            powerShell.Streams.Error.DataAdded += (_, eventArgs) => ReportError(powerShell.Streams.Error[eventArgs.Index].ToString());
            powerShell.AddScript("param($scriptPath, [string[]]$forwardedArguments) & $scriptPath @forwardedArguments")
                .AddParameter("scriptPath", scriptPath)
                .AddParameter("forwardedArguments", args);
            foreach (var output in powerShell.Invoke())
                ReportOutput(output?.ToString() ?? string.Empty);
            return powerShell.HadErrors ? 1 : 0;
        }
        catch (Exception exception)
        {
            ReportError(ExportSettings.IncludePowerShellStackInformation ? exception.ToString() : exception.Message);
            return 1;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(workspace))
            {
                try { Directory.Delete(workspace, recursive: true); } catch { }
            }
        }
    }

    private static void ReportOutput(string text)
    {
        if (!ExportSettings.IsGui)
            Console.Out.WriteLine(text);
        if (ExportSettings.WriteApplicationLog)
            AppendLog(text);
    }

    private static void ReportError(string text)
    {
        if (!ExportSettings.IsGui)
            Console.Error.WriteLine(text);
        if (ExportSettings.WriteApplicationLog)
            AppendLog(text);
        if (ExportSettings.IsGui && ExportSettings.ShowFatalErrorDialog)
            MessageBoxW(0, text, ExportSettings.ApplicationName, 0x10);
    }

    private static void AppendLog(string text)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PS7ScriptDesk", "ExportedApps");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, ExportSettings.ApplicationName + ".log"), $"[{DateTimeOffset.Now:O}] {text}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
""";

    private static string BuildInstalledPowerShellHostSource() => """
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? workspace = null;
        try
        {
            workspace = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk", "ExportedApps", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            var scriptPath = Path.Combine(workspace, ExportSettings.ScriptFileName);
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("PS7ScriptDesk.ExportedScript") ?? throw new InvalidOperationException("The embedded PowerShell script was not found."))
            using (var output = File.Create(scriptPath)) input.CopyTo(output);
            var executable = File.Exists(ExportSettings.PreferredPowerShellPath) ? ExportSettings.PreferredPowerShellPath : "pwsh.exe";
            var startInfo = new ProcessStartInfo { FileName = executable, UseShellExecute = false, WorkingDirectory = string.IsNullOrWhiteSpace(ExportSettings.WorkingDirectory) ? AppContext.BaseDirectory : ExportSettings.WorkingDirectory };
            startInfo.ArgumentList.Add("-NoLogo");
            if (!ExportSettings.LoadProfile) startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            foreach (var argument in args) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start PowerShell.");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception exception)
        {
            if (!ExportSettings.IsGui) Console.Error.WriteLine(exception.Message);
            if (ExportSettings.IsGui && ExportSettings.ShowFatalErrorDialog) MessageBoxW(0, exception.Message, ExportSettings.ApplicationName, 0x10);
            return 1;
        }
        finally { if (!string.IsNullOrWhiteSpace(workspace)) try { Directory.Delete(workspace, recursive: true); } catch { } }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
""";

    private static string MakeAssemblyName(string? name)
    {
        var builder = new StringBuilder();
        foreach (var character in name ?? string.Empty)
            builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        var value = builder.Length == 0 ? "ExportedPowerShellScript" : builder.ToString();
        return char.IsLetter(value[0]) || value[0] == '_' ? value : $"Exported_{value}";
    }

    private static string MakeScriptFileName(string sourcePath)
    {
        var name = Path.GetFileName(sourcePath);
        return string.IsNullOrWhiteSpace(name) || !name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ? "ExportedScript.ps1" : name;
    }

    private static string CSharp(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    private static string Xml(string value) => SecurityElement.Escape(value) ?? string.Empty;
}

internal sealed record GeneratedExeHostProject(string ProjectFilePath, string AssemblyName, ExeExportConfiguration Configuration);
