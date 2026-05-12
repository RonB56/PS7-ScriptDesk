using System.Collections.Generic;

namespace PowerShellStudio.Domain.Models
{
    public class RuntimeDiscoveryResult
    {
        public RuntimeDiscoveryResult(
            IReadOnlyList<PowerShellRuntimeInfo> detectedRuntimes,
            PowerShellRuntimeInfo? preferredRuntime,
            string summaryText,
            IReadOnlyList<RuntimeDiscoveryCandidateInfo>? candidateResults = null)
        {
            DetectedRuntimes = detectedRuntimes;
            PreferredRuntime = preferredRuntime;
            SummaryText = summaryText;
            CandidateResults = candidateResults ?? new List<RuntimeDiscoveryCandidateInfo>();
        }

        public IReadOnlyList<PowerShellRuntimeInfo> DetectedRuntimes { get; }

        public PowerShellRuntimeInfo? PreferredRuntime { get; }

        public string SummaryText { get; }

        public IReadOnlyList<RuntimeDiscoveryCandidateInfo> CandidateResults { get; }
    }
}
