using System.Text.RegularExpressions;

namespace PS7ScriptDesk.PowerShell.Services;

// Metadata only. Never retain script contents, bridge payloads, or policy environment values.
internal static class PsesExecutionPolicyDiagnostics
{
    private static readonly SemaphoreSlim ZoneProbeGate = new(1, 1);
    private static readonly Regex ZoneIdPattern = new(@"(?im)^\s*ZoneId\s*=\s*(\d{1,3})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string[] DescribeArguments(IReadOnlyList<string> arguments)
    {
        // Unknown future values are redacted by default.
        var policyValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Bypass", "RemoteSigned", "AllSigned", "Restricted", "Unrestricted", "Default", "Undefined"
        };
        var knownOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "-NoLogo", "-NoProfile", "-ExecutionPolicy", "-File", "-HostName", "-HostProfileId",
            "-HostVersion", "-BundledModulesPath", "-EnableConsoleRepl", "-SessionDetailsPath",
            "-LogPath", "-LogLevel"
        };
        var result = new List<string>(arguments.Count);
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (knownOptions.Contains(argument))
                result.Add(argument);
            else if (index > 0 && arguments[index - 1].Equals("-ExecutionPolicy", StringComparison.OrdinalIgnoreCase)
                && policyValues.Contains(argument))
                result.Add(argument);
            else if (index > 0 && arguments[index - 1].Equals("-LogLevel", StringComparison.OrdinalIgnoreCase)
                && argument is "Trace" or "Debug" or "Information" or "Warning" or "Error" or "Critical")
                result.Add(argument);
            else
                result.Add("<redacted>");
        }
        return result.ToArray();
    }

    internal static string ClassifyRuntimeSource(string root)
    {
        if (Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT") is { Length: > 0 })
            return "EnvironmentOverride";
        var bundled = Path.Combine(AppContext.BaseDirectory, "PSES");
        if (string.Equals(root, bundled, StringComparison.OrdinalIgnoreCase)) return "Bundled";
        var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PS7ScriptDesk", "PSES", "RuntimeCache") + Path.DirectorySeparatorChar;
        if (root.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase)) return "PackagedStaging";
        return "ExtensionOrOther";
    }

    internal static async Task<ZoneObservation> ObserveZoneAsync(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return new(null, null, null, "RemotePathSkipped");
        if (!ZoneProbeGate.Wait(0))
            return new(null, null, null, "ProbeBusy");
        try
        {
            return await Task.Run(() =>
            {
                try { return ObserveZone(path); }
                finally { ZoneProbeGate.Release(); }
            }).WaitAsync(TimeSpan.FromMilliseconds(250))
                .ConfigureAwait(false);
        }
        catch (TimeoutException) { return new(true, null, null, "TimedOut"); }
        catch { return new(null, null, null, "Unavailable"); }
    }

    private static ZoneObservation ObserveZone(string path)
    {
        if (!File.Exists(path)) return new(false, null, null, "PathMissing");
        try
        {
            using var stream = new FileStream(path + ":Zone.Identifier", FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1024, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            var buffer = new char[1024];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            var match = ZoneIdPattern.Match(new string(buffer, 0, count));
            return new(true, true, match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : null,
                match.Success ? "Read" : "ZoneIdUnavailable");
        }
        catch (FileNotFoundException) { return new(true, false, null, "NoZoneStream"); }
        catch (DirectoryNotFoundException) { return new(true, false, null, "NoZoneStream"); }
        catch { return new(true, null, null, "ZoneReadUnavailable"); }
    }

    internal static string ClassifyError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "None";
        if (error.Contains("PSSecurityException", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("digitally signed", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("digital signature", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("running scripts is disabled", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("AuthorizationManager check failed", StringComparison.OrdinalIgnoreCase))
            return "ExecutionPolicyCandidate";
        return "Other";
    }
}

internal sealed record ZoneObservation(bool? PathExists, bool? HasZoneIdentifier, int? ZoneId, string Status);
