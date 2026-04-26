using System;

namespace PowerShellStudio.Domain.Models
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
            bool isPreferred)
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
        }

        public string DisplayName { get; }

        public string Edition { get; }

        public string VersionText { get; }

        public Version Version { get; }

        public string Architecture { get; }

        public string ExecutablePath { get; }

        public string DiscoverySource { get; }

        public bool IsPowerShell7OrLater { get; }

        public bool IsWindowsPowerShell { get; }

        public bool IsPreferred { get; }
    }
}
