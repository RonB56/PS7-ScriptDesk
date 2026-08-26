using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

public sealed class ApiBuildPublishService : IApiBuildPublishService
{
    private const string HostExecutableName = "PS7ScriptDesk.RestApiProofHost.exe";
    private const int MaximumCapturedOutputCharacters = 64 * 1024;
    private readonly IApiProjectGenerator _projectGenerator;
    private readonly IRestApiHostRuntimeLocator _runtimeLocator;
    private readonly IRestApiHostSmokeTester _smokeTester;

    public ApiBuildPublishService()
        : this(new ApiProjectGenerator(), new RestApiHostRuntimeLocator(), new RestApiHostSmokeTester())
    {
    }

    internal ApiBuildPublishService(
        IApiProjectGenerator projectGenerator,
        IRestApiHostRuntimeLocator runtimeLocator,
        IRestApiHostSmokeTester smokeTester)
    {
        _projectGenerator = projectGenerator ?? throw new ArgumentNullException(nameof(projectGenerator));
        _runtimeLocator = runtimeLocator ?? throw new ArgumentNullException(nameof(runtimeLocator));
        _smokeTester = smokeTester ?? throw new ArgumentNullException(nameof(smokeTester));
    }

    public async Task<ApiBuildPublishResult> GenerateProjectAsync(
        ApiBuildPublishRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ApiBuildPublishProgressUpdate>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operationId = $"RestApiGenerate-{Guid.NewGuid():N}";
        using var scope = DeveloperDiagnostics.BeginScope(operationId: operationId);

        try
        {
            ReportProgress(progress, "GeneratingProject", "Generating REST API project files.");
            var generation = await GenerateProjectCoreAsync(request, cancellationToken).ConfigureAwait(false);
            if (!generation.Succeeded)
            {
                DeveloperDiagnostics.LogWarning(
                    "RestApiBuildPublish",
                    "REST API project generation failed.",
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["errorCount"] = generation.ValidationErrors.Count
                    });
                return ApiBuildPublishResult.Failure(
                    generation.SummaryMessage,
                    SanitizePublicLog(generation.DetailedLog),
                    generation.DestinationDirectory,
                    generation.ProjectFilePath);
            }

            DeveloperDiagnostics.LogInfo(
                "RestApiBuildPublish",
                "REST API project generation completed.",
                new Dictionary<string, object?>
                {
                    ["operationId"] = operationId,
                    ["projectName"] = Path.GetFileNameWithoutExtension(generation.ProjectFilePath),
                    ["generatedFileCount"] = generation.GeneratedFiles.Count
                });

            return ApiBuildPublishResult.Success(
                "REST API project generated successfully.",
                SanitizePublicLog(generation.DetailedLog),
                generation.DestinationDirectory,
                generation.ProjectFilePath,
                []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeveloperDiagnostics.LogWarning(
                "RestApiBuildPublish",
                "REST API project generation was cancelled.",
                new Dictionary<string, object?> { ["operationId"] = operationId });
            return ApiBuildPublishResult.Failure(
                "REST API project generation was cancelled.",
                "The operation was cancelled before a complete generated project was reported.",
                request.ProjectDirectory,
                wasCancelled: true);
        }
    }

    public async Task<ApiBuildPublishResult> BuildAsync(
        ApiBuildPublishRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ApiBuildPublishProgressUpdate>? progress = null)
        => await BuildOrPublishAsync(
            request,
            publish: false,
            successMessage: "REST API build completed successfully.",
            operationPrefix: "RestApiBuild",
            cancellationToken,
            progress).ConfigureAwait(false);

    public async Task<ApiBuildPublishResult> PublishAsync(
        ApiBuildPublishRequest request,
        CancellationToken cancellationToken = default,
        IProgress<ApiBuildPublishProgressUpdate>? progress = null)
        => await BuildOrPublishAsync(
            request,
            publish: true,
            successMessage: "REST API publish completed successfully.",
            operationPrefix: "RestApiPublish",
            cancellationToken,
            progress).ConfigureAwait(false);

