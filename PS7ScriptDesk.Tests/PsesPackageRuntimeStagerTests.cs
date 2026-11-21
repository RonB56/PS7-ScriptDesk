using System.Security.Cryptography;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class PsesPackageRuntimeStagerTests
{
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
            File.WriteAllLines(Path.Combine(source, "ScriptDeskEditorBridge.protocol"), ["PSES=4.4.0", "Protocol=1"]);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly)));
            File.WriteAllLines(Path.Combine(source, "ScriptDeskEditorBridge.manifest"),
            [
                "PSES=4.4.0", "Protocol=1",
                "Assembly=PowerShellEditorServices/bin/Core/Microsoft.PowerShell.EditorServices.dll",
                $"SHA256={hash}"
            ]);

            var first = PsesPackageRuntimeStager.Stage(source, "test-package_1.0.119.0", fixture);
            Assert.True(File.Exists(Path.Combine(first, "PowerShellEditorServices", "bin", "Core", "Microsoft.PowerShell.EditorServices.Hosting.dll")));
            Assert.Equal(first, PsesPackageRuntimeStager.Stage(source, "test-package_1.0.119.0", fixture));

            File.WriteAllText(Path.Combine(first, "PowerShellEditorServices", "bin", "Core", "Microsoft.PowerShell.EditorServices.Hosting.dll"), "damaged dependency");
            var replacement = PsesPackageRuntimeStager.Stage(source, "test-package_1.0.119.0", fixture);
            Assert.NotEqual(first, replacement);
            Assert.Equal("hosting dependency", File.ReadAllText(Path.Combine(replacement, "PowerShellEditorServices", "bin", "Core", "Microsoft.PowerShell.EditorServices.Hosting.dll")));
        }
        finally
        {
            if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true);
        }
    }
}
