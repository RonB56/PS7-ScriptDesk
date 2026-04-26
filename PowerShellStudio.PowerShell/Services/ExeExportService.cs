using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PowerShellStudio.Application.Interfaces;
using PowerShellStudio.Domain.Models;

namespace PowerShellStudio.PowerShell.Services
{
    public class ExeExportService : IExeExportService
    {
        public async Task<ExeExportResult> ExportScriptAsExeAsync(ExeExportRequest request, CancellationToken cancellationToken = default)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.RuntimeInfo is null || !request.RuntimeInfo.IsPowerShell7OrLater)
            {
                return BuildFailureResult(
                    request.OutputExecutablePath,
                    "Export as EXE requires a detected PowerShell 7.x runtime.",
                    "The selected runtime was missing or was not PowerShell 7 or later.");
            }

            if (!File.Exists(request.SourceScriptPath))
            {
                return BuildFailureResult(
                    request.OutputExecutablePath,
                    "Export as EXE requires the active script to be saved first.",
                    $"The saved source script path was not found: {request.SourceScriptPath}");
            }

            if (!File.Exists(request.RuntimeInfo.ExecutablePath))
            {
                return BuildFailureResult(
                    request.OutputExecutablePath,
                    "The selected PowerShell 7.x runtime could not be found on disk.",
                    $"Missing runtime path: {request.RuntimeInfo.ExecutablePath}");
            }

            var tempRoot = Path.Combine(
                Path.GetTempPath(),
                "PowerShellStudio",
                "ExeExport",
                Guid.NewGuid().ToString("N"));

            var projectDirectory = Path.Combine(tempRoot, "project");
            var publishDirectory = Path.Combine(tempRoot, "publish");
            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(publishDirectory);

