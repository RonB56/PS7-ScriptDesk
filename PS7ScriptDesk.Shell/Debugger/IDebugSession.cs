using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Shell.Debug
{
    public interface IDebugSession : IDisposable
    {
        DebugSessionState CurrentState { get; }

        event Action<DebugSessionState>? StateChanged;
        event Action<string?, int>? BreakpointHit;
        event Action? SessionEnded;
        event Action<string>? OutputReceived;

        Task StartAsync(PowerShellRuntimeInfo runtime, string launchScriptPath, IReadOnlyList<DebugBreakpointInfo> breakpoints);
        Task ContinueAsync();
        Task StepIntoAsync();
        Task StepOverAsync();
        Task StepOutAsync();
        Task<IReadOnlyList<DebugVariableInfo>> GetVariablesAsync();
        Task<IReadOnlyList<DebugCallStackFrame>> GetCallStackAsync();
    }
}
