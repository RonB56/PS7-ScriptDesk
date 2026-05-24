using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.UI.ViewModels
{
    public class RuntimeItemViewModel
    {
        public RuntimeItemViewModel(PowerShellRuntimeInfo runtime)
        {
            RuntimeInfo = runtime;
            DisplayName = runtime.DisplayName;
            Edition = runtime.Edition;
            VersionText = runtime.VersionText;
            Architecture = runtime.Architecture;
            ExecutablePath = runtime.ExecutablePath;
            DiscoverySource = runtime.DiscoverySource;
            IsPowerShell7OrLater = runtime.IsPowerShell7OrLater;
            IsWindowsPowerShell = runtime.IsWindowsPowerShell;
            IsPreferred = runtime.IsPreferred;
        }

        public PowerShellRuntimeInfo RuntimeInfo { get; }

        public string DisplayName { get; }

        public string Edition { get; }

        public string VersionText { get; }

        public string Architecture { get; }

        public string ExecutablePath { get; }

        public string DiscoverySource { get; }

        public bool IsPowerShell7OrLater { get; }

        public bool IsWindowsPowerShell { get; }

        public bool IsPreferred { get; }

        public string DisplayText => IsPreferred
            ? $"{DisplayName} [Preferred]"
            : DisplayName;

        public string DetailSummary => string.IsNullOrWhiteSpace(Architecture)
            ? $"{DisplayName} | Edition: {Edition} | Source: {DiscoverySource}"
            : $"{DisplayName} | Edition: {Edition} | Arch: {Architecture} | Source: {DiscoverySource}";
    }
}
