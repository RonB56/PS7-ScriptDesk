namespace PS7ScriptDesk.PowerShell.Services
{
    internal static class TerminalSessionEventPolicy
    {
        public static bool IsCurrentSession(
            int currentGeneration,
            int observedGeneration,
            bool teardownInProgress)
        {
            return !teardownInProgress && currentGeneration == observedGeneration;
        }

        public static bool IsCurrentDispatch(
            bool commandInProgress,
            int currentGeneration,
            int observedGeneration)
        {
            return commandInProgress && currentGeneration == observedGeneration;
        }

        public static bool IsInterruptRecoveryComplete(
            bool commandInProgress,
            bool processRunning,
            bool hasVisibleOwnedWindow)
        {
            return !processRunning || (!commandInProgress && !hasVisibleOwnedWindow);
        }

        public static bool ShouldIgnoreProcessExit(
            bool hasTrackedProcess,
            bool trackedCommandInProgress,
            int? exitedProcessId,
            int? currentProcessId,
            int? handledProcessId)
        {
            return (!hasTrackedProcess && !trackedCommandInProgress) ||
                   (exitedProcessId.HasValue && currentProcessId.HasValue && exitedProcessId.Value != currentProcessId.Value) ||
                   (exitedProcessId.HasValue && handledProcessId == exitedProcessId.Value);
        }
    }
}
