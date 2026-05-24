using System;
using System.Threading;
using System.Threading.Tasks;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Interfaces
{
    public interface ILiveConsoleService : IDisposable
    {
        bool IsSessionRunning { get; }

        bool IsCommandInProgress { get; }

        bool IsHostAttached { get; }

        PowerShellRuntimeInfo? ActiveRuntime { get; }

        string? CurrentWorkingDirectory { get; }

        /// <summary>
        /// Fires on the thread-pool when a script dispatched through <see cref="ExecuteScriptAsync"/>
        /// reaches its completion sentinel.
        /// </summary>
        event Action? ScriptExecutionCompleted;

        /// <summary>
        /// Fires on the thread-pool when any dispatched terminal operation (script or console
        /// command) reaches its completion sentinel.
        /// </summary>
        event Action? CommandExecutionCompleted;

        /// <summary>
        /// Fires on the thread-pool when the underlying pwsh.exe process exits for any
        /// reason (including the user calling <c>exit</c> inside a script).
        /// </summary>
        event Action? SessionTerminated;

        /// <summary>
        /// Fires on the thread-pool with raw (ANSI/VT100-intact) terminal output after
        /// the execution-done sentinel has been stripped. Subscribe to this event to
        /// forward ConPTY output directly to an xterm.js terminal emulator. When at
        /// least one handler is subscribed, the cleaned-text fallback path is skipped.
        /// </summary>
        event Action<string>? RawOutputReceived;

        void AttachHost(IntPtr hostHandle, int width, int height);

        void ResizeHost(int width, int height);

        /// <summary>
        /// Resizes the pseudo-console using exact character-grid dimensions supplied by
        /// the terminal emulator (e.g. xterm.js onResize cols/rows). Bypasses the
        /// pixel-to-character estimation used by <see cref="ResizeHost"/>.
        /// </summary>
        void ResizeConsole(int cols, int rows);

        void FocusConsole();

        /// <summary>
        /// Writes raw data directly to the ConPTY input pipe without appending a
        /// sentinel. Used for keystroke-by-keystroke forwarding from xterm.js.
        /// </summary>
        Task WriteRawInputAsync(string data, CancellationToken cancellationToken = default);

        Task StartSessionAsync(
            PowerShellRuntimeInfo runtime,
            Action<ExecutionOutputRecord> onOutput,
            string? startupWorkingDirectory = null,
            CancellationToken cancellationToken = default);

        Task<LiveConsoleCommandResult> ExecuteConsoleCommandAsync(
            string commandText,
            Action<ExecutionOutputRecord> onOutput,
            CancellationToken cancellationToken = default);

        Task<LiveConsoleCommandResult> ExecuteScriptAsync(
            string documentDisplayName,
            string scriptContent,
            Action<ExecutionOutputRecord> onOutput,
            bool executeInCurrentScope = false,
            CancellationToken cancellationToken = default);

        Task<bool> StopConsoleAsync(Action<ExecutionOutputRecord>? onOutput = null);

        /// <summary>
        /// Sends a Ctrl+C (ETX, <c>\x03</c>) interrupt signal to the ConPTY process.
        /// Falls back to a no-op when the session is not running.
        /// </summary>
        Task SendInterruptAsync();
    }
}
