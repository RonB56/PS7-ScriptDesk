using System.Security.Cryptography;

namespace PS7ScriptDesk.Domain.Models;

/// <summary>Destination-bound consent snapshot for replacing a single-file EXE export.</summary>
public sealed class ExeExportReplacementAuthorization
{
    private ExeExportReplacementAuthorization(string destinationPath, bool existed, long length, long lastWriteUtcTicks, string? sha256)
    {
        DestinationPath = destinationPath;
        Existed = existed;
        Length = length;
        LastWriteUtcTicks = lastWriteUtcTicks;
        Sha256 = sha256;
    }

    public string DestinationPath { get; }
    public bool Existed { get; }
    public long Length { get; }
    public long LastWriteUtcTicks { get; }
    public string? Sha256 { get; }

    public static ExeExportReplacementAuthorization Capture(string destinationPath)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        if (!File.Exists(fullPath))
            return new ExeExportReplacementAuthorization(fullPath, false, 0, 0, null);
        var info = new FileInfo(fullPath);
        using var stream = info.OpenRead();
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        info.Refresh();
        return new ExeExportReplacementAuthorization(fullPath, true, info.Length, info.LastWriteTimeUtc.Ticks, hash);
    }

    /// <summary>Captures a destination only when the captured state positively establishes absence.</summary>
    public static bool TryCaptureAbsentDestination(string destinationPath, out ExeExportReplacementAuthorization? authorization)
    {
        var captured = Capture(destinationPath);
        if (captured.Existed)
        {
            authorization = null;
            return false;
        }

        authorization = captured;
        return true;
    }

    public bool Matches(string destinationPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(destinationPath);
            if (!string.Equals(DestinationPath, fullPath, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!Existed)
                return !File.Exists(fullPath) && !Directory.Exists(fullPath);
            if (!File.Exists(fullPath) || Directory.Exists(fullPath))
                return false;
            var info = new FileInfo(fullPath);
            using var stream = info.OpenRead();
            return info.Length == Length && info.LastWriteTimeUtc.Ticks == LastWriteUtcTicks &&
                   string.Equals(Convert.ToHexString(SHA256.HashData(stream)), Sha256, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
