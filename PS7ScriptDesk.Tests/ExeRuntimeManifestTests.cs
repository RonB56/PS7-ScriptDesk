using System.Reflection;
using System.Security.Cryptography;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class ExeRuntimeManifestTests
{
    [Fact]
    public void ApplicationFolderManifest_ExcludesItselfAndCanBeRegenerated()
    {
        var root = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk-RuntimeManifest-{Guid.NewGuid():N}");
        var sourceRuntime = Path.Combine(root, "source-runtime");
        var projectDirectory = Path.Combine(root, "generated-project");
        Directory.CreateDirectory(sourceRuntime);
        try
        {
            WriteRuntimeFile(sourceRuntime, "pwsh.exe", "runtime executable fixture");
            WriteRuntimeFile(sourceRuntime, "pwsh.deps.json", "{}\n");
            WriteRuntimeFile(sourceRuntime, "pwsh.runtimeconfig.json", "{}\n");
            WriteRuntimeFile(sourceRuntime, "LICENSE.txt", "license fixture");
            WriteRuntimeFile(sourceRuntime, "ThirdPartyNotices.txt", "notices fixture");
            WriteRuntimeFile(sourceRuntime, "Modules/Microsoft.PowerShell.Utility/Utility.psm1", "utility fixture");
            WriteRuntimeFile(sourceRuntime, "Modules/Microsoft.PowerShell.Management/Management.psm1", "management fixture");

            var runtime = new PowerShellRuntimeInfo(
                "Manifest fixture", "Core", "7.6.3", new Version(7, 6, 3), "x64",
                Path.Combine(sourceRuntime, "pwsh.exe"), "test", true, false, true,
                psHome: sourceRuntime);
            var configuration = ExeExportConfiguration.CreatePreset(ExeExportPreset.WindowsConsoleApplication, "ManifestFixture");
            configuration.PackageFormat = ExePackageFormat.ApplicationFolder;
            configuration.OutputExecutablePath = Path.Combine(root, "ManifestFixture.exe");
            var request = new ExeExportRequest("fixture.ps1", "Write-Output 'fixture'", configuration.OutputExecutablePath, runtime, configuration);

            new ExeHostProjectGenerator().Generate(request, projectDirectory);

            var runtimeDirectory = Path.Combine(projectDirectory, "Runtime");
            var manifestPath = Path.Combine(runtimeDirectory, "runtime.manifest");
            var expectedFiles = new[]
            {
                "LICENSE.txt",
                "Modules/Microsoft.PowerShell.Management/Management.psm1",
                "Modules/Microsoft.PowerShell.Utility/Utility.psm1",
                "ThirdPartyNotices.txt",
                "pwsh.deps.json",
                "pwsh.exe",
                "pwsh.runtimeconfig.json"
            };

            var firstManifest = File.ReadAllLines(manifestPath);
            AssertManifestMatchesRuntime(firstManifest, runtimeDirectory, expectedFiles);

            var createManifest = typeof(ExeHostProjectGenerator).GetMethod("CreateRuntimeManifest", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(createManifest);
            createManifest!.Invoke(null, [runtimeDirectory, manifestPath]);

            var repeatedManifest = File.ReadAllLines(manifestPath);
            Assert.Equal(firstManifest, repeatedManifest);
            AssertManifestMatchesRuntime(repeatedManifest, runtimeDirectory, expectedFiles);

            var singleFileConfiguration = configuration.Clone();
            singleFileConfiguration.PackageFormat = ExePackageFormat.SingleFile;
            var singleFileRequest = new ExeExportRequest(
                "fixture.ps1", "Write-Output 'fixture'", singleFileConfiguration.OutputExecutablePath, runtime, singleFileConfiguration);
            var singleFileProjectDirectory = Path.Combine(root, "single-file-project");
            new ExeHostProjectGenerator().Generate(singleFileRequest, singleFileProjectDirectory);
            var singleFileManifest = File.ReadAllLines(Path.Combine(singleFileProjectDirectory, "runtime.manifest"));
            AssertManifestMatchesRuntime(singleFileManifest, sourceRuntime, expectedFiles);
            Assert.True(File.Exists(Path.Combine(singleFileProjectDirectory, "runtime.payload.zip")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteRuntimeFile(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static void AssertManifestMatchesRuntime(string[] lines, string runtimeDirectory, string[] expectedFiles)
    {
        var entries = lines.Select(line =>
        {
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            Assert.True(separator > 0, $"Manifest entry has invalid format: {line}");
            return (Hash: line[..separator], Path: line[(separator + 2)..]);
        }).ToArray();

        Assert.Equal(expectedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            entries.Select(entry => entry.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, entry => string.Equals(entry.Path, "runtime.manifest", StringComparison.OrdinalIgnoreCase));

        foreach (var entry in entries)
        {
            using var stream = File.OpenRead(Path.Combine(runtimeDirectory, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(stream)), entry.Hash);
        }
    }
}
