using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PowerShellStudio.TerminalSpike
{
    public sealed class TerminalHost : UserControl, IDisposable
    {
        private const string TerminalHtml = """
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="UTF-8">
            <meta http-equiv="Content-Security-Policy"
                  content="default-src 'none'; script-src 'unsafe-inline' https://terminal.local; style-src 'unsafe-inline' https://terminal.local;">
            <link rel="stylesheet" href="https://terminal.local/xterm.css">
            <style>
            * { box-sizing: border-box; margin: 0; padding: 0; }
            html, body {
              position: fixed;
              inset: 0;
              width: 100vw;
              height: 100vh;
              overflow: hidden !important;
              background: #000000;
            }
            body { display: block; }
            body::-webkit-scrollbar { width: 0 !important; height: 0 !important; display: none !important; }
            #terminal {
              position: fixed;
              inset: 0;
              width: 100vw;
              height: 100vh;
              min-width: 0;
              min-height: 0;
              overflow: hidden !important;
              background: #000000;
              outline: none;
            }
            .xterm { width: 100% !important; height: 100% !important; }
            .xterm-viewport { background: #000000 !important; }
            .xterm-screen { background: #000000 !important; }
            </style>
            </head>
            <body tabindex="0">
            <div id="terminal" tabindex="0"></div>
            <script src="https://terminal.local/xterm.min.js"></script>
            <script src="https://terminal.local/xterm-addon-fit.min.js"></script>
            <script>
            (function () {
              'use strict';

              function post(message) {
                try {
                  window.chrome.webview.postMessage(JSON.stringify(message));
                } catch (error) {
                }
              }

              function preview(value) {
                if (typeof value !== 'string') {
                  return '';
                }

                var text = value.replace(/\x1b/g, '<ESC>').replace(/\r/g, '<CR>').replace(/\n/g, '<LF>');
                return text.length <= 120 ? text : text.substring(0, 120) + '...';
              }

              post({ type: 'lifecycle', tag: 'XTERM', message: 'Inline terminal script loaded.' });

              var term = null;
              var fitAddon = null;
              var terminalElement = null;
              var readySent = false;

              window.PowerShellStudioTerminalSpike = {
                write: function (data) {
                  if (!term || typeof data !== 'string') {
                    return false;
                  }

                  try {
                    post({ type: 'write_probe', length: data.length, preview: preview(data) });
                    term.write(data, function () {
                      post({ type: 'output_ack', length: data.length });
                    });
                    return true;
                  } catch (error) {
                    post({ type: 'error', source: 'terminal.write', message: String(error) });
                    return false;
                  }
                },
                clear: function () {
                  if (!term) {
                    return false;
                  }

                  term.clear();
                  return true;
                },
                focus: function () {
                  focusTerminal('host.api.focus');
                  return true;
                },
                paste: function (data) {
                  if (typeof data === 'string' && data.length > 0) {
                    post({ type: 'input', data: data });
                  }

                  return true;
                }
              };

              function reportResize(source) {
                if (!term) {
                  return;
                }

                post({
                  type: 'resize',
                  source: source,
                  cols: term.cols,
                  rows: term.rows,
                  clientWidth: terminalElement ? terminalElement.clientWidth : 0,
                  clientHeight: terminalElement ? terminalElement.clientHeight : 0
                });
              }

              function focusTerminal(source) {
                if (!term) {
                  return;
                }

                try { window.focus(); } catch (ignore) { }
                try { document.body.focus(); } catch (ignore) { }
                try { terminalElement.focus(); } catch (ignore) { }
                term.focus();
                post({ type: 'focus', source: source, activeElement: document.activeElement ? document.activeElement.id || document.activeElement.tagName : '' });
              }

              function fitTerminal(source) {
                if (!fitAddon) {
                  return;
                }

                try {
                  fitAddon.fit();
                  reportResize(source);
                } catch (error) {
                  post({ type: 'error', source: source, message: String(error) });
                }
              }

              function signalReady(source) {
                if (readySent || !term) {
                  return;
                }

                readySent = true;
                post({
                  type: 'ready',
                  source: source,
                  cols: term.cols,
                  rows: term.rows,
                  clientWidth: terminalElement ? terminalElement.clientWidth : 0,
                  clientHeight: terminalElement ? terminalElement.clientHeight : 0
                });
              }

              window.addEventListener('error', function (event) {
                post({ type: 'error', source: 'window.onerror', message: String(event.message || event.error || 'unknown error') });
              });

              window.addEventListener('DOMContentLoaded', function () {
                try {
                  post({ type: 'lifecycle', tag: 'XTERM', message: 'DOMContentLoaded received.' });
                  terminalElement = document.getElementById('terminal');
                  if (!terminalElement) {
                    throw new Error('Missing terminal container element.');
                  }

                  terminalElement.tabIndex = 0;
                  post({ type: 'lifecycle', tag: 'XTERM', message: 'Creating xterm Terminal instance.' });
                  term = new Terminal({
                    fontFamily: "'Cascadia Mono','Cascadia Code',Consolas,'Courier New',monospace",
                    fontSize: 14,
                    lineHeight: 1.2,
                    scrollback: 10000,
                    cursorBlink: true,
                    cursorStyle: 'block',
                    cursorInactiveStyle: 'outline',
                    convertEol: false,
                    allowTransparency: false,
                    theme: {
                      background: '#000000',
                      foreground: '#f2f2f2',
                      cursor: '#00ff00',
                      cursorAccent: '#000000',
                      selectionBackground: 'rgba(88, 166, 255, 0.35)',
                      black: '#010409',
                      red: '#ff7b72',
                      green: '#3fb950',
                      yellow: '#d29922',
                      blue: '#79c0ff',
                      magenta: '#bc8cff',
                      cyan: '#39c5cf',
                      white: '#b1bac4',
                      brightBlack: '#6e7681',
                      brightRed: '#ffa198',
                      brightGreen: '#56d364',
                      brightYellow: '#e3b341',
                      brightBlue: '#a5d6ff',
                      brightMagenta: '#d2a8ff',
                      brightCyan: '#56d4dd',
                      brightWhite: '#f0f6fc'
                    }
                  });

                  fitAddon = new FitAddon.FitAddon();
                  post({ type: 'lifecycle', tag: 'XTERM', message: 'FitAddon created.' });
                  term.loadAddon(fitAddon);
                  post({ type: 'lifecycle', tag: 'XTERM', message: 'FitAddon loaded.' });
                  term.open(terminalElement);
                  post({ type: 'lifecycle', tag: 'XTERM', message: 'Terminal.open completed.' });
                  var computed = window.getComputedStyle(terminalElement);
                  post({
                    type: 'style_probe',
                    width: terminalElement.clientWidth,
                    height: terminalElement.clientHeight,
                    bg: computed.backgroundColor,
                    overflow: computed.overflow
                  });
                  term.write('\x1b[32mxterm render path ready\x1b[0m\r\n');
                  post({ type: 'render_self_test' });
                  term.onData(function (data) {
                    post({ type: 'input_probe', length: data.length, preview: preview(data) });
                    post({ type: 'input', data: data });
                  });
                  term.onResize(function (event) {
                    post({ type: 'resize', source: 'term.onResize', cols: event.cols, rows: event.rows });
                  });

                  term.attachCustomKeyEventHandler(function (event) {
                    if (event.type !== 'keydown') {
                      return true;
                    }

                    if (event.ctrlKey && event.key === 'v') {
                      post({ type: 'paste_request' });
                      return false;
                    }

                    if (event.ctrlKey && event.key === 'a') {
                      term.selectAll();
                      return false;
                    }

                    if (event.ctrlKey && event.key === 'c' && term.hasSelection()) {
                      post({ type: 'copy', text: term.getSelection() });
                      term.clearSelection();
                      return false;
                    }

                    return true;
                  });

                  document.body.addEventListener('mousedown', function () {
                    window.setTimeout(function () {
                      focusTerminal('body.mousedown');
                    }, 0);
                  });

                  terminalElement.addEventListener('mousedown', function () {
                    window.setTimeout(function () {
                      focusTerminal('terminal.mousedown');
                    }, 0);
                  });

                  terminalElement.addEventListener('click', function () {
                    window.setTimeout(function () {
                      focusTerminal('terminal.click');
                    }, 0);
                  });

                  document.addEventListener('contextmenu', function (event) {
                    event.preventDefault();
                    post({ type: 'paste_request' });
                  });

                  var resizeObserver = new ResizeObserver(function () {
                    window.requestAnimationFrame(function () {
                      fitTerminal('ResizeObserver');
                    });
                  });
                  resizeObserver.observe(terminalElement);

                  window.requestAnimationFrame(function () {
                    fitTerminal('startup.raf1');
                    window.requestAnimationFrame(function () {
                      fitTerminal('startup.raf2');
                      focusTerminal('startup.raf2');
                      signalReady('startup.raf2');
                    });
                  });
                } catch (error) {
                  post({ type: 'error', source: 'init', message: String(error) });
                }
              });

              function decodeBase64Utf8(value) {
                var binary = atob(value);
                var bytes = new Uint8Array(binary.length);
                for (var i = 0; i < binary.length; i++) {
                  bytes[i] = binary.charCodeAt(i);
                }
                return new TextDecoder('utf-8').decode(bytes);
              }

              window.chrome.webview.addEventListener('message', function (event) {
                try {
                  var message = (typeof event.data === 'string') ? JSON.parse(event.data) : event.data;
                  if (!term || !message || !message.type) {
                    return;
                  }

                  if (message.type === 'output_b64' && typeof message.data === 'string') {
                    window.PowerShellStudioTerminalSpike.write(decodeBase64Utf8(message.data));
                  } else if (message.type === 'output' && typeof message.data === 'string') {
                    window.PowerShellStudioTerminalSpike.write(message.data);
                  } else if (message.type === 'clear') {
                    window.PowerShellStudioTerminalSpike.clear();
                  } else if (message.type === 'focus') {
                    focusTerminal('host.focus');
                  } else if (message.type === 'paste' && typeof message.data === 'string' && message.data.length > 0) {
                    window.PowerShellStudioTerminalSpike.paste(message.data);
                  }
                } catch (error) {
                  post({ type: 'error', source: 'host.message', message: String(error) });
                }
              });
            })();
            </script>
            </body>
            </html>
            """;

        private readonly Grid _rootGrid;
        private readonly WebView2 _webView;
        private readonly Border _errorOverlay;
        private readonly TextBlock _errorTextBlock;
        private readonly List<string> _pendingOutput = new();
        private bool _isReady;
        private bool _isDisposed;

        public TerminalHost()
        {
            _webView = new WebView2
            {
                Focusable = true
            };

            _errorTextBlock = new TextBlock
            {
                Foreground = System.Windows.Media.Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Text = "WebView2 not initialized."
            };

            _errorOverlay = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32)),
                Padding = new Thickness(16),
                Child = _errorTextBlock,
                Visibility = Visibility.Collapsed
            };

            _rootGrid = new Grid();
            _rootGrid.Children.Add(_webView);
            _rootGrid.Children.Add(_errorOverlay);
            Content = _rootGrid;
        }

        public event EventHandler<TerminalReadyEventArgs>? Ready;

        public event EventHandler<TerminalResizeEventArgs>? ResizeReported;

        public event Action<string>? InputReceived;

        public event Action<string>? WebView2StatusChanged;

        public event Action<string>? XtermStatusChanged;

        public event Action<string>? ErrorOccurred;

        public async Task InitializeAsync()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(TerminalHost));
            }

            SetWebView2Status("Initializing");
            try
            {
                await _webView.EnsureCoreWebView2Async().ConfigureAwait(true);
                if (_webView.CoreWebView2 is null)
                {
                    throw new InvalidOperationException("CoreWebView2 was not created.");
                }

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

                var assemblyDirectory = Path.GetDirectoryName(typeof(TerminalHost).Assembly.Location)
                    ?? throw new InvalidOperationException("Could not resolve the spike assembly directory.");
                var terminalDirectory = Path.Combine(assemblyDirectory, "terminal");
                if (!Directory.Exists(terminalDirectory))
                {
                    throw new DirectoryNotFoundException($"Terminal asset directory was not found: {terminalDirectory}");
                }

                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "terminal.local",
                    terminalDirectory,
                    CoreWebView2HostResourceAccessKind.Allow);

                TerminalSpikeLogger.Info("WEBVIEW2", $"WebView2 initialized. Terminal assets={terminalDirectory}, xterm.js={File.Exists(Path.Combine(terminalDirectory, "xterm.min.js"))}, xterm.css={File.Exists(Path.Combine(terminalDirectory, "xterm.css"))}, fitAddon={File.Exists(Path.Combine(terminalDirectory, "xterm-addon-fit.min.js"))}");
                _webView.CoreWebView2.NavigateToString(TerminalHtml);
            }
            catch (Exception ex)
            {
                ShowError($"WebView2 initialization failed: {ex.Message}");
                SetWebView2Status("Failed");
                SetXtermStatus("Failed");
                TerminalSpikeLogger.Error("WEBVIEW2", "WebView2 initialization failed.", ex);
                ErrorOccurred?.Invoke($"WebView2 initialization failed: {ex.Message}");
                throw;
            }
        }

        public void Write(string data)
        {
            if (_isDisposed || string.IsNullOrEmpty(data))
            {
                return;
            }

            if (!_isReady)
            {
                _pendingOutput.Add(data);
                return;
            }

            ExecuteTerminalScript("write", data);
        }

        public void FocusTerminal()
        {
            if (_isDisposed)
            {
                return;
            }

            _webView.Focus();
            ExecuteTerminalScript("focus");
        }

        public void Clear()
        {
            if (_isDisposed)
            {
                return;
            }

            ExecuteTerminalScript("clear");
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            if (_webView.CoreWebView2 is not null)
            {
                _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            }

            _webView.Dispose();
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                SetWebView2Status("Ready");
                TerminalSpikeLogger.Info("WEBVIEW2", "WebView2 navigation completed successfully.");
            }
            else
            {
                var message = $"WebView2 navigation failed: {e.WebErrorStatus}";
                SetWebView2Status("Failed");
                SetXtermStatus("Failed");
                ShowError(message);
                TerminalSpikeLogger.Error("WEBVIEW2", message);
                ErrorOccurred?.Invoke(message);
            }
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var typeProperty)
                    ? typeProperty.GetString()
                    : null;

                switch (type)
                {
                    case "ready":
                        {
                            _isReady = true;
                            var args = CreateReadyEventArgs(root);
                            SetXtermStatus($"Ready ({args.Cols}x{args.Rows})");
                            TerminalSpikeLogger.Info("XTERM", $"xterm ready. Source={args.Source}, Cols={args.Cols}, Rows={args.Rows}, Size={args.ClientWidth}x{args.ClientHeight}");
                            FlushPendingOutput();
                            Ready?.Invoke(this, args);
                        }
                        break;

                    case "input":
                        if (root.TryGetProperty("data", out var dataProperty))
                        {
                            var data = dataProperty.GetString();
                            if (!string.IsNullOrEmpty(data))
                            {
                                InputReceived?.Invoke(data);
                            }
                        }
                        break;

                    case "resize":
                        {
                            var args = CreateResizeEventArgs(root);
                            ResizeReported?.Invoke(this, args);
                            if (args.Cols <= 0 || args.Rows <= 0)
                            {
                                TerminalSpikeLogger.Warning("RESIZE", $"Suspicious xterm resize reported. Source={args.Source}, Cols={args.Cols}, Rows={args.Rows}, Size={args.ClientWidth}x{args.ClientHeight}");
                            }
                            else
                            {
                                TerminalSpikeLogger.Info("RESIZE", $"xterm resize reported. Source={args.Source}, Cols={args.Cols}, Rows={args.Rows}, Size={args.ClientWidth}x{args.ClientHeight}");
                            }
                        }
                        break;

                    case "output_ack":
                        TerminalSpikeLogger.Debug("HOST_TO_XTERM", $"xterm output acknowledged. Length={GetInt(root, "length")}");
                        break;

                    case "lifecycle":
                        TerminalSpikeLogger.Info(GetString(root, "tag") ?? "XTERM", GetString(root, "message") ?? "Lifecycle message without text.");
                        break;

                    case "input_probe":
                        TerminalSpikeLogger.Debug("XTERM_TO_HOST", $"xterm onData fired. Length={GetInt(root, "length")}, Preview='{GetString(root, "preview")}'");
                        break;

                    case "write_probe":
                        TerminalSpikeLogger.Debug("HOST_TO_XTERM", $"xterm write invoked. Length={GetInt(root, "length")}, Preview='{GetString(root, "preview")}'");
                        break;

                    case "render_self_test":
                        TerminalSpikeLogger.Info("TerminalHost", "xterm render self-test was written.");
                        break;

                    case "style_probe":
                        TerminalSpikeLogger.Info("TerminalHost", $"xterm style probe. Size={GetInt(root, "width")}x{GetInt(root, "height")}, Background={GetString(root, "bg")}, Overflow={GetString(root, "overflow")}");
                        break;

                    case "focus":
                        TerminalSpikeLogger.Info("FOCUS", $"xterm focus reported. Source={GetString(root, "source")}");
                        break;

                    case "copy":
                        {
                            var text = GetString(root, "text");
                            if (!string.IsNullOrEmpty(text))
                            {
                                Clipboard.SetText(text);
                            }
                        }
                        break;

                    case "paste_request":
                        {
                            string pasteText = string.Empty;
                            if (Clipboard.ContainsText())
                            {
                                pasteText = Clipboard.GetText();
                            }

                            if (!string.IsNullOrEmpty(pasteText))
                            {
                                ExecuteTerminalScript("paste", pasteText);
                            }
                        }
                        break;

                    case "error":
                        {
                            var source = GetString(root, "source") ?? "unknown";
                            var message = GetString(root, "message") ?? "Unknown xterm error.";
                            var fullMessage = $"xterm error from {source}: {message}";
                            SetXtermStatus("Failed");
                            ShowError(fullMessage);
                            TerminalSpikeLogger.Error("XTERM_ERROR", fullMessage);
                            ErrorOccurred?.Invoke(fullMessage);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                var message = $"WebView2 message parsing failed: {ex.Message}";
                ShowError(message);
                SetXtermStatus("Failed");
                TerminalSpikeLogger.Error("XTERM_TO_HOST", message, ex);
                ErrorOccurred?.Invoke(message);
            }
        }

        private void FlushPendingOutput()
        {
            if (_pendingOutput.Count == 0)
            {
                return;
            }

            foreach (var pendingChunk in _pendingOutput)
            {
                PostMessage(new { type = "output", data = pendingChunk });
            }

            _pendingOutput.Clear();
        }

        private void ExecuteTerminalScript(string action, string? data = null)
        {
            if (_isDisposed || _webView.CoreWebView2 is null)
            {
                return;
            }

            object? payload = action switch
            {
                "write" => new
                {
                    type = "output_b64",
                    data = Convert.ToBase64String(Encoding.UTF8.GetBytes(data ?? string.Empty))
                },
                "paste" => new { type = "paste", data = data ?? string.Empty },
                "clear" => new { type = "clear" },
                "focus" => new { type = "focus" },
                _ => null
            };

            if (payload is not null)
            {
                PostMessage(payload);
            }
        }

        private void PostMessage(object payload)
        {
            if (_isDisposed || _webView.CoreWebView2 is null)
            {
                return;
            }

            try
            {
                var json = JsonSerializer.Serialize(payload);
                _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                TerminalSpikeLogger.Warning("HOST_TO_XTERM", $"PostWebMessageAsString failed: {ex.Message}");
            }
        }

        private void ShowError(string message)
        {
            _errorTextBlock.Text = message;
            _errorOverlay.Visibility = Visibility.Visible;
        }

        private void SetWebView2Status(string status)
        {
            WebView2StatusChanged?.Invoke(status);
        }

        private void SetXtermStatus(string status)
        {
            XtermStatusChanged?.Invoke(status);
        }

        private static int GetInt(JsonElement root, string propertyName)
        {
            return root.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
                ? value
                : 0;
        }

        private static string? GetString(JsonElement root, string propertyName)
        {
            return root.TryGetProperty(propertyName, out var property)
                ? property.GetString()
                : null;
        }

        private static TerminalReadyEventArgs CreateReadyEventArgs(JsonElement root)
        {
            return new TerminalReadyEventArgs(
                GetString(root, "source") ?? "unknown",
                root.TryGetProperty("cols", out var colsProperty) ? colsProperty.GetInt32() : 0,
                root.TryGetProperty("rows", out var rowsProperty) ? rowsProperty.GetInt32() : 0,
                root.TryGetProperty("clientWidth", out var widthProperty) ? widthProperty.GetInt32() : 0,
                root.TryGetProperty("clientHeight", out var heightProperty) ? heightProperty.GetInt32() : 0);
        }

        private static TerminalResizeEventArgs CreateResizeEventArgs(JsonElement root)
        {
            return new TerminalResizeEventArgs(
                GetString(root, "source") ?? "unknown",
                root.TryGetProperty("cols", out var colsProperty) ? colsProperty.GetInt32() : 0,
                root.TryGetProperty("rows", out var rowsProperty) ? rowsProperty.GetInt32() : 0,
                root.TryGetProperty("clientWidth", out var widthProperty) ? widthProperty.GetInt32() : 0,
                root.TryGetProperty("clientHeight", out var heightProperty) ? heightProperty.GetInt32() : 0);
        }
    }

    public sealed class TerminalReadyEventArgs : EventArgs
    {
        public TerminalReadyEventArgs(string source, int cols, int rows, int clientWidth, int clientHeight)
        {
            Source = source;
            Cols = cols;
            Rows = rows;
            ClientWidth = clientWidth;
            ClientHeight = clientHeight;
        }

        public string Source { get; }

        public int Cols { get; }

        public int Rows { get; }

        public int ClientWidth { get; }

        public int ClientHeight { get; }
    }

    public sealed class TerminalResizeEventArgs : EventArgs
    {
        public TerminalResizeEventArgs(string source, int cols, int rows, int clientWidth, int clientHeight)
        {
            Source = source;
            Cols = cols;
            Rows = rows;
            ClientWidth = clientWidth;
            ClientHeight = clientHeight;
        }

        public string Source { get; }

        public int Cols { get; }

        public int Rows { get; }

        public int ClientWidth { get; }

        public int ClientHeight { get; }
    }
}
