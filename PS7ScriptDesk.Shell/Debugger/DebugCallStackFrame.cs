namespace PS7ScriptDesk.Shell.Debug
{
    public sealed record DebugCallStackFrame(string FunctionName, string ScriptName, int LineNumber);
}