            try
            {
                var assemblyName = BuildSafeAssemblyName(Path.GetFileNameWithoutExtension(request.OutputExecutablePath));
                var projectFilePath = Path.Combine(projectDirectory, $"{assemblyName}.csproj");
                var programFilePath = Path.Combine(projectDirectory, "Program.cs");
                var runtimeIdentifier = GetRuntimeIdentifier();

                File.WriteAllText(projectFilePath, BuildProjectFile(assemblyName), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.WriteAllText(programFilePath, BuildProgramSource(request), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                var publishResult = await RunDotNetPublishAsync(
                    projectFilePath,
                    publishDirectory,
                    runtimeIdentifier,
                    cancellationToken).ConfigureAwait(false);

                if (!publishResult.Started)
                {
                    return BuildFailureResult(
                        request.OutputExecutablePath,
                        "Export as EXE requires the local .NET SDK 'dotnet' command.",
                        publishResult.LogText);
                }

                if (publishResult.ExitCode != 0)
                {
                    return BuildFailureResult(
                        request.OutputExecutablePath,
                        "The local wrapper build failed while publishing the exported executable.",
                        publishResult.LogText);
                }

                var publishedExecutablePath = Path.Combine(publishDirectory, $"{assemblyName}.exe");
                if (!File.Exists(publishedExecutablePath))
                {
                    var discoveredExecutablePath = Directory
                        .EnumerateFiles(publishDirectory, "*.exe", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault();

                    if (string.IsNullOrWhiteSpace(discoveredExecutablePath))
                    {
                        return BuildFailureResult(
                            request.OutputExecutablePath,
                            "The export completed without producing an executable file.",
                            publishResult.LogText);
                    }

                    publishedExecutablePath = discoveredExecutablePath;
                }

                var outputDirectory = Path.GetDirectoryName(request.OutputExecutablePath);
                if (string.IsNullOrWhiteSpace(outputDirectory))
                {
                    return BuildFailureResult(
                        request.OutputExecutablePath,
                        "The chosen EXE destination path was invalid.",
                        $"Invalid output path: {request.OutputExecutablePath}");
                }

                Directory.CreateDirectory(outputDirectory);
                File.Copy(publishedExecutablePath, request.OutputExecutablePath, overwrite: true);

                var successLog = new StringBuilder();
                successLog.AppendLine($"Selected runtime: {request.RuntimeInfo.DisplayName}");
                successLog.AppendLine($"Selected runtime path: {request.RuntimeInfo.ExecutablePath}");
                successLog.AppendLine($"Source script: {request.SourceScriptPath}");
                successLog.AppendLine($"Output executable: {request.OutputExecutablePath}");
                successLog.AppendLine($"Publish RID: {runtimeIdentifier}");
                successLog.AppendLine("Approach: local .NET windowed wrapper EXE that launches PowerShell 7 in STA mode and runs the embedded script through a guarded bootstrap command.");
                successLog.AppendLine("Runtime requirement at EXE launch time: PowerShell 7.x plus the matching .NET runtime for the generated wrapper.");
                successLog.AppendLine();
                successLog.Append(publishResult.LogText);

                return new ExeExportResult(
                    succeeded: true,
                    outputExecutablePath: request.OutputExecutablePath,
                    summaryMessage: "Export as EXE completed successfully.",
                    detailedLog: successLog.ToString().Trim());
            }
            catch (Exception ex)
            {
                return BuildFailureResult(
                    request.OutputExecutablePath,
                    "Export as EXE failed unexpectedly.",
                    ex.ToString());
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        private static string BuildProjectFile(string assemblyName)
        {
            return $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>{assemblyName}</AssemblyName>
    <RootNamespace>{assemblyName}</RootNamespace>
  </PropertyGroup>
</Project>
";
        }

        private static string BuildProgramSource(ExeExportRequest request)
        {
            var scriptBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.ScriptContent));
            var scriptFileName = Path.GetFileName(request.SourceScriptPath);
            var selectedRuntimePath = request.RuntimeInfo.ExecutablePath;
            var selectedRuntimeDisplayName = request.RuntimeInfo.DisplayName;
            var originalScriptDirectory = Path.GetDirectoryName(request.SourceScriptPath) ?? string.Empty;

            return $@"using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

internal static class Program
{{
    private const string EmbeddedScriptBase64 = {ToCSharpStringLiteral(scriptBase64)};
    private const string EmbeddedScriptFileName = {ToCSharpStringLiteral(scriptFileName)};
    private const string PreferredRuntimePath = {ToCSharpStringLiteral(selectedRuntimePath)};
    private const string PreferredRuntimeDisplayName = {ToCSharpStringLiteral(selectedRuntimeDisplayName)};
    private const string OriginalScriptDirectory = {ToCSharpStringLiteral(originalScriptDirectory)};

    [STAThread]
    private static int Main(string[] args)
    {{
        try
        {{
            var runtimePath = ResolveRuntimePath();
            if (string.IsNullOrWhiteSpace(runtimePath))
            {{
                var runtimeMessage =
                    ""This exported executable requires PowerShell 7.x (pwsh.exe) to be installed."" + Environment.NewLine + Environment.NewLine +
                    $""Preferred runtime at export time: {{PreferredRuntimeDisplayName}}"" + Environment.NewLine +
                    $""Preferred runtime path at export time: {{PreferredRuntimePath}}"";

                try
                {{
                    Console.Error.WriteLine(runtimeMessage);
                }}
                catch
                {{
                }}

                ShowLauncherError(""Exported PowerShell Script"", runtimeMessage);
                return 2;
            }}

            var scriptFileName = string.IsNullOrWhiteSpace(EmbeddedScriptFileName)
                ? ""exported-script.ps1""
                : EmbeddedScriptFileName;

            var tempDirectory = Path.Combine(Path.GetTempPath(), ""PowerShellStudio.ExportedScripts"", Guid.NewGuid().ToString(""N""));
            Directory.CreateDirectory(tempDirectory);
            var tempScriptPath = Path.Combine(tempDirectory, scriptFileName);
            var scriptText = Encoding.UTF8.GetString(Convert.FromBase64String(EmbeddedScriptBase64));
            File.WriteAllText(tempScriptPath, scriptText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            try
            {{
                var errorCapturePath = Path.Combine(tempDirectory, ""launcher-error.txt"");
                var bootstrapCommand = BuildBootstrapCommand(tempScriptPath);
                var encodedBootstrapCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrapCommand));

                var startInfo = new ProcessStartInfo
                {{
                    FileName = runtimePath,
                    UseShellExecute = false,
                    WorkingDirectory = ResolveWorkingDirectory(),
                    CreateNoWindow = true
                }};

                startInfo.ArgumentList.Add(""-NoLogo"");
                startInfo.ArgumentList.Add(""-NoProfile"");
                startInfo.ArgumentList.Add(""-STA"");
                startInfo.ArgumentList.Add(""-ExecutionPolicy"");
                startInfo.ArgumentList.Add(""Bypass"");
                startInfo.ArgumentList.Add(""-EncodedCommand"");
                startInfo.ArgumentList.Add(encodedBootstrapCommand);

                foreach (var arg in args)
                {{
                    startInfo.ArgumentList.Add(arg);
                }}

                if (!string.IsNullOrWhiteSpace(OriginalScriptDirectory))
                {{
                    startInfo.EnvironmentVariables[""POWERSHELLSTUDIO_EXPORTED_SCRIPT_ORIGINAL_DIRECTORY""] = OriginalScriptDirectory;
                }}

                startInfo.EnvironmentVariables[""POWERSHELLSTUDIO_EXPORTED_SCRIPT_ERROR_FILE""] = errorCapturePath;
                startInfo.EnvironmentVariables[""POWERSHELLSTUDIO_EXPORTED_SCRIPT_SOURCE""] = scriptFileName;

                using var process = Process.Start(startInfo);
                if (process is null)
                {{
                    ShowLauncherError(
                        ""Exported PowerShell Script"",
                        ""The exported executable could not start the selected PowerShell runtime."");
                    return 3;
                }}

                process.WaitForExit();

                if (process.ExitCode != 0 && File.Exists(errorCapturePath))
                {{
                    try
                    {{
                        var capturedError = File.ReadAllText(errorCapturePath);
                        if (!string.IsNullOrWhiteSpace(capturedError))
                        {{
                            ShowLauncherError(""Exported PowerShell Script Error"", capturedError.Trim());
                        }}
                    }}
                    catch
                    {{
                    }}
                }}

                return process.ExitCode;
            }}
            finally
            {{
                TryDeleteDirectory(tempDirectory);
            }}
        }}
        catch (Exception ex)
        {{
            var message = ""The exported PowerShell launcher failed."" + Environment.NewLine + Environment.NewLine + ex;

            try
            {{
                Console.Error.WriteLine(message);
            }}
            catch
            {{
            }}

            ShowLauncherError(""Exported PowerShell Script"", message);
            return 1;
        }}
    }}

    private static string BuildBootstrapCommand(string tempScriptPath)
    {{
        var builder = new StringBuilder();
        builder.AppendLine(""param([Parameter(ValueFromRemainingArguments = $true)][string[]]$ForwardedArguments)"");
        builder.AppendLine(""$ErrorActionPreference = 'Stop'"");
        builder.AppendLine($""$embeddedScriptPath = {{ToPowerShellSingleQuotedLiteral(tempScriptPath)}}"");
        builder.AppendLine(""$originalScriptDirectory = $env:POWERSHELLSTUDIO_EXPORTED_SCRIPT_ORIGINAL_DIRECTORY"");
        builder.AppendLine(""$errorCapturePath = $env:POWERSHELLSTUDIO_EXPORTED_SCRIPT_ERROR_FILE"");
        builder.AppendLine(""try"");
        builder.AppendLine(""{{"");
        builder.AppendLine(""    if (-not [string]::IsNullOrWhiteSpace($originalScriptDirectory) -and (Test-Path -LiteralPath $originalScriptDirectory -PathType Container))"");
        builder.AppendLine(""    {{"");
        builder.AppendLine(""        Set-Location -LiteralPath $originalScriptDirectory"");
        builder.AppendLine(""    }}"");
        builder.AppendLine("""");
        builder.AppendLine(""    & $embeddedScriptPath @ForwardedArguments"");
        builder.AppendLine("""");
        builder.AppendLine(""    if ($LASTEXITCODE -is [int])"");
        builder.AppendLine(""    {{"");
        builder.AppendLine(""        exit $LASTEXITCODE"");
        builder.AppendLine(""    }}"");
        builder.AppendLine("""");
        builder.AppendLine(""    exit 0"");
        builder.AppendLine(""}}"");
        builder.AppendLine(""catch"");
        builder.AppendLine(""{{"");
        builder.AppendLine(""    $errorText = $_ | Out-String"");
        builder.AppendLine("""");
        builder.AppendLine(""    if (-not [string]::IsNullOrWhiteSpace($errorCapturePath))"");
        builder.AppendLine(""    {{"");
        builder.AppendLine(""        try"");
        builder.AppendLine(""        {{"");
        builder.AppendLine(""            Set-Content -LiteralPath $errorCapturePath -Value $errorText -Encoding UTF8"");
        builder.AppendLine(""        }}"");
        builder.AppendLine(""        catch"");
        builder.AppendLine(""        {{"");
        builder.AppendLine(""        }}"");
        builder.AppendLine(""    }}"");
        builder.AppendLine("""");
        builder.AppendLine(""    try"");
        builder.AppendLine(""    {{"");
        builder.AppendLine(""        Add-Type -AssemblyName PresentationFramework -ErrorAction Stop"");
        builder.AppendLine(""        [System.Windows.MessageBox]::Show($errorText, 'Exported PowerShell Script Error', 'OK', 'Error') | Out-Null"");
        builder.AppendLine(""    }}"");
        builder.AppendLine(""    catch"");
        builder.AppendLine(""    {{"");
        builder.AppendLine(""        try"");
        builder.AppendLine(""        {{"");
        builder.AppendLine(""            [Console]::Error.WriteLine($errorText)"");
        builder.AppendLine(""        }}"");
        builder.AppendLine(""        catch"");
        builder.AppendLine(""        {{"");
        builder.AppendLine(""        }}"");
        builder.AppendLine(""    }}"");
        builder.AppendLine("""");
        builder.AppendLine(""    exit 1"");
        builder.AppendLine(""}}"");

        return builder.ToString();
    }}

    private static string ResolveWorkingDirectory()
    {{
        if (!string.IsNullOrWhiteSpace(OriginalScriptDirectory) && Directory.Exists(OriginalScriptDirectory))
        {{
            return OriginalScriptDirectory;
        }}

        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory) && Directory.Exists(AppContext.BaseDirectory))
        {{
            return AppContext.BaseDirectory;
        }}

        return Environment.CurrentDirectory;
    }}

    private static string ToPowerShellSingleQuotedLiteral(string value)
    {{
        return ""'"" + (value ?? string.Empty).Replace(""'"", ""''"", StringComparison.Ordinal) + ""'"";
    }}

    [System.Runtime.InteropServices.DllImport(""user32.dll"", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    private static void ShowLauncherError(string caption, string message)
    {{
        try
        {{
            MessageBoxW(0, message, caption, 0x00000010);
        }}
        catch
        {{
        }}
    }}

    private static string? ResolveRuntimePath()
    {{
        if (!string.IsNullOrWhiteSpace(PreferredRuntimePath) && File.Exists(PreferredRuntimePath))
        {{
            return PreferredRuntimePath;
        }}

        var candidates = new List<string>();
        AddPathCandidates(candidates);
        AddProgramFilesCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddProgramFilesCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        return candidates
            .Where(static path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }}

    private static void AddPathCandidates(List<string> candidates)
    {{
        var pathValue = Environment.GetEnvironmentVariable(""PATH"");
        if (string.IsNullOrWhiteSpace(pathValue))
        {{
            return;
        }}

        foreach (var segment in pathValue.Split(Path.PathSeparator))
        {{
            if (string.IsNullOrWhiteSpace(segment))
            {{
                continue;
            }}

            try
            {{
                var candidate = Path.Combine(segment.Trim(), ""pwsh.exe"");
                if (File.Exists(candidate))
                {{
                    candidates.Add(candidate);
                }}
            }}
            catch
            {{
            }}
        }}
    }}

    private static void AddProgramFilesCandidates(List<string> candidates, string programFilesRoot)
    {{
        if (string.IsNullOrWhiteSpace(programFilesRoot))
        {{
            return;
        }}

        try
        {{
            var powerShellRoot = Path.Combine(programFilesRoot, ""PowerShell"");
            if (!Directory.Exists(powerShellRoot))
            {{
                return;
            }}

            foreach (var directory in Directory.EnumerateDirectories(powerShellRoot))
            {{
                var candidate = Path.Combine(directory, ""pwsh.exe"");
                if (File.Exists(candidate))
                {{
                    candidates.Add(candidate);
                }}
            }}
        }}
        catch
        {{
        }}
    }}

    private static void TryDeleteDirectory(string directoryPath)
    {{
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {{
            return;
        }}

        try
        {{
            Directory.Delete(directoryPath, recursive: true);
        }}
        catch
        {{
        }}
    }}
}}
";
        }

        private static async Task<DotNetPublishResult> RunDotNetPublishAsync(
            string projectFilePath,
            string publishDirectory,
            string runtimeIdentifier,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(projectFilePath) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("publish");
            startInfo.ArgumentList.Add(Path.GetFileName(projectFilePath));
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("Release");
            startInfo.ArgumentList.Add("-r");
            startInfo.ArgumentList.Add(runtimeIdentifier);
            startInfo.ArgumentList.Add("--self-contained");
            startInfo.ArgumentList.Add("false");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(publishDirectory);
            startInfo.ArgumentList.Add("/p:PublishSingleFile=true");
            startInfo.ArgumentList.Add("/p:PublishTrimmed=false");
            startInfo.ArgumentList.Add("/p:DebugType=None");
            startInfo.ArgumentList.Add("/p:DebugSymbols=false");
            startInfo.ArgumentList.Add("/p:EnableCompressionInSingleFile=true");

            using var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                {
                    return new DotNetPublishResult(false, -1, "The local 'dotnet' process could not be started.");
                }
            }
            catch (Win32Exception ex)
            {
                return new DotNetPublishResult(false, -1, $"The local 'dotnet' command was not found or could not be started. {ex.Message}");
            }

            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var standardOutput = await standardOutputTask.ConfigureAwait(false);
            var standardError = await standardErrorTask.ConfigureAwait(false);

            var logText = BuildCombinedLogText(startInfo, standardOutput, standardError);
            return new DotNetPublishResult(true, process.ExitCode, logText);
        }

        private static string BuildCombinedLogText(ProcessStartInfo startInfo, string standardOutput, string standardError)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"Command: {startInfo.FileName} {string.Join(" ", startInfo.ArgumentList)}");

            if (!string.IsNullOrWhiteSpace(standardOutput))
            {
                builder.AppendLine();
                builder.AppendLine("dotnet stdout:");
                builder.AppendLine(standardOutput.Trim());
            }

            if (!string.IsNullOrWhiteSpace(standardError))
            {
                builder.AppendLine();
                builder.AppendLine("dotnet stderr:");
                builder.AppendLine(standardError.Trim());
            }

            return builder.ToString().Trim();
        }

        private static string BuildSafeAssemblyName(string? fileNameWithoutExtension)
        {
            var candidate = string.IsNullOrWhiteSpace(fileNameWithoutExtension)
                ? "ExportedPowerShellScript"
                : Regex.Replace(fileNameWithoutExtension, "[^A-Za-z0-9_.]", string.Empty);

            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = "ExportedPowerShellScript";
            }

            if (!char.IsLetter(candidate[0]) && candidate[0] != '_')
            {
                candidate = $"Exported_{candidate}";
            }

            return candidate;
        }

        private static string GetRuntimeIdentifier()
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "win-arm64",
                Architecture.X86 => "win-x86",
                _ => "win-x64"
            };
        }

        private static string ToCSharpStringLiteral(string value)
        {
            return "\"" + value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                + "\"";
        }

        private static ExeExportResult BuildFailureResult(string outputExecutablePath, string summaryMessage, string detailedLog)
        {
            return new ExeExportResult(
                succeeded: false,
                outputExecutablePath: outputExecutablePath,
                summaryMessage: summaryMessage,
                detailedLog: detailedLog);
        }

        private static void TryDeleteDirectory(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                return;
            }

            try
            {
                Directory.Delete(directoryPath, recursive: true);
            }
            catch
            {
            }
        }

        private sealed class DotNetPublishResult
        {
            public DotNetPublishResult(bool started, int exitCode, string logText)
            {
                Started = started;
                ExitCode = exitCode;
                LogText = logText;
            }

            public bool Started { get; }

            public int ExitCode { get; }

            public string LogText { get; }
        }
    }
}
