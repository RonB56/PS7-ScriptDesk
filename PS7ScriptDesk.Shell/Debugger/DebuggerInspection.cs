using System;

namespace PS7ScriptDesk.Shell.Debug
{
    public sealed record DebugFrameIdentity(
        Guid SessionId,
        long PauseGeneration,
        int FrameIndex,
        string FunctionName,
        string ScriptPath,
        int LineNumber,
        string InvocationName,
        bool IsCurrentFrame,
        bool IsSelectedInspectionFrame,
        bool IsNavigable)
    {
        public string FrameId => $"{SessionId:N}/{PauseGeneration}/{FrameIndex}";

        public static DebugFrameIdentity Create(
            Guid sessionId,
            long pauseGeneration,
            int frameIndex,
            string? functionName,
            string? scriptPath,
            int lineNumber,
            string? invocationName,
            bool isCurrentFrame)
        {
            var normalizedPath = scriptPath ?? string.Empty;
            return new DebugFrameIdentity(
                sessionId,
                pauseGeneration,
                frameIndex,
                functionName ?? string.Empty,
                normalizedPath,
                lineNumber,
                invocationName ?? string.Empty,
                isCurrentFrame,
                isCurrentFrame,
                lineNumber > 0 && !string.IsNullOrWhiteSpace(normalizedPath) && System.IO.File.Exists(normalizedPath));
        }
    }

    public static class DebugInspectionResultGuard
    {
        public static bool IsCurrent(
            Guid activeSessionId,
            long activePauseGeneration,
            Guid resultSessionId,
            long resultPauseGeneration,
            string? resultFrameId = null,
            string? activeFrameId = null)
        {
            return activeSessionId != Guid.Empty &&
                   activeSessionId == resultSessionId &&
                   activePauseGeneration == resultPauseGeneration &&
                   (activeFrameId is null || string.Equals(activeFrameId, resultFrameId, StringComparison.Ordinal));
        }
    }
}
