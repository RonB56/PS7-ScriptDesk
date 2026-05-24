using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PS7ScriptDesk.TerminalSpike
{
    /// <summary>
    /// Minimal ConPTY session used only by the isolated terminal spike.
    ///
    /// This class intentionally follows the Microsoft ConPTY lifecycle pattern:
    /// create host-side pipes, create the pseudoconsole with the child-side pipe
    /// ends, create the child with STARTUPINFOEX, then service input/output away
    /// from the UI thread.  It is deliberately independent from the main app's
    /// console runner, sentinels, and script-dispatch code.
    /// </summary>
    public sealed class ConPtySession : IAsyncDisposable, IDisposable
    {
        private const int HandleFlagInherit = 0x00000001;
        private const int ProcThreadAttributePseudoConsole = 0x00020016;
        private const uint ExtendedStartupInfoPresent = 0x00080000;

        private readonly object _syncRoot = new();
        private readonly UTF8Encoding _terminalEncoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

        private Process? _process;
        private IntPtr _pseudoConsoleHandle = IntPtr.Zero;
        private SafeFileHandle? _inputWriterHandle;
        private SafeFileHandle? _outputReaderHandle;
        private FileStream? _inputStream;
        private FileStream? _outputStream;
        private CancellationTokenSource? _readerCancellationSource;
        private Task? _readerTask;
        private bool _disposed;

        public bool IsRunning
        {
            get
            {
                lock (_syncRoot)
                {
                    return _process is not null && !_process.HasExited;
                }
            }
        }

        public event Action<string>? OutputReceived;
        public event Action<int>? InputBytesWritten;
        public event Action<int>? OutputBytesRead;
        public event Action<string>? StatusChanged;
        public event Action<string>? PowerShellStatusChanged;
        public event Action<string>? ErrorOccurred;
        public event Action<int?>? Exited;

        public async Task StartAsync(int cols, int rows)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ConPtySession));
            }

            if (IsRunning)
            {
                return;
            }

            StatusChanged?.Invoke("Starting");
            TerminalSpikeLogger.Info("CONPTY", $"StartAsync requested. RequestedSize={cols}x{rows}.");
            var preferredPowerShell = ResolvePowerShellPath();
            if (preferredPowerShell is null)
            {
                throw new InvalidOperationException("PowerShell 7.x was not found. Expected pwsh.exe under Program Files or PATH.");
            }

            var powerShellVersion = ResolvePowerShellVersion(preferredPowerShell);
            PowerShellStatusChanged?.Invoke($"{preferredPowerShell} ({powerShellVersion})");
            TerminalSpikeLogger.Info("CONPTY", $"Resolved PowerShell path: {preferredPowerShell} ({powerShellVersion})");

            await Task.Run(() => StartInternal(preferredPowerShell, powerShellVersion, cols, rows)).ConfigureAwait(false);
        }

        public async Task WriteInputAsync(string data, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(data))
            {
                return;
            }

            FileStream? inputStream;
            lock (_syncRoot)
            {
                inputStream = _inputStream;
            }

            if (inputStream is null)
            {
                TerminalSpikeLogger.Warning("CONPTY", "Input ignored because ConPTY input stream is not available.");
                return;
            }

            var bytes = _terminalEncoding.GetBytes(data);
            TerminalSpikeLogger.Debug("INPUT", $"Writing input to ConPTY. Bytes={bytes.Length}, Preview='{FormatForLog(data)}'");
            await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    inputStream.Write(bytes, 0, bytes.Length);
                    inputStream.Flush();
                },
                cancellationToken).ConfigureAwait(false);
            InputBytesWritten?.Invoke(bytes.Length);
        }

        public void Resize(int cols, int rows)
        {
            IntPtr pseudoConsole;
            lock (_syncRoot)
            {
                pseudoConsole = _pseudoConsoleHandle;
            }

            if (pseudoConsole == IntPtr.Zero)
            {
                return;
            }

            var safeCols = Math.Max(1, cols);
            var safeRows = Math.Max(1, rows);
            var result = ResizePseudoConsole(pseudoConsole, new Coord((short)safeCols, (short)safeRows));
            if (result != 0)
            {
                var message = $"ResizePseudoConsole failed with HRESULT 0x{result:X8}. RequestedSize={safeCols}x{safeRows}.";
                TerminalSpikeLogger.Warning("CONPTY", message);
                ErrorOccurred?.Invoke(message);
            }
            else
            {
                TerminalSpikeLogger.Debug("CONPTY", $"ResizePseudoConsole succeeded. Size={safeCols}x{safeRows}.");
                StatusChanged?.Invoke($"Started ({safeCols}x{safeRows})");
            }
        }

        public async Task StopAsync()
        {
            Process? processToStop;
            CancellationTokenSource? readerCancellationSource;
            Task? readerTask;
            FileStream? inputStream;
            FileStream? outputStream;
            SafeFileHandle? inputWriterHandle;
            SafeFileHandle? outputReaderHandle;
            IntPtr pseudoConsole;

            lock (_syncRoot)
            {
                processToStop = _process;
                readerCancellationSource = _readerCancellationSource;
                readerTask = _readerTask;
                inputStream = _inputStream;
                outputStream = _outputStream;
                inputWriterHandle = _inputWriterHandle;
                outputReaderHandle = _outputReaderHandle;
                pseudoConsole = _pseudoConsoleHandle;

                _process = null;
                _readerCancellationSource = null;
                _readerTask = null;
                _inputStream = null;
                _outputStream = null;
                _inputWriterHandle = null;
                _outputReaderHandle = null;
                _pseudoConsoleHandle = IntPtr.Zero;
            }

            StatusChanged?.Invoke("Stopping");
            readerCancellationSource?.Cancel();

            // Close the pseudoconsole and communication streams before awaiting the
            // reader task.  Waiting first can deadlock because a blocking Read on the
            // output pipe may never observe cancellation until the handle closes.
            if (pseudoConsole != IntPtr.Zero)
            {
                try
                {
                    ClosePseudoConsole(pseudoConsole);
                }
                catch (Exception ex)
                {
                    TerminalSpikeLogger.Warning("CONPTY", $"ClosePseudoConsole failed: {ex.Message}");
                }
            }

            try { inputStream?.Dispose(); } catch { }
            try { outputStream?.Dispose(); } catch { }
            try { inputWriterHandle?.Dispose(); } catch { }
            try { outputReaderHandle?.Dispose(); } catch { }

            if (processToStop is not null)
            {
                try
                {
                    if (!processToStop.HasExited)
                    {
                        processToStop.Kill(entireProcessTree: true);
                    }

                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try
                    {
                        await processToStop.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        TerminalSpikeLogger.Warning("CONPTY", "Timed out waiting for PowerShell process to exit during shutdown.");
                    }
                }
                catch (Exception ex)
                {
                    TerminalSpikeLogger.Warning("CONPTY", $"Process stop failed: {ex.Message}");
                }
                finally
                {
                    processToStop.Dispose();
                }
            }

            if (readerTask is not null)
            {
                var completed = await Task.WhenAny(readerTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                if (!ReferenceEquals(completed, readerTask))
                {
                    TerminalSpikeLogger.Warning("CONPTY", "Output reader did not stop within 2 seconds after handles were closed.");
                }
            }

            readerCancellationSource?.Dispose();
            StatusChanged?.Invoke("Exited");
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                TerminalSpikeLogger.Warning("CONPTY", $"Synchronous dispose failed: {ex.Message}");
            }
        }

        private void StartInternal(string powerShellPath, string powerShellVersion, int cols, int rows)
        {
            SecurityAttributes securityAttributes = new()
            {
                nLength = Marshal.SizeOf<SecurityAttributes>(),
                bInheritHandle = true
            };

            IntPtr inputReadSide = IntPtr.Zero;
            IntPtr inputWriteSide = IntPtr.Zero;
            IntPtr outputReadSide = IntPtr.Zero;
            IntPtr outputWriteSide = IntPtr.Zero;
            IntPtr pseudoConsole = IntPtr.Zero;
            IntPtr attributeListBuffer = IntPtr.Zero;
            IntPtr environmentBlock = IntPtr.Zero;
            ProcessInformation processInformation = default;

            try
            {
                if (!CreatePipe(out inputReadSide, out inputWriteSide, ref securityAttributes, 0))
                {
                    throw new InvalidOperationException($"CreatePipe(input) failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                if (!SetHandleInformation(inputWriteSide, HandleFlagInherit, 0))
                {
                    throw new InvalidOperationException($"SetHandleInformation(input) failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                if (!CreatePipe(out outputReadSide, out outputWriteSide, ref securityAttributes, 0))
                {
                    throw new InvalidOperationException($"CreatePipe(output) failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                if (!SetHandleInformation(outputReadSide, HandleFlagInherit, 0))
                {
                    throw new InvalidOperationException($"SetHandleInformation(output) failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                var size = new Coord((short)Math.Max(80, cols), (short)Math.Max(24, rows));
                var createPseudoConsoleResult = CreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, out pseudoConsole);
                if (createPseudoConsoleResult != 0)
                {
                    throw new InvalidOperationException($"CreatePseudoConsole failed with HRESULT 0x{createPseudoConsoleResult:X8}.");
                }

                TerminalSpikeLogger.Info("CONPTY", "CreatePseudoConsole succeeded.");
                // The child-side pipe handles are now owned by the pseudoconsole.
                CloseHandle(inputReadSide);
                inputReadSide = IntPtr.Zero;
                CloseHandle(outputWriteSide);
                outputWriteSide = IntPtr.Zero;

                IntPtr attributeListSize = IntPtr.Zero;
                _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
                attributeListBuffer = Marshal.AllocHGlobal(attributeListSize);

                if (!InitializeProcThreadAttributeList(attributeListBuffer, 1, 0, ref attributeListSize))
                {
                    throw new InvalidOperationException($"InitializeProcThreadAttributeList failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                if (!UpdateProcThreadAttribute(
                        attributeListBuffer,
                        0,
                        (IntPtr)ProcThreadAttributePseudoConsole,
                        pseudoConsole,
                        (IntPtr)IntPtr.Size,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new InvalidOperationException($"UpdateProcThreadAttribute failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                StartupInfoEx startupInfo = new();
                startupInfo.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
                startupInfo.lpAttributeList = attributeListBuffer;

                var startupCommand = "try { Set-PSReadLineOption -PredictionSource None -ErrorAction SilentlyContinue } catch { }";
                var commandLine = $"\"{powerShellPath}\" -NoLogo -NoExit -STA -Command {QuoteCommandArgument(startupCommand)}";
                environmentBlock = CreateEnvironmentBlock();
                TerminalSpikeLogger.Info("CONPTY", $"Starting pwsh under ConPTY in STA mode. CommandLine={commandLine}");

                TerminalSpikeLogger.Info("CONPTY", "Calling CreateProcessW with EXTENDED_STARTUPINFO_PRESENT.");
                if (!CreateProcessW(
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                        environmentBlock,
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ref startupInfo,
                        out processInformation))
                {
                    throw new InvalidOperationException($"CreateProcessW failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                TerminalSpikeLogger.Info("CONPTY", $"CreateProcessW succeeded. PID={processInformation.dwProcessId}.");
                var process = Process.GetProcessById((int)processInformation.dwProcessId);
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    TerminalSpikeLogger.Info("CONPTY", $"PowerShell process exited. Pid={process.Id}");
                    Exited?.Invoke(process.Id);
                };

                var safeInputWriterHandle = new SafeFileHandle(inputWriteSide, ownsHandle: true);
                inputWriteSide = IntPtr.Zero;
                var safeOutputReaderHandle = new SafeFileHandle(outputReadSide, ownsHandle: true);
                outputReadSide = IntPtr.Zero;

                // Anonymous pipe handles created for ConPTY are synchronous handles.
                // Do not wrap them with isAsync:true unless the native handles were
                // explicitly opened for overlapped I/O, otherwise FileStream throws
                // "Handle does not support asynchronous operations" before pwsh starts.
                var inputStream = new FileStream(safeInputWriterHandle, FileAccess.Write, bufferSize: 4096, isAsync: false);
                var outputStream = new FileStream(safeOutputReaderHandle, FileAccess.Read, bufferSize: 4096, isAsync: false);
                var readerCancellationSource = new CancellationTokenSource();
                var readerTask = Task.Run(() => ReadOutputLoop(outputStream, readerCancellationSource.Token));

                lock (_syncRoot)
                {
                    _process = process;
                    _pseudoConsoleHandle = pseudoConsole;
                    _inputWriterHandle = safeInputWriterHandle;
                    _outputReaderHandle = safeOutputReaderHandle;
                    _inputStream = inputStream;
                    _outputStream = outputStream;
                    _readerCancellationSource = readerCancellationSource;
                    _readerTask = readerTask;
                }

                pseudoConsole = IntPtr.Zero;
                StatusChanged?.Invoke($"Started ({size.X}x{size.Y})");
                PowerShellStatusChanged?.Invoke($"{powerShellPath} ({powerShellVersion}) PID {process.Id}");
                TerminalSpikeLogger.Info("CONPTY", $"ConPTY started successfully. Pid={process.Id}, Size={size.X}x{size.Y}");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("Failed");
                ErrorOccurred?.Invoke(ex.Message);
                TerminalSpikeLogger.Error("CONPTY", "ConPTY startup failed.", ex);
                throw;
            }
            finally
            {
                if (processInformation.hThread != IntPtr.Zero)
                {
                    CloseHandle(processInformation.hThread);
                }

                if (processInformation.hProcess != IntPtr.Zero)
                {
                    CloseHandle(processInformation.hProcess);
                }

                if (attributeListBuffer != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeListBuffer);
                    Marshal.FreeHGlobal(attributeListBuffer);
                }

                if (pseudoConsole != IntPtr.Zero)
                {
                    ClosePseudoConsole(pseudoConsole);
                }

                if (environmentBlock != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(environmentBlock);
                }

                if (inputReadSide != IntPtr.Zero)
                {
                    CloseHandle(inputReadSide);
                }

                if (inputWriteSide != IntPtr.Zero)
                {
                    CloseHandle(inputWriteSide);
                }

                if (outputReadSide != IntPtr.Zero)
                {
                    CloseHandle(outputReadSide);
                }

                if (outputWriteSide != IntPtr.Zero)
                {
                    CloseHandle(outputWriteSide);
                }
            }
        }

        private void ReadOutputLoop(FileStream outputStream, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            var decoder = _terminalEncoding.GetDecoder();
            var charBuffer = new char[_terminalEncoding.GetMaxCharCount(buffer.Length)];
            var firstChunkLogged = false;

            try
            {
                TerminalSpikeLogger.Info("OUTPUT", "ConPTY output read loop started.");
                while (!cancellationToken.IsCancellationRequested)
                {
                    var bytesRead = outputStream.Read(buffer, 0, buffer.Length);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    var charsRead = decoder.GetChars(buffer, 0, bytesRead, charBuffer, 0, flush: false);
                    if (charsRead <= 0)
                    {
                        continue;
                    }

                    var text = new string(charBuffer, 0, charsRead);
                    if (!firstChunkLogged)
                    {
                        firstChunkLogged = true;
                        TerminalSpikeLogger.Info("CONPTY", $"First output chunk received. Chars={charsRead}, Bytes={bytesRead}, Preview='{FormatForLog(text)}'");
                    }

                    TerminalSpikeLogger.Debug("OUTPUT", $"ConPTY output chunk. Bytes={bytesRead}, Chars={charsRead}, Preview='{FormatForLog(text)}'");
                    OutputBytesRead?.Invoke(bytesRead);
                    OutputReceived?.Invoke(text);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (ObjectDisposedException)
            {
                // Expected during shutdown.
            }
            catch (IOException ex) when (cancellationToken.IsCancellationRequested)
            {
                TerminalSpikeLogger.Debug("ConPty", $"Output reader stopped during cancellation: {ex.Message}");
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(ex.Message);
                TerminalSpikeLogger.Error("CONPTY", "ConPTY output reader failed.", ex);
            }
        }

        private static string? ResolvePowerShellPath()
        {
            var candidates = new List<string>();
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                candidates.Add(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
            }

            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                candidates.Add(Path.Combine(programFilesX86, "PowerShell", "7", "pwsh.exe"));
            }

            var pathEnvironment = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathEnvironment))
            {
                foreach (var path in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        candidates.Add(Path.Combine(path.Trim(), "pwsh.exe"));
                    }
                    catch
                    {
                        // Ignore malformed PATH entries.
                    }
                }
            }

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string ResolvePowerShellVersion(string powerShellPath)
        {
            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(powerShellPath);
                return string.IsNullOrWhiteSpace(versionInfo.ProductVersion)
                    ? "unknown"
                    : versionInfo.ProductVersion;
            }
            catch
            {
                return "unknown";
            }
        }

        private static IntPtr CreateEnvironmentBlock()
        {
            var environment = Environment.GetEnvironmentVariables();
            environment["TERM"] = "xterm-256color";
            environment["COLORTERM"] = "truecolor";

            var builder = new StringBuilder();
            foreach (System.Collections.DictionaryEntry entry in environment)
            {
                if (entry.Key is string key && entry.Value is string value)
                {
                    builder.Append(key).Append('=').Append(value).Append('\0');
                }
            }

            builder.Append('\0');
            return Marshal.StringToHGlobalUni(builder.ToString());
        }

        private static string QuoteCommandArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        private static string FormatForLog(string text, int maxLength = 160)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(Math.Min(text.Length * 2, maxLength + 8));
            foreach (var ch in text)
            {
                _ = ch switch
                {
                    '\r' => builder.Append("\\r"),
                    '\n' => builder.Append("\\n"),
                    '\t' => builder.Append("\\t"),
                    '\x1b' => builder.Append("\\x1b"),
                    _ when char.IsControl(ch) => builder.Append($"\\u{(int)ch:x4}"),
                    _ => builder.Append(ch)
                };

                if (builder.Length >= maxLength)
                {
                    builder.Append("...");
                    break;
                }
            }

            return builder.ToString();
        }

        private const uint CreateUnicodeEnvironment = 0x00000400;

        [StructLayout(LayoutKind.Sequential)]
        private struct Coord
        {
            public Coord(short x, short y)
            {
                X = x;
                Y = y;
            }

            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(
            out IntPtr hReadPipe,
            out IntPtr hWritePipe,
            ref SecurityAttributes lpPipeAttributes,
            int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, int dwMask, int dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern int CreatePseudoConsole(Coord size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = false)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            [In] ref StartupInfoEx lpStartupInfo,
            out ProcessInformation lpProcessInformation);
    }
}
