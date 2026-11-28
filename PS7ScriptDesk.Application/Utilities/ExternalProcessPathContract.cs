using System.Runtime.InteropServices;
using System.Text;

namespace PS7ScriptDesk.Application.Utilities;

/// <summary>Physical paths for application-owned files exchanged with a process outside the MSIX container.</summary>
public static class ExternalProcessPathContract
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    public static string? TryGetCurrentPackageFullName() => ReadPackageName(GetCurrentPackageFullName, "full name");
    public static string? TryGetCurrentPackageFamilyName() => ReadPackageName(GetCurrentPackageFamilyName, "family name");

    public static string DataRoot => ResolveDataRoot(TryGetCurrentPackageFullName(),
        TryGetCurrentPackageFamilyName(), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string TempRoot => ResolveTempRoot(TryGetCurrentPackageFullName(),
        TryGetCurrentPackageFamilyName(), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath());

    public static string ApplicationDataRoot => Path.Combine(DataRoot, ApplicationBranding.InternalName);

    public static string ResolveApplicationOwnedPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var logicalRoot = Path.GetFullPath(ApplicationBranding.LocalApplicationDataRoot);
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.Equals(logicalRoot, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(logicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The path is outside ScriptDesk application-owned LocalApplicationData.", nameof(path));
        return Path.Combine(ApplicationDataRoot, Path.GetRelativePath(logicalRoot, fullPath));
    }

    public static string ResolveDataRoot(string? fullName, string? familyName, string localAppData)
    {
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new InvalidOperationException("Windows LocalApplicationData is unavailable for external-process files.");
        if (fullName is null) return localAppData;
        if (string.IsNullOrWhiteSpace(familyName) || familyName is "." or ".." ||
            familyName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0)
            throw new InvalidOperationException("The Windows package family name is unavailable or invalid for external-process files.");
        return Path.Combine(localAppData, "Packages", familyName, "LocalCache", "Local");
    }

    public static string ResolveTempRoot(string? fullName, string? familyName, string localAppData, string ordinaryTemp)
        => fullName is null ? ordinaryTemp : Path.Combine(ResolveDataRoot(fullName, familyName, localAppData), "Temp");

    private static string? ReadPackageName(PackageNameReader reader, string kind)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var length = 0;
        var status = reader(ref length, null);
        if (status == AppModelErrorNoPackage) return null;
        if (status != ErrorInsufficientBuffer || length <= 0)
            throw new InvalidOperationException($"Unable to resolve Windows package {kind} (error {status}).");
        var buffer = new StringBuilder(length);
        status = reader(ref length, buffer);
        if (status != 0 || buffer.Length == 0)
            throw new InvalidOperationException($"Unable to read Windows package {kind} (error {status}).");
        return buffer.ToString().TrimEnd('\0');
    }

    private delegate int PackageNameReader(ref int length, StringBuilder? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFamilyName(ref int length, StringBuilder? name);
}
