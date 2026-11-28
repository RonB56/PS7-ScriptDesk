using System;
using System.IO;

namespace PS7ScriptDesk.Application.Utilities
{
    public static class AppTemporaryStorage
    {
        private const string ApplicationFolderName = ApplicationBranding.InternalName;
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

            var localAppData = ExternalProcessPathContract.DataRoot;
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
            return TryValidateManagedRootDirectory(
                candidateRoot,
                ExternalProcessPathContract.DataRoot,
                AppContext.BaseDirectory,
                Environment.CurrentDirectory,
                out normalizedRoot,
                out failureReason);
        }

        internal static bool TryValidateManagedRootDirectory(
            string candidateRoot,
            string localAppData,
            string runtimeBaseDirectory,
            string currentDirectory,
            out string normalizedRoot,
            out string failureReason)
        {
            normalizedRoot = string.Empty;
            failureReason = string.Empty;

            if (string.IsNullOrWhiteSpace(candidateRoot) || string.IsNullOrWhiteSpace(localAppData))
            {
                failureReason = "The managed temp root path was empty.";
                return false;
            }

            string managedTempBase;

            try
            {
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

            if (OverlapsRuntimeLocation(normalizedRoot, runtimeBaseDirectory))
            {
                failureReason = $"The managed temp root '{normalizedRoot}' overlaps the application runtime directory.";
                normalizedRoot = string.Empty;
                return false;
            }

            if (OverlapsCurrentDirectory(normalizedRoot, currentDirectory))
            {
                failureReason = $"The managed temp root '{normalizedRoot}' overlaps the current working directory.";
                normalizedRoot = string.Empty;
                return false;
            }

            return true;
        }

        private static bool OverlapsRuntimeLocation(string candidateRoot, string runtimeBaseDirectory)
        {
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

        private static bool OverlapsCurrentDirectory(string candidateRoot, string currentDirectory)
        {
            if (string.IsNullOrWhiteSpace(currentDirectory))
            {
                return false;
            }

            try
            {
                var normalizedCurrentDirectory = Path.GetFullPath(currentDirectory);
                return IsSameOrChildPath(normalizedCurrentDirectory, candidateRoot);
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
