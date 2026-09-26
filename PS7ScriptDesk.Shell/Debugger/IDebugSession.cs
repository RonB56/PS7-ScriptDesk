using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Shell.Debug
{
    public interface IDebugSession : IDisposable
    {
        DebugSessionState CurrentState { get; }
        Guid SessionId { get; }
        long PauseGeneration { get; }
        DebugTerminationInfo? TerminationInfo { get; }
        DebuggerPauseReason CurrentPauseReason { get; }

        event Action<DebugSessionState>? StateChanged;
        event Action<string?, int>? BreakpointHit;
        event Action? SessionEnded;
        event Action<DebugTerminationInfo>? Terminated;
        event Action<string>? OutputReceived;
        event Action<DebuggerEvent>? TypedEventReceived;

        Task StartAsync(PowerShellRuntimeInfo runtime, string launchScriptPath, IReadOnlyList<DebugBreakpointInfo> breakpoints);
        Task ContinueAsync();
        Task StepIntoAsync();
        Task StepOverAsync();
        Task StepOutAsync();
        Task<IReadOnlyList<DebugVariableInfo>> GetVariablesAsync();
        Task<IReadOnlyList<DebugCallStackFrame>> GetCallStackAsync();
        Task<bool> StopAsync(CancellationToken cancellationToken = default);
    }
}
