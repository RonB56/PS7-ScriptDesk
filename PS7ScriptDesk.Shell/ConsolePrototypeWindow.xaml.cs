using System;
using System.Threading.Tasks;
using System.Windows;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Shell
{
    public partial class ConsolePrototypeWindow : Window
    {
        private readonly RuntimeService _runtimeService = new();
        private readonly LiveConsoleService _liveConsoleService = new();
        private PowerShellRuntimeInfo? _runtime;
        private bool _terminalReady;
        private bool _sessionStarting;
        private bool _isClosing;
        private int _inputInfoLogCount;
        private int _outputInfoLogCount;

        public ConsolePrototypeWindow()
        {
            InitializeComponent();

            PrototypeTerminal.TerminalReady += OnTerminalReady;
            PrototypeTerminal.UserInput += OnTerminalUserInput;
            PrototypeTerminal.TerminalResized += OnTerminalResized;
            _liveConsoleService.RawOutputReceived += OnRawOutputReceived;
            _liveConsoleService.SessionTerminated += OnSessionTerminated;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("ConsolePrototype", "Prototype window loaded.");
            _liveConsoleService.AttachHost(IntPtr.Zero, 120, 30);
            UpdateStatus("Discovering PowerShell runtime...");

            try
            {
                var discoveryResult = await Task.Run(() => _runtimeService.DiscoverRuntimes()).ConfigureAwait(true);
                _runtime = discoveryResult.PreferredRuntime;

                if (_runtime is null)
                {
                    AppLogger.Warning("ConsolePrototype", "No PowerShell runtime was found for the prototype terminal.");
                    UpdateStatus("No PowerShell runtime was detected.");
                    return;
                }

                AppLogger.Info("ConsolePrototype", $"Using runtime '{_runtime.DisplayName}' from '{_runtime.ExecutablePath}'.");
                UpdateStatus($"Runtime ready: {_runtime.DisplayName}");
                await EnsureSessionStartedAsync(forceRestart: false).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLogger.Error("ConsolePrototype", "Prototype initialization failed.", ex);
                UpdateStatus($"Prototype initialization failed: {ex.Message}");
            }
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            _isClosing = true;
            AppLogger.Info("ConsolePrototype", "Prototype window closed.");
            PrototypeTerminal.TerminalReady -= OnTerminalReady;
            PrototypeTerminal.UserInput -= OnTerminalUserInput;
            PrototypeTerminal.TerminalResized -= OnTerminalResized;
            _liveConsoleService.SessionTerminated -= OnSessionTerminated;
            _liveConsoleService.RawOutputReceived -= OnRawOutputReceived;
            _liveConsoleService.Dispose();
        }

        private async void RestartSession_Click(object sender, RoutedEventArgs e)
        {
            await EnsureSessionStartedAsync(forceRestart: true).ConfigureAwait(true);
        }

        private async void OnTerminalReady()
        {
            _terminalReady = true;
            AppLogger.Info("ConsolePrototype", "Terminal control reported ready.");
            UpdateStatus(_runtime is null ? "Terminal ready. Waiting for runtime..." : $"Terminal ready. Starting {_runtime.DisplayName}...");
            await EnsureSessionStartedAsync(forceRestart: false).ConfigureAwait(true);
        }

        private async void OnTerminalUserInput(string data)
        {
            if (_inputInfoLogCount < 5)
            {
                _inputInfoLogCount++;
                AppLogger.Info("ConsolePrototype", $"Prototype forwarding terminal input #{_inputInfoLogCount}. Length={data.Length}.");
            }

            AppLogger.Debug("ConsolePrototype", $"Prototype received terminal input. Length={data.Length}.");
            await _liveConsoleService.WriteRawInputAsync(data).ConfigureAwait(false);
        }

        private void OnTerminalResized(int cols, int rows)
        {
            AppLogger.Debug("ConsolePrototype", $"Prototype terminal resized. Cols={cols}, Rows={rows}.");
            _liveConsoleService.ResizeConsole(cols, rows);
        }

        private void OnRawOutputReceived(string raw)
        {
            if (_outputInfoLogCount < 5)
            {
                _outputInfoLogCount++;
                AppLogger.Info("ConsolePrototype", $"Prototype received raw terminal output #{_outputInfoLogCount}. Length={raw.Length}.");
            }

            Dispatcher.BeginInvoke(new Action(() => PrototypeTerminal.WriteRaw(raw)));
        }

        private void OnSessionTerminated()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isClosing)
                {
                    return;
                }

                AppLogger.Info("ConsolePrototype", "Prototype PowerShell session terminated.");
                UpdateStatus("PowerShell session exited.");
            }));
        }

        private async Task EnsureSessionStartedAsync(bool forceRestart)
        {
            if (_isClosing || !_terminalReady || _runtime is null || _sessionStarting)
            {
                AppLogger.Debug(
                    "ConsolePrototype",
                    $"Skipping session start. Closing={_isClosing}, TerminalReady={_terminalReady}, RuntimeReady={_runtime is not null}, SessionStarting={_sessionStarting}, ForceRestart={forceRestart}.");
                return;
            }

            _sessionStarting = true;
            try
            {
                if (forceRestart && _liveConsoleService.IsSessionRunning)
                {
                    UpdateStatus("Stopping existing PowerShell session...");
                    await _liveConsoleService.StopConsoleAsync(HandleLifecycleOutput).ConfigureAwait(true);
                }

                UpdateStatus($"Starting {_runtime.DisplayName}...");
                AppLogger.Info("ConsolePrototype", $"Starting isolated terminal session with '{_runtime.DisplayName}'.");
                await _liveConsoleService.StartSessionAsync(
                    _runtime,
                    HandleLifecycleOutput,
                    startupWorkingDirectory: Environment.CurrentDirectory).ConfigureAwait(true);

                UpdateStatus($"Interactive terminal ready: {_runtime.DisplayName}");
                PrototypeTerminal.FocusTerminal();
            }
            catch (Exception ex)
            {
                AppLogger.Error("ConsolePrototype", "Prototype PowerShell session failed to start.", ex);
                UpdateStatus($"Session start failed: {ex.Message}");
            }
            finally
            {
                _sessionStarting = false;
            }
        }

        private void HandleLifecycleOutput(ExecutionOutputRecord record)
        {
            AppLogger.Info("ConsolePrototype", $"Lifecycle event: Kind={record.StreamKind}, Text={record.Text}");

            if (string.IsNullOrWhiteSpace(record.Text))
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isClosing)
                {
                    return;
                }

                if (record.Text.Contains("fallback", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateStatus("ConPTY unavailable. Prototype is running in limited fallback mode.");
                }
                else if (record.Text.Contains("failed", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateStatus(record.Text);
                }
                else if (record.Text.Contains("exited", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateStatus(record.Text);
                }
            }));
        }

        private void UpdateStatus(string text)
        {
            StatusTextBlock.Text = text;
        }
    }
}
