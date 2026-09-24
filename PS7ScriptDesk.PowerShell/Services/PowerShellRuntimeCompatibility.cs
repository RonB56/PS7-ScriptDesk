using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>
/// Prevents external PowerShell module binaries from being mixed with a different
/// in-process System.Management.Automation binary. Strong-name version equality is
/// not sufficient for this hosting boundary.
/// </summary>
public static class PowerShellRuntimeCompatibility
{
    public static void ValidateBuiltInModuleCompatibility(string runtimeHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeHome);

        var loadedSma = typeof(System.Management.Automation.PowerShell).Assembly;
        var runtimeSmaPath = Path.Combine(runtimeHome, "System.Management.Automation.dll");
        var utilityPath = Path.Combine(runtimeHome, "Microsoft.PowerShell.Commands.Utility.dll");
        var managementPath = Path.Combine(runtimeHome, "Microsoft.PowerShell.Commands.Management.dll");
        var requiredPaths = new[] { runtimeSmaPath, utilityPath, managementPath };
        var missing = requiredPaths.Where(path => !File.Exists(path)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"The selected PowerShell runtime is missing required compatibility binaries: {string.Join(", ", missing.Select(Path.GetFileName))}.");
        }

        var loadedSmaHash = ComputeSha256(loadedSma.Location);
        var runtimeSmaHash = ComputeSha256(runtimeSmaPath);
        var utilityAssemblyName = AssemblyName.GetAssemblyName(utilityPath);
        var managementAssemblyName = AssemblyName.GetAssemblyName(managementPath);
        var loadedContext = AssemblyLoadContext.GetLoadContext(loadedSma)?.Name ?? "(unknown)";

        DeveloperDiagnostics.LogInfo(
            "EditorExecutionBroker",
            "Validated selected runtime built-in module identities before in-process import.",
            new Dictionary<string, object?>
            {
                ["loadedSmaFullName"] = loadedSma.FullName,
                ["loadedSmaLocation"] = loadedSma.Location,
                ["loadedSmaLoadContext"] = loadedContext,
                ["loadedSmaSha256"] = loadedSmaHash,
                ["runtimeSmaFullName"] = AssemblyName.GetAssemblyName(runtimeSmaPath).FullName,
                ["runtimeSmaLocation"] = runtimeSmaPath,
                ["runtimeSmaSha256"] = runtimeSmaHash,
                ["utilityFullName"] = utilityAssemblyName.FullName,
                ["utilityLocation"] = utilityPath,
                ["managementFullName"] = managementAssemblyName.FullName,
                ["managementLocation"] = managementPath
            });

        if (!string.Equals(loadedSmaHash, runtimeSmaHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new PowerShellRuntimeCompatibilityException(
                "The selected external PowerShell built-in modules cannot be imported into ScriptDesk's in-process host because the external System.Management.Automation.dll is not byte-identical to the SMA assembly already loaded by ScriptDesk. " +
                $"LoadedSma='{loadedSma.Location}' ({loadedSma.FullName}, SHA256={loadedSmaHash}); " +
                $"ExternalSma='{runtimeSmaPath}' ({AssemblyName.GetAssemblyName(runtimeSmaPath).FullName}, SHA256={runtimeSmaHash}); " +
                $"Utility='{utilityPath}' ({utilityAssemblyName.FullName}); Management='{managementPath}' ({managementAssemblyName.FullName}).");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public sealed class PowerShellRuntimeCompatibilityException : InvalidOperationException
{
    public PowerShellRuntimeCompatibilityException(string message)
        : base(message)
    {
    }
}
