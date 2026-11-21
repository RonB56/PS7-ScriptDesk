using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>
/// Makes the packaged PSES modules loadable by the external pwsh process.
/// Windows does not grant that process DLL loading access to the app's MSIX path.
/// </summary>
internal static class PsesPackageRuntimeStager
{
    private const int ErrorInsufficientBuffer = 122;

    public static string? TryGetCurrentPackageFullName()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var length = 0;
        if (GetCurrentPackageFullName(ref length, null) != ErrorInsufficientBuffer || length <= 0)
            return null;

        var name = new StringBuilder(length);
        return GetCurrentPackageFullName(ref length, name) == 0 ? name.ToString() : null;
    }

    public static string Stage(string sourceRoot, string packageFullName, string localAppData)
    {
        if (!PsesEditorBridgeRuntimeContract.TryValidate(sourceRoot, out _))
            throw new InvalidDataException("The packaged private PSES runtime failed bridge validation.");

        var packageKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packageFullName)))[..16];
        var cacheRoot = Path.Combine(localAppData, "PS7ScriptDesk", "PSES", "RuntimeCache", packageKey);
        Directory.CreateDirectory(cacheRoot);

        // Each completed directory is immutable. A failed or interrupted copy is
        // never selected, and a running PSES session keeps its own runtime path.
        foreach (var candidate in Directory.EnumerateDirectories(cacheRoot, "runtime-*"))
        {
            try
            {
                if (MatchesSource(sourceRoot, candidate)) return candidate;
            }
            catch (IOException) { /* A damaged cache is replaced by a new completed copy. */ }
            catch (UnauthorizedAccessException) { /* A damaged cache is replaced by a new completed copy. */ }
        }

        var staged = Path.Combine(cacheRoot, "staging-" + Guid.NewGuid().ToString("N"));
        var completed = Path.Combine(cacheRoot, "runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(staged, Path.GetRelativePath(sourceRoot, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }

            if (!MatchesSource(sourceRoot, staged))
                throw new InvalidDataException("The staged PSES runtime did not match the packaged runtime.");

            Directory.Move(staged, completed);
            return completed;
        }
        finally
        {
            if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
        }
    }

    private static bool MatchesSource(string sourceRoot, string candidate)
    {
        if (!PsesEditorBridgeRuntimeContract.TryValidate(candidate, out _)) return false;

        var sourceFiles = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidateFiles = Directory.EnumerateFiles(candidate, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(candidate, path))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!sourceFiles.SequenceEqual(candidateFiles, StringComparer.OrdinalIgnoreCase)) return false;

        foreach (var relativePath in sourceFiles)
        {
            var source = Path.Combine(sourceRoot, relativePath);
            var copy = Path.Combine(candidate, relativePath);
            if (new FileInfo(source).Length != new FileInfo(copy).Length) return false;
            using var sourceStream = File.OpenRead(source);
            using var copyStream = File.OpenRead(copy);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(sourceStream), SHA256.HashData(copyStream)))
                return false;
        }

        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);
}
