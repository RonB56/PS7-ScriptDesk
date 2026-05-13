namespace PowerShellStudio.Domain.Models
{
    public class RuntimeValidationResult
    {
        public RuntimeValidationResult(PowerShellRuntimeInfo? runtimeInfo, RuntimeDiscoveryCandidateInfo candidateInfo)
        {
            RuntimeInfo = runtimeInfo;
            CandidateInfo = candidateInfo;
        }

        public PowerShellRuntimeInfo? RuntimeInfo { get; }

        public RuntimeDiscoveryCandidateInfo CandidateInfo { get; }

        public bool IsValid => RuntimeInfo is not null;

        public string FailureReason => CandidateInfo.FailureReason;
    }
}
