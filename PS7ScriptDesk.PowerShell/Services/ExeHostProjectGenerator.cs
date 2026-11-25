using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

internal sealed class ExeHostProjectGenerator
{
    private const string RuntimeManifestFileName = "runtime.manifest";

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
        var bundled = configuration.PowerShellRuntimeModel == ExePowerShellRuntimeModel.Embedded;
        var runtimeRoot = GetRuntimeRoot(request.RuntimeInfo);
        string? runtimeHash = null;
        var targetRuntimeArchitecture = configuration.Architecture == ExeTargetArchitecture.Arm64 ? "arm64" : "x64";
        if (!string.Equals(request.RuntimeInfo.Architecture, targetRuntimeArchitecture, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The selected PowerShell runtime architecture ({request.RuntimeInfo.Architecture}) does not match the export target ({targetRuntimeArchitecture}).");

        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(scriptFilePath, request.ScriptContent, new UTF8Encoding(false));
        if (bundled)
        {
            if (configuration.Architecture != ExeTargetArchitecture.X64)
                throw new InvalidOperationException("Bundled PowerShell is currently available only for Windows x64.");
            ValidateRuntimeDistribution(runtimeRoot, request.RuntimeInfo);
            if (configuration.PackageFormat == ExePackageFormat.SingleFile)
            {
                var archivePath = Path.Combine(projectDirectory, "runtime.payload.zip");
                ZipFile.CreateFromDirectory(runtimeRoot, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
                runtimeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath)));
                CreateRuntimeManifest(runtimeRoot, Path.Combine(projectDirectory, RuntimeManifestFileName));
            }
            else
            {
                CopyDirectory(runtimeRoot, Path.Combine(projectDirectory, "Runtime"));
                CreateRuntimeManifest(Path.Combine(projectDirectory, "Runtime"), Path.Combine(projectDirectory, "Runtime", RuntimeManifestFileName));
            }
        }
        File.WriteAllText(settingsFilePath, BuildSettingsSource(configuration, request.RuntimeInfo.LaunchExecutablePath, scriptFileName, bundled, runtimeHash, request.RuntimeInfo.Version.ToString()), new UTF8Encoding(false));
        File.WriteAllText(programFilePath, BuildNativeProcessHostSource(), new UTF8Encoding(false));

        string? iconFileName = null;
        if (!string.IsNullOrWhiteSpace(configuration.IconPath))
        {
            iconFileName = Path.GetFileName(configuration.IconPath);
            File.Copy(configuration.IconPath, Path.Combine(projectDirectory, iconFileName), overwrite: true);
        }

        var manifestFileName = "app.manifest";
        File.WriteAllText(Path.Combine(projectDirectory, manifestFileName), BuildManifest(configuration.AdministratorMode), new UTF8Encoding(false));
        File.WriteAllText(projectFilePath, BuildProjectFile(configuration, assemblyName, scriptFileName, manifestFileName, iconFileName, bundled), new UTF8Encoding(false));
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

