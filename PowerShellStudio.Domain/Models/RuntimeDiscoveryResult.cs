using System.Collections.Generic;

namespace PowerShellStudio.Domain.Models
{
    public class RuntimeDiscoveryResult
    {
        public RuntimeDiscoveryResult(
            IReadOnlyList<PowerShellRuntimeInfo> detectedRuntimes,
            PowerShellRuntimeInfo? preferredRuntime,
            string summaryText)
        {
            DetectedRuntimes = detectedRuntimes;
            PreferredRuntime = preferredRuntime;
            SummaryText = summaryText;
        }

        public IReadOnlyList<PowerShellRuntimeInfo> DetectedRuntimes { get; }

        public PowerShellRuntimeInfo? PreferredRuntime { get; }

        public string SummaryText { get; }
    }
}
