using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>Owns the native ConPTY transport and its single child process.</summary>
internal sealed class ConPtyProcess : IAsyncDisposable
{
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private SafeFileHandle? _inputWriter;
    private SafeFileHandle? _outputReader;
    private IntPtr _pseudoConsole;
    private IntPtr _processHandle;
    private IntPtr _threadHandle;
    private int _disposed;

    private ConPtyProcess(ProcessIdentity identity, FileStream input, FileStream output, SafeFileHandle inputWriter, SafeFileHandle outputReader, IntPtr pseudoConsole)
    {
        Identity = identity;
        Input = input;
        Output = output;
        _inputWriter = inputWriter;
        _outputReader = outputReader;
        _pseudoConsole = pseudoConsole;
    }

    public ProcessIdentity Identity { get; }
    public FileStream Input { get; }
    public FileStream Output { get; }
    public int ProcessId => Identity.ProcessId;
    public DateTimeOffset ProcessStartTimeUtc
    {
        get
        {
            try { return Identity.Process.StartTime.ToUniversalTime(); }
            catch { return DateTimeOffset.MinValue; }
        }
    }
    public bool HasExited
    {
        get
        {
            try { return Identity.Process.WaitForExit(0); }
            catch { return true; }
        }
    }

    public static ConPtyProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory, int columns = 120, int rows = 40)
    {
        using var inputRead = CreatePipe(read: true, inherit: false, out var inputWrite);
        using var outputWrite = CreatePipe(read: false, inherit: false, out var outputRead);
        DeveloperDiagnostics.LogInfo("Phase2CProof", "ConPTY pipe handles created with synchronous CreatePipe semantics.", new Dictionary<string, object?>
        {
            ["inputReadPassedToPseudoConsole"] = true,
            ["inputWriteRetainedByScriptDesk"] = true,
            ["outputWritePassedToPseudoConsole"] = true,
            ["outputReadRetainedByScriptDesk"] = true,
            ["pipeHandleMode"] = "synchronous",
            ["fileStreamIsAsync"] = false
        });
        var size = new COORD((short)Math.Clamp(columns, 1, short.MaxValue), (short)Math.Clamp(rows, 1, short.MaxValue));
        var hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out var pseudoConsole);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        DeveloperDiagnostics.LogInfo("Phase2CProof", "ConPTY pseudo-console created.", new Dictionary<string, object?>
        {
            ["pseudoConsoleCreated"] = true,
            ["inputReadPassedToPseudoConsole"] = true,
            ["outputWritePassedToPseudoConsole"] = true
        });

        try
        {
            var commandLine = BuildCommandLine(executable, arguments);
            // The ConPTY association is carried by PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE.
            // STARTF_USESTDHANDLES is required by the maintained Windows Terminal
            // launch shape, but the PTY transport handles must remain parent-owned and
            // must not be assigned as inherited child stdio handles.
            var startup = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>(),
                    dwFlags = StartfUseStdHandles
                }
            };
            var attributeSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
            startup.AttributeList = Marshal.AllocHGlobal(attributeSize);
            try
            {
                if (!InitializeProcThreadAttributeList(startup.AttributeList, 1, 0, ref attributeSize))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var pseudoConsoleValue = pseudoConsole;
                if (!UpdateProcThreadAttribute(startup.AttributeList, 0, ProcThreadAttributePseudoConsole, pseudoConsoleValue, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                using var processEnvironment = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
                if (!CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent, IntPtr.Zero, workingDirectory, ref startup, out var processInfo))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                // CreatePseudoConsole consumes these as the PTY-side endpoints. The
                // parent must close its copies after child creation; retaining them
                // prevents EOF/broken-pipe observation and leaks two handles per run.
                inputRead.Dispose();
                outputWrite.Dispose();

                var process = Process.GetProcessById((int)processInfo.dwProcessId);
                DeveloperDiagnostics.LogInfo("Phase2CProof", "ConPTY child process created; wrapping synchronous retained handles.", new Dictionary<string, object?>
                {
                    ["createProcessSucceeded"] = true,
                    ["childProcessId"] = processInfo.dwProcessId,
                    ["startupFlags"] = startup.StartupInfo.dwFlags,
                    ["explicitStandardHandlesAssigned"] = false,
                    ["inputWriteRetainedByScriptDesk"] = true,
                    ["outputReadRetainedByScriptDesk"] = true,
                    ["fileStreamIsAsync"] = false
                });

                FileStream? input = null;
                FileStream? output = null;
                try
                {
                    input = new FileStream(inputWrite, FileAccess.Write, 8192, isAsync: false);
                    output = new FileStream(outputRead, FileAccess.Read, 8192, isAsync: false);
                    inputRead.SetHandleAsInvalid();
                    outputWrite.SetHandleAsInvalid();

                    var result = new ConPtyProcess(
                        new ProcessIdentity(process, (int)processInfo.dwProcessId),
                        input,
                        output,
                        inputWrite,
                        outputRead,
                        pseudoConsole);
                    input = null;
                    output = null;
                    CloseHandle(processInfo.hThread);
                    CloseHandle(processInfo.hProcess);
                    DeveloperDiagnostics.LogInfo("Phase2CProof", "ConPTY process and synchronous streams constructed successfully.", new Dictionary<string, object?>
                    {
                        ["conPtyProcessConstructionSucceeded"] = true,
                        ["childProcessId"] = processInfo.dwProcessId,
                        ["inputStreamWrapped"] = true,
                        ["outputStreamWrapped"] = true,
                        ["fileStreamIsAsync"] = false
                    });
                    return result;
                }
                catch
                {
                    try { input?.Dispose(); } catch { }
                    try { output?.Dispose(); } catch { }
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(5000);
                        }
                    }
                    catch { }
                    process.Dispose();
                    inputWrite.Dispose();
                    outputRead.Dispose();
                    CloseHandle(processInfo.hThread);
                    CloseHandle(processInfo.hProcess);
                    DeveloperDiagnostics.LogWarning("Phase2CProof", "ConPTY startup failed after child creation; child and retained handles were cleaned up.", new Dictionary<string, object?>
                    {
                        ["childProcessId"] = processInfo.dwProcessId,
                        ["cleanupAttempted"] = true
                    });
                    throw;
                }
            }
            finally
            {
                DeleteProcThreadAttributeList(startup.AttributeList);
                Marshal.FreeHGlobal(startup.AttributeList);
            }
        }
        catch
        {
            ClosePseudoConsole(pseudoConsole);
            throw;
        }
    }

    public void Resize(int columns, int rows)
    {
        ThrowIfDisposed();
        var hr = ResizePseudoConsole(_pseudoConsole, new COORD((short)Math.Clamp(columns, 1, short.MaxValue), (short)Math.Clamp(rows, 1, short.MaxValue)));
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        DeveloperDiagnostics.LogInfo("Phase2CProof", "CONPTY_DISPOSE_BEGIN.", new Dictionary<string, object?>
        {
            ["ownedProcessId"] = ProcessId,
            ["processHasExitedBeforeStreamClose"] = HasExited
        });

        // The reader uses the synchronous CreatePipe/FileStream transport. Do not
        // await FileStream.DisposeAsync while a ReadAsync is blocked on the same
        // pipe; that ordering can leave shutdown waiting forever. Closing the
        // streams synchronously first releases the reader, after which the owned
        // process can be checked or terminated within the bounded fallback.
        try { Input.Dispose(); } catch { }
        try { Output.Dispose(); } catch { }
        try
        {
            if (!HasExited)
            {
                DeveloperDiagnostics.LogWarning("Phase2CProof", "Owned PSES process required bounded fallback termination during ConPTY disposal.", new Dictionary<string, object?>
                {
                    ["ownedProcessId"] = ProcessId
                });
                Identity.Process.Kill(entireProcessTree: true);
                await Identity.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch { }
        Identity.Process.Dispose();
        _inputWriter?.Dispose();
        _outputReader?.Dispose();
        if (_pseudoConsole != IntPtr.Zero) { ClosePseudoConsole(_pseudoConsole); _pseudoConsole = IntPtr.Zero; }
        if (_threadHandle != IntPtr.Zero) { CloseHandle(_threadHandle); _threadHandle = IntPtr.Zero; }
        if (_processHandle != IntPtr.Zero) { CloseHandle(_processHandle); _processHandle = IntPtr.Zero; }

        DeveloperDiagnostics.LogInfo("Phase2CProof", "CONPTY_DISPOSE_END.", new Dictionary<string, object?>
        {
            ["ownedProcessId"] = ProcessId,
            ["processHasExitedAfterDispose"] = HasExited
        });
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(ConPtyProcess));
    }

    private static SafeFileHandle CreatePipe(bool read, bool inherit, out SafeFileHandle opposite)
    {
        if (!CreatePipe(out var readHandle, out var writeHandle, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var selected = read ? readHandle : writeHandle;
        opposite = read ? writeHandle : readHandle;
        if (!inherit && !SetHandleInformation(selected, HandleFlagInherit, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return selected;
    }

    private static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
        => string.Join(" ", new[] { Quote(executable) }.Concat(arguments.Select(Quote)));

    private static string Quote(string value)
        => value.Length == 0 ? "\"\"" : value.Any(char.IsWhiteSpace) || value.Contains('"') ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : value;

    internal sealed record ProcessIdentity(Process Process, int ProcessId);

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; public COORD(short x, short y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFO { public int cb; public string? lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr attributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int CreatePseudoConsole(COORD size, SafeHandle input, SafeHandle output, uint flags, out IntPtr pseudoConsole);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr pseudoConsole, COORD size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr pseudoConsole);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, int attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string applicationName, string commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
