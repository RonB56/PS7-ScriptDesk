using System;

namespace PS7ScriptDesk.Domain.Models
{
    public class PowerShellRuntimeInfo
    {
        public PowerShellRuntimeInfo(
            string displayName,
            string edition,
            string versionText,
            Version version,
            string architecture,
            string executablePath,
            string discoverySource,
            bool isPowerShell7OrLater,
            bool isWindowsPowerShell,
            bool isPreferred,
            bool isValidated = false,
            bool isWindowsAppsAlias = false,
            string? resolvedExecutablePath = null,
            string? psHome = null,
            string? validationMessage = null)
        {
            DisplayName = displayName;
            Edition = edition;
            VersionText = versionText;
            Version = version;
            Architecture = architecture;
            ExecutablePath = executablePath;
            DiscoverySource = discoverySource;
            IsPowerShell7OrLater = isPowerShell7OrLater;
            IsWindowsPowerShell = isWindowsPowerShell;
            IsPreferred = isPreferred;
            IsValidated = isValidated;
            IsWindowsAppsAlias = isWindowsAppsAlias;
            ResolvedExecutablePath = resolvedExecutablePath ?? string.Empty;
            PsHome = psHome ?? string.Empty;
            ValidationMessage = validationMessage ?? string.Empty;
        }

        public string DisplayName { get; }

        public string Edition { get; }

        public string VersionText { get; }

        public Version Version { get; }

        public string Architecture { get; }

        public string ExecutablePath { get; }

        public string LaunchExecutablePath => string.IsNullOrWhiteSpace(ResolvedExecutablePath)
            ? ExecutablePath
            : ResolvedExecutablePath;

        public string DiscoverySource { get; }

        public bool IsPowerShell7OrLater { get; }

        public bool IsWindowsPowerShell { get; }

        public bool IsPreferred { get; }

        public bool IsValidated { get; }

        public bool IsWindowsAppsAlias { get; }

        public string ResolvedExecutablePath { get; }

        public string PsHome { get; }

        public string ValidationMessage { get; }
    }
}