    private static string BuildProjectFile(ExeExportConfiguration configuration, string assemblyName, string scriptFileName, string manifestFileName, string? iconFileName, bool bundled)
    {
        var outputType = configuration.ApplicationType == ExeApplicationType.WindowsGui ? "WinExe" : "Exe";
        var selfContained = configuration.DeploymentModel == ExeDeploymentModel.SelfContained ? "true" : "false";
        var singleFile = configuration.PackageFormat == ExePackageFormat.SingleFile ? "true" : "false";
        var readyToRun = configuration.OptimizationProfile == ExeOptimizationProfile.FastStartup ? "true" : "false";
        var iconProperty = string.IsNullOrWhiteSpace(iconFileName) ? string.Empty : $"\n    <ApplicationIcon>{Xml(iconFileName)}</ApplicationIcon>";
        var runtimeContent = bundled && configuration.PackageFormat == ExePackageFormat.ApplicationFolder
            ? "\n    <Content Include=\"Runtime\\**\\*\" CopyToPublishDirectory=\"PreserveNewest\" />"
            : string.Empty;
        var runtimeResource = bundled && configuration.PackageFormat == ExePackageFormat.SingleFile
            ? "\n    <EmbeddedResource Include=\"runtime.payload.zip\" LogicalName=\"PS7ScriptDesk.ExportedRuntime\" />\n    <EmbeddedResource Include=\"runtime.manifest\" LogicalName=\"PS7ScriptDesk.ExportedRuntimeManifest\" />"
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
  </ItemGroup><ItemGroup>{runtimeContent}{runtimeResource}
  </ItemGroup>
</Project>
""";
    }

    private static string BuildSettingsSource(ExeExportConfiguration configuration, string runtimePath, string scriptFileName, bool bundled, string? runtimeHash, string runtimeVersion)
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
            $"    public const bool BundledRuntime = {(bundled ? "true" : "false")};",
            $"    public const string ApartmentState = {CSharp(configuration.ApartmentState.ToString())};",
            $"    public const string RuntimeHash = {CSharp(runtimeHash ?? string.Empty)};",
            $"    public const string PackageFormat = {CSharp(configuration.PackageFormat.ToString())};",
            $"    public const string ScriptFileName = {CSharp(scriptFileName)};",
            $"    public const string PreferredPowerShellPath = {CSharp(runtimePath)};",
            $"    public const string WorkingDirectory = {CSharp(configuration.WorkingDirectory ?? string.Empty)};",
            $"    public const string AdditionalModulePaths = {CSharp(configuration.AdditionalModulePaths ?? string.Empty)};",
            $"    public const string ExpectedRuntimeVersion = {CSharp(runtimeVersion)};",
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

    private static string GetRuntimeRoot(PS7ScriptDesk.Domain.Models.PowerShellRuntimeInfo runtime)
    {
        var root = !string.IsNullOrWhiteSpace(runtime.PsHome) ? runtime.PsHome : Path.GetDirectoryName(runtime.LaunchExecutablePath);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException("The selected PowerShell runtime folder could not be located.");
        return Path.GetFullPath(root);
    }

    private static void ValidateRuntimeDistribution(string runtimeRoot, PS7ScriptDesk.Domain.Models.PowerShellRuntimeInfo runtime)
    {
        var pwsh = Path.Combine(runtimeRoot, "pwsh.exe");
        if (!File.Exists(pwsh) || !File.Exists(Path.Combine(runtimeRoot, "pwsh.deps.json")) ||
            !File.Exists(Path.Combine(runtimeRoot, "pwsh.runtimeconfig.json")) ||
            !Directory.Exists(Path.Combine(runtimeRoot, "Modules", "Microsoft.PowerShell.Utility")) ||
            !Directory.Exists(Path.Combine(runtimeRoot, "Modules", "Microsoft.PowerShell.Management")) ||
            !File.Exists(Path.Combine(runtimeRoot, "LICENSE.txt")) || !File.Exists(Path.Combine(runtimeRoot, "ThirdPartyNotices.txt")))
            throw new InvalidDataException("The selected PowerShell distribution is incomplete. A complete PowerShell 7 distribution with built-in modules and notices is required for bundled export.");
        if (!string.Equals(runtime.Architecture, "x64", StringComparison.OrdinalIgnoreCase) || runtime.Version < new Version(7, 4))
            throw new InvalidOperationException("Bundled export currently requires a validated PowerShell 7.4 or later Windows x64 runtime.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void CreateRuntimeManifest(string runtimeRoot, string manifestPath)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var lines = Directory.EnumerateFiles(runtimeRoot, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFullPath(path), fullManifestPath, StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                using var stream = File.OpenRead(path);
                var hash = Convert.ToHexString(SHA256.HashData(stream));
                return $"{hash}  {Path.GetRelativePath(runtimeRoot, path).Replace('\\', '/')}";
            })
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase);
        var manifestLines = lines.ToArray();
        File.WriteAllLines(fullManifestPath, manifestLines, new UTF8Encoding(false));
    }

    private static string BuildNativeProcessHostSource() => """
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? workspace = null;
        try
        {
            var applicationRoot = AppContext.BaseDirectory;
            var runtimeRoot = ExportSettings.BundledRuntime
                ? (ExportSettings.PackageFormat == "SingleFile" ? MaterializeRuntime() : Path.Combine(applicationRoot, "Runtime"))
                : Path.GetDirectoryName(ExportSettings.PreferredPowerShellPath);
            if (string.IsNullOrWhiteSpace(runtimeRoot)) throw new InvalidOperationException("The selected PowerShell runtime location is invalid.");
            runtimeRoot = Path.GetFullPath(runtimeRoot);
            var executable = ExportSettings.BundledRuntime ? Path.Combine(runtimeRoot, "pwsh.exe") : ExportSettings.PreferredPowerShellPath;
            ValidateRuntime(executable, runtimeRoot);

            workspace = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk", "ExportedApps", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            var scriptPath = Path.Combine(workspace, ExportSettings.ScriptFileName);
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("PS7ScriptDesk.ExportedScript") ?? throw new InvalidOperationException("The exported script resource is missing."))
            using (var output = File.Create(scriptPath)) input.CopyTo(output);

            var startInfo = new ProcessStartInfo { FileName = executable, UseShellExecute = false, WorkingDirectory = string.IsNullOrWhiteSpace(ExportSettings.WorkingDirectory) ? applicationRoot : ExportSettings.WorkingDirectory };
            startInfo.ArgumentList.Add("-NoLogo");
            if (!ExportSettings.LoadProfile) startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy"); startInfo.ArgumentList.Add("Bypass");
            if (ExportSettings.ApartmentState == "Sta") startInfo.ArgumentList.Add("-STA");
            else if (ExportSettings.ApartmentState == "Mta") startInfo.ArgumentList.Add("-MTA");
            startInfo.ArgumentList.Add("-File"); startInfo.ArgumentList.Add(scriptPath);
            foreach (var argument in args) startInfo.ArgumentList.Add(argument);
            startInfo.Environment["PSModulePath"] = Path.Combine(runtimeRoot, "Modules") + (string.IsNullOrWhiteSpace(ExportSettings.AdditionalModulePaths) ? "" : Path.PathSeparator + ExportSettings.AdditionalModulePaths);
            if (ExportSettings.IsGui) { startInfo.RedirectStandardOutput = true; startInfo.RedirectStandardError = true; }
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows could not start the selected PowerShell runtime.");
            var guiErrors = new StringBuilder();
            if (ExportSettings.IsGui)
            {
                process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null && ExportSettings.WriteApplicationLog) AppendLog(eventArgs.Data); };
                process.ErrorDataReceived += (_, eventArgs) =>
                {
                    if (eventArgs.Data is null) return;
                    lock (guiErrors) { if (guiErrors.Length < 8192) guiErrors.AppendLine(eventArgs.Data); }
                    if (ExportSettings.WriteApplicationLog) AppendLog(eventArgs.Data);
                };
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
            }
            process.WaitForExit();
            if (ExportSettings.IsGui && guiErrors.Length > 0)
            {
                var failure = guiErrors.ToString();
                if (ExportSettings.ShowFatalErrorDialog) MessageBoxW(0, failure, ExportSettings.ApplicationName, 0x10);
            }
            return process.ExitCode;
        }
        catch (Exception exception)
        {
            var message = ExportSettings.IncludePowerShellStackInformation ? exception.ToString() : exception.Message;
            ReportStartupFailure(message);
            return 1;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(workspace)) try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    private static void ValidateRuntime(string executable, string runtimeRoot)
    {
        if (!File.Exists(executable) || !File.Exists(Path.Combine(runtimeRoot, "pwsh.deps.json")) || !Directory.Exists(Path.Combine(runtimeRoot, "Modules", "Microsoft.PowerShell.Utility")) || !Directory.Exists(Path.Combine(runtimeRoot, "Modules", "Microsoft.PowerShell.Management")))
            throw new InvalidOperationException("The selected PowerShell runtime is incomplete or is not a compatible PowerShell 7 distribution.");
        var info = new ProcessStartInfo { FileName = executable, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add("-NoLogo"); info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop; Import-Module Microsoft.PowerShell.Management -ErrorAction Stop; $PSVersionTable.PSVersion.ToString()");
        info.Environment["PSModulePath"] = Path.Combine(runtimeRoot, "Modules");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("PowerShell runtime validation could not start.");
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0 || !Version.TryParse(output.Trim(), out var version) || version.Major < 7)
            throw new InvalidOperationException("The selected PowerShell runtime failed its version and built-in module startup check. " + error.Trim());
    }

    private static string MaterializeRuntime()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var archiveResource = assembly.GetManifestResourceStream("PS7ScriptDesk.ExportedRuntime") ?? throw new InvalidOperationException("The bundled PowerShell runtime payload is missing.");
        using var memory = new MemoryStream(); archiveResource.CopyTo(memory); var payload = memory.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        if (!string.Equals(hash, ExportSettings.RuntimeHash, StringComparison.Ordinal)) throw new InvalidDataException("The bundled PowerShell runtime payload failed its integrity check.");
        using var manifestStream = assembly.GetManifestResourceStream("PS7ScriptDesk.ExportedRuntimeManifest") ?? throw new InvalidOperationException("The bundled PowerShell runtime manifest is missing.");
        using var manifestReader = new StreamReader(manifestStream, Encoding.UTF8); var manifest = manifestReader.ReadToEnd();
        var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PS7ScriptDesk", "ExportedRuntime");
        var runtimePath = Path.Combine(cacheRoot, hash);
        Directory.CreateDirectory(cacheRoot);
        var gate = new Mutex(false, "Local\\PS7ScriptDesk_" + hash);
        gate.WaitOne();
        try
        {
            if (!ValidateManifest(runtimePath, manifest))
            {
                var staging = Path.Combine(cacheRoot, hash + ".stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
                try
                {
                    using var zip = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read);
                    foreach (var entry in zip.Entries)
                    {
                        var target = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                        if (!target.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The runtime payload contains an unsafe extraction path.");
                        if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!); using var input = entry.Open(); using var output = File.Create(target); input.CopyTo(output);
                    }
                    if (!ValidateManifest(staging, manifest)) throw new InvalidDataException("The extracted PowerShell runtime failed its integrity validation.");
                    if (Directory.Exists(runtimePath)) Directory.Delete(runtimePath, recursive: true);
                    Directory.Move(staging, runtimePath);
                }
                finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            }
            return runtimePath;
        }
        finally { gate.ReleaseMutex(); gate.Dispose(); }
    }

    private static bool ValidateManifest(string root, string manifest)
    {
        if (!Directory.Exists(root)) return false;
        foreach (var line in manifest.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf("  ", StringComparison.Ordinal); if (separator != 64) return false;
            var relative = line[(separator + 2)..].Replace('/', Path.DirectorySeparatorChar); var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
            using var stream = File.OpenRead(path); if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), line[..64], StringComparison.Ordinal)) return false;
        }
        return File.Exists(Path.Combine(root, "pwsh.exe")) && File.Exists(Path.Combine(root, "Modules", "Microsoft.PowerShell.Utility", "Microsoft.PowerShell.Utility.psd1"));
    }

    private static void ReportStartupFailure(string text)
    {
        if (!ExportSettings.IsGui) Console.Error.WriteLine(text);
        if (ExportSettings.WriteApplicationLog)
        {
            try { var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PS7ScriptDesk", "ExportedApps"); Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, ExportSettings.ApplicationName + ".log"), $"[{DateTimeOffset.Now:O}] {text}{Environment.NewLine}", Encoding.UTF8); } catch { }
        }
        if (ExportSettings.IsGui && ExportSettings.ShowFatalErrorDialog) MessageBoxW(0, text, ExportSettings.ApplicationName, 0x10);
    }

    private static void AppendLog(string text)
    {
        try { var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PS7ScriptDesk", "ExportedApps"); Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, ExportSettings.ApplicationName + ".log"), $"[{DateTimeOffset.Now:O}] {text}{Environment.NewLine}", Encoding.UTF8); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
""";

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
