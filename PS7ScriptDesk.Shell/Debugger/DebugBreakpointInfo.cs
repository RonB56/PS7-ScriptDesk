namespace PS7ScriptDesk.Shell.Debug
{
    public sealed record DebugBreakpointInfo(string ScriptPath, int LineNumber)
    {
        public Guid SourceDocumentId { get; init; }
        public long SourceRevision { get; init; }
    }
}