    private async Task<ApiBuildPublishResult> BuildOrPublishAsync(
        ApiBuildPublishRequest request,
        bool publish,
        string successMessage,
        string operationPrefix,
        CancellationToken cancellationToken,
        IProgress<ApiBuildPublishProgressUpdate>? progress)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operationId = $"{operationPrefix}-{Guid.NewGuid():N}";
        var artifacts = new List<ApiBuildPublishArtifact>();
        var details = new StringBuilder();
        using var scope = DeveloperDiagnostics.BeginScope(operationId: operationId);

        try
        {
            ReportProgress(progress, "GeneratingProject", "Generating REST API project files.");
            var generation = await GenerateProjectCoreAsync(request, cancellationToken).ConfigureAwait(false);
            if (!generation.Succeeded)
            {
                DeveloperDiagnostics.LogWarning(
                    "RestApiBuildPublish",
                    "REST API build/publish stopped because project generation failed.",
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["errorCount"] = generation.ValidationErrors.Count
                    });
                return ApiBuildPublishResult.Failure(
                    generation.SummaryMessage,
                    SanitizePublicLog(generation.DetailedLog),
                    generation.DestinationDirectory,
                    generation.ProjectFilePath);
            }

            details.AppendLine(generation.DetailedLog);
            foreach (var target in ExpandTargets(request.TargetArchitecture))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var runtimeIdentifier = ToRuntimeIdentifier(target);
                ReportProgress(
                    progress,
                    publish ? "PublishingRuntime" : "BuildingRuntime",
                    publish
                        ? $"Publishing self-contained REST API for {runtimeIdentifier}."
                        : $"Building REST API runtime artifact for {runtimeIdentifier}.");

                var runtimeRoot = _runtimeLocator.Resolve(runtimeIdentifier);
                if (string.IsNullOrWhiteSpace(runtimeRoot) || !Directory.Exists(runtimeRoot))
                {
                    return FailWithDiagnostics(
                        "The bundled REST API runtime could not be found.",
                        $"The bundled {runtimeIdentifier} REST API runtime is not available.",
                        operationId,
                        generation);
                }

                var outputDirectory = ResolveOperationOutputDirectory(request, generation.DestinationDirectory, runtimeIdentifier, publish);
                PrepareCleanOutputDirectory(generation.DestinationDirectory, outputDirectory);
                CopyRuntimeLayout(runtimeRoot, outputDirectory, cancellationToken);
                CopyGeneratedApiContent(generation.DestinationDirectory, outputDirectory, cancellationToken);
                WriteLauncher(outputDirectory);

                var validation = ValidateOutput(outputDirectory, target, request.Configuration.SourceScript);
                if (!validation.IsValid)
                {
                    return FailWithDiagnostics(
                        "The REST API artifact did not pass validation.",
                        string.Join(Environment.NewLine, validation.Errors),
                        operationId,
                        generation);
                }

                var executablePath = Path.Combine(outputDirectory, HostExecutableName);
                var runtimeValidated = false;
                if (!publish && IsCurrentArchitecture(target))
                {
                    ReportProgress(progress, "ValidatingRuntime", "Starting the built REST API briefly to verify readiness.");
                    var smokeResult = await _smokeTester.VerifyAsync(
                        outputDirectory,
                        executablePath,
                        Path.Combine("Config", "api.ps7api.json"),
                        cancellationToken).ConfigureAwait(false);
                    if (!smokeResult.Succeeded)
                    {
                        return FailWithDiagnostics(
                            "The REST API build failed runtime validation.",
                            smokeResult.DetailedLog,
                            operationId,
                            generation);
                    }

                    runtimeValidated = true;
                    details.AppendLine(smokeResult.DetailedLog);
                }

