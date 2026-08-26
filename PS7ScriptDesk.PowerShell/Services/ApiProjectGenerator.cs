using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

public sealed class ApiProjectGenerator : IApiProjectGenerator
{
    private const string MarkerFileName = ".ps7scriptdesk-generated-api";
    private const string PowerShellSdkVersion = "7.6.2";
    private const string PowerShellHostedRuntimeVersion = "7.6.1";
    private static readonly TimeSpan[] FileSystemRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400)
    ];

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private static readonly RuntimeSourceFile[] RuntimeSourceFiles =
    [
        new("PS7ScriptDesk.ApiProjectRuntime.Domain.Models.ApiPublishConfiguration.cs", "Runtime/PS7ScriptDesk.Domain/Models/ApiPublishConfiguration.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.Domain.Models.ApiMetadataResult.cs", "Runtime/PS7ScriptDesk.Domain/Models/ApiMetadataResult.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.Application.Interfaces.IApiMetadataService.cs", "Runtime/PS7ScriptDesk.Application/Interfaces/IApiMetadataService.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.Application.Interfaces.IApiPublishConfigurationValidator.cs", "Runtime/PS7ScriptDesk.Application/Interfaces/IApiPublishConfigurationValidator.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.Application.Services.ApiPublishConfigurationValidator.cs", "Runtime/PS7ScriptDesk.Application/Services/ApiPublishConfigurationValidator.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.PowerShell.Services.PowerShellApiMetadataService.cs", "Runtime/PS7ScriptDesk.PowerShell/Services/PowerShellApiMetadataService.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.Api.ApiEndpointParameterBinder.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/Api/ApiEndpointParameterBinder.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.Api.ApiEndpointResolver.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/Api/ApiEndpointResolver.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.Api.ApiInvocationErrorDescriptorMapper.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/Api/ApiInvocationErrorDescriptorMapper.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.Api.ApiKeyAuthenticationService.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/Api/ApiKeyAuthenticationService.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.Api.ApiJsonOptions.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/Api/ApiJsonOptions.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.Api.ApiInvocationProblemDetailsMapper.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/Api/ApiInvocationProblemDetailsMapper.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.Api.OpenApiDocumentBuilder.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/Api/OpenApiDocumentBuilder.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.Api.OpenApiEndpointMapper.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/Api/OpenApiEndpointMapper.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.Api.RestEndpointMapper.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/Api/RestEndpointMapper.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.Api.RestParameterBinder.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/Api/RestParameterBinder.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.PowerShell.ApiInvocationRequest.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/PowerShell/ApiInvocationRequest.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.PowerShell.ApiInvocationResult.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/PowerShell/ApiInvocationResult.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.PowerShell.IPowerShellFunctionInvoker.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/PowerShell/IPowerShellFunctionInvoker.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.PowerShell.PowerShellFailureClassifier.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/PowerShell/PowerShellFailureClassifier.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.PowerShell.PowerShellFunctionInvoker.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/PowerShell/PowerShellFunctionInvoker.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.PowerShell.PowerShellInvocationCoordinator.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/PowerShell/PowerShellInvocationCoordinator.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.PowerShell.PowerShellInvocationMetrics.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/PowerShell/PowerShellInvocationMetrics.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.PowerShell.PowerShellResultNormalizer.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/PowerShell/PowerShellResultNormalizer.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.PowerShell.RunspacePoolManager.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/PowerShell/RunspacePoolManager.cs"),
        new("PS7ScriptDesk.ApiProjectRuntime.RestApiProofHost.WebSockets.WebSocketProtocol.cs", "Runtime/PS7ScriptDesk.RestApiProofHost/WebSockets/WebSocketProtocol.cs")
    ];

    public async Task<ApiProjectGenerationResult> GenerateAsync(
        ApiProjectGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var operationId = $"ApiProjectGeneration-{Guid.NewGuid():N}";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = ValidateDestination(request);
            var projectName = MakeProjectName(request.ProjectName);
            var scriptFileName = MakeScriptFileName(request.SourceScriptPath);
            var scriptContent = await ReadScriptForMetadataAsync(request.SourceScriptPath, cancellationToken).ConfigureAwait(false);
            var metadata = new PowerShellApiMetadataService().Analyze(scriptContent);
            var validation = new ApiPublishConfigurationValidator().Validate(request.Configuration, metadata);

            if (!metadata.ParsedSuccessfully)
            {
                validation.AddError("API200", "The source script contains parse errors and cannot be generated as an API project.", "$.sourceScript");
            }

            if (!validation.IsValid)
            {
                DeveloperDiagnostics.LogWarning(
                    "ApiProjectGeneration",
                    "API project generation rejected invalid configuration.",
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["sourceFileName"] = Path.GetFileName(request.SourceScriptPath),
                        ["destinationName"] = Path.GetFileName(destination),
                        ["errorCount"] = validation.Errors.Count
                    });

                return ApiProjectGenerationResult.Failure(
                    "API project settings need attention before generation.",
                    FormatValidationDiagnostics(validation.Errors),
                    destination,
                    validation.Errors);
            }

            var parent = Path.GetDirectoryName(destination);
            if (string.IsNullOrWhiteSpace(parent))
            {
                return ApiProjectGenerationResult.Failure(
                    "The destination directory is invalid.",
                    "The destination directory does not have a usable parent directory.",
                    destination);
            }

            Directory.CreateDirectory(parent);
            var stagingDirectory = Path.Combine(parent, $".{Path.GetFileName(destination)}.ps7api-staging-{Guid.NewGuid():N}");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(stagingDirectory);
                await WriteProjectAsync(
                    stagingDirectory,
                    projectName,
                    scriptFileName,
                    request.SourceScriptPath,
                    request.Configuration,
                    cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                PrepareDestinationForMove(destination, request.OverwriteExistingGeneratedProject);
                await MoveDirectoryWithRetryAsync(stagingDirectory, destination, operationId, cancellationToken).ConfigureAwait(false);

                var generatedFiles = EnumerateGeneratedFiles(destination);
                stopwatch.Stop();
                DeveloperDiagnostics.LogInfo(
                    "ApiProjectGeneration",
                    "API project generation completed.",
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["projectName"] = projectName,
                        ["sourceFileName"] = scriptFileName,
                        ["endpointCount"] = request.Configuration.Endpoints.Count(endpoint => endpoint.IsEnabled),
                        ["generatedFileCount"] = generatedFiles.Count,
                        ["elapsedMilliseconds"] = stopwatch.ElapsedMilliseconds
                    });

                return ApiProjectGenerationResult.Success(
                    destination,
                    Path.Combine(destination, $"{projectName}.csproj"),
                    generatedFiles,
                    $"Generated {generatedFiles.Count} files for {projectName}.");
            }
            finally
            {
                TryDeleteDirectory(stagingDirectory, operationId, "staging");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeveloperDiagnostics.LogWarning(
                "ApiProjectGeneration",
                "API project generation was cancelled.",
                new Dictionary<string, object?> { ["operationId"] = operationId });
            return ApiProjectGenerationResult.Failure(
                "API project generation was cancelled.",
                "The operation was cancelled before a complete generated project was reported.");
        }
        catch (Exception exception)
        {
            DeveloperDiagnostics.LogException(
                "ApiProjectGeneration",
                exception,
                "API project generation failed unexpectedly.",
                new Dictionary<string, object?>
                {
                    ["operationId"] = operationId,
                    ["sourceFileName"] = Path.GetFileName(request.SourceScriptPath),
                    ["destinationName"] = Path.GetFileName(request.DestinationDirectory)
                });

            return ApiProjectGenerationResult.Failure(
                "API project generation failed unexpectedly.",
                exception.Message,
                request.DestinationDirectory);
        }
    }

    private static async Task WriteProjectAsync(
        string projectDirectory,
        string projectName,
        string scriptFileName,
        string sourceScriptPath,
        ApiPublishConfiguration configuration,
        CancellationToken cancellationToken)
    {
        WriteUtf8(Path.Combine(projectDirectory, MarkerFileName), "PS7 ScriptDesk generated API project\n");
        WriteUtf8(Path.Combine(projectDirectory, $"{projectName}.csproj"), BuildProjectFile(projectName));
        WriteUtf8(Path.Combine(projectDirectory, "Program.cs"), BuildProgramSource());
        WriteUtf8(Path.Combine(projectDirectory, "appsettings.json"), BuildAppSettings());
        WriteUtf8(Path.Combine(projectDirectory, "Properties", "launchSettings.json"), BuildLaunchSettings(projectName));
        WriteConfiguration(Path.Combine(projectDirectory, "Config", "api.ps7api.json"), configuration, scriptFileName);
        CopyScriptExact(sourceScriptPath, Path.Combine(projectDirectory, "Scripts", scriptFileName));

        foreach (var sourceFile in RuntimeSourceFiles.OrderBy(file => file.OutputPath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteUtf8(
                Path.Combine(projectDirectory, sourceFile.OutputPath.Replace('/', Path.DirectorySeparatorChar)),
                ReadEmbeddedRuntimeSource(sourceFile.ResourceName));
        }

        await Task.CompletedTask;
    }

    private static string ValidateDestination(ApiProjectGenerationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceScriptPath) || !File.Exists(request.SourceScriptPath))
        {
            throw new FileNotFoundException("The source PowerShell script was not found.");
        }

        if (!string.Equals(Path.GetExtension(request.SourceScriptPath), ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The source script must be a saved .ps1 file.");
        }

        if (string.IsNullOrWhiteSpace(request.DestinationDirectory))
        {
            throw new InvalidOperationException("A destination directory is required.");
        }

        var destination = Path.GetFullPath(request.DestinationDirectory);
        var root = Path.GetPathRoot(destination);
        if (string.Equals(destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The destination directory cannot be a drive or filesystem root.");
        }

        if (File.Exists(destination))
        {
            throw new InvalidOperationException("The destination path is an existing file.");
        }

        return destination;
    }

    private static void PrepareDestinationForMove(string destination, bool overwriteExistingGeneratedProject)
    {
        if (!Directory.Exists(destination))
        {
            return;
        }

        if (IsDirectoryEmpty(destination))
        {
            DeleteDirectoryWithRetry(destination, recursive: false, operationId: null, directoryKind: "emptyDestination");
            return;
        }

        var markerPath = Path.Combine(destination, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            throw new IOException("The destination directory is not empty and is not marked as a PS7 ScriptDesk generated API project.");
        }

        if (!overwriteExistingGeneratedProject)
        {
            throw new IOException("The destination already contains a generated API project. Enable overwrite to replace it.");
        }

        EnsureSafeGeneratedDirectoryForDeletion(destination);
        DeleteDirectoryWithRetry(destination, recursive: true, operationId: null, directoryKind: "existingGeneratedDestination");
    }

    private static void EnsureSafeGeneratedDirectoryForDeletion(string destination)
    {
        var fullPath = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent) ||
            string.Equals(fullPath, parent, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(fullPath, MarkerFileName)))
        {
            throw new IOException("The existing destination could not be verified as a safe generated project directory.");
        }
    }

    private static void WriteConfiguration(string path, ApiPublishConfiguration configuration, string scriptFileName)
    {
        var copy = CloneConfiguration(configuration);
        copy.SourceScript = scriptFileName;
        copy.Output.OutputDirectory = string.Empty;
        WriteUtf8(path, JsonSerializer.Serialize(copy, SerializerOptions) + Environment.NewLine);
    }

    private static ApiPublishConfiguration CloneConfiguration(ApiPublishConfiguration configuration)
    {
        var json = JsonSerializer.Serialize(configuration, SerializerOptions);
        return JsonSerializer.Deserialize<ApiPublishConfiguration>(json, SerializerOptions)
               ?? throw new InvalidOperationException("The API publish configuration could not be cloned.");
    }

    private static async Task<string> ReadScriptForMetadataAsync(string path, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void CopyScriptExact(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("The script destination directory could not be resolved."));
        File.Copy(sourcePath, destinationPath, overwrite: false);
    }

    private static string ReadEmbeddedRuntimeSource(string resourceName)
    {
        var assembly = typeof(ApiProjectGenerator).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"The API runtime template resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static List<string> EnumerateGeneratedFiles(string destination)
        => Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(destination, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static bool IsDirectoryEmpty(string directory)
        => !Directory.EnumerateFileSystemEntries(directory).Any();

    private static void WriteUtf8(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The output directory could not be resolved."));
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void TryDeleteDirectory(string directoryPath, string operationId, string directoryKind)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        try
        {
            DeleteDirectoryWithRetry(directoryPath, recursive: true, operationId, directoryKind);
        }
        catch (Exception exception)
        {
            DeveloperDiagnostics.LogException(
                "ApiProjectGeneration",
                exception,
                "Generated API project cleanup failed.",
                new Dictionary<string, object?>
                {
                    ["operationId"] = operationId,
                    ["directoryKind"] = directoryKind,
                    ["directoryName"] = Path.GetFileName(directoryPath)
                });
        }
    }

    private static async Task MoveDirectoryWithRetryAsync(
        string sourceDirectory,
        string destinationDirectory,
        string operationId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(sourceDirectory, destinationDirectory);
                return;
            }
            catch (Exception exception) when (IsTransientFileSystemException(exception) && attempt < FileSystemRetryDelays.Length)
            {
                DeveloperDiagnostics.LogWarning(
                    "ApiProjectGeneration",
                    "API project staging move hit a transient filesystem error and will be retried.",
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["attempt"] = attempt + 1,
                        ["sourceName"] = Path.GetFileName(sourceDirectory),
                        ["destinationName"] = Path.GetFileName(destinationDirectory),
                        ["exceptionType"] = exception.GetType().FullName
                    });
                await Task.Delay(FileSystemRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void DeleteDirectoryWithRetry(string directoryPath, bool recursive, string? operationId, string directoryKind)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            try
            {
                Directory.Delete(directoryPath, recursive);
                return;
            }
            catch (Exception exception) when (IsTransientFileSystemException(exception) && attempt < FileSystemRetryDelays.Length)
            {
                DeveloperDiagnostics.LogWarning(
                    "ApiProjectGeneration",
                    "API project directory cleanup hit a transient filesystem error and will be retried.",
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["directoryKind"] = directoryKind,
                        ["directoryName"] = Path.GetFileName(directoryPath),
                        ["attempt"] = attempt + 1,
                        ["exceptionType"] = exception.GetType().FullName
                    });
                Thread.Sleep(FileSystemRetryDelays[attempt]);
            }
        }
    }

    private static bool IsTransientFileSystemException(Exception exception)
        => exception is IOException or UnauthorizedAccessException;

    private static string FormatValidationDiagnostics(IReadOnlyList<ApiPublishValidationDiagnostic> diagnostics)
        => string.Join(
            Environment.NewLine,
            diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}"));

    private static string BuildProjectFile(string projectName)
        => $"""
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>{Xml(projectName)}</AssemblyName>
    <RootNamespace>{Xml(projectName)}</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="System.Management.Automation" Version="{PowerShellSdkVersion}" />
    <PackageReference Include="Microsoft.PowerShell.SDK" Version="{PowerShellHostedRuntimeVersion}" />
  </ItemGroup>

  <ItemGroup>
    <Content Update="Config\api.ps7api.json" CopyToOutputDirectory="PreserveNewest" />
    <Content Update="Scripts\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
""";

    private static string BuildProgramSource()
        => """
using System.Text.Json;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;
using PS7ScriptDesk.RestApiProofHost.Api;
using PS7ScriptDesk.RestApiProofHost.PowerShell;

var builder = WebApplication.CreateBuilder(args);
var contentRoot = builder.Environment.ContentRootPath;
var configuration = LoadConfiguration(contentRoot);
var scriptPath = ResolveScriptPath(contentRoot, configuration);
var metadata = LoadStaticMetadata(scriptPath);
var validation = new ApiPublishConfigurationValidator().Validate(configuration, metadata);
if (!validation.IsValid)
{
    throw new InvalidOperationException("Generated API configuration is invalid: " + string.Join("; ", validation.Errors.Select(error => error.Code)));
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = Math.Max(1, configuration.Runtime.RequestBodySizeLimitBytes);
});

builder.Services.AddSingleton(configuration);
builder.Services.AddSingleton(metadata);
builder.Services.AddSingleton<ApiKeyAuthenticationService>();
builder.Services.AddSingleton(RestParameterBinder.Shared);
builder.Services.AddSingleton<OpenApiDocumentBuilder>();
builder.Services.AddSingleton(PowerShellResultNormalizer.Shared);
builder.Services.AddSingleton<RunspacePoolManager>();
builder.Services.AddSingleton<IPowerShellFunctionInvoker, PowerShellFunctionInvoker>();
builder.Services.AddSingleton<PowerShellInvocationCoordinator>();

var app = builder.Build();
app.MapGet("/healthz", () => Results.Json(new { status = "Ready" }, ApiJsonOptions.Shared));
OpenApiEndpointMapper.MapOpenApiEndpoints(app);
RestEndpointMapper.MapConfiguredEndpoints(app);

var coordinator = app.Services.GetRequiredService<PowerShellInvocationCoordinator>();
await coordinator.InitializeAsync(
    scriptPath,
    configuration.Endpoints.Where(endpoint => endpoint.IsEnabled).Select(endpoint => endpoint.PowerShellFunctionName),
    configuration.Runtime,
    app.Lifetime.ApplicationStopping);

await app.RunAsync();

static ApiPublishConfiguration LoadConfiguration(string contentRoot)
{
    var configurationPath = Path.Combine(contentRoot, "Config", "api.ps7api.json");
    using var stream = File.OpenRead(configurationPath);
    return JsonSerializer.Deserialize<ApiPublishConfiguration>(stream, ApiJsonOptions.Shared)
           ?? throw new InvalidDataException("The generated API configuration is empty.");
}

static string ResolveScriptPath(string contentRoot, ApiPublishConfiguration configuration)
{
    var scriptFileName = Path.GetFileName(configuration.SourceScript);
    if (string.IsNullOrWhiteSpace(scriptFileName))
    {
        throw new InvalidDataException("The generated API configuration does not identify a script file.");
    }

    var scriptPath = Path.GetFullPath(Path.Combine(contentRoot, "Scripts", scriptFileName));
    var scriptsRoot = Path.GetFullPath(Path.Combine(contentRoot, "Scripts"));
    if (!scriptPath.StartsWith(scriptsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(scriptPath, scriptsRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException("The generated API script path is invalid.");
    }

    if (!File.Exists(scriptPath))
    {
        throw new FileNotFoundException("The generated API script file was not found.");
    }

    return scriptPath;
}

static ApiMetadataResult LoadStaticMetadata(string scriptPath)
{
    var source = File.ReadAllText(scriptPath);
    return new PowerShellApiMetadataService().Analyze(source);
}
""";

    private static string BuildAppSettings()
        => """
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
""";

    private static string BuildLaunchSettings(string projectName)
        => $$"""
{
  "profiles": {
    "{{projectName}}": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "applicationUrl": "http://127.0.0.1:5274",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  }
}
""";

    private static string MakeProjectName(string? name)
    {
        var builder = new StringBuilder();
        foreach (var character in name ?? string.Empty)
        {
            builder.Append(char.IsLetterOrDigit(character) || character == '_' || character == '.' ? character : '_');
        }

        var value = builder.Length == 0 ? "GeneratedPowerShellApi" : builder.ToString().Trim('.');
        if (string.IsNullOrWhiteSpace(value))
        {
            value = "GeneratedPowerShellApi";
        }

        return char.IsLetter(value[0]) || value[0] == '_' ? value : $"Generated_{value}";
    }

    private static string MakeScriptFileName(string sourceScriptPath)
    {
        var fileName = Path.GetFileName(sourceScriptPath);
        return string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
            ? "ApiScript.ps1"
            : fileName;
    }

    private static string Xml(string value)
        => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record RuntimeSourceFile(string ResourceName, string OutputPath);
}
