namespace PowerShellStudio.Domain.Models
{
    public class RuntimeDiscoveryCandidateInfo
    {
        public RuntimeDiscoveryCandidateInfo(
            string candidatePath,
            string source,
            bool exists,
            bool isWindowsAppsAlias,
            bool validationAttempted,
            bool launchSucceeded,
            bool validationSucceeded,
            bool timedOut,
            int? exitCode,
            string edition,
            string versionText,
            string architecture,
            string resolvedExecutablePath,
            string psHome,
            string stdoutSummary,
            string stderrSummary,
            string fileVersion,
            string productVersion,
            string failureReason)
        {
            CandidatePath = candidatePath;
            Source = source;
            Exists = exists;
            IsWindowsAppsAlias = isWindowsAppsAlias;
            ValidationAttempted = validationAttempted;
            LaunchSucceeded = launchSucceeded;
            ValidationSucceeded = validationSucceeded;
            TimedOut = timedOut;
            ExitCode = exitCode;
            Edition = edition;
            VersionText = versionText;
            Architecture = architecture;
            ResolvedExecutablePath = resolvedExecutablePath;
            PsHome = psHome;
            StdoutSummary = stdoutSummary;
            StderrSummary = stderrSummary;
            FileVersion = fileVersion;
            ProductVersion = productVersion;
            FailureReason = failureReason;
        }

        public string CandidatePath { get; }

        public string Source { get; }

        public bool Exists { get; }

        public bool IsWindowsAppsAlias { get; }

        public bool ValidationAttempted { get; }

        public bool LaunchSucceeded { get; }

        public bool ValidationSucceeded { get; }

        public bool TimedOut { get; }

        public int? ExitCode { get; }

        public string Edition { get; }

        public string VersionText { get; }

        public string Architecture { get; }

        public string ResolvedExecutablePath { get; }

        public string PsHome { get; }

        public string StdoutSummary { get; }

        public string StderrSummary { get; }

        public string FileVersion { get; }

        public string ProductVersion { get; }

        public string FailureReason { get; }
    }
}
