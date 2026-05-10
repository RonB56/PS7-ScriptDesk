namespace PowerShellStudio.Shell.Debug
{
    public sealed record DebugCallStackFrame(string FunctionName, string ScriptName, int LineNumber);
}