                var artifact = new ApiBuildPublishArtifact(
                    target,
                    runtimeIdentifier,
                    outputDirectory,
                    executablePath,
                    new FileInfo(executablePath).Length,
                    runtimeValidated);
                artifacts.Add(artifact);
                details.AppendLine($"{runtimeIdentifier}: {outputDirectory}");
            }

            DeveloperDiagnostics.LogInfo(
                "RestApiBuildPublish",
                publish ? "REST API publish completed." : "REST API build completed.",
                new Dictionary<string, object?>
                {
                    ["operationId"] = operationId,
                    ["targetArchitecture"] = request.TargetArchitecture.ToString(),
                    ["artifactCount"] = artifacts.Count
                });

            ReportProgress(progress, "Ready", publish ? "REST API publish is ready." : "REST API build is ready.");
            return ApiBuildPublishResult.Success(
                successMessage,
                SanitizePublicLog(details.ToString()),
                generation.DestinationDirectory,
                generation.ProjectFilePath,
                artifacts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeveloperDiagnostics.LogWarning(
                "RestApiBuildPublish",
                publish ? "REST API publish was cancelled." : "REST API build was cancelled.",
                new Dictionary<string, object?> { ["operationId"] = operationId });
            return ApiBuildPublishResult.Failure(
                publish ? "REST API publish was cancelled." : "REST API build was cancelled.",
                "The operation was cancelled before a verified artifact was reported as ready.",
                request.ProjectDirectory,
                wasCancelled: true);
        }
        catch (Exception exception)
        {
            DeveloperDiagnostics.LogException(
                "RestApiBuildPublish",
                exception,
                publish ? "REST API publish failed unexpectedly." : "REST API build failed unexpectedly.",
                new Dictionary<string, object?>
                {
                    ["operationId"] = operationId,
                    ["targetArchitecture"] = request.TargetArchitecture.ToString()
                });
            return ApiBuildPublishResult.Failure(
                publish ? "REST API publish failed unexpectedly." : "REST API build failed unexpectedly.",
                SanitizePublicLog(exception.Message),
                request.ProjectDirectory);
        }
    }

    private async Task<ApiProjectGenerationResult> GenerateProjectCoreAsync(
        ApiBuildPublishRequest request,
        CancellationToken cancellationToken)
        => await _projectGenerator.GenerateAsync(
            new ApiProjectGenerationRequest(
                request.SourceScriptPath,
                request.Configuration,
                request.ProjectDirectory,
                request.ProjectName,
                request.OverwriteExistingGeneratedProject),
            cancellationToken).ConfigureAwait(false);

    private static ApiBuildPublishResult FailWithDiagnostics(
        string summaryMessage,
        string detailedLog,
        string operationId,
        ApiProjectGenerationResult generation)
    {
        var safeLog = SanitizePublicLog(detailedLog);
        var metadata = new Dictionary<string, object?>
        {
            ["operationId"] = operationId,
            ["projectName"] = Path.GetFileNameWithoutExtension(generation.ProjectFilePath)
        };
        foreach (var entry in DeveloperDiagnostics.CreatePrivateTextMetadata(detailedLog))
        {
            metadata[$"details{char.ToUpperInvariant(entry.Key[0])}{entry.Key[1..]}"] = entry.Value;
        }

        DeveloperDiagnostics.LogError("RestApiBuildPublish", summaryMessage, metadata);
        return ApiBuildPublishResult.Failure(
            summaryMessage,
            safeLog,
            generation.DestinationDirectory,
            generation.ProjectFilePath);
    }

    private static IReadOnlyList<ApiPublishTargetArchitecture> ExpandTargets(ApiPublishTargetArchitecture architecture)
        => architecture == ApiPublishTargetArchitecture.Both
            ? [ApiPublishTargetArchitecture.WinX64, ApiPublishTargetArchitecture.WinArm64]
            : [architecture];

    private static string ToRuntimeIdentifier(ApiPublishTargetArchitecture architecture)
        => architecture == ApiPublishTargetArchitecture.WinArm64 ? "win-arm64" : "win-x64";

    private static bool IsCurrentArchitecture(ApiPublishTargetArchitecture architecture)
        => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? architecture == ApiPublishTargetArchitecture.WinArm64
            : architecture == ApiPublishTargetArchitecture.WinX64;

    private static string ResolveOperationOutputDirectory(
        ApiBuildPublishRequest request,
        string projectDirectory,
        string runtimeIdentifier,
        bool publish)
    {
        if (!publish)
        {
            return Path.Combine(projectDirectory, "bin", "ScriptDeskBuild", runtimeIdentifier);
        }

        var root = string.IsNullOrWhiteSpace(request.PublishDirectory)
            ? Path.Combine(projectDirectory, "publish")
            : Path.GetFullPath(request.PublishDirectory);
        return Path.Combine(root, runtimeIdentifier);
    }

    private static void PrepareCleanOutputDirectory(string projectDirectory, string outputDirectory)
    {
        var fullProjectDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        if (!fullOutputDirectory.StartsWith(fullProjectDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The REST API build output must be inside the generated project directory.");
        }

        if (Directory.Exists(fullOutputDirectory))
        {
            Directory.Delete(fullOutputDirectory, recursive: true);
        }

        Directory.CreateDirectory(fullOutputDirectory);
    }

    private static void CopyRuntimeLayout(string runtimeRoot, string outputDirectory, CancellationToken cancellationToken)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(runtimeRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(runtimeRoot, sourcePath);
            if (IsGeneratedContentPath(relativePath) || IsWpfShellArtifact(relativePath))
            {
                continue;
            }

            var destinationPath = Path.Combine(outputDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? outputDirectory);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    private static void CopyGeneratedApiContent(string projectDirectory, string outputDirectory, CancellationToken cancellationToken)
    {
        foreach (var relativeRoot in new[] { "Config", "Scripts" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRoot = Path.Combine(projectDirectory, relativeRoot);
            if (!Directory.Exists(sourceRoot))
            {
                throw new DirectoryNotFoundException($"The generated API project is missing the '{relativeRoot}' folder.");
            }

            CopyDirectory(sourceRoot, Path.Combine(outputDirectory, relativeRoot), cancellationToken);
        }
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            var destinationPath = Path.Combine(destinationRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? destinationRoot);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    private static bool IsGeneratedContentPath(string relativePath)
        => relativePath.StartsWith("Config" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
           relativePath.StartsWith("Scripts" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
           relativePath.StartsWith("Config/", StringComparison.OrdinalIgnoreCase) ||
           relativePath.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase);

    private static bool IsWpfShellArtifact(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return string.Equals(fileName, "PS7ScriptDesk.Shell.dll", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "PS7ScriptDesk.Shell.exe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "MainWindow.xaml", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteLauncher(string outputDirectory)
    {
        var launcherPath = Path.Combine(outputDirectory, "Start-PS7ScriptDeskApi.cmd");
        var content = """
@echo off
setlocal
"%~dp0PS7ScriptDesk.RestApiProofHost.exe" --content-root "%~dp0." --config "Config\api.ps7api.json" %*
""";
        File.WriteAllText(launcherPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static ExeExportValidationResult ValidateOutput(
        string outputDirectory,
        ApiPublishTargetArchitecture architecture,
        string configuredScriptFileName)
    {
        var result = new ExeExportValidationResult();
        var executablePath = Path.Combine(outputDirectory, HostExecutableName);
        var verifier = new ExecutableVerifier();
        var executableValidation = verifier.Verify(
            executablePath,
            architecture == ApiPublishTargetArchitecture.WinArm64 ? ExeTargetArchitecture.Arm64 : ExeTargetArchitecture.X64);
        result.Errors.AddRange(executableValidation.Errors);

        RequireFile(result, outputDirectory, "PS7ScriptDesk.RestApiProofHost.deps.json");
        RequireFile(result, outputDirectory, "PS7ScriptDesk.RestApiProofHost.runtimeconfig.json");
        RequireFile(result, outputDirectory, "Microsoft.AspNetCore.dll");
        RequireFile(result, outputDirectory, "System.Management.Automation.dll");
        RequireFile(result, outputDirectory, "System.DirectoryServices.dll");
        RequireFile(result, outputDirectory, "THIRD-PARTY-NOTICES.txt");
        RequireFile(result, outputDirectory, Path.Combine("Config", "api.ps7api.json"));
        RequireFile(result, outputDirectory, Path.Combine("Scripts", Path.GetFileName(configuredScriptFileName)));
        RequireFile(result, outputDirectory, "Start-PS7ScriptDeskApi.cmd");
        RequireFile(
            result,
            outputDirectory,
            Path.Combine(
                "runtimes",
                "win",
                "lib",
                "net10.0",
                "Modules",
                "Microsoft.PowerShell.Utility",
                "Microsoft.PowerShell.Utility.psd1"));

        if (File.Exists(Path.Combine(outputDirectory, "PS7ScriptDesk.Shell.dll")))
        {
            result.Errors.Add("The REST API publish output contains WPF shell artifacts.");
        }

        if (File.Exists(Path.Combine(outputDirectory, "MainWindow.xaml")))
        {
            result.Errors.Add("The REST API publish output contains WPF window artifacts.");
        }

        return result;
    }

    private static void RequireFile(ExeExportValidationResult result, string root, string relativePath)
    {
        if (!File.Exists(Path.Combine(root, relativePath)))
        {
            result.Errors.Add($"The REST API artifact is missing '{relativePath}'.");
        }
    }

    private static string SanitizePublicLog(string text)
    {
        var sanitized = DeveloperDiagnostics.SanitizePreview(text, MaximumCapturedOutputCharacters);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return string.Empty;
        }

        return sanitized.Trim();
    }

    private static void ReportProgress(
        IProgress<ApiBuildPublishProgressUpdate>? progress,
        string stage,
        string statusMessage)
    {
        progress?.Report(new ApiBuildPublishProgressUpdate(stage, statusMessage, IsIndeterminate: true));
        DeveloperDiagnostics.LogInfo(
            "RestApiBuildPublishProgress",
            "REST API build/publish stage changed.",
            new Dictionary<string, object?>
            {
                ["stage"] = stage,
                ["statusMessage"] = statusMessage
            });
    }
}

internal interface IRestApiHostRuntimeLocator
{
    string? Resolve(string runtimeIdentifier);
}

internal sealed class RestApiHostRuntimeLocator : IRestApiHostRuntimeLocator
{
    private const string HostExecutableName = "PS7ScriptDesk.RestApiProofHost.exe";

    public string? Resolve(string runtimeIdentifier)
    {
        foreach (var candidate in EnumerateCandidates(runtimeIdentifier))
        {
            if (IsRunnableHostLayout(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidates(string runtimeIdentifier)
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, "RestApi", runtimeIdentifier);
        if (IsCurrentRuntimeIdentifier(runtimeIdentifier))
        {
            yield return Path.Combine(baseDirectory, "RestApi");
            yield return baseDirectory;
        }

        var current = new DirectoryInfo(baseDirectory);
        while (current is not null)
        {
            foreach (var configuration in new[] { "Release", "Debug" })
            {
                yield return Path.Combine(
                    current.FullName,
                    "PS7ScriptDesk.RestApiProofHost",
                    "obj",
                    "restapi-proofhost",
                    configuration,
                    runtimeIdentifier,
                    "publish");
                yield return Path.Combine(
                    current.FullName,
                    "PS7ScriptDesk.RestApiProofHost",
                    "bin",
                    configuration,
                    "net10.0",
                    runtimeIdentifier,
                    "publish");
            }

            current = current.Parent;
        }
    }

    private static bool IsCurrentRuntimeIdentifier(string runtimeIdentifier)
        => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? string.Equals(runtimeIdentifier, "win-arm64", StringComparison.OrdinalIgnoreCase)
            : string.Equals(runtimeIdentifier, "win-x64", StringComparison.OrdinalIgnoreCase);

    private static bool IsRunnableHostLayout(string directory)
        => Directory.Exists(directory) &&
           File.Exists(Path.Combine(directory, HostExecutableName)) &&
           File.Exists(Path.Combine(directory, "PS7ScriptDesk.RestApiProofHost.deps.json")) &&
           File.Exists(Path.Combine(directory, "System.DirectoryServices.dll"));
}

internal interface IRestApiHostSmokeTester
{
    Task<RestApiHostSmokeTestResult> VerifyAsync(
        string contentRoot,
        string executablePath,
        string configurationRelativePath,
        CancellationToken cancellationToken);
}

internal sealed class RestApiHostSmokeTester : IRestApiHostSmokeTester
{
    private const int MaximumCapturedOutputCharacters = 64 * 1024;
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _httpClient = new();

    public async Task<RestApiHostSmokeTestResult> VerifyAsync(
        string contentRoot,
        string executablePath,
        string configurationRelativePath,
        CancellationToken cancellationToken)
    {
        using var process = CreateProcess(contentRoot, executablePath, configurationRelativePath, GetFreeLoopbackPort());
        var log = new BoundedTextBuffer(MaximumCapturedOutputCharacters);
        try
        {
            if (!process.Start())
            {
                return RestApiHostSmokeTestResult.Failure("The REST API host process could not be started.");
            }
        }
        catch (Win32Exception ex)
        {
            return RestApiHostSmokeTestResult.Failure($"The REST API host process could not be started. {ex.Message}");
        }

        var outputTask = ReadStreamAsync("stdout", process.StandardOutput, log);
        var errorTask = ReadStreamAsync("stderr", process.StandardError, log);

        try
        {
            var ready = await WaitForReadinessAsync(process, log, cancellationToken).ConfigureAwait(false);
            if (!ready)
            {
                return RestApiHostSmokeTestResult.Failure(
                    "The REST API host did not become ready during build validation." + Environment.NewLine + log.Snapshot());
            }

            return RestApiHostSmokeTestResult.Success("REST API host readiness validation passed." + Environment.NewLine + log.Snapshot());
        }
        finally
        {
            await StopProcessAsync(process, cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        }
    }

    private async Task<bool> WaitForReadinessAsync(
        Process process,
        BoundedTextBuffer log,
        CancellationToken cancellationToken)
    {
        var baseAddress = process.StartInfo.ArgumentList
            .Select((value, index) => new { value, index })
            .Where(item => string.Equals(item.value, "--url", StringComparison.Ordinal))
            .Select(item => item.index + 1 < process.StartInfo.ArgumentList.Count ? process.StartInfo.ArgumentList[item.index + 1] : null)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(baseAddress))
        {
            return false;
        }

        var readinessUri = new Uri(new Uri(baseAddress), "/healthz");
        var deadline = DateTimeOffset.UtcNow + ReadinessTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                log.AppendLine($"process exited: {process.ExitCode}");
                return false;
            }

            try
            {
                using var response = await _httpClient.GetAsync(readinessUri, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static Process CreateProcess(
        string contentRoot,
        string executablePath,
        string configurationRelativePath,
        int port)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? contentRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--content-root");
        process.StartInfo.ArgumentList.Add(contentRoot);
        process.StartInfo.ArgumentList.Add("--config");
        process.StartInfo.ArgumentList.Add(configurationRelativePath);
        process.StartInfo.ArgumentList.Add("--url");
        process.StartInfo.ArgumentList.Add($"http://127.0.0.1:{port}");
        return process;
    }

    private static async Task ReadStreamAsync(string streamName, StreamReader reader, BoundedTextBuffer log)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            log.AppendLine($"{streamName}: {line}");
        }
    }

    private static async Task StopProcessAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

internal sealed record RestApiHostSmokeTestResult(bool Succeeded, string DetailedLog)
{
    public static RestApiHostSmokeTestResult Success(string detailedLog) => new(true, detailedLog);
    public static RestApiHostSmokeTestResult Failure(string detailedLog) => new(false, detailedLog);
}

internal sealed class BoundedTextBuffer
{
    private readonly int _maximumCharacters;
    private readonly StringBuilder _builder = new();

    public BoundedTextBuffer(int maximumCharacters)
    {
        _maximumCharacters = Math.Max(1, maximumCharacters);
    }

    public void AppendLine(string line)
    {
        var value = DeveloperDiagnostics.SanitizePreview(line, 2048);
        if (_builder.Length + value.Length + Environment.NewLine.Length > _maximumCharacters)
        {
            return;
        }

        _builder.AppendLine(value);
    }

    public string Snapshot() => _builder.ToString().Trim();
}
