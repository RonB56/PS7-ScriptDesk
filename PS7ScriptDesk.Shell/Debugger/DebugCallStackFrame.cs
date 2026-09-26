using System;

namespace PS7ScriptDesk.Shell.Debug
{
    public sealed record DebugCallStackFrame(string FunctionName, string ScriptName, int LineNumber)
    {
        public Guid SessionId { get; init; }
        public long PauseGeneration { get; init; }
        public int FrameIndex { get; init; }
        public string InvocationName { get; init; } = string.Empty;
        public bool IsCurrentFrame { get; init; }
        public bool IsSelectedInspectionFrame { get; init; }
        public bool IsNavigable { get; init; }
        public Guid? MappedSourceDocumentId { get; init; }
        public string? MappedSourcePath { get; init; }
        public int? MappedSourceLine { get; init; }
        public DebugSourceMappingStatus SourceMappingStatus { get; init; } = DebugSourceMappingStatus.Unmapped;
        public string FrameId => $"{SessionId:N}/{PauseGeneration}/{FrameIndex}";
        public string DisplayScriptName => DebuggerCoordinatePresentation.FormatCallStackScript(ScriptName, SourceMappingStatus);
        public string DisplayLineNumber => DebuggerCoordinatePresentation.FormatCallStackLine(LineNumber, SourceMappingStatus);

        public DebugFrameIdentity Identity => new(
            SessionId,
            PauseGeneration,
            FrameIndex,
            FunctionName,
            ScriptName,
            LineNumber,
            InvocationName,
            IsCurrentFrame,
            IsSelectedInspectionFrame,
            IsNavigable);
    }
}
