using System.Security.Cryptography;

namespace PS7ScriptDesk.PowerShell.Services;

internal static class PsesEditorBridgeRuntimeContract
{
    private const string AssemblyRelativePath = "PowerShellEditorServices/bin/Core/Microsoft.PowerShell.EditorServices.dll";

    public static bool TryValidate(string modulesRoot, out string identity)
    {
        identity = "unverified";
        try
        {
            var markerPath = Path.Combine(modulesRoot, "ScriptDeskEditorBridge.protocol");
            var manifestPath = Path.Combine(modulesRoot, "ScriptDeskEditorBridge.manifest");
            var assemblyPath = Path.Combine(modulesRoot, AssemblyRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(markerPath) || !File.Exists(manifestPath) || !File.Exists(assemblyPath)) return false;

            var marker = File.ReadAllLines(markerPath);
            if (marker.Length != 2 || marker[0] != "PSES=4.4.0" || marker[1] != "Protocol=1") return false;

            var manifest = File.ReadAllLines(manifestPath)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
            if (manifest.Count != 4 || manifest.GetValueOrDefault("PSES") != "4.4.0" ||
                manifest.GetValueOrDefault("Protocol") != "1" || manifest.GetValueOrDefault("Assembly") != AssemblyRelativePath ||
                !IsSha256(manifest.GetValueOrDefault("SHA256"))) return false;

            using var stream = File.OpenRead(assemblyPath);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash), Convert.FromHexString(manifest["SHA256"]))) return false;

            var moduleManifest = Path.Combine(modulesRoot, "PowerShellEditorServices", "PowerShellEditorServices.psd1");
            if (!File.Exists(moduleManifest) || !File.ReadAllText(moduleManifest).Contains("ModuleVersion = '4.4.0'", StringComparison.Ordinal)) return false;
            identity = $"PSES-4.4.0;Bridge-Protocol-1;Assembly-SHA256-{actualHash}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
