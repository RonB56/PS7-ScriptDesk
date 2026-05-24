using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Shell.Controls
{
    /// <summary>
    /// A WPF UserControl that hosts an xterm.js terminal emulator inside a WebView2
    /// control, providing full VT100/ANSI rendering of ConPTY output.
    ///
    /// Data flow:
    ///   ConPTY → LiveConsoleService.RawOutputReceived → WriteRaw() → xterm.js
    ///   xterm.js onData → UserInput event → ILiveConsoleService.WriteRawInputAsync()
    ///   xterm.js onResize → TerminalResized event → ILiveConsoleService.ResizeConsole()
    /// </summary>
    public partial class TerminalControl : System.Windows.Controls.UserControl
    {
        // ── xterm.js HTML page ───────────────────────────────────────────────────
        //
        // xterm.js and its addons are served from the virtual host "terminal.local"
        // which is mapped to the <output>/terminal/ folder via
        // SetVirtualHostNameToFolderMapping in OnLoaded. This avoids CDN dependency
        // and null-origin CSP issues from NavigateToString.
        //
        // Files required in <output>/terminal/:
        //   xterm.min.js                 (xterm@5.3.0)
        //   xterm.css                    (xterm@5.3.0)
        //   xterm-addon-fit.min.js       (xterm-addon-fit@0.8.0)
        //   xterm-addon-web-links.min.js (xterm-addon-web-links@0.9.0)
        private const string TerminalHtml = """
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="UTF-8">
            <meta http-equiv="Content-Security-Policy"
                  content="default-src 'none'; script-src 'unsafe-inline' https://terminal.local; style-src 'unsafe-inline' https://terminal.local; connect-src 'none';">
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
            <link rel="stylesheet" href="https://terminal.local/xterm.css">
            </head>
            <body>
            <div id="terminal"></div>
            <script src="https://terminal.local/xterm.min.js"></script>
            <script src="https://terminal.local/xterm-addon-fit.min.js"></script>
            <script src="https://terminal.local/xterm-addon-web-links.min.js"></script>
            <script>
            document.title = 'XTERM_LOADED_' + (typeof Terminal !== 'undefined');
            (function () {
              'use strict';

              function post(obj) {
                try { window.chrome.webview.postMessage(JSON.stringify(obj)); } catch (e) {}
              }

              function decodeBase64Utf8(value) {
                var binary = atob(value);
                var bytes = new Uint8Array(binary.length);
                for (var i = 0; i < binary.length; i++) {
                  bytes[i] = binary.charCodeAt(i);
                }
                return new TextDecoder('utf-8').decode(bytes);
              }

              var termApi = null;
              var readyPosted = false;

              function reportFocus(type, source) {
                var activeElement = document.activeElement;
                var activeElementTag = activeElement ? activeElement.tagName : '';
                var activeElementClass = activeElement && activeElement.className ? String(activeElement.className) : '';
                post({
                  type: type,
                  source: source,
                  documentHasFocus: document.hasFocus(),
                  activeElement: activeElementClass ? activeElementTag + '.' + activeElementClass : activeElementTag
                });
              }

              try {
                var term = new Terminal({
                  theme: {
                    background:          '#000000',
                    foreground:          '#F2F2F2',
                    cursor:              '#00FF00',
                    cursorAccent:        '#000000',
                    selectionBackground: 'rgba(255, 255, 255, 0.3)',
                    selectionForeground: '#FFFFFF',
                    black:        '#5C6370', brightBlack:   '#9AA4B2',
                    red:          '#FF5555', brightRed:     '#FF7A7A',
                    green:        '#50FA7B', brightGreen:   '#69FF94',
                    yellow:       '#F1FA8C', brightYellow:  '#FFFFA5',
                    blue:         '#66B2FF', brightBlue:    '#99CCFF',
                    magenta:      '#FF79C6', brightMagenta: '#FF99D6',
                    cyan:         '#8BE9FD', brightCyan:    '#A4F3FF',
                    white:        '#F2F2F2', brightWhite:   '#FFFFFF'
                  },
                  fontFamily:  "'Cascadia Code','Cascadia Mono',Consolas,'Courier New',monospace",
                  fontSize:     14,
                  lineHeight:   1.2,
                  scrollback:   10000,
                  cursorBlink:  true,
                  cursorStyle:  'block',
                  cursorInactiveStyle: 'outline',
                  cursorWidth:  2,
                  convertEol:   false,
                  allowTransparency: false
                });

                var fitAddon = new FitAddon.FitAddon();
                var webLinksAddon = new WebLinksAddon.WebLinksAddon();
                var terminalElement = document.getElementById('terminal');
                terminalElement.tabIndex = 0;
                term.loadAddon(fitAddon);
                term.loadAddon(webLinksAddon);
                term.open(terminalElement);

                function reportLayout(source) {
                  post({
                    type: 'layout',
                    source: source,
                    cols: term.cols,
                    rows: term.rows,
                    clientWidth: terminalElement.clientWidth,
                    clientHeight: terminalElement.clientHeight
                  });
                }

                function fitTerminal(source) {
                  try {
                    fitAddon.fit();
                  } catch (fitErr) {
                    post({ type: 'xterm_fit_error', source: source, message: String(fitErr) });
                  }

                  reportLayout(source);
                }

                function focusTerminal(source) {
                  try { window.focus(); } catch (ignore) { }
                  try { document.body.focus(); } catch (ignore) { }
                  try { terminalElement.focus(); } catch (ignore) { }
                  term.focus();
                  reportFocus('focus', source);
                }

                function signalReady(source) {
                  if (readyPosted) {
                    return;
                  }

                  readyPosted = true;
                  post({
                    type: 'ready',
                    source: source,
                    cols: term.cols,
                    rows: term.rows,
                    clientWidth: terminalElement.clientWidth,
                    clientHeight: terminalElement.clientHeight
                  });
                }

                function initializeTerminalHost() {
                  window.requestAnimationFrame(function() {
                    fitTerminal('startup.raf1');
                    window.requestAnimationFrame(function() {
                      fitTerminal('startup.raf2');
                      focusTerminal('startup.raf2');
                      signalReady('startup.raf2');
                    });
                  });

                  window.setTimeout(function() {
                    fitTerminal('startup.timeout50');
                    focusTerminal('startup.timeout50');
                  }, 50);
                }

                // Re-focus on mouse activation so the helper textarea becomes the
                // active input element after clicking away to the editor.
                terminalElement.addEventListener('mousedown', function() {
                  post({ type: 'activated', source: 'terminal.mousedown' });
                  window.setTimeout(function() {
                    fitTerminal('terminal.mousedown');
                    focusTerminal('terminal.mousedown');
                  }, 0);
                });

                terminalElement.addEventListener('click', function() {
                  window.setTimeout(function() {
                    fitTerminal('terminal.click');
                    focusTerminal('terminal.click');
                  }, 0);
                });

                // Re-focus when the WebView2 host window gains focus (e.g. Alt+Tab back).
                window.addEventListener('focus', function() {
                  fitTerminal('window.focus');
                  focusTerminal('window.focus');
                });

                terminalElement.addEventListener('focusin', function() {
                  reportFocus('focus', 'terminal.focusin');
                });

                terminalElement.addEventListener('focusout', function() {
                  reportFocus('blur', 'terminal.focusout');
                });

                term.onData(function (data) { post({ type: 'input', data: data }); });
                term.onResize(function (e)  { post({ type: 'resize', cols: e.cols, rows: e.rows }); });

                // ── Copy / paste via C# clipboard bridge ─────────────────────────
                // Ctrl+C with selection → copy; without selection → SIGINT (\x03).
                // Ctrl+V / Shift+Insert → let xterm.js handle the native textarea paste path.
                // Ctrl+A → select all.
                // Right-click → paste via the host clipboard bridge.
                term.attachCustomKeyEventHandler(function(e) {
                  if (e.type !== 'keydown') return true;

                  if (e.ctrlKey && !e.altKey && !e.metaKey && e.key && e.key.toLowerCase() === 'f') {
                    post({ type: 'app_shortcut', command: 'find' });
                    return false;
                  }

                  if (e.ctrlKey && !e.altKey && !e.metaKey && e.key && e.key.toLowerCase() === 'h') {
                    post({ type: 'app_shortcut', command: 'replace' });
                    return false;
                  }

                  if (e.ctrlKey && e.key === 'c') {
                    if (term.hasSelection()) {
                      post({ type: 'copy', text: term.getSelection() });
                      term.clearSelection();
                      return false;
                    }
                    return true; // no selection → pass through as \x03 (SIGINT)
                  }
                  if (e.ctrlKey && e.key === 'a') {
                    term.selectAll();
                    return false;
                  }
                  return true;
                });

                document.addEventListener('contextmenu', function(e) {
                  e.preventDefault();
                  post({ type: 'paste_request' });
                });

                var ro = new ResizeObserver(function () {
                  window.requestAnimationFrame(function() {
                    fitTerminal('resizeObserver');
                  });
                });
                ro.observe(terminalElement);

                termApi = {
                  write: function (d) {
                    try {
                      term.write(d);
                    } catch (writeErr) {
                      post({ type: 'xterm_write_error', message: String(writeErr) });
                    }
                  },
                  paste: function (d) {
                    try {
                      term.paste(d);
                    } catch (pasteErr) {
                      post({ type: 'xterm_write_error', message: String(pasteErr) });
                    }
                  },
                  clear: function ()  {
                    try { term.clear(); } catch (ignoreClear) { }
                    try { term.write('\x1b[2J\x1b[3J\x1b[H'); } catch (ignoreErase) { }
                    focusTerminal('termApi.clear');
                  },
                  focus: function ()  { focusTerminal('termApi.focus'); }
                };
                initializeTerminalHost();
              } catch (initErr) {
                post({ type: 'xterm_init_error', message: String(initErr) });
              }

              // ── App-level theme integration ──────────────────────────────────────
              var highContrastTerminalTheme = {
                  background: '#000000', foreground: '#F2F2F2',
                  cursor: '#00FF00', cursorAccent: '#000000',
                  selectionBackground: 'rgba(88,166,255,0.35)',
                  selectionForeground: '#FFFFFF',
                  black: '#5C6370', brightBlack: '#9AA4B2',
                  red: '#FF5555', brightRed: '#FF7A7A',
                  green: '#50FA7B', brightGreen: '#69FF94',
                  yellow: '#F1FA8C', brightYellow: '#FFFFA5',
                  blue: '#66B2FF', brightBlue: '#99CCFF',
                  magenta: '#FF79C6', brightMagenta: '#FF99D6',
                  cyan: '#8BE9FD', brightCyan: '#A4F3FF',
                  white: '#F2F2F2', brightWhite: '#FFFFFF'
                };

              var terminalThemes = {
                Dark: highContrastTerminalTheme,
                Light: highContrastTerminalTheme,
                IseBlue: highContrastTerminalTheme
              };

              // ── Receive messages from C# ─────────────────────────────────────────
              window.chrome.webview.addEventListener('message', function (e) {
                try {
                  var msg = (typeof e.data === 'string') ? JSON.parse(e.data) : e.data;
                  if (!termApi || !msg || !msg.type) return;
                  if      (msg.type === 'output_b64' && typeof msg.data === 'string') { termApi.write(decodeBase64Utf8(msg.data)); }
                  else if (msg.type === 'output') { termApi.write(msg.data || ''); }
                  else if (msg.type === 'clear')  { termApi.clear(); }
                  else if (msg.type === 'focus')  {
                    termApi.focus();
                    reportFocus('focus', 'host.message.focus');
                  }
                  else if (msg.type === 'paste' && typeof msg.data === 'string') { termApi.paste(msg.data); }
                  else if (msg.type === 'settheme' && msg.data && terminalThemes[msg.data]) {
                    term.options.theme = terminalThemes[msg.data] || highContrastTerminalTheme;
                  }
                } catch (err) {
                  post({ type: 'xterm_host_message_error', message: String(err) });
                }
              });

            })();
            </script>
            </body>
            </html>
            """;

        // ── State ────────────────────────────────────────────────────────────────

        private readonly object        _queueLock   = new();
        private readonly List<string>  _outputQueue = new();
        private readonly object        _pendingOutputLock = new();
        private readonly StringBuilder _pendingOutputBuffer = new();
        private volatile bool          _isReady;
        private bool                   _webView2Available = true;
        private bool                   _outputFlushScheduled;
        private bool                   _firstOutputQueuedLogged;
        private bool                   _firstOutputPostedLogged;
        private bool                   _firstInputReceivedLogged;
        private int                    _inputInfoLogCount;
        private readonly DispatcherTimer _transcriptPreservationTimer;
        private DateTimeOffset         _preserveTranscriptUntilUtc = DateTimeOffset.MinValue;
        private string?                _transcriptPreservationReason;
        private TranscriptPreservationMode _transcriptPreservationMode;
        private bool                   _hasDeferredResize;
        private int                    _deferredResizeCols;
        private int                    _deferredResizeRows;
        private string?                _pendingPromptRestoreText;
        private string?                _pendingPromptRestoreReason;
        private bool                   _lastVisibleOutputEndedWithLineBreak = true;
        private static readonly Regex PromptRegex = new(@"PS\s+.+?>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private enum TranscriptPreservationMode
        {
            None,
            General,
            DebugTranscript
        }

        // ── Events ────────────────────────────────────────────────────────────────

        /// <summary>Fires (on the UI thread) the first time xterm.js signals ready and the output queue is flushed.</summary>
        public event Action? TerminalReady;

        /// <summary>Fires when the user types in xterm.js (keystroke data to send to ConPTY).</summary>
        public event Action<string>? UserInput;

        /// <summary>Fires when xterm.js reports a resize (cols, rows to send to ConPTY).</summary>
        public event Action<int, int>? TerminalResized;

        /// <summary>Fires when the terminal is explicitly activated by click/focus/input.</summary>
        public event Action<string>? TerminalActivated;

        /// <summary>Fires when xterm.js captures a keyboard gesture that belongs to the host app, such as Ctrl+F or Ctrl+H.</summary>
        public event Action<string>? AppShortcutRequested;

        // ── Constructor ───────────────────────────────────────────────────────────

        public TerminalControl()
        {
            InitializeComponent();
            _transcriptPreservationTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _transcriptPreservationTimer.Tick += TranscriptPreservationTimer_Tick;
            Loaded += OnLoaded;
            WebView.PreviewMouseDown += WebView_PreviewMouseDown;
            WebView.GotKeyboardFocus += WebView_GotKeyboardFocus;
            WebView.LostKeyboardFocus += WebView_LostKeyboardFocus;
        }

        // ── Initialization ────────────────────────────────────────────────────────

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                AppLogger.Debug("Terminal", "Initializing WebView2 terminal host.");
                await WebView.EnsureCoreWebView2Async().ConfigureAwait(true);
                WebView.CoreWebView2.Settings.IsStatusBarEnabled               = false;
                WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled    = false;
                WebView.CoreWebView2.Settings.IsZoomControlEnabled             = false;
                WebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

                // Map virtual hostname → terminal/ so xterm.js files can be loaded via
                // https://terminal.local/... Production builds have terminal/ next to the
                // exe (copied by build). Development builds may not — walk up the tree to
                // find the source copy so the app works without a Clean+Rebuild.
                var assemblyDir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location)!;
                var terminalDir = System.IO.Path.Combine(assemblyDir, "terminal");

                if (!System.IO.Directory.Exists(terminalDir) ||
                    !System.IO.File.Exists(System.IO.Path.Combine(terminalDir, "xterm.min.js")))
                {
                    var searchDir = assemblyDir;
                    for (var i = 0; i < 8; i++)
                    {
                        var candidate = System.IO.Path.Combine(searchDir, "terminal");
                        if (System.IO.Directory.Exists(candidate) &&
                            System.IO.File.Exists(System.IO.Path.Combine(candidate, "xterm.min.js")))
                        {
                            terminalDir = candidate;
                            break;
                        }
                        var parent = System.IO.Path.GetDirectoryName(searchDir);
                        if (parent is null || parent == searchDir) break;
                        searchDir = parent;
                    }
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[TerminalControl] Terminal assets: {terminalDir} (exists={System.IO.Directory.Exists(terminalDir)})");

                WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "terminal.local", terminalDir,
                    CoreWebView2HostResourceAccessKind.Allow);

                // Keep WebView2 lifecycle diagnostics out of the visible terminal.
                // A normal PowerShell console should begin with PowerShell output/prompt,
                // not app-host debug lines.

                WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                // Log navigation success/failure to the debug output; navigation errors
                // mean the HTML page could not be loaded at all (very rare).
                WebView.CoreWebView2.NavigationCompleted += (_, nav) =>
                {
                    var title = WebView.CoreWebView2.DocumentTitle;
                    if (nav.IsSuccess)
                        System.Diagnostics.Debug.WriteLine(
                            $"[TerminalControl] NavigationCompleted — success | title={title}");
                    else
                        System.Diagnostics.Debug.WriteLine(
                            $"[TerminalControl] NavigationCompleted — FAILED ({nav.WebErrorStatus}) | title={title}");
                };

                WebView.CoreWebView2.NavigateToString(TerminalHtml);
                System.Diagnostics.Debug.WriteLine("[TerminalControl] WebView2 initialized — navigating to terminal page");
                AppLogger.Debug("Terminal", "WebView2 terminal page navigation started.");
            }
            catch (Exception ex)
            {
                _webView2Available = false;
                FallbackBanner.Visibility = Visibility.Visible;
                WebView.Visibility        = Visibility.Collapsed;
                System.Diagnostics.Debug.WriteLine(
                    $"[TerminalControl] WebView2 initialization failed: {ex.Message}");
                AppLogger.Error("Terminal", "WebView2 terminal initialization failed.", ex);
            }
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes raw VT100/ANSI data to xterm.js. ANSI escape sequences are
        /// preserved so xterm.js renders colors, cursor movement, etc.
        /// Thread-safe — may be called from any thread.
        /// </summary>
        public void WriteRaw(string data)
        {
            if (string.IsNullOrEmpty(data) || !_webView2Available)
            {
                return;
            }

            if (ShouldSuppressPromptRedrawChunk(data, out var suppressionReason))
            {
                AppLogger.Info("Terminal", $"Suppressed terminal output chunk during transcript preservation. Reason={suppressionReason}, Preview='{FormatForLog(data)}'.");
                DeveloperDiagnostics.LogDecision(
                    "Terminal",
                    "WriteRaw",
                    "Terminal output chunk was suppressed during transcript preservation.",
                    "SuppressPromptRedrawChunk",
                    new Dictionary<string, object?>(DeveloperDiagnostics.CreateTextMetadata(data))
                    {
                        ["reason"] = suppressionReason,
                        ["preservationReason"] = _transcriptPreservationReason,
                        ["preserveUntilUtc"] = _preserveTranscriptUntilUtc
                    });
                return;
            }

            if (IsTranscriptPreservationActive())
            {
                DeveloperDiagnostics.LogDecision(
                    "Terminal",
                    "WriteRaw",
                    "Terminal output chunk was allowed during transcript preservation.",
                    "AllowOutputDuringPreservation",
                    new Dictionary<string, object?>(DeveloperDiagnostics.CreateTextMetadata(data))
                    {
                        ["reason"] = "Chunk did not match prompt-redraw suppression heuristics.",
                        ["preservationReason"] = _transcriptPreservationReason,
                        ["preserveUntilUtc"] = _preserveTranscriptUntilUtc
                    });
            }

            if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseTerminalEnabled())
            {
                DeveloperDiagnostics.LogDebug(
                    "Terminal",
                    "Terminal output received for WebView dispatch.",
                    new Dictionary<string, object?>(DeveloperDiagnostics.CreateTextMetadata(data))
                    {
                        ["isReady"] = _isReady,
                        ["queuedOutputCount"] = _outputQueue.Count
                    });
            }

            lock (_queueLock)
            {
                if (!_isReady)
                {
                    if (!_firstOutputQueuedLogged)
                    {
                        _firstOutputQueuedLogged = true;
                        AppLogger.Info("Terminal", $"Queued terminal output before xterm.js was ready. Length={data.Length}.");
                        DeveloperDiagnostics.LogInfo("Terminal", "Terminal output queued before xterm.js was ready.", new Dictionary<string, object?> { ["length"] = data.Length });
                    }

                    _outputQueue.Add(data);
                    return;
                }
            }

            if (!_firstOutputPostedLogged)
            {
                _firstOutputPostedLogged = true;
                AppLogger.Info("Terminal", $"Posting first terminal output chunk to xterm.js. Length={data.Length}.");
                DeveloperDiagnostics.LogInfo("Terminal", "First terminal output chunk posted to xterm.js.", new Dictionary<string, object?> { ["length"] = data.Length });
            }

            EnqueueOutputForWebView(data);
        }

        /// <summary>
        /// Writes plain text to xterm.js. Newlines are passed as-is; no ANSI escaping is applied.
        /// App diagnostics should generally go to the application log, not the visible terminal.
        /// </summary>
        public void WriteText(string text)
        {
            WriteRaw(text);
        }

        /// <summary>Clears the xterm.js terminal display and returns keyboard focus to it.</summary>
        public void Clear()
        {
            if (!_webView2Available) return;
            DeveloperDiagnostics.LogUserAction("Terminal", "TerminalClearRequested", "Terminal clear requested.");
            _lastVisibleOutputEndedWithLineBreak = true;
            PostToWebView("clear", string.Empty);
        }

        /// <summary>Focuses the xterm.js terminal so keystrokes are captured immediately.</summary>
        public void FocusTerminal()
        {
            if (!_webView2Available) return;
            DeveloperDiagnostics.LogUserAction("Terminal", "TerminalFocusRequested", "Terminal focus requested.");
            ActivateTerminalHost("FocusTerminal");
        }

        public void PreserveVisibleTranscriptFor(TimeSpan duration, string reason)
        {
            if (duration <= TimeSpan.Zero)
            {
                return;
            }

            void ActivatePreservation()
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var requestedUntilUtc = nowUtc + duration;
                if (requestedUntilUtc > _preserveTranscriptUntilUtc)
                {
                    _preserveTranscriptUntilUtc = requestedUntilUtc;
                }

                _transcriptPreservationReason = reason;
                _transcriptPreservationMode = DetermineTranscriptPreservationMode(reason);
                _transcriptPreservationTimer.Interval = duration < TimeSpan.FromMilliseconds(250)
                    ? duration
                    : TimeSpan.FromMilliseconds(250);
                if (!_transcriptPreservationTimer.IsEnabled)
                {
                    _transcriptPreservationTimer.Start();
                }

                AppLogger.Info("Terminal", $"Visible transcript preservation activated. DurationMs={duration.TotalMilliseconds:F0}, Reason={reason}, UntilUtc={_preserveTranscriptUntilUtc:O}.");
                DeveloperDiagnostics.LogInfo(
                    "Terminal",
                    "Visible transcript preservation activated.",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["mode"] = _transcriptPreservationMode.ToString(),
                        ["durationMs"] = duration.TotalMilliseconds,
                        ["preserveUntilUtc"] = _preserveTranscriptUntilUtc
                    });
            }

            if (Dispatcher.CheckAccess())
            {
                ActivatePreservation();
            }
            else
            {
                Dispatcher.BeginInvoke((Action)ActivatePreservation);
            }
        }

        public void RestoreVisiblePromptAfterDebug(string promptText, string reason)
        {
            if (string.IsNullOrWhiteSpace(promptText) || !_webView2Available)
            {
                DeveloperDiagnostics.LogDecision(
                    "Terminal",
                    "RestoreVisiblePromptAfterDebug",
                    "Visible prompt restore was skipped because prompt text was empty or WebView2 was unavailable.",
                    "SkipVisiblePromptRestore",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["webViewAvailable"] = _webView2Available,
                        ["promptTextEmpty"] = string.IsNullOrWhiteSpace(promptText)
                    });
                return;
            }

            void RequestPromptRestore()
            {
                var normalizedPromptText = promptText.Trim();
                _pendingPromptRestoreText = normalizedPromptText;
                _pendingPromptRestoreReason = reason;
                DeveloperDiagnostics.LogInfo(
                    "Terminal",
                    "Visible prompt restoration requested after debug completion.",
                    new Dictionary<string, object?>(DeveloperDiagnostics.CreateTextMetadata(normalizedPromptText))
                    {
                        ["reason"] = reason,
                        ["preservationActive"] = IsTranscriptPreservationActive(),
                        ["preservationMode"] = _transcriptPreservationMode.ToString()
                    });

                if (!IsTranscriptPreservationActive())
                {
                    ShowPendingPromptRestore("Prompt restoration requested without an active preservation window.");
                }
            }

            if (Dispatcher.CheckAccess())
            {
                RequestPromptRestore();
            }
            else
            {
                Dispatcher.BeginInvoke((Action)RequestPromptRestore);
            }
        }

        /// <summary>Updates the xterm.js colour theme to match the active application theme.</summary>
        public void ApplyAppTheme(string themeName)
        {
            PostToWebView("settheme", themeName);
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private void EnqueueOutputForWebView(string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                return;
            }

            _lastVisibleOutputEndedWithLineBreak = data.EndsWith('\n') || data.EndsWith('\r');

            lock (_pendingOutputLock)
            {
                _pendingOutputBuffer.Append(data);

                if (_outputFlushScheduled)
                {
                    return;
                }

                _outputFlushScheduled = true;
            }

            Dispatcher.BeginInvoke(
                new Action(FlushPendingOutputToWebView),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void FlushPendingOutputToWebView()
        {
            string data;

            lock (_pendingOutputLock)
            {
                if (_pendingOutputBuffer.Length == 0)
                {
                    _outputFlushScheduled = false;
                    return;
                }

                data = _pendingOutputBuffer.ToString();
                _pendingOutputBuffer.Clear();
                _outputFlushScheduled = false;
            }

            PostToWebView("output", data);
        }

        private void PostToWebView(string type, string data)
        {
            if (!_webView2Available) return;

            void Send()
            {
                if (WebView.CoreWebView2 is null) return;
                try
                {
                    if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseTerminalEnabled())
                    {
                        DeveloperDiagnostics.LogDebug(
                            "Terminal",
                            $"Posting host message to terminal. Type={type}.",
                            type == "output"
                                ? new Dictionary<string, object?>(DeveloperDiagnostics.CreateTextMetadata(data))
                                : new Dictionary<string, object?> { ["type"] = type, ["data"] = DeveloperDiagnostics.SanitizePreview(data) });
                    }
                    var msg = type switch
                    {
                        "output" => JsonSerializer.Serialize(new
                        {
                            type = "output_b64",
                            data = Convert.ToBase64String(Encoding.UTF8.GetBytes(data ?? string.Empty))
                        }),
                        "clear" or "focus" => JsonSerializer.Serialize(new { type }),
                        _ => JsonSerializer.Serialize(new { type, data })
                    };
                    WebView.CoreWebView2.PostWebMessageAsString(msg);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[TerminalControl] PostWebMessageAsString failed: {ex.Message}");
                    DeveloperDiagnostics.LogException("Terminal", ex, "Posting host message to WebView2 terminal failed.", new Dictionary<string, object?> { ["type"] = type });
                }
            }

            if (Dispatcher.CheckAccess())
                Send();
            else
                Dispatcher.BeginInvoke(Send);
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(json)) return;

                using var doc  = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) return;
                var type = typeProp.GetString();

                switch (type)
                {
                    case "ready":
                        var readySource = root.TryGetProperty("source", out var readySourceProp)
                            ? readySourceProp.GetString()
                            : "unknown";
                        var readyCols = root.TryGetProperty("cols", out var readyColsProp) ? readyColsProp.GetInt32() : 0;
                        var readyRows = root.TryGetProperty("rows", out var readyRowsProp) ? readyRowsProp.GetInt32() : 0;
                        var readyClientWidth = root.TryGetProperty("clientWidth", out var readyClientWidthProp) ? readyClientWidthProp.GetInt32() : 0;
                        var readyClientHeight = root.TryGetProperty("clientHeight", out var readyClientHeightProp) ? readyClientHeightProp.GetInt32() : 0;
                        System.Diagnostics.Debug.WriteLine("[TerminalControl] Received 'ready' from xterm.js — flushing output queue");
                        AppLogger.Info("Terminal", $"xterm.js signaled ready. Source={readySource}, Cols={readyCols}, Rows={readyRows}, ClientWidth={readyClientWidth}, ClientHeight={readyClientHeight}.");
                        DeveloperDiagnostics.LogStateTransition(
                            "Terminal",
                            "TerminalReady",
                            "Initializing",
                            "Ready",
                            "xterm.js signaled ready.",
                            new Dictionary<string, object?>
                            {
                                ["source"] = readySource,
                                ["cols"] = readyCols,
                                ["rows"] = readyRows,
                                ["clientWidth"] = readyClientWidth,
                                ["clientHeight"] = readyClientHeight
                            });
                        FlushOutputQueue();
                        break;

                    case "xterm_init_error":
                        var reason = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "unknown";
                        System.Diagnostics.Debug.WriteLine($"[TerminalControl] xterm.js init error: {reason}");
                        AppLogger.Error("Terminal", $"xterm.js initialization failed inside WebView2. Reason={reason}");
                        DeveloperDiagnostics.LogError("Terminal", "xterm.js initialization failed inside WebView2.", new Dictionary<string, object?> { ["reason"] = reason });
                        break;

                    case "xterm_fit_error":
                        var fitReason = root.TryGetProperty("message", out var fitMsgProp) ? fitMsgProp.GetString() : "unknown";
                        var fitSource = root.TryGetProperty("source", out var fitSourceProp) ? fitSourceProp.GetString() : "unknown";
                        AppLogger.Error("Terminal", $"xterm.js fit failed inside WebView2. Source={fitSource}, Reason={fitReason}");
                        DeveloperDiagnostics.LogError("Terminal", "xterm.js fit failed inside WebView2.", new Dictionary<string, object?> { ["source"] = fitSource, ["reason"] = fitReason });
                        break;

                    case "xterm_write_error":
                    case "xterm_host_message_error":
                        {
                            var writeReason = root.TryGetProperty("message", out var writeMsgProp) ? writeMsgProp.GetString() : "unknown";
                            AppLogger.Error("Terminal", $"xterm.js message/write failed inside WebView2. Type={type}, Reason={writeReason}");
                            DeveloperDiagnostics.LogError("Terminal", "xterm.js message/write failed inside WebView2.", new Dictionary<string, object?> { ["type"] = type, ["reason"] = writeReason });
                        }
                        break;

                    case "layout":
                        {
                            var source = root.TryGetProperty("source", out var layoutSourceProp)
                                ? layoutSourceProp.GetString()
                                : "unknown";
                            var cols = root.TryGetProperty("cols", out var layoutColsProp) ? layoutColsProp.GetInt32() : 0;
                            var rows = root.TryGetProperty("rows", out var layoutRowsProp) ? layoutRowsProp.GetInt32() : 0;
                            var clientWidth = root.TryGetProperty("clientWidth", out var clientWidthProp) ? clientWidthProp.GetInt32() : 0;
                            var clientHeight = root.TryGetProperty("clientHeight", out var clientHeightProp) ? clientHeightProp.GetInt32() : 0;

                            if (cols <= 1 || rows <= 1 || clientWidth <= 0 || clientHeight <= 0)
                            {
                                AppLogger.Warning("Terminal", $"xterm layout reported a suspicious size. Source={source}, Cols={cols}, Rows={rows}, ClientWidth={clientWidth}, ClientHeight={clientHeight}.");
                            }
                            else if (AppLogger.IsDebugEnabled)
                            {
                                AppLogger.Debug("Terminal", $"xterm layout reported. Source={source}, Cols={cols}, Rows={rows}, ClientWidth={clientWidth}, ClientHeight={clientHeight}.");
                            }
                        }
                        break;

                    case "activated":
                        {
                            var source = root.TryGetProperty("source", out var activatedSourceProp)
                                ? activatedSourceProp.GetString()
                                : "unknown";
                            AppLogger.Debug("Terminal", $"xterm activation message received. Source={source}, WebViewFocused={WebView.IsKeyboardFocusWithin}.");
                            DeveloperDiagnostics.LogUserAction("Terminal", "TerminalActivated", "xterm activation message received.", new Dictionary<string, object?> { ["source"] = source });
                            RaiseTerminalActivated(source ?? "unknown");
                        }
                        break;

                    case "focus":
                        {
                            var source = root.TryGetProperty("source", out var focusSourceProp)
                                ? focusSourceProp.GetString()
                                : "unknown";
                            var activeElement = root.TryGetProperty("activeElement", out var activeElementProp)
                                ? activeElementProp.GetString()
                                : null;
                            var documentHasFocus = root.TryGetProperty("documentHasFocus", out var documentHasFocusProp) &&
                                documentHasFocusProp.ValueKind == JsonValueKind.True;
                            AppLogger.Debug(
                                "Terminal",
                                $"xterm focus reported. Source={source}, DocumentHasFocus={documentHasFocus}, ActiveElement={activeElement}, WebViewFocused={WebView.IsKeyboardFocusWithin}.");
                            if (DeveloperDiagnostics.IsVerboseTerminalEnabled())
                            {
                                DeveloperDiagnostics.LogDebug("Terminal", "xterm focus reported.", new Dictionary<string, object?> { ["source"] = source, ["documentHasFocus"] = documentHasFocus, ["activeElement"] = activeElement });
                            }
                            RaiseTerminalActivated(source ?? "unknown");
                        }
                        break;

                    case "blur":
                        {
                            var source = root.TryGetProperty("source", out var blurSourceProp)
                                ? blurSourceProp.GetString()
                                : "unknown";
                            AppLogger.Debug("Terminal", $"xterm blur reported. Source={source}, WebViewFocused={WebView.IsKeyboardFocusWithin}.");
                        }
                        break;

                    case "app_shortcut":
                        {
                            var command = root.TryGetProperty("command", out var commandProp)
                                ? commandProp.GetString()
                                : null;
                            if (!string.IsNullOrWhiteSpace(command))
                            {
                                AppLogger.Debug("Terminal", $"xterm requested host shortcut. Command={command}.");
                                DeveloperDiagnostics.LogUserAction(
                                    "Terminal",
                                    "AppShortcutRequested",
                                    "xterm requested a host-level shortcut.",
                                    new Dictionary<string, object?> { ["command"] = command });
                                AppShortcutRequested?.Invoke(command);
                            }
                        }
                        break;

                    case "copy":
                        // JavaScript requests that selected text be copied to the clipboard.
                        // Must run on the UI thread (already here — WebMessageReceived fires on UI thread).
                        if (root.TryGetProperty("text", out var copyTextProp))
                        {
                            var copyText = copyTextProp.GetString();
                            if (!string.IsNullOrEmpty(copyText))
                            {
                                try { System.Windows.Clipboard.SetText(copyText); }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[TerminalControl] Clipboard.SetText failed: {ex.Message}");
                                }
                            }
                        }
                        break;

                    case "paste_request":
                        // JavaScript requests clipboard text for explicit host-driven paste
                        // flows such as right-click paste. Keyboard paste stays on xterm.js'
                        // native textarea path so the clipboard payload is not injected twice.
                        {
                            AppLogger.Debug("Terminal", "Paste requested by xterm.js.");
                            DeveloperDiagnostics.LogUserAction("Terminal", "PasteRequest", "Paste requested by xterm.js.");
                            string pasteText = string.Empty;
                            try
                            {
                                if (System.Windows.Clipboard.ContainsText())
                                    pasteText = System.Windows.Clipboard.GetText();
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[TerminalControl] Clipboard.GetText failed: {ex.Message}");
                            }
                            if (!string.IsNullOrEmpty(pasteText))
                                PostToWebView("paste", pasteText);
                        }
                        break;

                    case "input":
                        if (root.TryGetProperty("data", out var dataProp))
                        {
                            var data = dataProp.GetString();
                            if (!string.IsNullOrEmpty(data))
                            {
                                if (!_firstInputReceivedLogged)
                                {
                                    _firstInputReceivedLogged = true;
                                    AppLogger.Info("Terminal", $"Received first xterm.js input message from WebView2. Length={data.Length}, Data='{FormatForLog(data)}'.");
                                }
                                else if (_inputInfoLogCount < 4)
                                {
                                    _inputInfoLogCount++;
                                    AppLogger.Info("Terminal", $"Received additional xterm.js input message from WebView2. Index={_inputInfoLogCount + 1}, Length={data.Length}, Data='{FormatForLog(data)}'.");
                                }

                                AppLogger.Debug("Terminal", $"xterm input received from WebView2. Length={data.Length}, Data='{FormatForLog(data)}'.");
                                DeveloperDiagnostics.LogUserAction(
                                    "Terminal",
                                    "TerminalInput",
                                    "xterm input received from WebView2.",
                                    new Dictionary<string, object?>(DeveloperDiagnostics.CreateTextMetadata(data)));
                                RaiseTerminalActivated("xterm.onData");
                                UserInput?.Invoke(data);
                            }
                        }
                        break;

                    case "resize":
                        if (root.TryGetProperty("cols", out var colsProp) &&
                            root.TryGetProperty("rows", out var rowsProp))
                        {
                            var cols = colsProp.GetInt32();
                            var rows = rowsProp.GetInt32();
                            AppLogger.Debug("Terminal", $"xterm resize reported. Cols={cols}, Rows={rows}.");
                            DeveloperDiagnostics.LogInfo("Terminal", "xterm resize reported.", new Dictionary<string, object?> { ["cols"] = cols, ["rows"] = rows });
                            if (IsTranscriptPreservationActive())
                            {
                                _hasDeferredResize = true;
                                _deferredResizeCols = cols;
                                _deferredResizeRows = rows;
                                AppLogger.Info("Terminal", $"Deferred xterm resize during transcript preservation. Cols={cols}, Rows={rows}, Reason={_transcriptPreservationReason}.");
                                DeveloperDiagnostics.LogDecision(
                                    "Terminal",
                                    "OnWebMessageReceived",
                                    "xterm resize was deferred during transcript preservation.",
                                    "DeferResizeDuringTranscriptPreservation",
                                    new Dictionary<string, object?>
                                    {
                                        ["cols"] = cols,
                                        ["rows"] = rows,
                                        ["preservationReason"] = _transcriptPreservationReason,
                                        ["preserveUntilUtc"] = _preserveTranscriptUntilUtc
                                    });
                            }
                            else
                            {
                                TerminalResized?.Invoke(cols, rows);
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TerminalControl] WebMessage parse error: {ex.Message}");
                AppLogger.Error("Terminal", "WebView2 terminal message parsing failed.", ex);
                DeveloperDiagnostics.LogException("Terminal", ex, "WebView2 terminal message parsing failed.");
            }
        }

        private void FlushOutputQueue()
        {
            List<string> toFlush;
            lock (_queueLock)
            {
                _isReady = true;
                toFlush = new List<string>();
                toFlush.AddRange(_outputQueue);
                _outputQueue.Clear();
            }

            AppLogger.Info("Terminal", $"Flushing queued terminal output to xterm.js. Chunks={toFlush.Count}.");
            DeveloperDiagnostics.LogInfo("Terminal", "Flushing queued terminal output to xterm.js.", new Dictionary<string, object?> { ["chunkCount"] = toFlush.Count });

            if (toFlush.Count > 0)
            {
                EnqueueOutputForWebView(string.Concat(toFlush));
            }

            // Auto-focus so the user can type immediately.
            ActivateTerminalHost("FlushOutputQueue");

            // Notify subscribers (e.g. MainWindow) that the terminal is ready.
            // This is the signal to start the ConPTY session so output has
            // somewhere to go as soon as it arrives.
            TerminalReady?.Invoke();
        }

        private void WebView_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            AppLogger.Debug("Terminal", $"Terminal host received mouse activation. Button={e.ChangedButton}, WebViewFocused={WebView.IsKeyboardFocusWithin}.");
            DeveloperDiagnostics.LogUserAction("Terminal", "TerminalMouseActivation", "Terminal host received mouse activation.", new Dictionary<string, object?> { ["button"] = e.ChangedButton.ToString() });
            RaiseTerminalActivated($"WebView.{e.ChangedButton}MouseDown");
            ActivateTerminalHost($"WebView.{e.ChangedButton}MouseDown");
        }

        private void WebView_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            AppLogger.Debug("Terminal", $"WebView2 host received keyboard focus. NewFocus={e.NewFocus?.GetType().Name ?? "(null)"}.");
            DeveloperDiagnostics.LogUserAction("Terminal", "TerminalKeyboardFocus", "WebView2 host received keyboard focus.", new Dictionary<string, object?> { ["newFocus"] = e.NewFocus?.GetType().FullName });
            RaiseTerminalActivated("WebView.GotKeyboardFocus");
        }

        private void WebView_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            AppLogger.Debug("Terminal", $"WebView2 host lost keyboard focus. NewFocus={e.NewFocus?.GetType().Name ?? "(null)"}.");
        }

        private void ActivateTerminalHost(string source)
        {
            if (!_webView2Available)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_webView2Available)
                {
                    return;
                }

                var focusResult = WebView.Focus();
                AppLogger.Debug(
                    "Terminal",
                    $"Terminal host focus requested. Source={source}, FocusResult={focusResult}, IsKeyboardFocused={WebView.IsKeyboardFocused}, IsKeyboardFocusWithin={WebView.IsKeyboardFocusWithin}, CoreReady={WebView.CoreWebView2 is not null}.");
                DeveloperDiagnostics.LogUiThreadDispatch("Terminal", "TerminalFocusDispatch", "Terminal host focus requested.", Dispatcher.CheckAccess(), new Dictionary<string, object?> { ["source"] = source, ["focusResult"] = focusResult });
                PostToWebView("focus", string.Empty);
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void RaiseTerminalActivated(string source)
        {
            if (ShouldEndTranscriptPreservationForActivation(source))
            {
                EndTranscriptPreservation("User terminal interaction resumed normal behavior.", flushDeferredResize: true);
            }

            TerminalActivated?.Invoke(source);
        }

        private void TranscriptPreservationTimer_Tick(object? sender, EventArgs e)
        {
            if (!IsTranscriptPreservationActive())
            {
                EndTranscriptPreservation("Transcript preservation timer expired.", flushDeferredResize: true);
            }
        }

        private bool IsTranscriptPreservationActive()
        {
            return DateTimeOffset.UtcNow < _preserveTranscriptUntilUtc;
        }

        private bool ShouldSuppressPromptRedrawChunk(string data, out string reason)
        {
            reason = string.Empty;
            if (!IsTranscriptPreservationActive())
            {
                return false;
            }

            var containsCursorHome = data.Contains("\x1b[H", StringComparison.Ordinal);
            var eraseLineCount = CountOccurrences(data, "\x1b[K");
            var hasPrompt = PromptRegex.IsMatch(data);
            var hasCarriageReturn = data.Contains('\r');
            var containsLineFeed = data.Contains('\n');

            if (!containsCursorHome)
            {
                reason = "Allowed because no cursor-home escape sequence was present.";
                return false;
            }

            if (eraseLineCount < 2)
            {
                reason = "Allowed because erase-line count was below the prompt-redraw threshold.";
                return false;
            }

            if (!hasPrompt)
            {
                reason = "Allowed because no PowerShell prompt signature was present.";
                return false;
            }

            reason = $"Suppressed prompt redraw chunk during transcript preservation. ContainsCursorHome={containsCursorHome}, EraseLineCount={eraseLineCount}, HasPrompt={hasPrompt}, HasCarriageReturn={hasCarriageReturn}, ContainsLineFeed={containsLineFeed}.";
            return true;
        }

        private void EndTranscriptPreservation(string reason, bool flushDeferredResize)
        {
            var wasActive = _preserveTranscriptUntilUtc != DateTimeOffset.MinValue;
            var previousReason = _transcriptPreservationReason;
            var previousMode = _transcriptPreservationMode;
            var previousUntilUtc = _preserveTranscriptUntilUtc;
            _transcriptPreservationTimer.Stop();
            _preserveTranscriptUntilUtc = DateTimeOffset.MinValue;
            _transcriptPreservationReason = null;
            _transcriptPreservationMode = TranscriptPreservationMode.None;

            if (wasActive)
            {
                AppLogger.Info("Terminal", $"Visible transcript preservation ended. Reason={reason}, PreviousReason={previousReason}, PreviousMode={previousMode}, FlushDeferredResize={flushDeferredResize}, HadDeferredResize={_hasDeferredResize}.");
                DeveloperDiagnostics.LogInfo(
                    "Terminal",
                    "Visible transcript preservation ended.",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["previousReason"] = previousReason,
                        ["previousMode"] = previousMode.ToString(),
                        ["previousPreserveUntilUtc"] = previousUntilUtc,
                        ["flushDeferredResize"] = flushDeferredResize,
                        ["hadDeferredResize"] = _hasDeferredResize
                    });
            }

            if (previousMode == TranscriptPreservationMode.DebugTranscript && _hasDeferredResize)
            {
                var discardedCols = _deferredResizeCols;
                var discardedRows = _deferredResizeRows;
                _hasDeferredResize = false;
                _deferredResizeCols = 0;
                _deferredResizeRows = 0;
                AppLogger.Info("Terminal", $"Deferred resize discarded after debug transcript preservation to avoid prompt redraw wiping debug output. Cols={discardedCols}, Rows={discardedRows}, EndReason={reason}.");
                DeveloperDiagnostics.LogDecision(
                    "Terminal",
                    "EndTranscriptPreservation",
                    "Deferred resize was discarded after debug transcript preservation to avoid prompt redraw wiping visible debug output.",
                    "DiscardDeferredResizeAfterDebugTranscriptPreservation",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["previousReason"] = previousReason,
                        ["previousMode"] = previousMode.ToString(),
                        ["cols"] = discardedCols,
                        ["rows"] = discardedRows,
                        ["flushDeferredResize"] = flushDeferredResize
                    });
            }
            else if (flushDeferredResize && _hasDeferredResize)
            {
                var cols = _deferredResizeCols;
                var rows = _deferredResizeRows;
                _hasDeferredResize = false;
                _deferredResizeCols = 0;
                _deferredResizeRows = 0;
                AppLogger.Info("Terminal", $"Applying deferred xterm resize after transcript preservation. Cols={cols}, Rows={rows}.");
                DeveloperDiagnostics.LogInfo(
                    "Terminal",
                    "Applying deferred xterm resize after transcript preservation.",
                    new Dictionary<string, object?>
                    {
                        ["cols"] = cols,
                        ["rows"] = rows,
                        ["reason"] = reason
                    });
                TerminalResized?.Invoke(cols, rows);
            }
            else if (!flushDeferredResize)
            {
                var hadDeferredResize = _hasDeferredResize;
                _hasDeferredResize = false;
                _deferredResizeCols = 0;
                _deferredResizeRows = 0;
                DeveloperDiagnostics.LogDecision(
                    "Terminal",
                    "EndTranscriptPreservation",
                    "Deferred resize state was cleared because preservation ended without replaying deferred resize.",
                    "ClearDeferredResizeWithoutReplay",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["previousReason"] = previousReason,
                        ["previousMode"] = previousMode.ToString(),
                        ["hadDeferredResize"] = hadDeferredResize
                    });
            }

            if (previousMode == TranscriptPreservationMode.DebugTranscript)
            {
                ShowPendingPromptRestore(reason);
            }
        }

        private static TranscriptPreservationMode DetermineTranscriptPreservationMode(string? reason)
        {
            if (!string.IsNullOrWhiteSpace(reason) &&
                reason.Contains("Debug teardown", StringComparison.Ordinal))
            {
                return TranscriptPreservationMode.DebugTranscript;
            }

            return TranscriptPreservationMode.General;
        }

        private void ShowPendingPromptRestore(string triggerReason)
        {
            if (string.IsNullOrWhiteSpace(_pendingPromptRestoreText))
            {
                DeveloperDiagnostics.LogDecision(
                    "Terminal",
                    "ShowPendingPromptRestore",
                    "Visible prompt restoration was skipped because no pending prompt text was available.",
                    "SkipPendingPromptRestore",
                    new Dictionary<string, object?>
                    {
                        ["triggerReason"] = triggerReason,
                        ["pendingReason"] = _pendingPromptRestoreReason
                    });
                return;
            }

            var promptText = _pendingPromptRestoreText;
            var promptReason = _pendingPromptRestoreReason ?? triggerReason;
            _pendingPromptRestoreText = null;
            _pendingPromptRestoreReason = null;
            ShowNonDestructivePrompt(promptText, promptReason, triggerReason);
        }

        private void ShowNonDestructivePrompt(string promptText, string reason, string triggerReason)
        {
            var output = (_lastVisibleOutputEndedWithLineBreak ? string.Empty : "\r\n") + "\x1b[?25h" + promptText;
            _lastVisibleOutputEndedWithLineBreak = false;
            AppLogger.Info("Terminal", $"Showing non-destructive visible prompt after debug completion. Prompt='{FormatForLog(promptText)}', Reason={reason}, TriggerReason={triggerReason}.");
            DeveloperDiagnostics.LogDecision(
                "Terminal",
                "ShowNonDestructivePrompt",
                "Non-destructive visible prompt restoration was posted to xterm after debug completion.",
                "ShowVisiblePromptAfterDebug",
                new Dictionary<string, object?>(DeveloperDiagnostics.CreateTextMetadata(promptText))
                {
                    ["reason"] = reason,
                    ["triggerReason"] = triggerReason,
                    ["cursorShowRequested"] = true,
                    ["prependedLineBreak"] = output.StartsWith("\r\n", StringComparison.Ordinal)
                });
            EnqueueOutputForWebView(output);
            DeveloperDiagnostics.LogDecision(
                "Terminal",
                "ShowNonDestructivePrompt",
                "xterm cursor-show request was sent with the visible prompt restoration output.",
                "ShowCursorWithVisiblePromptRestore",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["triggerReason"] = triggerReason
                });
            ActivateTerminalHost("ShowNonDestructivePrompt");
        }

        private static int CountOccurrences(string text, string value)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value))
            {
                return 0;
            }

            var count = 0;
            var searchIndex = 0;
            while (true)
            {
                var foundIndex = text.IndexOf(value, searchIndex, StringComparison.Ordinal);
                if (foundIndex < 0)
                {
                    return count;
                }

                count++;
                searchIndex = foundIndex + value.Length;
            }
        }

        private static bool ShouldEndTranscriptPreservationForActivation(string source)
        {
            return source.StartsWith("WebView.", StringComparison.Ordinal) ||
                   string.Equals(source, "xterm.onData", StringComparison.Ordinal) ||
                   string.Equals(source, "terminal.click", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatForLog(string? text, int maxLength = 80)
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
    }
}
