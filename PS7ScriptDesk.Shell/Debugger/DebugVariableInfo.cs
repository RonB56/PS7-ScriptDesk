using System;

namespace PS7ScriptDesk.Shell.Debug
{
    public sealed record DebugVariableInfo(string Name, string Type, string Value)
    {
        public Guid SessionId { get; init; }
        public long PauseGeneration { get; init; }
        public string Scope { get; init; } = "Current";
        public string FrameId { get; init; } = string.Empty;
        public bool IsNull { get; init; }
        public bool IsExpandable { get; init; }
        public bool HasChildren { get; init; }
        public int ChildCount { get; init; }
        public bool IsTruncated { get; init; }
        public bool IsSensitive { get; init; }
        public string LoadState { get; init; } = "Loaded";
        public string? Error { get; init; }

        public string DisplayValue => Value;
    }
}
