namespace PS7ScriptDesk.PowerShell.Services
{
    internal static class TerminalSessionEventPolicy
    {
        public static bool IsCurrentDispatch(
            bool commandInProgress,
            int currentGeneration,
            int observedGeneration)
        {
            return commandInProgress && currentGeneration == observedGeneration;
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
