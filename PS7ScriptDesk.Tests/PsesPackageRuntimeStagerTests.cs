using System.Security.Cryptography;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class PsesPackageRuntimeStagerTests
{
    [Fact]
    public void ExternalProcessDataRoot_UsesPhysicalPackageCacheAndPreservesUnpackagedLocation()
    {
        var local = Path.Combine(Path.GetTempPath(), "resolver fixture");
        Assert.Equal(local, PsesPackageRuntimeStager.ResolveExternalProcessDataRoot(null, null, local));

        var first = PsesPackageRuntimeStager.ResolveExternalProcessDataRoot(
            "Publisher.App_1.0.0.0_x64__alpha", "Publisher.App_alpha", local);
        var second = PsesPackageRuntimeStager.ResolveExternalProcessDataRoot(
            "Publisher.App_2.0.0.0_arm64__beta", "Publisher.App_beta", local);
        Assert.Equal(Path.Combine(local, "Packages", "Publisher.App_alpha", "LocalCache", "Local"), first);
        Assert.Equal(Path.Combine(local, "Packages", "Publisher.App_beta", "LocalCache", "Local"), second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ExternalProcessDataRoot_RejectsMissingOrInvalidPackageIdentity()
    {
        var local = Path.GetTempPath();
        Assert.Throws<InvalidOperationException>(() =>
            PsesPackageRuntimeStager.ResolveExternalProcessDataRoot("package", null, local));
        Assert.Throws<InvalidOperationException>(() =>
            PsesPackageRuntimeStager.ResolveExternalProcessDataRoot("package", @"..\other", local));
        Assert.Throws<InvalidOperationException>(() =>
            PsesPackageRuntimeStager.ResolveExternalProcessDataRoot("package", "family", ""));
    }

    [Fact]
    public void Stage_ReusesCompleteRuntimeAndReplacesDamagedCache()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "PsesPackageRuntimeStagerTests", Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(fixture, "package", "PSES");
            var module = Path.Combine(source, "PowerShellEditorServices");
            var core = Path.Combine(module, "bin", "Core");
            Directory.CreateDirectory(core);
            var assembly = Path.Combine(core, "Microsoft.PowerShell.EditorServices.dll");
            File.WriteAllText(assembly, "private bridge test assembly");
            File.WriteAllText(Path.Combine(core, "Microsoft.PowerShell.EditorServices.Hosting.dll"), "hosting dependency");
            File.WriteAllText(Path.Combine(module, "PowerShellEditorServices.psd1"), "ModuleVersion = '4.4.0'");
            File.WriteAllText(Path.Combine(module, "Start-EditorServices.ps1"), "# startup fixture");
            File.WriteAllLines(Path.Combine(source, "ScriptDeskEditorBridge.protocol"), ["PSES=4.4.0", "Protocol=1"]);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly)));
            File.WriteAllLines(Path.Combine(source, "ScriptDeskEditorBridge.manifest"),
            [
                "PSES=4.4.0", "Protocol=1",
                "Assembly=PowerShellEditorServices/bin/Core/Microsoft.PowerShell.EditorServices.dll",
                $"SHA256={hash}"
            ]);

            var first = PsesPackageRuntimeStager.Stage(source, "test-package_1.0.119.0", fixture);
            Assert.True(File.Exists(Path.Combine(first, "PowerShellEditorServices", "Start-EditorServices.ps1")));
            Assert.True(File.Exists(Path.Combine(first, "PowerShellEditorServices", "bin", "Core", "Microsoft.PowerShell.EditorServices.Hosting.dll")));
            var startupScript = Path.Combine(first, "PowerShellEditorServices", "Start-EditorServices.ps1");
            var sessionRoot = Path.Combine(fixture, "PS7ScriptDesk", "PSES", "session-fixture");
            var sessionDetails = Path.Combine(sessionRoot, "session.json");
            var logDirectory = Path.Combine(sessionRoot, "Logs");
            var arguments = PsesOwnedInteractiveSessionService.BuildArguments(first, startupScript,
                sessionDetails, logDirectory, "5.0.1");
            Assert.Equal(startupScript, ValueAfter(arguments, "-File"));
            Assert.Equal(first, ValueAfter(arguments, "-BundledModulesPath"));
            Assert.Equal(sessionDetails, ValueAfter(arguments, "-SessionDetailsPath"));
            Assert.Equal(logDirectory, ValueAfter(arguments, "-LogPath"));
            Assert.Equal(first, PsesPackageRuntimeStager.Stage(source, "test-package_1.0.119.0", fixture));

            var incomplete = Path.Combine(Path.GetDirectoryName(first)!, "staging-incomplete");
            Directory.CreateDirectory(incomplete);
            File.WriteAllText(Path.Combine(incomplete, "partial.txt"), "incomplete");

            File.WriteAllText(Path.Combine(first, "PowerShellEditorServices", "bin", "Core", "Microsoft.PowerShell.EditorServices.Hosting.dll"), "damaged dependency");
            var replacement = PsesPackageRuntimeStager.Stage(source, "test-package_1.0.119.0", fixture);
            Assert.NotEqual(first, replacement);
            Assert.True(File.Exists(Path.Combine(replacement, "PowerShellEditorServices", "Start-EditorServices.ps1")));
            Assert.Equal("hosting dependency", File.ReadAllText(Path.Combine(replacement, "PowerShellEditorServices", "bin", "Core", "Microsoft.PowerShell.EditorServices.Hosting.dll")));
        }
        finally
        {
            if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true);
        }
    }

    private static string ValueAfter(IReadOnlyList<string> arguments, string name)
    {
        var index = Array.IndexOf(arguments.ToArray(), name);
        Assert.True(index >= 0 && index + 1 < arguments.Count);
        return arguments[index + 1];
    }
}
