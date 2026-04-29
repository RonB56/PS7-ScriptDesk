using System;
using System.IO;

namespace PowerShellStudio.Application.Utilities
{
    public static class AppTemporaryStorage
    {
        private const string ApplicationFolderName = "PowerShellStudio";
        private const string TempFolderName = "Temp";

        public static bool TryGetManagedRootDirectory(string areaName, bool createIfMissing, out string rootDirectory, out string failureReason)
        {
            rootDirectory = string.Empty;
            failureReason = string.Empty;

            if (string.IsNullOrWhiteSpace(areaName))
            {
                failureReason = "The temporary storage area name was empty.";
                return false;
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                failureReason = "LocalAppData could not be resolved.";
                return false;
            }

            var safeAreaName = SanitizePathSegment(areaName);
            var candidateRoot = Path.Combine(localAppData, ApplicationFolderName, TempFolderName, safeAreaName);
            if (!TryValidateManagedRootDirectory(candidateRoot, out var normalizedRoot, out failureReason))
            {
                return false;
            }

            if (createIfMissing)
            {
                try
                {
                    Directory.CreateDirectory(normalizedRoot);
                }
                catch (Exception ex)
                {
                    failureReason = $"The managed temp directory '{normalizedRoot}' could not be created: {ex.Message}";
                    return false;
                }
            }

            rootDirectory = normalizedRoot;
            return true;
        }

        public static bool TryValidateManagedPath(string rootDirectory, string path, out string normalizedRoot, out string normalizedPath, out string failureReason)
        {
            normalizedRoot = string.Empty;
            normalizedPath = string.Empty;
            failureReason = string.Empty;

            if (!TryValidateManagedRootDirectory(rootDirectory, out normalizedRoot, out failureReason))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                failureReason = "The candidate temp path was empty.";
                return false;
            }

            try
            {
                normalizedPath = Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                failureReason = $"The candidate temp path '{path}' could not be normalized: {ex.Message}";
                return false;
            }

            if (!IsSameOrChildPath(normalizedPath, normalizedRoot))
            {
                failureReason = $"The candidate temp path '{normalizedPath}' is outside the managed temp root '{normalizedRoot}'.";
                normalizedPath = string.Empty;
                return false;
            }

            return true;
        }

        private static bool TryValidateManagedRootDirectory(string candidateRoot, out string normalizedRoot, out string failureReason)
        {
            normalizedRoot = string.Empty;
            failureReason = string.Empty;

            if (string.IsNullOrWhiteSpace(candidateRoot))
            {
                failureReason = "The managed temp root path was empty.";
                return false;
            }

            string localAppData;
            string managedTempBase;

            try
            {
                localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                managedTempBase = Path.GetFullPath(Path.Combine(localAppData, ApplicationFolderName, TempFolderName));
                normalizedRoot = Path.GetFullPath(candidateRoot);
            }
            catch (Exception ex)
            {
                failureReason = $"The managed temp root could not be normalized: {ex.Message}";
                normalizedRoot = string.Empty;
                return false;
            }

            if (!IsSameOrChildPath(normalizedRoot, managedTempBase))
            {
                failureReason = $"The managed temp root '{normalizedRoot}' is outside the application temp base '{managedTempBase}'.";
                normalizedRoot = string.Empty;
                return false;
            }

            if (OverlapsRuntimeLocation(normalizedRoot))
            {
                failureReason = $"The managed temp root '{normalizedRoot}' overlaps the application runtime directory.";
                normalizedRoot = string.Empty;
                return false;
            }

            if (OverlapsCurrentDirectory(normalizedRoot))
            {
                failureReason = $"The managed temp root '{normalizedRoot}' overlaps the current working directory.";
                normalizedRoot = string.Empty;
                return false;
            }

            return true;
        }

        private static bool OverlapsRuntimeLocation(string candidateRoot)
        {
            var runtimeBaseDirectory = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(runtimeBaseDirectory))
            {
                return false;
            }

            try
            {
                var normalizedRuntimeBase = Path.GetFullPath(runtimeBaseDirectory);
                return IsSameOrChildPath(candidateRoot, normalizedRuntimeBase) || IsSameOrChildPath(normalizedRuntimeBase, candidateRoot);
            }
            catch
            {
                return false;
            }
        }

        private static bool OverlapsCurrentDirectory(string candidateRoot)
        {
            var currentDirectory = Environment.CurrentDirectory;
            if (string.IsNullOrWhiteSpace(currentDirectory))
            {
                return false;
            }

            try
            {
                var normalizedCurrentDirectory = Path.GetFullPath(currentDirectory);
                return IsSameOrChildPath(candidateRoot, normalizedCurrentDirectory) || IsSameOrChildPath(normalizedCurrentDirectory, candidateRoot);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSameOrChildPath(string candidatePath, string parentPath)
        {
            var normalizedParent = parentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedCandidate = candidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(normalizedCandidate, normalizedParent, StringComparison.OrdinalIgnoreCase)
                || normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || normalizedCandidate.StartsWith(normalizedParent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string SanitizePathSegment(string value)
        {
            var sanitized = value.Trim();
            foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
            {
                sanitized = sanitized.Replace(invalidCharacter, '_');
            }

            return string.IsNullOrWhiteSpace(sanitized) ? "General" : sanitized;
        }
    }
}
