using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;

namespace PowerShellStudio.TerminalSpike
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private readonly ConPtySession _session = new();
        private bool _terminalReady;
        private bool _sessionStarted;
        private bool _isClosingAfterCleanup;
        private int _lastTerminalCols;
        private int _lastTerminalRows;
        private string _webView2Status = "Not started";
        private string _xtermStatus = "Not started";
        private string _conPtyStatus = "Not started";
        private string _powerShellStatus = "Not started";
        private string _lastError = string.Empty;
        private long _inputByteCount;
        private long _outputByteCount;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            TerminalSpikeLogger.Info("WINDOW", "MainWindow constructed and DataContext assigned.");

            TerminalHostControl.WebView2StatusChanged += status => Dispatcher.Invoke(() => WebView2Status = status);
            TerminalHostControl.XtermStatusChanged += status => Dispatcher.Invoke(() => XtermStatus = status);
            TerminalHostControl.Ready += OnTerminalReady;
            TerminalHostControl.InputReceived += OnTerminalInputReceived;
            TerminalHostControl.ResizeReported += OnTerminalResizeReported;
            TerminalHostControl.ErrorOccurred += OnTerminalErrorOccurred;

            _session.StatusChanged += status => Dispatcher.Invoke(() => ConPtyStatus = status);
            _session.PowerShellStatusChanged += status => Dispatcher.Invoke(() => PowerShellStatus = status);
            _session.OutputReceived += OnSessionOutputReceived;
            _session.InputBytesWritten += bytes => Dispatcher.Invoke(() => InputByteCount += bytes);
            _session.OutputBytesRead += bytes => Dispatcher.Invoke(() => OutputByteCount += bytes);
            _session.ErrorOccurred += OnSessionErrorOccurred;
            _session.Exited += OnSessionExited;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string WebView2Status
        {
            get => _webView2Status;
            private set => SetProperty(ref _webView2Status, value);
        }

        public string XtermStatus
        {
            get => _xtermStatus;
            private set => SetProperty(ref _xtermStatus, value);
        }

        public string ConPtyStatus
        {
            get => _conPtyStatus;
            private set => SetProperty(ref _conPtyStatus, value);
        }

        public string PowerShellStatus
        {
            get => _powerShellStatus;
            private set => SetProperty(ref _powerShellStatus, value);
        }

        public string LastError
        {
            get => _lastError;
            private set => SetProperty(ref _lastError, value);
        }

        public long InputByteCount
        {
            get => _inputByteCount;
            private set => SetProperty(ref _inputByteCount, value);
        }

        public long OutputByteCount
        {
            get => _outputByteCount;
            private set => SetProperty(ref _outputByteCount, value);
        }

        public string LogPath => TerminalSpikeLogger.FilePath;

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            TerminalSpikeLogger.Info("WINDOW", "Terminal spike window loaded.");
            try
            {
                await TerminalHostControl.InitializeAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                SetLastError($"Terminal host initialization failed: {ex.Message}");
                TerminalSpikeLogger.Error("WEBVIEW2", "Terminal host initialization failed.", ex);
            }
        }

        private async void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_isClosingAfterCleanup)
            {
                return;
            }

            e.Cancel = true;
            _isClosingAfterCleanup = true;
            IsEnabled = false;
            TerminalSpikeLogger.Info("WINDOW", "Terminal spike window closing; starting non-blocking cleanup.");

            try
            {
                TerminalHostControl.Dispose();
                var stopTask = _session.StopAsync();
                var completed = await System.Threading.Tasks.Task.WhenAny(stopTask, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
                if (!ReferenceEquals(completed, stopTask))
                {
                    TerminalSpikeLogger.Warning("CONPTY", "ConPTY cleanup did not complete within 5 seconds; closing window anyway.");
                }
            }
            catch (Exception ex)
            {
                TerminalSpikeLogger.Warning("WINDOW", $"Terminal spike cleanup failed: {ex.Message}");
            }

            Close();
        }

        private async void OnTerminalReady(object? sender, TerminalReadyEventArgs e)
        {
            _terminalReady = true;
            _lastTerminalCols = e.Cols;
            _lastTerminalRows = e.Rows;
            XtermStatus = $"Ready ({e.Cols}x{e.Rows})";
            TerminalSpikeLogger.Info("XTERM", $"Terminal reported ready. Source={e.Source}, Cols={e.Cols}, Rows={e.Rows}, Size={e.ClientWidth}x{e.ClientHeight}.");

            if (_sessionStarted)
            {
                return;
            }

            try
            {
                _sessionStarted = true;
                await _session.StartAsync(e.Cols, e.Rows).ConfigureAwait(true);
                ForceConPtyResize("terminal-ready-after-start");
                TerminalHostControl.FocusTerminal();
            }
            catch (Exception ex)
            {
                _sessionStarted = false;
                SetLastError($"ConPTY start failed: {ex.Message}");
                TerminalSpikeLogger.Error("CONPTY", "ConPTY session failed to start.", ex);
            }
        }

        private void OnSessionOutputReceived(string data)
        {
            TerminalSpikeLogger.Debug("OUTPUT", $"ConPTY output event received. Length={data.Length}, Preview='{Preview(data)}'");
            _ = Dispatcher.BeginInvoke(new Action(() => TerminalHostControl.Write(data)));
        }

        private void OnTerminalInputReceived(string data)
        {
            TerminalSpikeLogger.Debug("INPUT", $"xterm input event received. Length={data.Length}, Preview='{Preview(data)}'");
            _ = ForwardInputAsync(data);
        }

        private async System.Threading.Tasks.Task ForwardInputAsync(string data)
        {
            if (!_terminalReady || !_session.IsRunning)
            {
                TerminalSpikeLogger.Warning("INPUT", $"Input ignored. TerminalReady={_terminalReady}, SessionRunning={_session.IsRunning}, Length={data.Length}, Preview='{Preview(data)}'");
                return;
            }

            try
            {
                await _session.WriteInputAsync(data).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _ = Dispatcher.BeginInvoke(new Action(() => SetLastError($"Input forwarding failed: {ex.Message}")));
                TerminalSpikeLogger.Error("INPUT", "Terminal input forwarding failed.", ex);
            }
        }

        private void OnTerminalResizeReported(object? sender, TerminalResizeEventArgs e)
        {
            if (e.Cols <= 0 || e.Rows <= 0)
            {
                return;
            }

            _lastTerminalCols = e.Cols;
            _lastTerminalRows = e.Rows;

            if (_session.IsRunning)
            {
                ForceConPtyResize($"xterm-resize:{e.Source}");
            }

            XtermStatus = $"Ready ({e.Cols}x{e.Rows})";
        }

        private void OnTerminalErrorOccurred(string message)
        {
            _ = Dispatcher.BeginInvoke(new Action(() => SetLastError(message)));
        }

        private void OnSessionErrorOccurred(string message)
        {
            _ = Dispatcher.BeginInvoke(new Action(() => SetLastError(message)));
        }

        private void OnSessionExited(int? processId)
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                ConPtyStatus = "Exited";
                PowerShellStatus = processId.HasValue
                    ? $"Exited (PID {processId.Value})"
                    : "Exited";
            }));
        }

        private void ForceConPtyResize(string reason)
        {
            if (!_session.IsRunning || _lastTerminalCols <= 0 || _lastTerminalRows <= 0)
            {
                TerminalSpikeLogger.Debug("RESIZE", $"Resize skipped. Reason={reason}, SessionRunning={_session.IsRunning}, LastSize={_lastTerminalCols}x{_lastTerminalRows}.");
                return;
            }

            TerminalSpikeLogger.Info("RESIZE", $"Forwarding resize to ConPTY. Reason={reason}, Size={_lastTerminalCols}x{_lastTerminalRows}.");
            _session.Resize(_lastTerminalCols, _lastTerminalRows);
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TerminalSpikeLogger.Info("LOGGER", "Open Log Folder clicked.");
                TerminalSpikeLogger.OpenLogFolder();
            }
            catch (Exception ex)
            {
                SetLastError($"Could not open log folder: {ex.Message}");
                TerminalSpikeLogger.Error("LOGGER", "Could not open log folder.", ex);
            }
        }

        private void OpenLogFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TerminalSpikeLogger.Info("LOGGER", "Open Log File clicked.");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = TerminalSpikeLogger.FilePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                SetLastError($"Could not open log file: {ex.Message}");
                TerminalSpikeLogger.Error("LOGGER", "Could not open log file.", ex);
            }
        }

        private static string Preview(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var normalized = value
                .Replace("\u001b", "<ESC>", StringComparison.Ordinal)
                .Replace("\r", "<CR>", StringComparison.Ordinal)
                .Replace("\n", "<LF>", StringComparison.Ordinal);

            return normalized.Length <= 120
                ? normalized
                : normalized.Substring(0, 120) + "...";
        }

        private void SetLastError(string message)
        {
            LastError = message;
        }

        private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
