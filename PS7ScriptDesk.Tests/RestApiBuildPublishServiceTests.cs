using System.Runtime.InteropServices;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class RestApiBuildPublishServiceTests
{
    [Fact]
    public async Task BuildAsync_SucceedsCreatesRunnableArtifactAndSmokeTestsCurrentArchitecture()
    {
        using var workspace = new TemporaryWorkspace();
        var current = CurrentTargetArchitecture();
        var runtimeRoot = workspace.CreateRuntimeLayout(ToRuntimeIdentifier(current), current);
        var service = workspace.CreateService(new Dictionary<string, string>
        {
            [ToRuntimeIdentifier(current)] = runtimeRoot
        });

        var result = await service.BuildAsync(workspace.CreateRequest(targetArchitecture: current));

        Assert.True(result.Succeeded, result.DetailedLog);
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(current, artifact.Architecture);
        Assert.True(artifact.RuntimeValidated);
        Assert.Equal(1, workspace.SmokeTester.CallCount);
        AssertArtifactIsValid(artifact, current);
    }

    [Fact]
    public async Task BuildAsync_ReturnsFailureWhenGenerationFails()
    {
        using var workspace = new TemporaryWorkspace();
        var service = workspace.CreateService(
            new Dictionary<string, string>(),
            generationFailure: ApiProjectGenerationResult.Failure("Generation failed.", "invalid project"));

        var result = await service.BuildAsync(workspace.CreateRequest());

        Assert.False(result.Succeeded);
        Assert.Contains("Generation failed", result.SummaryMessage, StringComparison.Ordinal);
        Assert.Empty(result.Artifacts);
        Assert.Equal(0, workspace.SmokeTester.CallCount);
    }

    [Fact]
    public async Task BuildAsync_DoesNotReportSuccessWhenRuntimeValidationFails()
    {
        using var workspace = new TemporaryWorkspace();
        var current = CurrentTargetArchitecture();
        var runtimeRoot = workspace.CreateRuntimeLayout(ToRuntimeIdentifier(current), current);
        var service = workspace.CreateService(
            new Dictionary<string, string> { [ToRuntimeIdentifier(current)] = runtimeRoot },
            smokeResult: RestApiHostSmokeTestResult.Failure("process exited: 1"));

        var result = await service.BuildAsync(workspace.CreateRequest(targetArchitecture: current));

        Assert.False(result.Succeeded);
        Assert.Contains("runtime validation", result.SummaryMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Artifacts);
        Assert.Equal(1, workspace.SmokeTester.CallCount);
    }

    [Fact]
    public async Task PublishAsync_WinX64_CreatesValidatedSelfContainedArtifact()
    {
        using var workspace = new TemporaryWorkspace();
        var runtimeRoot = workspace.CreateRuntimeLayout("win-x64", ApiPublishTargetArchitecture.WinX64);
        var service = workspace.CreateService(new Dictionary<string, string> { ["win-x64"] = runtimeRoot });

        var result = await service.PublishAsync(workspace.CreateRequest(targetArchitecture: ApiPublishTargetArchitecture.WinX64));

        Assert.True(result.Succeeded, result.DetailedLog);
        var artifact = Assert.Single(result.Artifacts);
        Assert.False(artifact.RuntimeValidated);
        AssertArtifactIsValid(artifact, ApiPublishTargetArchitecture.WinX64);
        Assert.Equal(0, workspace.SmokeTester.CallCount);
    }

    [Fact]
    public async Task PublishAsync_WinArm64_CreatesValidatedArtifactWithoutExecution()
    {
        using var workspace = new TemporaryWorkspace();
        var runtimeRoot = workspace.CreateRuntimeLayout("win-arm64", ApiPublishTargetArchitecture.WinArm64);
        var service = workspace.CreateService(new Dictionary<string, string> { ["win-arm64"] = runtimeRoot });

        var result = await service.PublishAsync(workspace.CreateRequest(targetArchitecture: ApiPublishTargetArchitecture.WinArm64));

        Assert.True(result.Succeeded, result.DetailedLog);
        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal(ApiPublishTargetArchitecture.WinArm64, artifact.Architecture);
        Assert.False(artifact.RuntimeValidated);
        AssertArtifactIsValid(artifact, ApiPublishTargetArchitecture.WinArm64);
        Assert.Equal(0, workspace.SmokeTester.CallCount);
    }

    [Fact]
    public async Task PublishAsync_Both_CreatesX64AndArm64Artifacts()
    {
        using var workspace = new TemporaryWorkspace();
        var service = workspace.CreateService(new Dictionary<string, string>
        {
            ["win-x64"] = workspace.CreateRuntimeLayout("win-x64", ApiPublishTargetArchitecture.WinX64),
            ["win-arm64"] = workspace.CreateRuntimeLayout("win-arm64", ApiPublishTargetArchitecture.WinArm64)
        });

        var result = await service.PublishAsync(workspace.CreateRequest(targetArchitecture: ApiPublishTargetArchitecture.Both));

        Assert.True(result.Succeeded, result.DetailedLog);
        Assert.Collection(
            result.Artifacts,
            artifact => AssertArtifactIsValid(artifact, ApiPublishTargetArchitecture.WinX64),
            artifact => AssertArtifactIsValid(artifact, ApiPublishTargetArchitecture.WinArm64));
    }

    [Fact]
    public async Task PublishAsync_RetainsGeneratedProjectAndDoesNotCopyWpfArtifacts()
    {
        using var workspace = new TemporaryWorkspace();
        var runtimeRoot = workspace.CreateRuntimeLayout("win-x64", ApiPublishTargetArchitecture.WinX64);
        File.WriteAllText(Path.Combine(runtimeRoot, "PS7ScriptDesk.Shell.dll"), "should not be copied");
        var service = workspace.CreateService(new Dictionary<string, string> { ["win-x64"] = runtimeRoot });

        var result = await service.PublishAsync(workspace.CreateRequest(targetArchitecture: ApiPublishTargetArchitecture.WinX64));

        Assert.True(result.Succeeded, result.DetailedLog);
        Assert.True(File.Exists(Path.Combine(workspace.GeneratedProjectDirectory, "WidgetApi.csproj")));
        var artifact = Assert.Single(result.Artifacts);
        Assert.False(File.Exists(Path.Combine(artifact.OutputDirectory, "PS7ScriptDesk.Shell.dll")));
        Assert.False(File.Exists(Path.Combine(artifact.OutputDirectory, "MainWindow.xaml")));
    }

    private static void AssertArtifactIsValid(ApiBuildPublishArtifact artifact, ApiPublishTargetArchitecture architecture)
    {
        Assert.True(File.Exists(artifact.ExecutablePath));
        Assert.True(File.Exists(Path.Combine(artifact.OutputDirectory, "PS7ScriptDesk.RestApiProofHost.deps.json")));
        Assert.True(File.Exists(Path.Combine(artifact.OutputDirectory, "PS7ScriptDesk.RestApiProofHost.runtimeconfig.json")));
        Assert.True(File.Exists(Path.Combine(artifact.OutputDirectory, "Microsoft.AspNetCore.dll")));
        Assert.True(File.Exists(Path.Combine(artifact.OutputDirectory, "System.Management.Automation.dll")));
        Assert.True(File.Exists(Path.Combine(artifact.OutputDirectory, "System.DirectoryServices.dll")));
        Assert.True(File.Exists(Path.Combine(artifact.OutputDirectory, "THIRD-PARTY-NOTICES.txt")));
        Assert.True(File.Exists(Path.Combine(artifact.OutputDirectory, "Config", "api.ps7api.json")));
        Assert.True(File.Exists(Path.Combine(artifact.OutputDirectory, "Scripts", "Widget.ps1")));
        Assert.True(File.Exists(Path.Combine(artifact.OutputDirectory, "Start-PS7ScriptDeskApi.cmd")));
        Assert.Contains(ToRuntimeIdentifier(architecture), artifact.OutputDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static ApiPublishTargetArchitecture CurrentTargetArchitecture()
        => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? ApiPublishTargetArchitecture.WinArm64
            : ApiPublishTargetArchitecture.WinX64;

    private static string ToRuntimeIdentifier(ApiPublishTargetArchitecture architecture)
        => architecture == ApiPublishTargetArchitecture.WinArm64 ? "win-arm64" : "win-x64";

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk-RestBuildPublishTests", Guid.NewGuid().ToString("N"));

        public TemporaryWorkspace()
        {
            Directory.CreateDirectory(_root);
            SourceScriptPath = Path.Combine(_root, "Widget.ps1");
            File.WriteAllText(SourceScriptPath, "function Get-Widget { param([string]$Name) $Name }");
            GeneratedProjectDirectory = Path.Combine(_root, "Generated", "WidgetApi");
        }

        public string SourceScriptPath { get; }

        public string GeneratedProjectDirectory { get; }

        public FakeRestApiHostSmokeTester SmokeTester { get; private set; } = new(RestApiHostSmokeTestResult.Success("ready"));

        public ApiBuildPublishService CreateService(
            IReadOnlyDictionary<string, string> runtimeRoots,
            ApiProjectGenerationResult? generationFailure = null,
            RestApiHostSmokeTestResult? smokeResult = null)
        {
            SmokeTester = new FakeRestApiHostSmokeTester(smokeResult ?? RestApiHostSmokeTestResult.Success("ready"));
            return new ApiBuildPublishService(
                new FakeApiProjectGenerator(generationFailure),
                new FakeRestApiHostRuntimeLocator(runtimeRoots),
                SmokeTester);
        }

        public ApiBuildPublishRequest CreateRequest(
            ApiPublishTargetArchitecture targetArchitecture = ApiPublishTargetArchitecture.WinX64)
            => new(
                SourceScriptPath,
                CreateConfiguration(),
                GeneratedProjectDirectory,
                "WidgetApi",
                targetArchitecture,
                overwriteExistingGeneratedProject: true);

        public string CreateRuntimeLayout(string runtimeIdentifier, ApiPublishTargetArchitecture architecture)
        {
            var runtimeRoot = Path.Combine(_root, "Runtime", runtimeIdentifier);
            Directory.CreateDirectory(runtimeRoot);
            WritePeExecutable(
                Path.Combine(runtimeRoot, "PS7ScriptDesk.RestApiProofHost.exe"),
                architecture == ApiPublishTargetArchitecture.WinArm64 ? (ushort)0xAA64 : (ushort)0x8664);
            foreach (var relativePath in new[]
                     {
                         "PS7ScriptDesk.RestApiProofHost.deps.json",
                         "PS7ScriptDesk.RestApiProofHost.runtimeconfig.json",
                         "Microsoft.AspNetCore.dll",
                         "System.Management.Automation.dll",
                         "System.DirectoryServices.dll",
                         "THIRD-PARTY-NOTICES.txt",
                         Path.Combine("runtimes", "win", "lib", "net10.0", "Modules", "Microsoft.PowerShell.Utility", "Microsoft.PowerShell.Utility.psd1")
                     })
            {
                var path = Path.Combine(runtimeRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? runtimeRoot);
                File.WriteAllText(path, relativePath);
            }

            return runtimeRoot;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private ApiPublishConfiguration CreateConfiguration()
        {
            var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath(SourceScriptPath);
            configuration.SourceScript = "Widget.ps1";
            configuration.Endpoints =
            [
                new ApiEndpointConfiguration
                {
                    EndpointId = "get-widget",
                    PowerShellFunctionName = "Get-Widget",
                    DisplayName = "Get-Widget",
                    IsEnabled = true
                }
            ];
            return configuration;
        }

        private static void WritePeExecutable(string path, ushort machine)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var bytes = new byte[160];
            bytes[0] = 0x4D;
            bytes[1] = 0x5A;
            bytes[0x3C] = 0x80;
            bytes[0x80] = 0x50;
            bytes[0x81] = 0x45;
            bytes[0x82] = 0x00;
            bytes[0x83] = 0x00;
            bytes[0x84] = (byte)(machine & 0xFF);
            bytes[0x85] = (byte)(machine >> 8);
            File.WriteAllBytes(path, bytes);
        }
    }

    private sealed class FakeApiProjectGenerator : IApiProjectGenerator
    {
        private readonly ApiProjectGenerationResult? _generationFailure;

        public FakeApiProjectGenerator(ApiProjectGenerationResult? generationFailure)
        {
            _generationFailure = generationFailure;
        }

        public Task<ApiProjectGenerationResult> GenerateAsync(ApiProjectGenerationRequest request, CancellationToken cancellationToken = default)
        {
            if (_generationFailure is not null)
            {
                return Task.FromResult(_generationFailure);
            }

            Directory.CreateDirectory(request.DestinationDirectory);
            var projectPath = Path.Combine(request.DestinationDirectory, "WidgetApi.csproj");
            var configPath = Path.Combine(request.DestinationDirectory, "Config", "api.ps7api.json");
            var scriptPath = Path.Combine(request.DestinationDirectory, "Scripts", "Widget.ps1");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? request.DestinationDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(scriptPath) ?? request.DestinationDirectory);
            File.WriteAllText(projectPath, "<Project />");
            File.WriteAllText(configPath, "{}");
            File.WriteAllText(scriptPath, "function Get-Widget { 'ok' }");
            return Task.FromResult(ApiProjectGenerationResult.Success(
                request.DestinationDirectory,
                projectPath,
                [projectPath, configPath, scriptPath],
                "generated"));
        }
    }

    private sealed class FakeRestApiHostRuntimeLocator : IRestApiHostRuntimeLocator
    {
        private readonly IReadOnlyDictionary<string, string> _runtimeRoots;

        public FakeRestApiHostRuntimeLocator(IReadOnlyDictionary<string, string> runtimeRoots)
        {
            _runtimeRoots = runtimeRoots;
        }

        public string? Resolve(string runtimeIdentifier)
            => _runtimeRoots.TryGetValue(runtimeIdentifier, out var root) ? root : null;
    }

    private sealed class FakeRestApiHostSmokeTester : IRestApiHostSmokeTester
    {
        private readonly RestApiHostSmokeTestResult _result;

        public FakeRestApiHostSmokeTester(RestApiHostSmokeTestResult result)
        {
            _result = result;
        }

        public int CallCount { get; private set; }

        public Task<RestApiHostSmokeTestResult> VerifyAsync(
            string contentRoot,
            string executablePath,
            string configurationRelativePath,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_result);
        }
    }
}
