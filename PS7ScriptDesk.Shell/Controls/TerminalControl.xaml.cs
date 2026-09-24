using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Shell.Controls
{
    /// <summary>
    /// A WPF UserControl that hosts an xterm.js terminal emulator inside a WebView2
    /// control, providing full VT100/ANSI rendering of ConPTY output.
    ///
    /// Data flow:
    ///   ConPTY → LiveConsoleService.RawOutputReceived (generation, output) → WriteRaw() → xterm.js
    ///   xterm.js onData → UserInput event → ILiveConsoleService.WriteRawInputAsync()
    ///   ResizeObserver → proposed exact grid → TerminalResized event → ILiveConsoleService.ResizeConsole() → xterm.js resize commit
    /// </summary>
    public partial class TerminalControl : System.Windows.Controls.UserControl
    {
        private const string ClipboardCopyOperation = "ClipboardCopy";
        private const string ClipboardPasteReadOperation = "ClipboardPasteRead";
        private const string XtermVersion = "5.3.0";
        private const string XtermWindowsPtyBackend = "conpty";

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
        // No Unicode-width addon is packaged; xterm's built-in Unicode v6 provider is used.
        private static string TerminalHtml => TerminalHtmlTemplate.Replace(
            "__PS7_XTERM_VERSION__",
            XtermVersion).Replace(
            "__PS7_WINDOWS_PTY_BACKEND__",
            XtermWindowsPtyBackend).Replace(
            "__PS7_WINDOWS_PTY_BUILD_NUMBER__",
            GetWindowsPtyBuildNumber());

        private const string TerminalHtmlTemplate = """
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
              background: var(--terminal-background, #000000);
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
              background: var(--terminal-background, #000000);
              outline: none;
            }
            .xterm { width: 100% !important; height: 100% !important; }
            .xterm-viewport { background: var(--terminal-background, #000000) !important; }
            .xterm-screen { background: var(--terminal-background, #000000) !important; }
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

              // The integrated console remains a traditional black terminal regardless
              // of the surrounding application theme. This also keeps PowerShell's
              // ANSI-coloured prompt and output readable when the shell is in Light mode.
              var traditionalTerminalTheme = {
                background: '#000000', foreground: '#F2F2F2',
                cursor: '#00FF00', cursorAccent: '#000000',
                selectionBackground: 'rgba(88,166,255,0.35)', selectionForeground: '#FFFFFF',
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
                Dark: traditionalTerminalTheme,
                Light: traditionalTerminalTheme,
                IseBlue: traditionalTerminalTheme
              };

              function applyTerminalTheme(name) {
                var theme = terminalThemes[name] || terminalThemes.Dark;
                document.documentElement.style.setProperty('--terminal-background', theme.background);
                if (termApi) term.options.theme = theme;
                post({
                  type: 'terminal_theme_applied',
                  theme: terminalThemes[name] ? name : 'Dark',
                  background: theme.background,
                  foreground: theme.foreground,
                  selectionBackground: theme.selectionBackground
                });
              }

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
                  theme: terminalThemes.Dark,
                  fontFamily:  "'Cascadia Code','Cascadia Mono',Consolas,'Courier New',monospace",
                  fontSize:     14,
                  lineHeight:   1.2,
                  scrollback:   10000,
                  cursorBlink:  true,
                  cursorStyle:  'block',
                  cursorInactiveStyle: 'outline',
                  cursorWidth:  2,
                  convertEol:   false,
                  allowTransparency: false,
                  minimumContrastRatio: 4.5,
                  screenReaderMode: true,
                  windowsPty: {
                    backend: '__PS7_WINDOWS_PTY_BACKEND__',
                    buildNumber: __PS7_WINDOWS_PTY_BUILD_NUMBER__
                  }
                });

                var fitAddon = new FitAddon.FitAddon();
                var webLinksAddon = new WebLinksAddon.WebLinksAddon();
                var terminalElement = document.getElementById('terminal');
                terminalElement.tabIndex = 0;
                terminalElement.setAttribute('aria-label', 'Interactive PowerShell terminal');
                term.loadAddon(fitAddon);
                term.loadAddon(webLinksAddon);
                term.open(terminalElement);

                var terminalState = function () {
                  var buffer = term.buffer && term.buffer.active ? term.buffer.active : null;
                  var canvas = terminalElement.querySelector('canvas');
                  return {
                    cols: term.cols,
                    rows: term.rows,
                    cursorX: buffer ? buffer.cursorX : 0,
                    cursorY: buffer ? buffer.cursorY : 0,
                    baseY: buffer ? buffer.baseY : 0,
                    viewportY: buffer ? buffer.viewportY : 0,
                    absoluteCursorY: buffer ? buffer.baseY + buffer.cursorY : 0,
                    bufferType: buffer && buffer.type ? buffer.type : 'unknown',
                    bufferLength: buffer && buffer.length ? buffer.length : 0,
                    scrollbackLength: buffer && buffer.length ? buffer.length : 0,
                    // Preserve the established diagnostic field while also
                    // exposing the clearer bounded extent name.
                    scrollback: term.options && Number.isSafeInteger(term.options.scrollback) ? term.options.scrollback : 0,
                    scrollbackExtent: term.options && Number.isSafeInteger(term.options.scrollback) ? term.options.scrollback : 0,
                    scrollPosition: buffer ? buffer.viewportY : 0,
                    atBottom: !!(buffer && buffer.viewportY >= buffer.baseY),
                    alternateBuffer: !!(buffer && buffer.type === 'alternate'),
                    selectionPresent: !!(term.hasSelection && term.hasSelection()),
                    rendererDisposed: false,
                    clientWidth: terminalElement.clientWidth,
                    clientHeight: terminalElement.clientHeight,
                    canvasWidth: canvas ? canvas.width : 0,
                    canvasHeight: canvas ? canvas.height : 0
                  };
                };

                // These helpers are referenced by the WebView message handler below
                // the initialization try/catch. Use function-scoped variables so
                // strict-mode block scoping cannot hide them from that handler.
                var scanMarkerRows = function () {
                  var buffer = term.buffer && term.buffer.active ? term.buffer.active : null;
                  if (!buffer || typeof buffer.getLine !== 'function') return [];
                  var viewportStart = Math.max(0, Number.isFinite(buffer.viewportY) ? buffer.viewportY : 0);
                  // Inspect only a bounded window around the active viewport. This
                  // captures completed fixture rows that moved just above/below the
                  // viewport during reflow without scanning the entire scrollback on
                  // every diagnostic snapshot.
                  var start = Math.max(0, viewportStart - 96);
                  var limit = Math.min(buffer.length || 0, viewportStart + Math.min((term.rows || 0) + 96, 192));
                  var rows = [];
                  for (var row = start; row < limit && rows.length < 32; row++) {
                    var line = buffer.getLine(row);
                    if (!line || typeof line.translateToString !== 'function') continue;
                    var text = line.translateToString(true) || '';
                    var match = null;
                    var markerDefinitions = [
                      { text: 'PHASEB_INTERRUPT_TICK', identity: 'e04d332b01f42572' },
                      { text: 'COMPLETION_TEST_START', identity: 'completion-start-7b3e' },
                      { text: 'COMPLETION_TEST_END', identity: 'completion-end-4a91' }
                    ];
                    for (var definitionIndex = 0; definitionIndex < markerDefinitions.length; definitionIndex++) {
                      var definition = markerDefinitions[definitionIndex];
                      var definitionIndexInRow = text.indexOf(definition.text);
                      if (definitionIndexInRow >= 0) {
                        match = {
                          markerText: definition.text,
                          markerIdentity: definition.identity,
                          markerStartColumn: definitionIndexInRow,
                          numberedSequence: null
                        };
                        break;
                      }
                    }
                    if (!match) {
                      var numberedMatch = text.match(/COMPLETION_TEST\s+(\d{1,3})/);
                      if (numberedMatch) {
                        match = {
                          markerText: 'COMPLETION_TEST',
                          markerIdentity: 'completion-numbered-c2c',
                          markerStartColumn: numberedMatch.index,
                          numberedSequence: Number(numberedMatch[1])
                        };
                      }
                    }
                    if (!match) {
                      var legacyMarker = 'PHASEB_INTERRUPT_TICK';
                      for (var partialLength = Math.min(legacyMarker.length - 1, text.length); partialLength >= 4; partialLength--) {
                        if (text.endsWith(legacyMarker.substring(0, partialLength)) || text.startsWith(legacyMarker.substring(legacyMarker.length - partialLength))) {
                          match = {
                            markerText: legacyMarker.substring(0, partialLength),
                            markerIdentity: 'e04d332b01f42572',
                            markerStartColumn: -1,
                            numberedSequence: null,
                            markerMatchKind: 'partial'
                          };
                          break;
                        }
                      }
                    }
                    if (match) {
                      rows.push({
                        markerIdentity: match.markerIdentity,
                        markerMatchKind: match.markerMatchKind || 'exact',
                        markerPrefixLength: match.markerText.length,
                        markerStartColumn: match.markerStartColumn,
                        markerEndColumn: match.markerStartColumn + match.markerText.length,
                        numberedSequence: Number.isSafeInteger(match.numberedSequence) ? match.numberedSequence : null,
                        absoluteRow: row,
                        viewportRelativeRow: row - viewportStart,
                        isWrapped: !!line.isWrapped,
                        contentOmitted: true
                      });
                    }
                  }
                  return rows;
                };

                var postScreenStateSnapshot = function (reason, msg, promptBoundary) {
                  try {
                    var state = terminalState();
                    state.type = 'xterm_screen_state_snapshot';
                    state.reason = reason || 'unknown';
                    state.rendererGeneration = msg && Number.isSafeInteger(msg.rendererGeneration) ? msg.rendererGeneration : 0;
                    state.terminalSessionGeneration = msg && Number.isSafeInteger(msg.generation) ? msg.generation : 0;
                    state.outputSequence = msg && Number.isSafeInteger(msg.sequence) ? msg.sequence : 0;
                    state.submissionId = msg && Number.isSafeInteger(msg.submissionId) ? msg.submissionId : 0;
                    state.promptBoundary = promptBoundary === true;
                    state.markerRows = scanMarkerRows();
                    // DeveloperDiagnostics flattens nested objects through ToString;
                    // retain a JSON form so marker identities and row coordinates
                    // survive the host boundary without retaining terminal text.
                    state.markerRowsJson = JSON.stringify(state.markerRows);
                    state.rendererIdentity = 'xterm-webview-active';
                    state.contentOmitted = true;
                    post(state);
                  } catch (snapshotErr) {
                    post({ type: 'xterm_screen_state_snapshot_error', reason: reason || 'unknown', message: String(snapshotErr), contentOmitted: true });
                  }
                };

                var captureViewportAnchor = function () {
                  var buffer = term.buffer && term.buffer.active ? term.buffer.active : null;
                  if (!buffer) return null;
                  var baseY = Number.isFinite(buffer.baseY) ? buffer.baseY : 0;
                  var viewportY = Number.isFinite(buffer.viewportY) ? buffer.viewportY : baseY;
                  return {
                    atBottom: viewportY >= baseY,
                    distanceFromBottom: Math.max(0, baseY - viewportY),
                    viewportY: viewportY,
                    baseY: baseY
                  };
                };

                var restoreViewportAnchor = function (anchor, source) {
                  if (!anchor || !term.buffer || !term.buffer.active) return;
                  var buffer = term.buffer.active;
                  var targetViewportY = anchor.atBottom
                    ? buffer.baseY
                    : Math.max(0, buffer.baseY - anchor.distanceFromBottom);
                  if (typeof term.scrollToLine === 'function') {
                    term.scrollToLine(targetViewportY);
                  } else if (typeof term.scrollLines === 'function') {
                    term.scrollLines(targetViewportY - buffer.viewportY);
                  }
                  postTerminalState('Xterm.ViewportRestored', source, {
                    viewportAnchorAtBottom: anchor.atBottom,
                    viewportAnchorDistanceFromBottom: anchor.distanceFromBottom,
                    viewportRestoreTarget: targetViewportY
                  });
                };

                var postTerminalState = function (stage, source, extra) {
                  var state = terminalState();
                  state.type = 'xterm_resize_trace';
                  state.stage = stage;
                  state.source = source || 'unknown';
                  if (extra) {
                    Object.keys(extra).forEach(function (key) { state[key] = extra[key]; });
                  }
                  post(state);
                };

                var tryPostTerminalState = function (stage, source, extra) {
                  try {
                    if (typeof postTerminalState === 'function') {
                      postTerminalState(stage, source, extra);
                    } else {
                      post({ type: 'xterm_resize_trace_error', source: source || 'unknown', stage: stage, message: 'postTerminalState unavailable' });
                    }
                  } catch (traceErr) {
                    post({ type: 'xterm_resize_trace_error', source: source || 'unknown', stage: stage, message: String(traceErr) });
                  }
                };

                var createControlSummary = function () {
                  return {
                    carriageReturnCount: 0,
                    lineFeedCount: 0,
                    carriageReturnLineFeedPairCount: 0,
                    escapeCount: 0,
                    csiCount: 0,
                    csiCursorUpCount: 0,
                    csiCursorDownCount: 0,
                    csiCursorForwardCount: 0,
                    csiCursorBackwardCount: 0,
                    csiCursorPositionCount: 0,
                    csiEraseLineCount: 0,
                    csiEraseDisplayCount: 0,
                    csiSaveCursorCount: 0,
                    csiRestoreCursorCount: 0,
                    csiInsertLineCount: 0,
                    csiDeleteLineCount: 0,
                    csiScrollUpCount: 0,
                    csiScrollDownCount: 0,
                    csiSgrCount: 0,
                    csiOtherCount: 0,
                    oscCount: 0,
                    otherEscapeCount: 0,
                    otherControlCount: 0,
                    printableCharacterCount: 0,
                    csiCommands: [],
                    cursorDestinations: []
                  };
                };

                var appendControlSummaryPart = function (parts, name, value) {
                  if (value > 0) parts.push(name + '=' + value);
                };

                var formatControlSummary = function (summary) {
                  var parts = [];
                  appendControlSummaryPart(parts, 'CR', summary.carriageReturnCount);
                  appendControlSummaryPart(parts, 'LF', summary.lineFeedCount);
                  appendControlSummaryPart(parts, 'CRLF', summary.carriageReturnLineFeedPairCount);
                  appendControlSummaryPart(parts, 'ESC', summary.escapeCount);
                  appendControlSummaryPart(parts, 'CSI', summary.csiCount);
                  appendControlSummaryPart(parts, 'CSI_CursorUp', summary.csiCursorUpCount);
                  appendControlSummaryPart(parts, 'CSI_CursorDown', summary.csiCursorDownCount);
                  appendControlSummaryPart(parts, 'CSI_CursorForward', summary.csiCursorForwardCount);
                  appendControlSummaryPart(parts, 'CSI_CursorBackward', summary.csiCursorBackwardCount);
                  appendControlSummaryPart(parts, 'CSI_CursorPosition', summary.csiCursorPositionCount);
                  appendControlSummaryPart(parts, 'CSI_EraseLine', summary.csiEraseLineCount);
                  appendControlSummaryPart(parts, 'CSI_EraseDisplay', summary.csiEraseDisplayCount);
                  appendControlSummaryPart(parts, 'CSI_SaveCursor', summary.csiSaveCursorCount);
                  appendControlSummaryPart(parts, 'CSI_RestoreCursor', summary.csiRestoreCursorCount);
                  appendControlSummaryPart(parts, 'CSI_InsertLine', summary.csiInsertLineCount);
                  appendControlSummaryPart(parts, 'CSI_DeleteLine', summary.csiDeleteLineCount);
                  appendControlSummaryPart(parts, 'CSI_ScrollUp', summary.csiScrollUpCount);
                  appendControlSummaryPart(parts, 'CSI_ScrollDown', summary.csiScrollDownCount);
                  appendControlSummaryPart(parts, 'SGR', summary.csiSgrCount);
                  appendControlSummaryPart(parts, 'CSI_Other', summary.csiOtherCount);
                  appendControlSummaryPart(parts, 'OSC', summary.oscCount);
                  appendControlSummaryPart(parts, 'ESC_Other', summary.otherEscapeCount);
                  appendControlSummaryPart(parts, 'OtherControl', summary.otherControlCount);
                  appendControlSummaryPart(parts, 'Printable', summary.printableCharacterCount);
                  return parts.length === 0 ? '(none)' : parts.join(' ');
                };

                var findCsiEnd = function (data, start) {
                  for (var index = start; index < data.length; index++) {
                    var code = data.charCodeAt(index);
                    if (code >= 0x40 && code <= 0x7e) return index;
                  }

                  return -1;
                };

                var findOscEnd = function (data, start) {
                  for (var index = start; index < data.length; index++) {
                    if (data.charAt(index) === '\x07') return index;
                    if (data.charAt(index) === '\x1b' && index + 1 < data.length && data.charAt(index + 1) === '\\') return index + 1;
                  }

                  return data.length - 1;
                };

                var parseCsiParameters = function (data, start, end) {
                  var raw = data.substring(start + 2, end);
                  var sanitized = raw.replace(/[? >!]/g, '');
                  var numericParameters = sanitized.length === 0
                    ? []
                    : sanitized.split(';').map(function (value) {
                        var parsed = Number(value);
                        return Number.isFinite(parsed) ? parsed : null;
                      });
                  return { raw: raw.substring(0, 64), numericParameters: numericParameters.slice(0, 8) };
                };

                var classifyTerminalControls = function (data) {
                  var summary = createControlSummary();
                  if (!data) {
                    summary.summaryText = formatControlSummary(summary);
                    return summary;
                  }

                  for (var index = 0; index < data.length; index++) {
                    var character = data.charAt(index);
                    if (character === '\r') {
                      summary.carriageReturnCount++;
                      if (index + 1 < data.length && data.charAt(index + 1) === '\n') summary.carriageReturnLineFeedPairCount++;
                      continue;
                    }

                    if (character === '\n') {
                      summary.lineFeedCount++;
                      continue;
                    }

                    if (character !== '\x1b') {
                      var code = data.charCodeAt(index);
                      if ((code >= 0 && code < 32) || code === 127) {
                        summary.otherControlCount++;
                      } else {
                        summary.printableCharacterCount++;
                      }
                      continue;
                    }

                    summary.escapeCount++;
                    if (index + 1 >= data.length) {
                      summary.otherEscapeCount++;
                      continue;
                    }

                    var next = data.charAt(index + 1);
                    if (next === '[') {
                      var csiEnd = findCsiEnd(data, index + 2);
                      if (csiEnd < 0) {
                        summary.otherEscapeCount++;
                        continue;
                      }

                      summary.csiCount++;
                      var csiParameters = parseCsiParameters(data, index, csiEnd);
                      if (summary.csiCommands.length < 32) {
                        summary.csiCommands.push({
                          command: data.charAt(csiEnd),
                          parameters: csiParameters.raw,
                          numericParameters: csiParameters.numericParameters
                        });
                      }
                      switch (data.charAt(csiEnd)) {
                        case 'A': summary.csiCursorUpCount++; break;
                        case 'B': summary.csiCursorDownCount++; break;
                        case 'C': summary.csiCursorForwardCount++; break;
                        case 'D': summary.csiCursorBackwardCount++; break;
                        case 'H':
                        case 'f':
                        case 'G':
                        case 'd':
                          summary.csiCursorPositionCount++;
                          break;
                        case 'J': summary.csiEraseDisplayCount++; break;
                        case 'K': summary.csiEraseLineCount++; break;
                        case 's': summary.csiSaveCursorCount++; break;
                        case 'u': summary.csiRestoreCursorCount++; break;
                        case 'L': summary.csiInsertLineCount++; break;
                        case 'M': summary.csiDeleteLineCount++; break;
                        case 'S': summary.csiScrollUpCount++; break;
                        case 'T': summary.csiScrollDownCount++; break;
                        case 'm': summary.csiSgrCount++; break;
                        default: summary.csiOtherCount++; break;
                      }

                      index = csiEnd;
                      continue;
                    }

                    if (next === ']') {
                      summary.oscCount++;
                      index = findOscEnd(data, index + 2);
                      continue;
                    }

                    switch (next) {
                      case '7':
                        summary.csiSaveCursorCount++;
                        break;
                      case '8':
                        summary.csiRestoreCursorCount++;
                        break;
                      default:
                        summary.otherEscapeCount++;
                        break;
                    }

                    index++;
                  }

                  summary.cursorDestinations = summary.csiCommands.map(function (command) {
                    var parameters = command.numericParameters || [];
                    var first = parameters.length > 0 && parameters[0] !== null && parameters[0] > 0 ? parameters[0] : 1;
                    var second = parameters.length > 1 && parameters[1] !== null && parameters[1] > 0 ? parameters[1] : 1;
                    if (command.command === 'H' || command.command === 'f') {
                      return { command: command.command, row: first, column: second };
                    }
                    if (command.command === 'G') {
                      return { command: command.command, row: null, column: first };
                    }
                    if (command.command === 'd') {
                      return { command: command.command, row: first, column: null };
                    }
                    return { command: command.command, row: null, column: null };
                  });
                  summary.summaryText = formatControlSummary(summary);
                  return summary;
                };

                var postOutputCursorTraceError = function (stage, message) {
                  post({
                    type: 'xterm_output_cursor_trace_error',
                    source: 'renderer.outputWrite',
                    stage: stage,
                    message: String(message),
                    contentOmitted: true
                  });
                };

                var tryPostOutputCursorTraceError = function (stage, message) {
                  try {
                    if (typeof postOutputCursorTraceError === 'function') {
                      postOutputCursorTraceError(stage, message);
                    } else {
                      post({
                        type: 'xterm_output_cursor_trace_error',
                        source: 'renderer.outputWrite',
                        stage: stage,
                        message: String(message),
                        contentOmitted: true
                      });
                    }
                  } catch (traceErrorReportErr) {
                  }
                };

                var postOutputCursorTrace = function (stage, msg, beforeState, classification) {
                  var state = terminalState();
                  var summary = classification || createControlSummary();
                  post({
                    type: 'xterm_output_cursor_trace',
                    stage: stage,
                    source: 'renderer.outputWrite',
                    rendererGeneration: Number.isSafeInteger(msg.rendererGeneration) ? msg.rendererGeneration : 0,
                    terminalSessionGeneration: Number.isSafeInteger(msg.generation) ? msg.generation : 0,
                    outputSequence: Number.isSafeInteger(msg.sequence) ? msg.sequence : 0,
                    submissionId: Number.isSafeInteger(msg.submissionId) ? msg.submissionId : 0,
                    resizeAdjacent: msg.resizeAdjacent === true,
                    resizeGeneration: Number.isSafeInteger(msg.resizeGeneration) ? msg.resizeGeneration : 0,
                    resizeElapsedMilliseconds: typeof msg.resizeElapsedMilliseconds === 'number' ? msg.resizeElapsedMilliseconds : -1,
                    outputCharacterLength: Number.isSafeInteger(msg.outputCharacterLength) ? msg.outputCharacterLength : 0,
                    hostControlSummary: typeof msg.hostControlSummary === 'string' ? msg.hostControlSummary : '',
                    classificationSummary: summary.summaryText || formatControlSummary(summary),
                    carriageReturnCount: summary.carriageReturnCount,
                    lineFeedCount: summary.lineFeedCount,
                    carriageReturnLineFeedPairCount: summary.carriageReturnLineFeedPairCount,
                    escapeCount: summary.escapeCount,
                    csiCount: summary.csiCount,
                    csiCursorUpCount: summary.csiCursorUpCount,
                    csiCursorDownCount: summary.csiCursorDownCount,
                    csiCursorForwardCount: summary.csiCursorForwardCount,
                    csiCursorBackwardCount: summary.csiCursorBackwardCount,
                    csiCursorPositionCount: summary.csiCursorPositionCount,
                    csiEraseLineCount: summary.csiEraseLineCount,
                    csiEraseDisplayCount: summary.csiEraseDisplayCount,
                    csiSaveCursorCount: summary.csiSaveCursorCount,
                    csiRestoreCursorCount: summary.csiRestoreCursorCount,
                    csiInsertLineCount: summary.csiInsertLineCount,
                    csiDeleteLineCount: summary.csiDeleteLineCount,
                    csiScrollUpCount: summary.csiScrollUpCount,
                    csiScrollDownCount: summary.csiScrollDownCount,
                    csiSgrCount: summary.csiSgrCount,
                    csiOtherCount: summary.csiOtherCount,
                    oscCount: summary.oscCount,
                    otherEscapeCount: summary.otherEscapeCount,
                    otherControlCount: summary.otherControlCount,
                    printableCharacterCount: summary.printableCharacterCount,
                    csiCommands: summary.csiCommands,
                    cursorDestinations: summary.cursorDestinations,
                    cols: state.cols,
                    rows: state.rows,
                    cursorX: state.cursorX,
                    cursorY: state.cursorY,
                    baseY: state.baseY,
                    viewportY: state.viewportY,
                    absoluteCursorY: state.absoluteCursorY,
                    scrollbackLength: state.scrollbackLength,
                    beforeCursorX: beforeState ? beforeState.cursorX : null,
                    beforeCursorY: beforeState ? beforeState.cursorY : null,
                    beforeBaseY: beforeState ? beforeState.baseY : null,
                    beforeViewportY: beforeState ? beforeState.viewportY : null,
                    beforeAbsoluteCursorY: beforeState ? beforeState.absoluteCursorY : null,
                    deltaCursorX: beforeState ? state.cursorX - beforeState.cursorX : null,
                    deltaCursorY: beforeState ? state.cursorY - beforeState.cursorY : null,
                    deltaBaseY: beforeState ? state.baseY - beforeState.baseY : null,
                    deltaViewportY: beforeState ? state.viewportY - beforeState.viewportY : null,
                    deltaAbsoluteCursorY: beforeState ? state.absoluteCursorY - beforeState.absoluteCursorY : null,
                    contentOmitted: true
                  });
                  return state;
                };

                function reportLayout(source) {
                  var state = terminalState();
                  state.type = 'layout';
                  state.source = source;
                  post(state);
                }

                function fitTerminal(source) {
                  try {
                    postTerminalState('Xterm.BeforeFit', source, { fitCommitted: true });
                    fitAddon.fit();
                    postTerminalState('Xterm.AfterFit', source, { fitCommitted: true });
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
                  post({
                    type: 'terminal_compatibility',
                    screenReaderMode: true,
                    unicodeWidthProvider: 'built-in-v6',
                    binaryInputBridge: false,
                    mousePasteGesture: 'shift-right-click',
                    leaveTerminalShortcut: 'ctrl-shift-f6',
                    xtermVersion: '__PS7_XTERM_VERSION__',
                    windowsPtyBackend: '__PS7_WINDOWS_PTY_BACKEND__',
                    windowsPtyBuildNumber: __PS7_WINDOWS_PTY_BUILD_NUMBER__
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

                }

                // Re-focus on mouse activation so the helper textarea becomes the
                // active input element after clicking away to the editor.
                terminalElement.addEventListener('mousedown', function() {
                  post({ type: 'activated', source: 'terminal.mousedown' });
                  window.setTimeout(function() {
                    focusTerminal('terminal.mousedown');
                  }, 0);
                });

                terminalElement.addEventListener('click', function() {
                  window.setTimeout(function() {
                    focusTerminal('terminal.click');
                  }, 0);
                });

                // Re-focus when the WebView2 host window gains focus (e.g. Alt+Tab back).
                window.addEventListener('focus', function() {
                  focusTerminal('window.focus');
                });

                terminalElement.addEventListener('focusin', function() {
                  reportFocus('focus', 'terminal.focusin');
                });

                terminalElement.addEventListener('focusout', function() {
                  reportFocus('blur', 'terminal.focusout');
                });

                term.onData(function (data) { post({ type: 'input', data: data }); });
                var lastRequestedCols = 0;
                var lastRequestedRows = 0;
                var pendingResizeCommit = null;
                var pendingResizeObserverAnchor = null;
                var requestedResizeAnchor = null;
                term.onResize(function (e) {
                  if (!e || e.cols <= 0 || e.rows <= 0) return;
                  var commit = pendingResizeCommit;
                  postTerminalState('Xterm.OnResize', commit ? 'host.resizeCommit' : 'xterm.internal', {
                    reportedCols: e.cols,
                    reportedRows: e.rows,
                    rendererGeneration: commit ? commit.rendererGeneration : null,
                    resizeGeneration: commit ? commit.resizeGeneration : null,
                    hostResizeCommit: !!commit
                  });
                });

                // ── Contextual terminal key handling ─────────────────────────────
                // Preserve PSReadLine's Ctrl+A, Ctrl+F, and Ctrl+H bindings. Copy uses
                // Ctrl+C only with a selection; Ctrl+Shift+A is terminal select-all.
                // Ctrl+Shift+F6 is the documented accessibility override to leave xterm.
                term.attachCustomKeyEventHandler(function(e) {
                  if (e.type !== 'keydown') return true;

                  if (e.ctrlKey && e.shiftKey && !e.altKey && !e.metaKey && e.key === 'F6') {
                    post({ type: 'app_shortcut', command: 'leave_terminal' });
                    return false;
                  }

                  if (e.ctrlKey && !e.altKey && !e.metaKey && e.key && e.key.toLowerCase() === 'c') {
                    if (term.hasSelection()) {
                      post({ type: 'copy', text: term.getSelection() });
                      term.clearSelection();
                      return false;
                    }
                    return true; // no selection → pass through as \x03 (SIGINT)
                  }
                  if (e.ctrlKey && !e.altKey && !e.metaKey && e.key === 'Insert' && term.hasSelection()) {
                    post({ type: 'copy', text: term.getSelection() });
                    term.clearSelection();
                    return false;
                  }
                  if (e.ctrlKey && e.shiftKey && !e.altKey && !e.metaKey && e.key && e.key.toLowerCase() === 'a') {
                    term.selectAll();
                    return false;
                  }
                  return true;
                });

                // Do not turn right-click into paste: terminal mouse protocols need
                // unmodified pointer events. Shift+right-click is the explicit paste path.
                terminalElement.addEventListener('contextmenu', function(e) {
                  if (!e.shiftKey) return;
                  e.preventDefault();
                  post({ type: 'paste_request' });
                });

                var resizeFramePending = false;
                var resizeRequested = false;
                var scheduleResizeFit = function () {
                  // Capture the user's logical viewport position at the resize
                  // notification boundary. Browser/xterm layout can transiently
                  // reflow the active buffer before the host resize commit arrives;
                  // capturing at commit time can then mistake that transient state
                  // for an intentional scroll position.
                  pendingResizeObserverAnchor = captureViewportAnchor();
                  resizeRequested = true;
                  if (resizeFramePending) return;
                  resizeFramePending = true;
                  window.requestAnimationFrame(function() {
                    resizeFramePending = false;
                    if (!resizeRequested) return;
                    resizeRequested = false;
                    if (terminalElement.clientWidth <= 0 || terminalElement.clientHeight <= 0) return;
                    postTerminalState('ResizeObserver.Observed', 'resizeObserver');
                    postTerminalState('Xterm.BeforeFit', 'resizeObserver', { fitCommitted: false });
                    var proposed = fitAddon.proposeDimensions ? fitAddon.proposeDimensions() : null;
                    if (!proposed || proposed.cols <= 0 || proposed.rows <= 0) {
                      post({ type: 'xterm_fit_error', source: 'resizeObserver.proposeDimensions', message: 'fit-addon did not return usable dimensions' });
                      return;
                    }
                    postTerminalState('Xterm.AfterFit', 'resizeObserver', {
                      fitCommitted: false,
                      proposedCols: proposed.cols,
                      proposedRows: proposed.rows
                    });
                    if (proposed.cols === lastRequestedCols && proposed.rows === lastRequestedRows) return;
                    requestedResizeAnchor = {
                      cols: proposed.cols,
                      rows: proposed.rows,
                      anchor: pendingResizeObserverAnchor
                    };
                    pendingResizeObserverAnchor = null;
                    lastRequestedCols = proposed.cols;
                    lastRequestedRows = proposed.rows;
                    var state = terminalState();
                    state.type = 'resize_request';
                    state.source = 'resizeObserver';
                    state.proposedCols = proposed.cols;
                    state.proposedRows = proposed.rows;
                    post(state);
                  });
                };
                var ro = new ResizeObserver(function () {
                  scheduleResizeFit();
                });
                ro.observe(terminalElement);

                termApi = {
                  write: function (d, callback) {
                    try {
                      term.write(d, callback);
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
                  focus: function ()  { focusTerminal('termApi.focus'); },
                  normalizeInteractiveState: function () {
                    try {
                      // A completed or interrupted native/GUI command can leave xterm's
                      // cursor layer stale until the next input repaint. Restore the
                      // normal visible cursor and repaint without moving keyboard focus
                      // away from the editor.
                      term.write('\x1b[0m\x1b[?25h', function () {
                        try { term.refresh(0, Math.max(0, term.rows - 1)); } catch (ignoreRefresh) { }
                        post({ type: 'interactive_state_normalized' });
                      });
                    } catch (normalizeErr) {
                      post({ type: 'xterm_normalize_error', message: String(normalizeErr) });
                    }
                  },
                  observePromptBoundary: function (msg) {
                    postScreenStateSnapshot('prompt-boundary', msg || {}, true);
                  }
                };
                window.__ps7ScriptDeskFocusTerminal = function () {
                  var helperTextarea = terminalElement.querySelector('textarea.xterm-helper-textarea');
                  if (!termApi || !helperTextarea) {
                    return {
                      terminalAvailable: !!termApi,
                      inputActive: false,
                      activeElement: document.activeElement ? document.activeElement.tagName : '',
                      failureReason: 'xterm-input-unavailable'
                    };
                  }

                  focusTerminal('host.executeScript.focus');
                  var activeElement = document.activeElement;
                  var activeElementClass = activeElement && activeElement.className ? String(activeElement.className) : '';
                  return {
                    terminalAvailable: true,
                    inputActive: activeElement === helperTextarea,
                    activeElement: activeElementClass ? activeElement.tagName + '.' + activeElementClass : activeElement.tagName,
                    failureReason: activeElement === helperTextarea ? '' : 'xterm-input-not-active'
                  };
                };
                applyTerminalTheme('Dark');
                initializeTerminalHost();
              } catch (initErr) {
                post({ type: 'xterm_init_error', message: String(initErr) });
              }

              // ── Receive messages from C# ─────────────────────────────────────────
              window.chrome.webview.addEventListener('message', function (e) {
                try {
                  var msg = (typeof e.data === 'string') ? JSON.parse(e.data) : e.data;
                  if (!termApi || !msg || !msg.type) return;
                  if      (msg.type === 'output_b64' && typeof msg.data === 'string') {
                    const generation = Number.isSafeInteger(msg.generation) ? msg.generation : null;
                    const sequence = Number.isSafeInteger(msg.sequence) ? msg.sequence : null;
                    const decodedOutput = decodeBase64Utf8(msg.data);
                    const traceOutputCursor = msg.resizeAdjacent === true;
                    var outputControlSummary = null;
                    var beforeOutputWriteState = null;
                    if (traceOutputCursor) {
                      try {
                        outputControlSummary = classifyTerminalControls(decodedOutput);
                        tryPostTerminalState('Xterm.RedrawWriteBegin', 'renderer.outputWrite', {
                          outputSequence: Number.isSafeInteger(msg.sequence) ? msg.sequence : 0,
                          submissionId: Number.isSafeInteger(msg.submissionId) ? msg.submissionId : 0,
                          resizeGeneration: Number.isSafeInteger(msg.resizeGeneration) ? msg.resizeGeneration : 0,
                          outputCharacterLength: decodedOutput.length
                        });
                        beforeOutputWriteState = postOutputCursorTrace('Xterm.OutputBeforeWrite', msg, null, outputControlSummary);
                      } catch (traceBeforeErr) {
                        tryPostOutputCursorTraceError('Xterm.OutputBeforeWrite', traceBeforeErr);
                      }
                    }

                    termApi.write(decodedOutput, () => {
                      try {
                        if (traceOutputCursor) {
                          try {
                            if (!outputControlSummary) outputControlSummary = classifyTerminalControls(decodedOutput);
                            postOutputCursorTrace('Xterm.OutputAfterWrite', msg, beforeOutputWriteState, outputControlSummary);
                            tryPostTerminalState('Xterm.RedrawWriteCallbackComplete', 'renderer.outputWrite', {
                              outputSequence: Number.isSafeInteger(msg.sequence) ? msg.sequence : 0,
                              submissionId: Number.isSafeInteger(msg.submissionId) ? msg.submissionId : 0,
                              resizeGeneration: Number.isSafeInteger(msg.resizeGeneration) ? msg.resizeGeneration : 0,
                              outputCharacterLength: decodedOutput.length
                            });
                          } catch (traceAfterErr) {
                            tryPostOutputCursorTraceError('Xterm.OutputAfterWrite', traceAfterErr);
                          }
                        }
                        if (msg.markerCandidate === true) postScreenStateSnapshot('marker-write-after', msg, false);
                        if (msg.resizeAdjacent === true) postScreenStateSnapshot('resize-adjacent-output-after', msg, false);
                      } finally {
                        if (generation !== null && sequence !== null) post({ type: 'output_ack', rendererGeneration: Number.isSafeInteger(msg.rendererGeneration) ? msg.rendererGeneration : 0, generation: generation, sequence: sequence });
                      }
                    });
                  }
                  else if (msg.type === 'output') { termApi.write(msg.data || ''); }
                  else if (msg.type === 'prompt_boundary') { postScreenStateSnapshot('prompt-boundary', msg, true); }
                  else if (msg.type === 'clear')  { termApi.clear(); }
                  else if (msg.type === 'resize_commit') {
                    var commitCols = Number.isSafeInteger(msg.cols) ? msg.cols : 0;
                    var commitRows = Number.isSafeInteger(msg.rows) ? msg.rows : 0;
                    if (commitCols > 0 && commitRows > 0) {
                      var commit = {
                        rendererGeneration: Number.isSafeInteger(msg.rendererGeneration) ? msg.rendererGeneration : null,
                        terminalSessionGeneration: Number.isSafeInteger(msg.terminalSessionGeneration) ? msg.terminalSessionGeneration : null,
                        resizeGeneration: Number.isSafeInteger(msg.resizeGeneration) ? msg.resizeGeneration : null
                      };
                      tryPostTerminalState('Xterm.BeforeHostResizeCommit', 'host.resizeCommit', {
                        rendererGeneration: commit.rendererGeneration,
                        resizeGeneration: commit.resizeGeneration,
                        committedCols: commitCols,
                        committedRows: commitRows
                      });
                      tryPostTerminalState('Xterm.ResizeCommitReceived', 'host.resizeCommit', {
                        committedCols: commitCols,
                        committedRows: commitRows,
                        resizeGeneration: commit.resizeGeneration
                      });
                      postScreenStateSnapshot('before-host-resize-commit', msg, false);
                      var viewportAnchor = requestedResizeAnchor &&
                        requestedResizeAnchor.cols === commitCols &&
                        requestedResizeAnchor.rows === commitRows
                        ? requestedResizeAnchor.anchor
                        : captureViewportAnchor();
                      requestedResizeAnchor = null;
                      pendingResizeCommit = commit;
                      try {
                        term.resize(commitCols, commitRows);
                        restoreViewportAnchor(viewportAnchor, 'host.resizeCommit');
                        tryPostTerminalState('Xterm.AfterTermResize', 'host.resizeCommit', {
                          committedCols: commitCols,
                          committedRows: commitRows,
                          resizeGeneration: commit.resizeGeneration
                        });
                        postScreenStateSnapshot('after-host-resize-commit', msg, false);
                        var acknowledgedState = terminalState();
                        post({
                          type: 'resize_commit_ack',
                          rendererGeneration: commit.rendererGeneration,
                          terminalSessionGeneration: commit.terminalSessionGeneration,
                          resizeGeneration: commit.resizeGeneration,
                          actualCols: acknowledgedState.cols,
                          actualRows: acknowledgedState.rows,
                          cursorX: acknowledgedState.cursorX,
                          cursorY: acknowledgedState.cursorY,
                          baseY: acknowledgedState.baseY,
                          viewportY: acknowledgedState.viewportY
                        });
                        tryPostTerminalState('Xterm.ResizeCommitAckPosted', 'host.resizeCommit', {
                          committedCols: acknowledgedState.cols,
                          committedRows: acknowledgedState.rows,
                          resizeGeneration: commit.resizeGeneration
                        });
                      } finally {
                        pendingResizeCommit = null;
                      }
                      lastRequestedCols = commitCols;
                      lastRequestedRows = commitRows;
                      tryPostTerminalState('Xterm.AfterHostResizeCommit', 'host.resizeCommit', {
                        rendererGeneration: commit.rendererGeneration,
                        resizeGeneration: commit.resizeGeneration,
                        committedCols: commitCols,
                        committedRows: commitRows
                      });
                    }
                  }
                  else if (msg.type === 'focus')  {
                    termApi.focus();
                    reportFocus('focus', 'host.message.focus');
                  }
                  else if (msg.type === 'normalize_interactive_state') {
                    termApi.normalizeInteractiveState();
                  }
                  else if (msg.type === 'paste' && typeof msg.data === 'string') { termApi.paste(msg.data); }
                  else if (msg.type === 'settheme' && msg.data && terminalThemes[msg.data]) {
                    applyTerminalTheme(msg.data);
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

        private static readonly TimeSpan ResizeAdjacentOutputTraceWindow = TimeSpan.FromSeconds(1);

        private readonly TerminalOutputFlowController _outputFlowController = new();
        private readonly TerminalRendererRecoveryStateMachine _rendererRecovery = new();
        private readonly TerminalPersistentOutputHistory _persistentOutputHistory = new();
        private readonly TerminalResizeOutputBarrier _resizeOutputBarrier = new();
        private readonly TerminalProtocolOutputFilter _terminalProtocolOutputFilter = new();
        private readonly TerminalResizePolicy _terminalResizePolicy = new();
        private readonly DispatcherTimer _resizeOutputBarrierTimer;
        private volatile bool          _isReady;
        private bool                   _webView2Available = true;
        private bool                   _firstOutputQueuedLogged;
        private bool                   _firstOutputPostedLogged;
        private bool                   _firstInputReceivedLogged;
        private bool                   _firstInputObservedForDiagnostics;
        private bool                   _firstOutputAfterInputLogged;
        private bool                   _rendererReadyReplayPending;
        private int                    _inputInfoLogCount;
        private bool                   _clipboardCopyFailureEpisodeActive;
        private bool                   _clipboardPasteReadFailureEpisodeActive;
        private long                   _droppedOutputCharacters;
        private long                   _reportedDroppedOutputCharacters;
        private readonly object _rendererSyncRoot = new();
        private readonly object _resizeOutputIntegrationSyncRoot = new();
        private WebView2? _webView;
        private TerminalWebView2LifecyclePolicy _webViewLifecycle = new();
        private CoreWebView2?          _subscribedCoreWebView2;
        private bool                   _webViewDetachedFromLayout;
        private int                    _rendererInstanceGeneration;
        private long                   _nextOutputSubmissionId;
        private DateTimeOffset         _lastResizeCommitAcknowledgedUtc = DateTimeOffset.MinValue;
        private long                   _lastResizeCommitAcknowledgedGeneration;
        private int                    _lastResizeCommitAcknowledgedColumns;
        private int                    _lastResizeCommitAcknowledgedRows;
        private TerminalResizeDecision? _pendingResizeDecision;

        private TerminalWebView2FallbackState _fallbackState;

        private readonly record struct RendererOutputDiagnostics(
            bool ResizeAdjacent,
            long ResizeGeneration,
            double ResizeElapsedMilliseconds,
            int ResizeColumns,
            int ResizeRows,
            string ControlSummary);

        // ── Events ────────────────────────────────────────────────────────────────

        /// <summary>Fires (on the UI thread) the first time xterm.js signals ready and the output queue is flushed.</summary>
        public event Action? TerminalReady;

        /// <summary>Fires when the user types in xterm.js (keystroke data to send to ConPTY).</summary>
        public event Action<string>? UserInput;

        /// <summary>Fires when xterm.js reports a resize (cols, rows to send to ConPTY).</summary>
        public event Action<int, int>? TerminalResized;

        /// <summary>Fires when the terminal is explicitly activated by click/focus/input.</summary>
        public event Action<string>? TerminalActivated;

        /// <summary>Fires when the current WebView2 renderer is permanently retired.</summary>
        public event Action<string>? TerminalRendererUnavailable;

        /// <summary>Fires when xterm.js captures a keyboard gesture that belongs to the host app, such as Ctrl+F or Ctrl+H.</summary>
        public event Action<string>? AppShortcutRequested;

        /// <summary>Current WebView2/xterm renderer identity for diagnostics and UAT correlation.</summary>
        public int RendererGeneration => Volatile.Read(ref _rendererInstanceGeneration);

        /// <summary>Current PowerShell terminal session generation accepted by the output flow.</summary>
        public int? ActiveSessionGeneration => _outputFlowController.ActiveGeneration;

        /// <summary>Latest resize generation evaluated by the exact-grid resize policy.</summary>
        public long ResizeGeneration => _terminalResizePolicy.ResizeGeneration;

        // ── Constructor ───────────────────────────────────────────────────────────

        public TerminalControl()
        {
            InitializeComponent();
            TerminalStartupTrace.Write("TERMINAL_CONTROL_CONSTRUCTED", $"controlId={GetHashCode():X8}");
            _resizeOutputBarrierTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _resizeOutputBarrierTimer.Tick += OnResizeOutputBarrierTimerTick;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        // ── Initialization ────────────────────────────────────────────────────────

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            TerminalStartupTrace.Write("RENDERER_INIT_START", $"controlId={GetHashCode():X8}; uiThread={Dispatcher.CheckAccess()}");
            StartupLifecycleTrace.Write("TerminalControl.OnLoaded", "ENTER");
            if (!TryCreateRenderer(out var renderer, out var lifecycle))
            {
                TerminalStartupTrace.Write("RENDERER_INIT_SKIPPED", "reason=renderer-already-active-or-creation-failed");
                StartupLifecycleTrace.Write("TerminalControl.OnLoaded", "SKIP", "renderer already active or creation failed");
                return;
            }

            await InitializeRendererAsync(renderer, lifecycle).ConfigureAwait(true);
            StartupLifecycleTrace.Write("TerminalControl.OnLoaded", "EXIT");
        }

        public void ResetRendererForRetry()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ResetRendererForRetry), DispatcherPriority.Send);
                return;
            }

            if (!IsLoaded || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (!TryCreateRenderer(out var renderer, out var lifecycle))
            {
                return;
            }

            _ = InitializeRendererAsync(renderer, lifecycle);
        }

        private bool TryCreateRenderer(
            out WebView2 renderer,
            out TerminalWebView2LifecyclePolicy lifecycle)
        {
            renderer = null!;
            lifecycle = null!;

            lock (_rendererSyncRoot)
            {
                if (_webView is not null && !_webViewLifecycle.IsRetired)
                {
                    return false;
                }

                renderer = new WebView2
                {
                    Focusable = true,
                    ToolTip = "Interactive PowerShell terminal. Ctrl+Shift+F6 moves focus back to the editor."
                };
                System.Windows.Automation.AutomationProperties.SetName(renderer, "Interactive PowerShell terminal");
                System.Windows.Automation.AutomationProperties.SetHelpText(renderer, "Uses xterm screen-reader support when provided by WebView2. Ctrl+Shift+F6 moves focus back to the editor.");
                lifecycle = new TerminalWebView2LifecyclePolicy();
                var replacementRequested = _fallbackState != TerminalWebView2FallbackState.None;
                lifecycle.TryBeginInitialization();
                _webView = renderer;
                _webViewLifecycle = lifecycle;
                _webViewDetachedFromLayout = false;
                _webView2Available = true;
                _fallbackState = TerminalWebView2FallbackState.None;
                _rendererInstanceGeneration++;
                _terminalResizePolicy.Reset(_rendererInstanceGeneration);
                _rendererRecovery.RendererCreated(_rendererInstanceGeneration);
                StartupLifecycleTrace.Write("TerminalControl.Renderer", "CREATED", $"generation={_rendererInstanceGeneration}");
                AppLogger.Info("Terminal", $"Created fresh dynamic WebView2 terminal renderer. RendererInstanceGeneration={_rendererInstanceGeneration}.");
                DeveloperDiagnostics.LogStateTransition(
                    "Terminal",
                    "WebView2Renderer",
                    "Retired",
                    "Initializing",
                    "Created a fresh dynamic WebView2 renderer instance.",
                    new Dictionary<string, object?>
                    {
                        ["rendererInstanceGeneration"] = _rendererInstanceGeneration,
                        ["replacementRequested"] = replacementRequested,
                        ["replayPersistentHistory"] = _rendererRecovery.ReplacementPending,
                        ["recoveryCycle"] = _rendererRecovery.Cycle
                    });
            }

            try
            {
                WebViewHost.Children.Insert(0, renderer);
                renderer.PreviewMouseDown += WebView_PreviewMouseDown;
                renderer.GotKeyboardFocus += WebView_GotKeyboardFocus;
                renderer.LostKeyboardFocus += WebView_LostKeyboardFocus;
                FallbackBanner.Visibility = Visibility.Collapsed;
                FallbackDetailsText.Visibility = Visibility.Collapsed;
                return true;
            }
            catch (Exception ex)
            {
                RetireWebView2Renderer("RendererAttachFailed", ex, renderer, lifecycle);
                return false;
            }
        }

        private async Task InitializeRendererAsync(
            WebView2 renderer,
            TerminalWebView2LifecyclePolicy lifecycle)
        {
            try
            {
                TerminalStartupTrace.Write("WEBVIEW2_INIT_START", $"controlId={GetHashCode():X8}");
                AppLogger.Debug("Terminal", "Initializing WebView2 terminal host.");
                StartupLifecycleTrace.Write("TerminalControl.WebView2", "INITIALIZE_ENTER");
                await renderer.EnsureCoreWebView2Async().ConfigureAwait(true);
                TerminalStartupTrace.Write("WEBVIEW2_INIT_SUCCESS");
                StartupLifecycleTrace.Write("TerminalControl.WebView2", "CORE_READY");
                if (!IsCurrentRenderer(renderer, lifecycle) ||
                    !lifecycle.CanAcceptRendererCallback ||
                    Dispatcher.HasShutdownStarted ||
                    Dispatcher.HasShutdownFinished)
                {
                    RetireWebView2Renderer("InitializationAbandoned", null, renderer, lifecycle);
                    return;
                }

                var coreWebView2 = renderer.CoreWebView2;
                if (coreWebView2 is null)
                {
                    RetireWebView2Renderer("InitializationCompletedWithoutCore", null, renderer, lifecycle);
                    return;
                }

                coreWebView2.Settings.IsStatusBarEnabled               = false;
                coreWebView2.Settings.AreDefaultContextMenusEnabled    = false;
                coreWebView2.Settings.IsZoomControlEnabled             = false;
                coreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

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

                coreWebView2.SetVirtualHostNameToFolderMapping(
                    "terminal.local", terminalDir,
                    CoreWebView2HostResourceAccessKind.Allow);

                // Keep WebView2 lifecycle diagnostics out of the visible terminal.
                // A normal PowerShell console should begin with PowerShell output/prompt,
                // not app-host debug lines.

                if (!IsCurrentRenderer(renderer, lifecycle))
                {
                    RetireWebView2Renderer("InitializationSuperseded", null, renderer, lifecycle);
                    return;
                }

                _subscribedCoreWebView2 = coreWebView2;
                coreWebView2.WebMessageReceived += OnWebMessageReceived;

                coreWebView2.NavigationCompleted += OnNavigationCompleted;

                coreWebView2.NavigateToString(TerminalHtml);
                lifecycle.MarkReady();
                TerminalStartupTrace.Write("XTERM_INIT_START", "NavigateToString completed; waiting for xterm ready signal");
                StartupLifecycleTrace.Write("TerminalControl.WebView2", "NAVIGATION_STARTED");
                System.Diagnostics.Debug.WriteLine("[TerminalControl] WebView2 initialized — navigating to terminal page");
                AppLogger.Debug("Terminal", "WebView2 terminal page navigation started.");
            }
            catch (Exception ex)
            {
                TerminalStartupTrace.Write("WEBVIEW2_INIT_FAILURE", $"exception={ex.GetType().Name}; message={ex.Message}; stack={ex.StackTrace}");
                StartupLifecycleTrace.Write("TerminalControl.WebView2", "FAULT", $"exception={ex.GetType().Name}: {ex.Message}");
                RetireWebView2Renderer(
                    IsWebView2RuntimeAvailable()
                        ? "InitializationFailed"
                        : "RuntimeUnavailable",
                    ex,
                    renderer,
                    lifecycle);
                System.Diagnostics.Debug.WriteLine(
                    $"[TerminalControl] WebView2 initialization failed: {ex.Message}");
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            RetireWebView2Renderer("TerminalControlUnloaded", null);
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs navigation)
        {
            if (sender is not CoreWebView2 coreWebView2 ||
                !IsCurrentCoreWebView2(coreWebView2) ||
                !TryGetCurrentRenderer(out var renderer, out var lifecycle) ||
                !lifecycle.CanAcceptRendererCallback)
            {
                return;
            }

            if (navigation.IsSuccess)
            {
                TerminalStartupTrace.Write("WEBVIEW2_NAVIGATION_COMPLETED", "success=true");
                if (!TryGetCoreWebView2("NavigationCompleted", out var currentCoreWebView2, renderer, lifecycle))
                {
                    return;
                }

                try
                {
                    var title = currentCoreWebView2.DocumentTitle;
                    System.Diagnostics.Debug.WriteLine(
                        $"[TerminalControl] NavigationCompleted — success | title={title}");
                }
                catch (Exception ex) when (IsWebView2LifecycleException(ex))
                {
                    RetireWebView2Renderer("NavigationCompletedDocumentTitle", ex, renderer, lifecycle);
                }
                return;
            }

            var dispatcherShutdownStarted = Dispatcher.HasShutdownStarted;
            var dispatcherShutdownFinished = Dispatcher.HasShutdownFinished;
            if (dispatcherShutdownStarted || dispatcherShutdownFinished || !IsLoaded)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TerminalControl] Ignored NavigationCompleted failure during terminal lifecycle shutdown ({navigation.WebErrorStatus}).");
                return;
            }

            RetireWebView2Renderer("NavigationFailed", null, renderer, lifecycle);

            var metadata = new Dictionary<string, object?>
            {
                ["webErrorStatus"] = navigation.WebErrorStatus.ToString(),
                ["navigationStage"] = "TerminalHtmlBootstrap",
                ["navigationOrigin"] = "NavigateToString",
                ["rendererAvailable"] = _webView2Available,
                ["isReady"] = _isReady,
                ["isLoaded"] = IsLoaded,
                ["dispatcherHasShutdownStarted"] = dispatcherShutdownStarted,
                ["dispatcherHasShutdownFinished"] = dispatcherShutdownFinished,
                ["terminalContentOmitted"] = true
            };
            AppLogger.Error(
                "Terminal",
                $"WebView2 terminal bootstrap navigation failed. WebErrorStatus={navigation.WebErrorStatus}.");
            DeveloperDiagnostics.LogError(
                "Terminal",
                "WebView2 terminal bootstrap navigation failed.",
                metadata);
        }

        private bool TryGetCoreWebView2(
            string source,
            out CoreWebView2 coreWebView2,
            WebView2? expectedRenderer = null,
            TerminalWebView2LifecyclePolicy? expectedLifecycle = null)
        {
            coreWebView2 = null!;
            if (!_webView2Available ||
                Dispatcher.HasShutdownStarted ||
                Dispatcher.HasShutdownFinished)
            {
                return false;
            }

            if (!TryGetCurrentRenderer(expectedRenderer, expectedLifecycle, out var renderer, out var lifecycle) ||
                !lifecycle.CanUseRenderer)
            {
                return false;
            }

            try
            {
                if (renderer.CoreWebView2 is not { } currentCoreWebView2)
                {
                    return false;
                }

                coreWebView2 = currentCoreWebView2;
                return true;
            }
            catch (Exception ex) when (IsWebView2LifecycleException(ex))
            {
                RetireWebView2Renderer(source, ex, renderer, lifecycle);
                return false;
            }
        }

        private bool IsCurrentRenderer(
            WebView2 renderer,
            TerminalWebView2LifecyclePolicy lifecycle)
        {
            lock (_rendererSyncRoot)
            {
                return ReferenceEquals(_webView, renderer) &&
                       ReferenceEquals(_webViewLifecycle, lifecycle) &&
                       !_webViewDetachedFromLayout;
            }
        }

        private bool TryGetCurrentRenderer(
            WebView2? expectedRenderer,
            TerminalWebView2LifecyclePolicy? expectedLifecycle,
            out WebView2 renderer,
            out TerminalWebView2LifecyclePolicy lifecycle)
        {
            lock (_rendererSyncRoot)
            {
                if (_webView is null ||
                    _webViewDetachedFromLayout ||
                    (expectedRenderer is not null && !ReferenceEquals(_webView, expectedRenderer)) ||
                    (expectedLifecycle is not null && !ReferenceEquals(_webViewLifecycle, expectedLifecycle)))
                {
                    renderer = null!;
                    lifecycle = null!;
                    return false;
                }

                renderer = _webView;
                lifecycle = _webViewLifecycle;
                return true;
            }
        }

        private bool TryGetCurrentRenderer(
            WebView2 renderer,
            out TerminalWebView2LifecyclePolicy lifecycle)
        {
            return TryGetCurrentRenderer(renderer, null, out _, out lifecycle);
        }

        private bool TryGetCurrentRenderer(
            out WebView2 renderer,
            out TerminalWebView2LifecyclePolicy lifecycle)
        {
            return TryGetCurrentRenderer(null, null, out renderer, out lifecycle);
        }

        private bool IsCurrentCoreWebView2(CoreWebView2 coreWebView2)
        {
            lock (_rendererSyncRoot)
            {
                return _webView is not null &&
                       !_webViewDetachedFromLayout &&
                       _webViewLifecycle.CanAcceptRendererCallback &&
                       ReferenceEquals(_subscribedCoreWebView2, coreWebView2);
            }
        }

        private void RetireWebView2Renderer(
            string reason,
            Exception? exception,
            WebView2? expectedRenderer = null,
            TerminalWebView2LifecyclePolicy? expectedLifecycle = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(
                    new Action(() => RetireWebView2Renderer(reason, exception, expectedRenderer, expectedLifecycle)),
                    DispatcherPriority.Send);
                return;
            }

            CancelResizeTransaction(reason);
            TerminalCriticalTrace.LogStage(
                "TerminalRendererRecoveryBegin",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["rendererGeneration"] = _rendererInstanceGeneration,
                    ["exceptionType"] = exception?.GetType().FullName,
                    ["contentOmitted"] = true
                });

            WebView2? retiredRenderer;
            TerminalWebView2LifecyclePolicy retiredLifecycle;
            CoreWebView2? subscribedCoreWebView2;
            lock (_rendererSyncRoot)
            {
                if (_webView is null ||
                    (expectedRenderer is not null && !ReferenceEquals(_webView, expectedRenderer)) ||
                    (expectedLifecycle is not null && !ReferenceEquals(_webViewLifecycle, expectedLifecycle)))
                {
                    return;
                }

                retiredRenderer = _webView;
                retiredLifecycle = _webViewLifecycle;
                subscribedCoreWebView2 = _subscribedCoreWebView2;
                _webView = null;
                _subscribedCoreWebView2 = null;
                _webViewDetachedFromLayout = true;
                _webView2Available = false;
                _isReady = false;
                _rendererRecovery.RendererRetired(_rendererInstanceGeneration);
                _fallbackState = reason == "RuntimeUnavailable"
                    ? TerminalWebView2FallbackState.RuntimeUnavailable
                    : reason == "InitializationFailed" || reason == "InitializationCompletedWithoutCore"
                        ? TerminalWebView2FallbackState.InitializationFailed
                        : TerminalWebView2FallbackState.Faulted;
                retiredLifecycle.MarkFaulted();
                retiredLifecycle.TryBeginDisposal();
            }

            var firstTransition = retiredLifecycle.State == TerminalWebView2LifecycleState.Disposing;
            var discardedOutput = _outputFlowController.MarkRendererUnavailable();
            RecordDroppedTerminalOutput(discardedOutput.DiscardedCharacters);
            ReportDroppedTerminalOutputIfNeeded();

            if (subscribedCoreWebView2 is not null)
            {
                try
                {
                    subscribedCoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    subscribedCoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                }
                catch (Exception ex) when (IsWebView2LifecycleException(ex))
                {
                    AppLogger.Warning("Terminal", $"WebView2 terminal event unsubscription hit a disposed controller. Reason={reason}; ExceptionType={ex.GetType().Name}.");
                }
            }

            try
            {
                System.Windows.Input.Keyboard.ClearFocus();
                Focus();
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Terminal", $"Moving focus away from the retired WebView2 host failed. Reason={reason}; ExceptionType={ex.GetType().Name}.");
            }

            retiredRenderer.PreviewMouseDown -= WebView_PreviewMouseDown;
            retiredRenderer.GotKeyboardFocus -= WebView_GotKeyboardFocus;
            retiredRenderer.LostKeyboardFocus -= WebView_LostKeyboardFocus;
            try
            {
                if (WebViewHost.Children.Contains(retiredRenderer))
                {
                    WebViewHost.Children.Remove(retiredRenderer);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Terminal", $"Removing the retired WebView2 host from the visual tree failed. Reason={reason}; ExceptionType={ex.GetType().Name}.");
            }

            ShowFallback(_fallbackState);

            void DisposeRetiredRenderer()
            {
                retiredLifecycle.MarkDisposed();
                try
                {
                    retiredRenderer.Dispose();
                }
                catch (Exception ex) when (IsWebView2LifecycleException(ex))
                {
                    AppLogger.Warning("Terminal", $"Disposing the retired WebView2 host hit a stale controller. Reason={reason}; ExceptionType={ex.GetType().Name}.");
                }
            }

            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                DisposeRetiredRenderer();
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(DisposeRetiredRenderer), DispatcherPriority.ContextIdle);
            }

            if (!firstTransition)
            {
                return;
            }

            try
            {
                TerminalRendererUnavailable?.Invoke(reason);
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Terminal", $"Terminal renderer-unavailable notification failed. Reason={reason}; ExceptionType={ex.GetType().Name}.");
            }

            var metadata = new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["exceptionType"] = exception?.GetType().FullName,
                ["hResult"] = exception?.HResult,
                ["discardedOutputCharacters"] = discardedOutput.DiscardedCharacters,
                ["webViewDetachedFromLayout"] = true,
                ["rendererInstanceGeneration"] = _rendererInstanceGeneration,
                ["terminalContentOmitted"] = true
            };
            if (exception is not null)
            {
                AppLogger.Error("Terminal", $"WebView2 terminal renderer disabled. Reason={reason}; DiscardedOutputCharacters={discardedOutput.DiscardedCharacters}.", exception);
                DeveloperDiagnostics.LogException("Terminal", exception, "WebView2 terminal renderer disabled after lifecycle failure.", metadata);
            }
            else
            {
                AppLogger.Info("Terminal", $"WebView2 terminal renderer disabled. Reason={reason}; DiscardedOutputCharacters={discardedOutput.DiscardedCharacters}.");
                DeveloperDiagnostics.LogInfo("Terminal", "WebView2 terminal renderer disabled.", metadata);
            }
        }

        private void ShowFallback(TerminalWebView2FallbackState state)
        {
            FallbackMessageText.Text = TerminalWebView2FallbackPolicy.GetMessage(state);
            FallbackDetailsText.Visibility = TerminalWebView2FallbackPolicy.ShowsRuntimeInstallDetails(state)
                ? Visibility.Visible
                : Visibility.Collapsed;
            FallbackBanner.Visibility = Visibility.Visible;
        }

        private static bool IsWebView2RuntimeAvailable()
        {
            try
            {
                return !string.IsNullOrWhiteSpace(
                    CoreWebView2Environment.GetAvailableBrowserVersionString(null));
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return false;
            }
            catch
            {
                // An unrelated probe failure is not proof that the runtime is
                // missing; keep the fallback message honest and report an init
                // failure instead.
                return true;
            }
        }

        private static bool IsWebView2LifecycleException(Exception exception)
        {
            return exception is InvalidComObjectException ||
                   exception is COMException ||
                   exception is ObjectDisposedException ||
                   exception is InvalidOperationException;
        }

        private bool TryGetWebViewKeyboardFocusWithin()
        {
            if (!_webView2Available ||
                !TryGetCurrentRenderer(out var renderer, out var lifecycle) ||
                lifecycle.IsRetired)
            {
                return false;
            }

            try
            {
                return renderer.IsKeyboardFocusWithin;
            }
            catch (Exception ex) when (IsWebView2LifecycleException(ex))
            {
                RetireWebView2Renderer("ReadWebViewKeyboardFocus", ex, renderer, lifecycle);
                return false;
            }
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes raw VT100/ANSI data to xterm.js. ANSI escape sequences are
        /// preserved so xterm.js renders colors, cursor movement, etc.
        /// Thread-safe — may be called from any thread.
        /// </summary>
        public void BeginTerminalOutputGeneration(int generation)
        {
            CancelResizeTransaction("session-start");
            _firstInputObservedForDiagnostics = false;
            _firstOutputAfterInputLogged = false;
            _terminalProtocolOutputFilter.Reset();
            _persistentOutputHistory.ActivateGeneration(generation);
            var result = _outputFlowController.ActivateGeneration(generation);
            ReportDiscardedTerminalOutput(result, "session-start");
        }

        /// <summary>Invalidates pending output from a terminal session that is stopping.</summary>
        public void InvalidateTerminalOutputGeneration(int generation)
        {
            CancelResizeTransaction("session-stop");
            _terminalProtocolOutputFilter.Reset();
            _persistentOutputHistory.InvalidateGeneration(generation);
            var result = _outputFlowController.InvalidateGeneration(generation);
            ReportDiscardedTerminalOutput(result, "session-stop");
        }

        public void WriteRaw(int generation, string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                return;
            }

            using var performanceScope = PerformanceTrace.Begin(
                "Terminal",
                "RendererWriteRaw",
                generation: generation,
                properties: new Dictionary<string, object?>
                {
                    ["payloadCharacters"] = data.Length
                });
            TerminalOutputCorrelationTrace.Record("TerminalControlWriteRaw", generation, data);

            TerminalCriticalTrace.LogStage(
                "TerminalControl.WriteRaw.Begin",
                new Dictionary<string, object?>
                {
                    ["terminalSessionGeneration"] = generation,
                    ["rendererGeneration"] = _rendererInstanceGeneration,
                    ["outputCharacterLength"] = data.Length,
                    ["rawControlSummary"] = TerminalOutputControlClassifier.Summarize(data).ToDiagnosticString(),
                    ["contentOmitted"] = true
                });
            var filteredOutput = _terminalProtocolOutputFilter.Process(data);
            if (filteredOutput.FilteredRecordCount > 0)
            {
                var visibleControlSummary = TerminalOutputControlClassifier
                    .Summarize(filteredOutput.VisibleText)
                    .ToDiagnosticString();
                DeveloperDiagnostics.LogDebug(
                    "Terminal",
                    "Removed ScriptDesk protocol records before terminal renderer submission.",
                    new Dictionary<string, object?>
                    {
                        ["rawOutputCharacterLength"] = data.Length,
                        ["visibleOutputCharacterLength"] = filteredOutput.VisibleText.Length,
                        ["filteredCharacters"] = filteredOutput.FilteredCharacters,
                        ["filteredRecordCount"] = filteredOutput.FilteredRecordCount,
                        ["sessionGeneration"] = generation,
                        ["rawControlSummary"] = TerminalOutputControlClassifier.Summarize(data).ToDiagnosticString(),
                        ["visibleControlSummary"] = visibleControlSummary,
                        ["removedProtocolControlSummary"] = filteredOutput.RemovedProtocolControlSummary.ToDiagnosticString(),
                        ["preservedProtocolControlSummary"] = filteredOutput.PreservedProtocolControlSummary.ToDiagnosticString(),
                        ["contentOmitted"] = true
                    });
                TerminalCriticalTrace.LogStage(
                    "TerminalControl.ProtocolFilter.Characterized",
                    new Dictionary<string, object?>
                    {
                        ["terminalSessionGeneration"] = generation,
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["rawOutputCharacterLength"] = data.Length,
                        ["visibleOutputCharacterLength"] = filteredOutput.VisibleText.Length,
                        ["filteredCharacters"] = filteredOutput.FilteredCharacters,
                        ["filteredRecordCount"] = filteredOutput.FilteredRecordCount,
                        ["rawControlSummary"] = TerminalOutputControlClassifier.Summarize(data).ToDiagnosticString(),
                        ["visibleControlSummary"] = visibleControlSummary,
                        ["removedProtocolControlSummary"] = filteredOutput.RemovedProtocolControlSummary.ToDiagnosticString(),
                        ["preservedProtocolControlSummary"] = filteredOutput.PreservedProtocolControlSummary.ToDiagnosticString(),
                        ["contentOmitted"] = true
                    });
            }

            data = filteredOutput.VisibleText;
            WriteVisibleOutput(generation, data, "ConPTY");
            TerminalCriticalTrace.LogStage(
                "TerminalControl.WriteRaw.End",
                new Dictionary<string, object?>
                {
                    ["terminalSessionGeneration"] = generation,
                    ["rendererGeneration"] = _rendererInstanceGeneration,
                    ["outputCharacterLength"] = data.Length,
                    ["filteredRecordCount"] = filteredOutput.FilteredRecordCount,
                    ["visibleControlSummary"] = TerminalOutputControlClassifier.Summarize(data).ToDiagnosticString(),
                    ["contentOmitted"] = true
                });
        }

        public void WriteStructuredOutput(int generation, string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                return;
            }

            TerminalCriticalTrace.LogStage(
                "TerminalControl.WriteStructuredOutput.Begin",
                new Dictionary<string, object?>
                {
                    ["terminalSessionGeneration"] = generation,
                    ["rendererGeneration"] = _rendererInstanceGeneration,
                    ["outputCharacterLength"] = data.Length,
                    ["contentOmitted"] = true
                });
            if (TerminalOutputMultiplexer.ContainsPrivateProtocol(data))
            {
                AppLogger.Error("Terminal", $"Structured editor output contained private ScriptDesk terminal protocol. SessionGeneration={generation}, Length={data.Length}, ContentOmitted=True.");
                DeveloperDiagnostics.LogWarning(
                    "Terminal",
                    "Rejected structured editor output containing private terminal protocol.",
                    new Dictionary<string, object?>
                    {
                        ["sessionGeneration"] = generation,
                        ["length"] = data.Length,
                        ["contentOmitted"] = true
                    });
                return;
            }

            WriteVisibleOutput(generation, data, "StructuredEditor");
            TerminalCriticalTrace.LogStage(
                "TerminalControl.WriteStructuredOutput.End",
                new Dictionary<string, object?>
                {
                    ["terminalSessionGeneration"] = generation,
                    ["rendererGeneration"] = _rendererInstanceGeneration,
                    ["outputCharacterLength"] = data.Length,
                    ["contentOmitted"] = true
                });
        }

        private void WriteVisibleOutput(int generation, string data, string source)
        {
            // Keep the capture-to-render enqueue boundary atomic with resize-ack release.
            // This prevents a reader callback from overtaking bytes released for the
            // completed grid.
            lock (_resizeOutputIntegrationSyncRoot)
            {
                WriteVisibleOutputCore(generation, data, source);
            }
        }

        private void WriteVisibleOutputCore(int generation, string data, string source)
        {
            if (string.IsNullOrEmpty(data))
            {
                return;
            }

            if (_outputFlowController.ActiveGeneration != generation)
            {
                return;
            }

            _persistentOutputHistory.Append(generation, data);

            TerminalCriticalTrace.LogStage(
                "TerminalControl.WriteVisibleOutput.Begin",
                new Dictionary<string, object?>
                {
                    ["terminalSessionGeneration"] = generation,
                    ["rendererGeneration"] = _rendererInstanceGeneration,
                    ["source"] = source,
                    ["outputCharacterLength"] = data.Length,
                    ["rendererReady"] = _isReady,
                    ["rendererAvailable"] = _webView2Available,
                    ["contentOmitted"] = true
                });
            if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseTerminalEnabled())
            {
                DeveloperDiagnostics.LogDebug(
                    "Terminal",
                    "Terminal output received for WebView dispatch.",
                    new Dictionary<string, object?>(DeveloperDiagnostics.CreatePrivateTextMetadata(data))
                    {
                        ["isReady"] = _isReady,
                        ["rendererAvailable"] = _webView2Available,
                        ["sessionGeneration"] = generation,
                        ["source"] = source
                    });
            }

            if (!_webView2Available)
            {
                RecordDroppedTerminalOutput(data.Length);
                TerminalCriticalTrace.LogStage(
                    "TerminalControl.WriteVisibleOutput.RendererUnavailableDrop",
                    new Dictionary<string, object?>
                    {
                        ["terminalSessionGeneration"] = generation,
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["source"] = source,
                        ["outputCharacterLength"] = data.Length,
                        ["contentOmitted"] = true
                    });
                return;
            }

            var barrierCapture = _resizeOutputBarrier.Capture(
                _rendererInstanceGeneration,
                generation,
                source,
                data);
            if (barrierCapture.Status == TerminalResizeBarrierCaptureStatus.Buffered)
            {
                TerminalCriticalTrace.LogStage(
                    "ResizeTransaction.OutputBuffered",
                    new Dictionary<string, object?>
                    {
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["terminalSessionGeneration"] = generation,
                        ["source"] = source,
                        ["outputCharacterLength"] = data.Length,
                        ["bufferedCharacters"] = barrierCapture.TotalBufferedCharacters,
                        ["contentOmitted"] = true
                    });
                return;
            }

            if (barrierCapture.Status == TerminalResizeBarrierCaptureStatus.BoundedLimitExceeded)
            {
                TerminalCriticalTrace.LogStage(
                    "ResizeOutputBarrierLimitExceeded",
                    new Dictionary<string, object?>
                    {
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["terminalSessionGeneration"] = generation,
                        ["bufferedCharacters"] = barrierCapture.TotalBufferedCharacters,
                        ["contentOmitted"] = true
                    });
                AppLogger.Error(
                    "Terminal",
                    $"Resize output barrier exceeded its bounded policy. SessionGeneration={generation}, Length={data.Length}, BufferedCharacters={barrierCapture.TotalBufferedCharacters}, Source={source}, ContentOmitted=True.");
                DeveloperDiagnostics.LogError(
                    "Terminal",
                    "Resize output barrier exceeded its bounded policy; cancelling the transient resize without retiring the renderer.",
                    new Dictionary<string, object?>
                    {
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["terminalSessionGeneration"] = generation,
                        ["source"] = source,
                        ["outputCharacterLength"] = data.Length,
                        ["bufferedCharacters"] = barrierCapture.TotalBufferedCharacters,
                        ["contentOmitted"] = true
                    });
                var cancelled = CancelResizeTransaction("buffer-limit-exceeded");
                RequeueCancelledResizeOutput(cancelled, generation);
                EnqueueVisibleOutputAfterResizeCancellation(generation, data);
                RequestOutputFlush(scheduleFlush: true);
                return;
            }

            if (!_isReady && !_firstOutputQueuedLogged)
            {
                _firstOutputQueuedLogged = true;
                AppLogger.Info("Terminal", $"Queued terminal output before xterm.js was ready. Length={data.Length}, Source={source}.");
                DeveloperDiagnostics.LogInfo(
                    "Terminal",
                    "First terminal output observed before xterm.js was ready.",
                    new Dictionary<string, object?>
                    {
                        ["length"] = data.Length,
                        ["source"] = source,
                        ["vtControlSummary"] = SummarizeVtControls(data)
                    });
            }

            var enqueueResult = _outputFlowController.Enqueue(generation, data);
            TerminalOutputCorrelationTrace.Record(
                "TerminalBridgeEnqueued",
                generation,
                data,
                pendingCharacters: enqueueResult.PendingCharacters);
            TerminalCriticalTrace.LogStage(
                "TerminalOutputFlowController.Enqueue.Result",
                new Dictionary<string, object?>
                {
                    ["terminalSessionGeneration"] = generation,
                    ["rendererGeneration"] = _rendererInstanceGeneration,
                    ["source"] = source,
                    ["outputCharacterLength"] = data.Length,
                    ["scheduleFlush"] = enqueueResult.ScheduleFlush,
                    ["acceptedCharacters"] = enqueueResult.AcceptedCharacters,
                    ["droppedCharacters"] = enqueueResult.DroppedCharacters,
                    ["pendingCharacters"] = enqueueResult.PendingCharacters,
                    ["rejectedStaleCharacters"] = enqueueResult.RejectedStaleCharacters,
                    ["contentOmitted"] = true
                });
            if (enqueueResult.RejectedStaleCharacters > 0)
            {
                DeveloperDiagnostics.LogDebug("Terminal", "Stale terminal output was rejected before WebView dispatch.", new Dictionary<string, object?>
                {
                    ["sessionGeneration"] = generation,
                    ["length"] = enqueueResult.RejectedStaleCharacters,
                    ["contentOmitted"] = true
                });
                return;
            }
            if (enqueueResult.DroppedCharacters > 0)
            {
                RecordDroppedTerminalOutput(enqueueResult.DroppedCharacters);
                return;
            }

            if (!_isReady)
            {
                TerminalStartupTrace.Write("BUFFERED_FOR_RENDERER", $"generation={generation}; chars={data.Length}; pendingCharacters={enqueueResult.PendingCharacters}; contentOmitted=true");
            }

            TerminalStartupTrace.Write("FIRST_TERMINAL_WRITE_REQUESTED", $"generation={generation}; chars={data.Length}; rendererReady={_isReady}; source={source}");

            if (_isReady && !_firstOutputPostedLogged)
            {
                _firstOutputPostedLogged = true;
                AppLogger.Info("Terminal", $"Posting first terminal output chunk to xterm.js. Length={data.Length}.");
                DeveloperDiagnostics.LogInfo(
                    "Terminal",
                    "First terminal output chunk posted to xterm.js.",
                    new Dictionary<string, object?>
                    {
                        ["length"] = data.Length,
                        ["vtControlSummary"] = SummarizeVtControls(data)
                    });
            }

            if (_firstInputObservedForDiagnostics && !_firstOutputAfterInputLogged)
            {
                _firstOutputAfterInputLogged = true;
                DeveloperDiagnostics.LogInfo(
                    "Terminal",
                    "First terminal output after the first xterm input was observed.",
                    new Dictionary<string, object?>
                    {
                        ["length"] = data.Length,
                        ["vtControlSummary"] = SummarizeVtControls(data),
                        ["contentOmitted"] = true
                    });
            }

            RequestOutputFlush(enqueueResult.ScheduleFlush);
            TerminalCriticalTrace.LogStage(
                "TerminalControl.WriteVisibleOutput.End",
                new Dictionary<string, object?>
                {
                    ["terminalSessionGeneration"] = generation,
                    ["rendererGeneration"] = _rendererInstanceGeneration,
                    ["source"] = source,
                    ["outputCharacterLength"] = data.Length,
                    ["scheduleFlush"] = enqueueResult.ScheduleFlush,
                    ["contentOmitted"] = true
                });
        }

        /// <summary>Clears the xterm.js terminal display and returns keyboard focus to it.</summary>
        public void Clear()
        {
            if (!_webView2Available) return;
            DeveloperDiagnostics.LogUserAction("Terminal", "TerminalClearRequested", "Terminal clear requested.");
            PostToWebView("clear", string.Empty);
        }

        /// <summary>
        /// Restores the visible xterm cursor and repaints the terminal after a command
        /// completes, without changing WPF or browser keyboard focus.
        /// </summary>
        public void NormalizeInteractiveState()
        {
            if (!_webView2Available) return;
            DeveloperDiagnostics.LogInfo(
                "Terminal",
                "Terminal interactive display normalization requested without changing focus.");
            PostToWebView("normalize_interactive_state", string.Empty);
        }

        /// <summary>Focuses the xterm.js terminal so keystrokes are captured immediately.</summary>
        public void FocusTerminal()
        {
            if (!_webView2Available) return;
            DeveloperDiagnostics.LogUserAction("Terminal", "TerminalFocusRequested", "Terminal focus requested.");
            ActivateTerminalHost("FocusTerminal");
        }

        /// <summary>Requests a bounded renderer snapshot at an authoritative backend prompt boundary.</summary>
        public void ObservePromptBoundary(int generation)
        {
            if (!_webView2Available)
            {
                return;
            }

            void Send()
            {
                if (!TryGetCoreWebView2("ObservePromptBoundary", out var coreWebView2))
                {
                    return;
                }

                try
                {
                    coreWebView2.PostWebMessageAsString(
                        TerminalWebMessageSerializer.SerializePromptBoundary(generation));
                }
                catch (Exception ex) when (IsWebView2LifecycleException(ex))
                {
                    RetireWebView2Renderer("ObservePromptBoundary", ex);
                }
                catch (Exception ex)
                {
                    DeveloperDiagnostics.LogException(
                        "Terminal",
                        ex,
                        "Posting prompt-boundary screen-state request failed.",
                        new Dictionary<string, object?>
                        {
                            ["generation"] = generation,
                            ["contentOmitted"] = true
                        });
                }
            }

            if (Dispatcher.CheckAccess()) Send();
            else Dispatcher.BeginInvoke(Send);
        }

        /// <summary>
        /// Focuses the WPF host, WebView2, and the live xterm helper textarea, then
        /// verifies the browser-side active element. The caller owns session-generation
        /// validation; the generation is recorded only as safe diagnostic metadata here.
        /// </summary>
        public Task<TerminalFocusRestoreResult> RestoreTerminalFocusAsync(
            int generation,
            CancellationToken cancellationToken = default)
        {
            if (Dispatcher.CheckAccess())
            {
                return RestoreTerminalFocusCoreAsync(generation, cancellationToken);
            }

            return Dispatcher.InvokeAsync(
                    () => RestoreTerminalFocusCoreAsync(generation, cancellationToken),
                    DispatcherPriority.Input)
                .Task
                .Unwrap();
        }

        /// <summary>Updates the xterm.js colour theme to match the active application theme.</summary>
        public void ApplyAppTheme(string themeName)
        {
            PostToWebView("settheme", themeName);
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private async Task<TerminalFocusRestoreResult> RestoreTerminalFocusCoreAsync(
            int generation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_webView2Available ||
                !_isReady ||
                !TryGetCurrentRenderer(out var renderer, out var lifecycle) ||
                !TryGetCoreWebView2("RestoreTerminalFocus", out var coreWebView2, renderer, lifecycle))
            {
                AppLogger.Info("Terminal", $"Skipped verified terminal focus because the WebView2 renderer is not ready. Generation={generation}, RendererReady={_isReady}.");
                return new TerminalFocusRestoreResult(false, false, false, false, null, "renderer-not-ready");
            }

            bool wpfHostFocused;
            bool webViewFocused;
            try
            {
                wpfHostFocused = renderer.Focus();
                webViewFocused = renderer.IsKeyboardFocused || renderer.IsKeyboardFocusWithin;
            }
            catch (Exception ex) when (IsWebView2LifecycleException(ex))
            {
                RetireWebView2Renderer("RestoreTerminalFocusWpfFocus", ex, renderer, lifecycle);
                return new TerminalFocusRestoreResult(false, false, false, false, null, "renderer-lifecycle-fault");
            }

            AppLogger.Debug(
                "Terminal",
                $"Verified terminal focus requested. Generation={generation}, WpfHostFocusResult={wpfHostFocused}, WebViewFocused={webViewFocused}.");
            DeveloperDiagnostics.LogUiThreadDispatch(
                "Terminal",
                "VerifiedTerminalFocus",
                "Requested WPF and WebView2 terminal focus before browser-side xterm verification.",
                Dispatcher.CheckAccess(),
                new Dictionary<string, object?>
                {
                    ["generation"] = generation,
                    ["wpfHostFocusResult"] = wpfHostFocused,
                    ["webViewFocused"] = webViewFocused
                });

            if (!wpfHostFocused || !webViewFocused)
            {
                return new TerminalFocusRestoreResult(
                    wpfHostFocused,
                    webViewFocused,
                    false,
                    false,
                    null,
                    "webview-focus-failed");
            }

            try
            {
                const string focusScript = "window.__ps7ScriptDeskFocusTerminal ? window.__ps7ScriptDeskFocusTerminal() : ({ terminalAvailable: false, inputActive: false, activeElement: '', failureReason: 'focus-bridge-unavailable' });";
                var scriptResult = await coreWebView2.ExecuteScriptAsync(focusScript).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                using var resultDocument = JsonDocument.Parse(scriptResult);
                var root = resultDocument.RootElement;
                var terminalAvailable = root.TryGetProperty("terminalAvailable", out var terminalAvailableProperty) &&
                                        terminalAvailableProperty.ValueKind == JsonValueKind.True;
                var inputActive = root.TryGetProperty("inputActive", out var inputActiveProperty) &&
                                  inputActiveProperty.ValueKind == JsonValueKind.True;
                var activeElement = root.TryGetProperty("activeElement", out var activeElementProperty)
                    ? activeElementProperty.GetString()
                    : null;
                var failureReason = root.TryGetProperty("failureReason", out var failureReasonProperty)
                    ? failureReasonProperty.GetString()
                    : null;
                var browserFocusCommandExecuted = terminalAvailable;

                AppLogger.Info(
                    "Terminal",
                    $"Verified browser xterm focus result. Generation={generation}, BrowserFocusCommandExecuted={browserFocusCommandExecuted}, XtermInputActive={inputActive}, ActiveElement={activeElement ?? "(none)"}.");
                DeveloperDiagnostics.LogDecision(
                    "Terminal",
                    "VerifiedTerminalFocus",
                    "Browser-side xterm focus command completed.",
                    inputActive ? "InputActive" : "InputInactive",
                    new Dictionary<string, object?>
                    {
                        ["generation"] = generation,
                        ["browserFocusCommandExecuted"] = browserFocusCommandExecuted,
                        ["xtermInputActive"] = inputActive,
                        ["activeElement"] = activeElement,
                        ["failureReason"] = failureReason
                    });
                return new TerminalFocusRestoreResult(
                    wpfHostFocused,
                    webViewFocused,
                    browserFocusCommandExecuted,
                    inputActive,
                    activeElement,
                    failureReason);
            }
            catch (Exception ex) when (IsWebView2LifecycleException(ex))
            {
                RetireWebView2Renderer("RestoreTerminalFocusBrowserScript", ex, renderer, lifecycle);
                return new TerminalFocusRestoreResult(
                    wpfHostFocused,
                    webViewFocused,
                    false,
                    false,
                    null,
                    "renderer-lifecycle-fault");
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Terminal", $"Verified browser xterm focus failed. Generation={generation}, ExceptionType={ex.GetType().Name}.");
                DeveloperDiagnostics.LogException(
                    "Terminal",
                    ex,
                    "Browser-side xterm focus verification failed.",
                    new Dictionary<string, object?> { ["generation"] = generation });
                return new TerminalFocusRestoreResult(
                    wpfHostFocused,
                    webViewFocused,
                    false,
                    false,
                    null,
                    "browser-focus-exception");
            }
        }

        private void RequestOutputFlush(bool scheduleFlush)
        {
            if (!scheduleFlush)
            {
                return;
            }

            TerminalCriticalTrace.LogStage(
                "TerminalControl.RequestOutputFlush.Scheduled",
                new Dictionary<string, object?>
                {
                    ["rendererGeneration"] = _rendererInstanceGeneration,
                    ["terminalSessionGeneration"] = _outputFlowController.ActiveGeneration
                });
            Dispatcher.BeginInvoke(
                new Action(FlushPendingOutputToWebView),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private RendererOutputDiagnostics CreateRendererOutputDiagnostics(TerminalOutputBatch outputBatch)
        {
            var resizeGeneration = _lastResizeCommitAcknowledgedGeneration;
            var elapsed = resizeGeneration > 0
                ? DateTimeOffset.UtcNow - _lastResizeCommitAcknowledgedUtc
                : TimeSpan.MaxValue;
            var resizeAdjacent = resizeGeneration > 0 &&
                elapsed >= TimeSpan.Zero &&
                elapsed <= ResizeAdjacentOutputTraceWindow;
            var elapsedMilliseconds = resizeAdjacent ? elapsed.TotalMilliseconds : -1;
            var controlSummary = TerminalOutputControlClassifier
                .Summarize(outputBatch.Data)
                .ToDiagnosticString();

            return new RendererOutputDiagnostics(
                resizeAdjacent,
                resizeGeneration,
                elapsedMilliseconds,
                _lastResizeCommitAcknowledgedColumns,
                _lastResizeCommitAcknowledgedRows,
                controlSummary);
        }

        private void FlushPendingOutputToWebView()
        {
            TerminalCriticalTrace.LogStage(
                "TerminalControl.FlushPendingOutputToWebView.Begin",
                new Dictionary<string, object?>
                {
                    ["rendererGeneration"] = _rendererInstanceGeneration,
                    ["terminalSessionGeneration"] = _outputFlowController.ActiveGeneration
                });
            ReportDroppedTerminalOutputIfNeeded();
            if (_resizeOutputBarrier.IsActive)
            {
                TerminalCriticalTrace.LogStage(
                    "ResizeTransaction.RendererDeliveryBlocked",
                    new Dictionary<string, object?>
                    {
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["terminalSessionGeneration"] = _outputFlowController.ActiveGeneration,
                        ["contentOmitted"] = true
                    });
                return;
            }

            var batch = _outputFlowController.TryBeginDelivery();
            if (batch is not { } outputBatch)
            {
                TerminalCriticalTrace.LogStage(
                    "TerminalOutputFlowController.TryBeginDelivery.Empty",
                    new Dictionary<string, object?>
                    {
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["terminalSessionGeneration"] = _outputFlowController.ActiveGeneration
                    });
                return;
            }

            if (_rendererReadyReplayPending)
            {
                _rendererReadyReplayPending = false;
                TerminalStartupTrace.Write("REPLAY_OUTPUT", $"sequence={outputBatch.Sequence}; chars={outputBatch.Data.Length}; contentOmitted=true");
            }

            TerminalCriticalTrace.LogStage(
                "TerminalOutputFlowController.TryBeginDelivery.Batch",
                new Dictionary<string, object?>
                {
                    ["rendererGeneration"] = _rendererInstanceGeneration,
                    ["terminalSessionGeneration"] = outputBatch.Generation,
                    ["outputSequence"] = outputBatch.Sequence,
                    ["outputCharacterLength"] = outputBatch.Data.Length,
                    ["contentOmitted"] = true
                });
            if (!TryGetCoreWebView2("FlushPendingOutput", out var coreWebView2))
            {
                RecordDroppedTerminalOutput(
                    _outputFlowController.DiscardInFlight(outputBatch.Generation, outputBatch.Sequence));
                ReportDroppedTerminalOutputIfNeeded();
                TerminalCriticalTrace.LogStage(
                    "TerminalControl.FlushPendingOutputToWebView.NoCoreWebView2",
                    new Dictionary<string, object?>
                    {
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["terminalSessionGeneration"] = outputBatch.Generation,
                        ["outputSequence"] = outputBatch.Sequence,
                        ["outputCharacterLength"] = outputBatch.Data.Length,
                        ["contentOmitted"] = true
                    });
                return;
            }

            try
            {
                var submissionId = System.Threading.Interlocked.Increment(ref _nextOutputSubmissionId);
                var outputDiagnostics = CreateRendererOutputDiagnostics(outputBatch);
                coreWebView2.PostWebMessageAsString(
                    TerminalWebMessageSerializer.SerializeOutput(
                        outputBatch.Generation,
                        outputBatch.Sequence,
                        outputBatch.Data,
                        _rendererInstanceGeneration,
                        submissionId,
                        outputDiagnostics.ResizeAdjacent,
                        outputDiagnostics.ResizeGeneration,
                        outputDiagnostics.ResizeElapsedMilliseconds,
                        outputDiagnostics.ControlSummary,
                        TerminalScreenStateDiagnostics.ContainsInterruptMarker(outputBatch.Data)));
                TerminalStartupTrace.Write("JS_WRITE_INVOKED", $"sequence={outputBatch.Sequence}; submissionId={submissionId}; chars={outputBatch.Data.Length}; contentOmitted=true");
                TerminalOutputCorrelationTrace.Record(
                    "WebView2OutputSubmitted",
                    outputBatch.Generation,
                    outputBatch.Data,
                    rendererSequence: outputBatch.Sequence);
                TerminalCriticalTrace.LogStage(
                    "TerminalControl.WebView2.PostOutput",
                    new Dictionary<string, object?>
                    {
                        ["submissionId"] = submissionId,
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["terminalSessionGeneration"] = outputBatch.Generation,
                        ["outputSequence"] = outputBatch.Sequence,
                        ["outputCharacterLength"] = outputBatch.Data.Length,
                        ["resizeAdjacent"] = outputDiagnostics.ResizeAdjacent,
                        ["resizeGeneration"] = outputDiagnostics.ResizeGeneration,
                        ["resizeElapsedMilliseconds"] = outputDiagnostics.ResizeElapsedMilliseconds,
                        ["resizeColumns"] = outputDiagnostics.ResizeColumns,
                        ["resizeRows"] = outputDiagnostics.ResizeRows,
                        ["visibleControlSummary"] = outputDiagnostics.ControlSummary,
                        ["contentOmitted"] = true
                    });
                DeveloperDiagnostics.LogDebug(
                    "Terminal",
                    "Submitted terminal output batch to xterm.js.",
                    new Dictionary<string, object?>
                    {
                        ["submissionId"] = submissionId,
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["sessionGeneration"] = outputBatch.Generation,
                        ["length"] = outputBatch.Data.Length,
                        ["source"] = "ConPTY",
                        ["replayOrRecovery"] = false,
                        ["containsCR"] = outputBatch.Data.Contains('\r'),
                        ["containsLF"] = outputBatch.Data.Contains('\n'),
                        ["resizeGeneration"] = _terminalResizePolicy.ResizeGeneration,
                        ["resizeAdjacent"] = outputDiagnostics.ResizeAdjacent,
                        ["resizeAdjacentGeneration"] = outputDiagnostics.ResizeGeneration,
                        ["resizeElapsedMilliseconds"] = outputDiagnostics.ResizeElapsedMilliseconds,
                        ["resizeColumns"] = outputDiagnostics.ResizeColumns,
                        ["resizeRows"] = outputDiagnostics.ResizeRows,
                        ["visibleControlSummary"] = outputDiagnostics.ControlSummary,
                        ["resizeCausedSubmission"] = false,
                        ["contentOmitted"] = true
                    });
            }
            catch (Exception ex) when (IsWebView2LifecycleException(ex))
            {
                TerminalCriticalTrace.LogException(
                    "TerminalControl.WebView2.PostOutputLifecycleException",
                    ex,
                    new Dictionary<string, object?>
                    {
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["terminalSessionGeneration"] = outputBatch.Generation,
                        ["outputSequence"] = outputBatch.Sequence,
                        ["outputCharacterLength"] = outputBatch.Data.Length,
                        ["contentOmitted"] = true
                    });
                RetireWebView2Renderer("FlushPendingOutput", ex);
                RecordDroppedTerminalOutput(
                    _outputFlowController.DiscardInFlight(outputBatch.Generation, outputBatch.Sequence));
                ReportDroppedTerminalOutputIfNeeded();
            }
            catch (Exception ex)
            {
                RecordDroppedTerminalOutput(
                    _outputFlowController.DiscardInFlight(outputBatch.Generation, outputBatch.Sequence));
                ReportDroppedTerminalOutputIfNeeded();
                TerminalCriticalTrace.LogException(
                    "TerminalControl.WebView2.PostOutputException",
                    ex,
                    new Dictionary<string, object?>
                    {
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["terminalSessionGeneration"] = outputBatch.Generation,
                        ["outputSequence"] = outputBatch.Sequence,
                        ["outputCharacterLength"] = outputBatch.Data.Length,
                        ["contentOmitted"] = true
                    });
                System.Diagnostics.Debug.WriteLine(
                    $"[TerminalControl] PostWebMessageAsString failed: {ex.Message}");
                DeveloperDiagnostics.LogException(
                    "Terminal",
                    ex,
                    "Posting terminal output batch to WebView2 failed.",
                    new Dictionary<string, object?>
                    {
                        ["generation"] = outputBatch.Generation,
                        ["sequence"] = outputBatch.Sequence,
                        ["length"] = outputBatch.Data.Length,
                        ["contentOmitted"] = true
                    });
            }
        }

        private void RecordDroppedTerminalOutput(int characterCount)
        {
            if (characterCount <= 0)
            {
                return;
            }

            System.Threading.Interlocked.Add(ref _droppedOutputCharacters, characterCount);
        }

        private static void ReportDiscardedTerminalOutput(
            TerminalOutputGenerationInvalidationResult result,
            string reason)
        {
            if (result.DiscardedCharacters <= 0)
            {
                return;
            }

            AppLogger.Info("Terminal", $"Discarded queued terminal output during generation transition. Reason={reason}, SessionGeneration={result.Generation}, Length={result.DiscardedCharacters}, ContentOmitted=True.");
            DeveloperDiagnostics.LogInfo("Terminal", "Queued terminal output discarded during generation transition.", new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["sessionGeneration"] = result.Generation,
                ["length"] = result.DiscardedCharacters,
                ["contentOmitted"] = true
            });
        }

        private void ReportDroppedTerminalOutputIfNeeded()
        {
            var totalDroppedCharacters = System.Threading.Interlocked.Read(ref _droppedOutputCharacters);
            var previouslyReported = System.Threading.Interlocked.Exchange(
                ref _reportedDroppedOutputCharacters,
                totalDroppedCharacters);
            var newlyDroppedCharacters = totalDroppedCharacters - previouslyReported;
            if (newlyDroppedCharacters <= 0)
            {
                return;
            }

            AppLogger.Warning(
                "Terminal",
                $"Terminal renderer output was dropped under bounded flow control. NewlyDroppedCharacters={newlyDroppedCharacters}, TotalDroppedCharacters={totalDroppedCharacters}, ContentOmitted=True.");
            DeveloperDiagnostics.LogInfo(
                "Terminal",
                "Terminal renderer output was dropped under the bounded flow-control policy.",
                new Dictionary<string, object?>
                {
                    ["newlyDroppedCharacters"] = newlyDroppedCharacters,
                    ["totalDroppedCharacters"] = totalDroppedCharacters,
                    ["contentOmitted"] = true
                });
        }

        private void PostToWebView(string type, string data)
        {
            if (!_webView2Available) return;

            void Send()
            {
                if (!TryGetCoreWebView2($"PostToWebView:{type}", out var coreWebView2))
                {
                    return;
                }

                try
                {
                    if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseTerminalEnabled())
                    {
                        DeveloperDiagnostics.LogDebug(
                            "Terminal",
                            $"Posting host message to terminal. Type={type}.",
                            new Dictionary<string, object?>(DeveloperDiagnostics.CreatePrivateTextMetadata(data))
                            {
                                ["type"] = type
                            });
                    }
                    var msg = TerminalWebMessageSerializer.Serialize(type, data);
                    coreWebView2.PostWebMessageAsString(msg);
                }
                catch (Exception ex) when (IsWebView2LifecycleException(ex))
                {
                    RetireWebView2Renderer($"PostToWebView:{type}", ex);
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

        private void PostResizeCommitToWebView(
            TerminalResizeDecision resizeDecision,
            int terminalSessionGeneration)
        {
            if (!_webView2Available)
            {
                return;
            }

            void Send()
            {
                if (!TryGetCoreWebView2("PostResizeCommitToWebView", out var coreWebView2))
                {
                    return;
                }

                try
                {
                    coreWebView2.PostWebMessageAsString(
                        TerminalWebMessageSerializer.SerializeResizeCommit(
                            _rendererInstanceGeneration,
                            terminalSessionGeneration,
                            resizeDecision.ResizeGeneration,
                            resizeDecision.Columns,
                            resizeDecision.Rows));
                    TerminalCriticalTrace.LogStage(
                        "ResizeCommit.Posted",
                        new Dictionary<string, object?>
                        {
                            ["resizeGeneration"] = resizeDecision.ResizeGeneration,
                            ["rendererGeneration"] = _rendererInstanceGeneration,
                            ["terminalSessionGeneration"] = terminalSessionGeneration,
                            ["committedColumns"] = resizeDecision.Columns,
                            ["committedRows"] = resizeDecision.Rows,
                            ["outputSubmissionOccurred"] = false,
                            ["contentOmitted"] = true
                        });
                    DeveloperDiagnostics.LogInfo(
                        "Terminal",
                        "Resize commit posted to the browser renderer.",
                        new Dictionary<string, object?>
                        {
                            ["resizeGeneration"] = resizeDecision.ResizeGeneration,
                            ["rendererGeneration"] = _rendererInstanceGeneration,
                            ["terminalSessionGeneration"] = terminalSessionGeneration,
                            ["cols"] = resizeDecision.Columns,
                            ["rows"] = resizeDecision.Rows,
                            ["contentOmitted"] = true
                        });
                }
                catch (Exception ex) when (IsWebView2LifecycleException(ex))
                {
                    RetireWebView2Renderer("PostResizeCommitToWebView", ex);
                }
                catch (Exception ex)
                {
                    TerminalCriticalTrace.LogException(
                        "ResizeCommit.PostException",
                        ex,
                        new Dictionary<string, object?>
                        {
                            ["resizeGeneration"] = resizeDecision.ResizeGeneration,
                            ["rendererGeneration"] = _rendererInstanceGeneration,
                            ["terminalSessionGeneration"] = terminalSessionGeneration,
                            ["committedColumns"] = resizeDecision.Columns,
                            ["committedRows"] = resizeDecision.Rows,
                            ["contentOmitted"] = true
                        });
                    DeveloperDiagnostics.LogException(
                        "Terminal",
                        ex,
                        "Posting terminal resize commit to WebView2 failed.",
                        new Dictionary<string, object?>
                        {
                            ["resizeGeneration"] = resizeDecision.ResizeGeneration,
                            ["rendererGeneration"] = _rendererInstanceGeneration,
                            ["contentOmitted"] = true
                        });
                }
            }

            if (Dispatcher.CheckAccess())
                Send();
            else
                Dispatcher.BeginInvoke(Send);
        }

        private void LogXtermResizeTrace(JsonElement root)
        {
            var stage = GetStringProperty(root, "stage", "Xterm.ResizeTrace");
            var source = GetStringProperty(root, "source", "unknown");
            var metadata = CreateResizeTraceMetadata(root, source);
            TerminalCriticalTrace.LogStage(stage, metadata);
            DeveloperDiagnostics.LogDebug(
                "Terminal",
                $"Browser resize stage observed: {stage}.",
                metadata);
            var conceptualStage = stage switch
            {
                "Xterm.BeforeFit" => "TerminalFitBegin",
                "Xterm.AfterFit" => "TerminalFitCompleted",
                "ResizeObserver.Observed" => "TerminalViewportChanged",
                "Xterm.BeforeHostResizeCommit" => "TerminalResizeRequested",
                "Xterm.AfterHostResizeCommit" => "TerminalResizeApplied",
                "Xterm.ViewportRestored" => "TerminalViewportChanged",
                _ => null
            };
            if (conceptualStage is not null)
            {
                TerminalCriticalTrace.LogStage(conceptualStage, metadata);
            }
        }

        private void HandleResizeRequest(JsonElement root)
        {
            var cols = GetIntProperty(root, "proposedCols", GetIntProperty(root, "cols", 0));
            var rows = GetIntProperty(root, "proposedRows", GetIntProperty(root, "rows", 0));
            var source = GetStringProperty(root, "source", "unknown");
            var resizeDecision = _terminalResizePolicy.Evaluate(
                cols,
                rows,
                _rendererInstanceGeneration);

            var metadata = CreateResizeTraceMetadata(root, source);
            metadata["resizeGeneration"] = resizeDecision.ResizeGeneration;
            metadata["rendererGeneration"] = _rendererInstanceGeneration;
            metadata["terminalSessionGeneration"] = _outputFlowController.ActiveGeneration;
            metadata["requestedColumns"] = cols;
            metadata["requestedRows"] = rows;
            metadata["accepted"] = resizeDecision.Accepted;
            metadata["reason"] = resizeDecision.Reason;
            metadata["conptyColumns"] = resizeDecision.Accepted ? resizeDecision.Columns : 0;
            metadata["conptyRows"] = resizeDecision.Accepted ? resizeDecision.Rows : 0;
            metadata["outputSubmissionOccurred"] = false;
            metadata["filterFlushOccurred"] = false;

            TerminalCriticalTrace.LogStage("ResizeMessage.Received", metadata);
            TerminalCriticalTrace.LogStage("TerminalResizeRequested", metadata);
            TerminalCriticalTrace.LogStage(
                resizeDecision.Accepted ? "ResizePolicy.Accepted" : "ResizePolicy.Rejected",
                metadata);

            AppLogger.Debug(
                "Terminal",
                $"xterm resize requested. Cols={cols}, Rows={rows}, Accepted={resizeDecision.Accepted}, Reason={resizeDecision.Reason}.");
            DeveloperDiagnostics.LogInfo(
                "Terminal",
                "xterm terminal geometry evaluated.",
                metadata);
            DeveloperDiagnostics.LogInfo(
                "Terminal",
                resizeDecision.Accepted ? "Resize request accepted for ConPTY transaction." : "Resize request rejected by terminal resize policy.",
                metadata);

            if (!resizeDecision.Accepted)
            {
                return;
            }

            if (_outputFlowController.ActiveGeneration is not { } terminalSessionGeneration)
            {
                TerminalCriticalTrace.LogStage(
                    "ResizeTransaction.RejectedNoSession",
                    new Dictionary<string, object?>
                    {
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["resizeGeneration"] = resizeDecision.ResizeGeneration,
                        ["requestedColumns"] = resizeDecision.Columns,
                        ["requestedRows"] = resizeDecision.Rows,
                        ["contentOmitted"] = true
                    });
                return;
            }

            if (_resizeOutputBarrier.IsActive)
            {
                _pendingResizeDecision = resizeDecision;
                TerminalCriticalTrace.LogStage(
                    "ResizeTransaction.Coalesced",
                    new Dictionary<string, object?>
                    {
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["terminalSessionGeneration"] = terminalSessionGeneration,
                        ["resizeGeneration"] = resizeDecision.ResizeGeneration,
                        ["committedColumns"] = resizeDecision.Columns,
                        ["committedRows"] = resizeDecision.Rows,
                        ["contentOmitted"] = true
                    });
                return;
            }

            if (_outputFlowController.HasOutstandingOutput)
            {
                _pendingResizeDecision = resizeDecision;
                TerminalCriticalTrace.LogStage(
                    "ResizeTransaction.DeferredUntilRendererIdle",
                    new Dictionary<string, object?>
                    {
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["terminalSessionGeneration"] = terminalSessionGeneration,
                        ["resizeGeneration"] = resizeDecision.ResizeGeneration,
                        ["committedColumns"] = resizeDecision.Columns,
                        ["committedRows"] = resizeDecision.Rows,
                        ["contentOmitted"] = true
                    });
                RequestOutputFlush(scheduleFlush: true);
                return;
            }

            StartResizeTransaction(resizeDecision, terminalSessionGeneration);
        }

        private void StartResizeTransaction(
            TerminalResizeDecision resizeDecision,
            int terminalSessionGeneration)
        {
            var beginResult = _resizeOutputBarrier.Begin(
                _rendererInstanceGeneration,
                terminalSessionGeneration,
                resizeDecision.ResizeGeneration,
                resizeDecision.Columns,
                resizeDecision.Rows);
            if (!beginResult.Accepted)
            {
                _pendingResizeDecision = resizeDecision;
                return;
            }

            _resizeOutputBarrierTimer.Start();
            TerminalCriticalTrace.LogStage(
                "ResizeTransaction.Started",
                new Dictionary<string, object?>
                {
                    ["rendererGeneration"] = _rendererInstanceGeneration,
                    ["terminalSessionGeneration"] = terminalSessionGeneration,
                    ["resizeGeneration"] = resizeDecision.ResizeGeneration,
                    ["committedColumns"] = resizeDecision.Columns,
                    ["committedRows"] = resizeDecision.Rows,
                    ["contentOmitted"] = true
                });
            TerminalCriticalTrace.LogStage(
                "TerminalResizeBarrierBegin",
                new Dictionary<string, object?>
                {
                    ["rendererGeneration"] = _rendererInstanceGeneration,
                    ["terminalSessionGeneration"] = terminalSessionGeneration,
                    ["resizeGeneration"] = resizeDecision.ResizeGeneration,
                    ["committedColumns"] = resizeDecision.Columns,
                    ["committedRows"] = resizeDecision.Rows,
                    ["contentOmitted"] = true
                });

            try
            {
                TerminalResized?.Invoke(resizeDecision.Columns, resizeDecision.Rows);
            TerminalCriticalTrace.LogStage(
                "ResizePseudoConsole.CompletedBeforeXtermCommit",
                new Dictionary<string, object?>
                {
                    ["resizeGeneration"] = resizeDecision.ResizeGeneration,
                    ["rendererGeneration"] = _rendererInstanceGeneration,
                    ["terminalSessionGeneration"] = terminalSessionGeneration,
                    ["committedColumns"] = resizeDecision.Columns,
                    ["committedRows"] = resizeDecision.Rows,
                    ["contentOmitted"] = true
                });
                PostResizeCommitToWebView(resizeDecision, terminalSessionGeneration);
            }
            catch
            {
                CancelResizeTransaction("resize-handler-failed");
                throw;
            }
        }

        private void HandleResizeCommitAcknowledgement(JsonElement root)
        {
            if (!TryReadResizeCommitAcknowledgement(root, out var rendererGeneration, out var sessionGeneration, out var resizeGeneration, out var columns, out var rows))
            {
                TerminalCriticalTrace.LogStage(
                    "ResizeCommit.AckRejected",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = "invalid-ack-payload",
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["contentOmitted"] = true
                    });
                return;
            }

            TerminalResizeBarrierAcknowledgementResult acknowledgement;
            lock (_resizeOutputIntegrationSyncRoot)
            {
                acknowledgement = _resizeOutputBarrier.Acknowledge(
                    rendererGeneration,
                    sessionGeneration,
                    resizeGeneration,
                    columns,
                    rows);
                if (acknowledgement.Accepted)
                {
                    foreach (var bufferedOutput in acknowledgement.ReleasedOutput)
                    {
                        var enqueueResult = _outputFlowController.Enqueue(
                            bufferedOutput.TerminalSessionGeneration,
                            bufferedOutput.Data);
                        if (enqueueResult.DroppedCharacters > 0)
                        {
                            RecordDroppedTerminalOutput(enqueueResult.DroppedCharacters);
                        }
                    }
                }
            }

            TerminalCriticalTrace.LogStage(
                acknowledgement.Accepted ? "ResizeCommit.AckAccepted" : "ResizeCommit.AckRejected",
                new Dictionary<string, object?>
                {
                    ["rendererGeneration"] = rendererGeneration,
                    ["terminalSessionGeneration"] = sessionGeneration,
                    ["resizeGeneration"] = resizeGeneration,
                    ["actualColumns"] = columns,
                    ["actualRows"] = rows,
                    ["releasedCharacters"] = acknowledgement.BufferedCharacters,
                    ["reason"] = acknowledgement.Reason,
                    ["contentOmitted"] = true
                });
            DeveloperDiagnostics.LogInfo(
                "Terminal",
                acknowledgement.Accepted ? "Resize commit acknowledgement received; output barrier evaluated." : "Resize commit acknowledgement rejected.",
                new Dictionary<string, object?>
                {
                    ["rendererGeneration"] = rendererGeneration,
                    ["terminalSessionGeneration"] = sessionGeneration,
                    ["resizeGeneration"] = resizeGeneration,
                    ["cols"] = columns,
                    ["rows"] = rows,
                    ["releasedCharacters"] = acknowledgement.BufferedCharacters,
                    ["contentOmitted"] = true
                });

            if (acknowledgement.Accepted)
            {
                TerminalCriticalTrace.LogStage(
                    "TerminalResizeBarrierCompleted",
                    new Dictionary<string, object?>
                    {
                        ["rendererGeneration"] = rendererGeneration,
                        ["terminalSessionGeneration"] = sessionGeneration,
                        ["resizeGeneration"] = resizeGeneration,
                        ["actualColumns"] = columns,
                        ["actualRows"] = rows,
                        ["releasedCharacters"] = acknowledgement.BufferedCharacters,
                        ["contentOmitted"] = true
                    });
            }

            if (!acknowledgement.Accepted)
            {
                return;
            }

            _lastResizeCommitAcknowledgedUtc = DateTimeOffset.UtcNow;
            _lastResizeCommitAcknowledgedGeneration = resizeGeneration;
            _lastResizeCommitAcknowledgedColumns = columns;
            _lastResizeCommitAcknowledgedRows = rows;
            _resizeOutputBarrierTimer.Stop();
            // The release is deliberately posted after the host commit message. A queued
            // resize waits until those released bytes have also drained and been acked.
            RequestOutputFlush(scheduleFlush: true);
            TryStartPendingResizeIfRendererIdle();
        }

        private void TryStartPendingResizeIfRendererIdle()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(
                    new Action(TryStartPendingResizeIfRendererIdle),
                    DispatcherPriority.Background);
                return;
            }

            if (_resizeOutputBarrier.IsActive ||
                _pendingResizeDecision is not { } pendingDecision ||
                _outputFlowController.HasOutstandingOutput)
            {
                return;
            }

            _pendingResizeDecision = null;
            if (_outputFlowController.ActiveGeneration is not { } sessionGeneration)
            {
                return;
            }

            StartResizeTransaction(pendingDecision, sessionGeneration);
        }

        private void OnResizeOutputBarrierTimerTick(object? sender, EventArgs e)
        {
            var expiration = _resizeOutputBarrier.Expire();
            if (!expiration.Expired)
            {
                return;
            }

            _resizeOutputBarrierTimer.Stop();
            var cancelled = _resizeOutputBarrier.Cancel();
            _pendingResizeDecision = null;
            TerminalCriticalTrace.LogStage(
                "ResizeOutputBarrierTimeout",
                new Dictionary<string, object?>
                {
                    ["rendererGeneration"] = _rendererInstanceGeneration,
                    ["terminalSessionGeneration"] = _outputFlowController.ActiveGeneration,
                    ["bufferedCharacters"] = cancelled.BufferedCharacters,
                    ["bufferedChunks"] = cancelled.BufferedChunks,
                    ["contentOmitted"] = true
                });
            TerminalCriticalTrace.LogStage(
                "TerminalResizeBarrierTimeout",
                new Dictionary<string, object?>
                {
                    ["rendererGeneration"] = _rendererInstanceGeneration,
                    ["terminalSessionGeneration"] = _outputFlowController.ActiveGeneration,
                    ["bufferedCharacters"] = cancelled.BufferedCharacters,
                    ["bufferedChunks"] = cancelled.BufferedChunks,
                    ["contentOmitted"] = true
                });
            AppLogger.Error(
                "Terminal",
                $"Resize output barrier timed out before xterm.js acknowledged the exact grid. BufferedCharacters={cancelled.BufferedCharacters}, BufferedChunks={cancelled.BufferedChunks}, ContentOmitted=True.");
            DeveloperDiagnostics.LogError(
                "Terminal",
                "Resize output barrier timed out; retiring the renderer instead of releasing bytes under an unknown grid.",
                new Dictionary<string, object?>
                {
                    ["bufferedCharacters"] = cancelled.BufferedCharacters,
                    ["bufferedChunks"] = cancelled.BufferedChunks,
                    ["contentOmitted"] = true
                });
            RequeueCancelledResizeOutput(cancelled, _outputFlowController.ActiveGeneration);
            RequestOutputFlush(scheduleFlush: true);
        }

        private TerminalResizeBarrierCancellationResult CancelResizeTransaction(string reason)
        {
            TerminalResizeBarrierCancellationResult cancelled;
            lock (_resizeOutputIntegrationSyncRoot)
            {
                cancelled = _resizeOutputBarrier.Cancel();
            }

            _pendingResizeDecision = null;
            if (Dispatcher.CheckAccess())
            {
                _resizeOutputBarrierTimer.Stop();
            }
            else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                Dispatcher.BeginInvoke(
                    new Action(() => _resizeOutputBarrierTimer.Stop()),
                    DispatcherPriority.Send);
            }

            if (cancelled.BufferedCharacters > 0)
            {
                TerminalCriticalTrace.LogStage(
                    "ResizeTransaction.Cancelled",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["bufferedCharacters"] = cancelled.BufferedCharacters,
                        ["bufferedChunks"] = cancelled.BufferedChunks,
                        ["contentOmitted"] = true
                    });
            }

            return cancelled;
        }

        private void RequeueCancelledResizeOutput(
            TerminalResizeBarrierCancellationResult cancelled,
            int? expectedSessionGeneration)
        {
            if (expectedSessionGeneration is not { } sessionGeneration ||
                cancelled.ReleasedOutput.Count == 0)
            {
                return;
            }

            foreach (var bufferedOutput in cancelled.ReleasedOutput)
            {
                if (bufferedOutput.TerminalSessionGeneration != sessionGeneration)
                {
                    continue;
                }

                var enqueueResult = _outputFlowController.Enqueue(
                    bufferedOutput.TerminalSessionGeneration,
                    bufferedOutput.Data);
                if (enqueueResult.DroppedCharacters > 0)
                {
                    RecordDroppedTerminalOutput(enqueueResult.DroppedCharacters);
                }
            }
        }

        private void EnqueueVisibleOutputAfterResizeCancellation(int generation, string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                return;
            }

            var enqueueResult = _outputFlowController.Enqueue(generation, data);
            if (enqueueResult.DroppedCharacters > 0)
            {
                RecordDroppedTerminalOutput(enqueueResult.DroppedCharacters);
            }
        }

        private static bool TryReadResizeCommitAcknowledgement(
            JsonElement root,
            out int rendererGeneration,
            out int sessionGeneration,
            out long resizeGeneration,
            out int columns,
            out int rows)
        {
            rendererGeneration = GetIntProperty(root, "rendererGeneration", 0);
            sessionGeneration = GetIntProperty(root, "terminalSessionGeneration", 0);
            resizeGeneration = GetLongProperty(root, "resizeGeneration", 0);
            columns = GetIntProperty(root, "actualCols", 0);
            rows = GetIntProperty(root, "actualRows", 0);
            return rendererGeneration > 0 && sessionGeneration > 0 && resizeGeneration > 0 && columns > 0 && rows > 0;
        }

        private static Dictionary<string, object?> CreateResizeTraceMetadata(JsonElement root, string? source)
        {
            return new Dictionary<string, object?>
            {
                ["source"] = source ?? "unknown",
                ["cols"] = GetIntProperty(root, "cols", 0),
                ["rows"] = GetIntProperty(root, "rows", 0),
                ["cursorX"] = GetIntProperty(root, "cursorX", 0),
                ["cursorY"] = GetIntProperty(root, "cursorY", 0),
                ["baseY"] = GetIntProperty(root, "baseY", 0),
                ["viewportY"] = GetIntProperty(root, "viewportY", 0),
                ["absoluteCursorY"] = GetIntProperty(root, "absoluteCursorY", 0),
                ["scrollbackLength"] = GetIntProperty(root, "scrollbackLength", 0),
                ["scrollback"] = GetIntProperty(root, "scrollback", 0),
                ["alternateBuffer"] = GetBooleanProperty(root, "alternateBuffer"),
                ["clientWidth"] = GetIntProperty(root, "clientWidth", 0),
                ["clientHeight"] = GetIntProperty(root, "clientHeight", 0),
                ["canvasWidth"] = GetIntProperty(root, "canvasWidth", 0),
                ["canvasHeight"] = GetIntProperty(root, "canvasHeight", 0),
                ["viewportAnchorAtBottom"] = GetBooleanProperty(root, "viewportAnchorAtBottom"),
                ["viewportAnchorDistanceFromBottom"] = GetIntProperty(root, "viewportAnchorDistanceFromBottom", 0),
                ["viewportRestoreTarget"] = GetIntProperty(root, "viewportRestoreTarget", 0),
                ["proposedColumns"] = GetIntProperty(root, "proposedCols", 0),
                ["proposedRows"] = GetIntProperty(root, "proposedRows", 0),
                ["reportedColumns"] = GetIntProperty(root, "reportedCols", 0),
                ["reportedRows"] = GetIntProperty(root, "reportedRows", 0),
                ["committedColumns"] = GetIntProperty(root, "committedCols", 0),
                ["committedRows"] = GetIntProperty(root, "committedRows", 0),
                ["messageRendererGeneration"] = GetIntProperty(root, "rendererGeneration", 0),
                ["messageResizeGeneration"] = GetLongProperty(root, "resizeGeneration", 0),
                ["fitCommitted"] = GetBooleanProperty(root, "fitCommitted"),
                ["hostResizeCommit"] = GetBooleanProperty(root, "hostResizeCommit"),
                ["contentOmitted"] = true
            };
        }

        private void LogXtermOutputCursorTrace(JsonElement root)
        {
            var metadata = new Dictionary<string, object?>
            {
                ["stage"] = GetStringProperty(root, "stage", "unknown"),
                ["source"] = GetStringProperty(root, "source", "unknown"),
                ["rendererGeneration"] = GetIntProperty(root, "rendererGeneration", 0),
                ["terminalSessionGeneration"] = GetIntProperty(root, "terminalSessionGeneration", 0),
                ["outputSequence"] = GetLongProperty(root, "outputSequence", 0),
                ["submissionId"] = GetLongProperty(root, "submissionId", 0),
                ["resizeAdjacent"] = GetBooleanProperty(root, "resizeAdjacent"),
                ["resizeGeneration"] = GetLongProperty(root, "resizeGeneration", 0),
                ["resizeElapsedMilliseconds"] = GetDoubleProperty(root, "resizeElapsedMilliseconds", -1),
                ["outputCharacterLength"] = GetIntProperty(root, "outputCharacterLength", 0),
                ["hostControlSummary"] = GetStringProperty(root, "hostControlSummary", string.Empty),
                ["classificationSummary"] = GetStringProperty(root, "classificationSummary", string.Empty),
                ["carriageReturnCount"] = GetIntProperty(root, "carriageReturnCount", 0),
                ["lineFeedCount"] = GetIntProperty(root, "lineFeedCount", 0),
                ["carriageReturnLineFeedPairCount"] = GetIntProperty(root, "carriageReturnLineFeedPairCount", 0),
                ["escapeCount"] = GetIntProperty(root, "escapeCount", 0),
                ["csiCount"] = GetIntProperty(root, "csiCount", 0),
                ["csiCursorUpCount"] = GetIntProperty(root, "csiCursorUpCount", 0),
                ["csiCursorDownCount"] = GetIntProperty(root, "csiCursorDownCount", 0),
                ["csiCursorForwardCount"] = GetIntProperty(root, "csiCursorForwardCount", 0),
                ["csiCursorBackwardCount"] = GetIntProperty(root, "csiCursorBackwardCount", 0),
                ["csiCursorPositionCount"] = GetIntProperty(root, "csiCursorPositionCount", 0),
                ["csiEraseLineCount"] = GetIntProperty(root, "csiEraseLineCount", 0),
                ["csiEraseDisplayCount"] = GetIntProperty(root, "csiEraseDisplayCount", 0),
                ["csiSaveCursorCount"] = GetIntProperty(root, "csiSaveCursorCount", 0),
                ["csiRestoreCursorCount"] = GetIntProperty(root, "csiRestoreCursorCount", 0),
                ["csiInsertLineCount"] = GetIntProperty(root, "csiInsertLineCount", 0),
                ["csiDeleteLineCount"] = GetIntProperty(root, "csiDeleteLineCount", 0),
                ["csiScrollUpCount"] = GetIntProperty(root, "csiScrollUpCount", 0),
                ["csiScrollDownCount"] = GetIntProperty(root, "csiScrollDownCount", 0),
                ["csiSgrCount"] = GetIntProperty(root, "csiSgrCount", 0),
                ["csiOtherCount"] = GetIntProperty(root, "csiOtherCount", 0),
                ["oscCount"] = GetIntProperty(root, "oscCount", 0),
                ["otherEscapeCount"] = GetIntProperty(root, "otherEscapeCount", 0),
                ["otherControlCount"] = GetIntProperty(root, "otherControlCount", 0),
                ["printableCharacterCount"] = GetIntProperty(root, "printableCharacterCount", 0),
                ["csiCommandsJson"] = GetBoundedJsonArrayProperty(root, "csiCommands"),
                ["cursorDestinationsJson"] = GetBoundedJsonArrayProperty(root, "cursorDestinations"),
                ["cols"] = GetIntProperty(root, "cols", 0),
                ["rows"] = GetIntProperty(root, "rows", 0),
                ["cursorX"] = GetIntProperty(root, "cursorX", 0),
                ["cursorY"] = GetIntProperty(root, "cursorY", 0),
                ["baseY"] = GetIntProperty(root, "baseY", 0),
                ["viewportY"] = GetIntProperty(root, "viewportY", 0),
                ["absoluteCursorY"] = GetIntProperty(root, "absoluteCursorY", 0),
                ["scrollbackLength"] = GetIntProperty(root, "scrollbackLength", 0),
                ["beforeCursorX"] = GetIntProperty(root, "beforeCursorX", 0),
                ["beforeCursorY"] = GetIntProperty(root, "beforeCursorY", 0),
                ["beforeBaseY"] = GetIntProperty(root, "beforeBaseY", 0),
                ["beforeViewportY"] = GetIntProperty(root, "beforeViewportY", 0),
                ["beforeAbsoluteCursorY"] = GetIntProperty(root, "beforeAbsoluteCursorY", 0),
                ["deltaCursorX"] = GetIntProperty(root, "deltaCursorX", 0),
                ["deltaCursorY"] = GetIntProperty(root, "deltaCursorY", 0),
                ["deltaBaseY"] = GetIntProperty(root, "deltaBaseY", 0),
                ["deltaViewportY"] = GetIntProperty(root, "deltaViewportY", 0),
                ["deltaAbsoluteCursorY"] = GetIntProperty(root, "deltaAbsoluteCursorY", 0),
                ["contentOmitted"] = true
            };

            TerminalCriticalTrace.LogStage("Xterm.OutputCursorTrace", metadata);
            DeveloperDiagnostics.LogDebug(
                "Terminal",
                "Captured xterm cursor state around resize-adjacent renderer output write.",
                metadata);
        }

        private void LogXtermScreenStateSnapshot(JsonElement root)
        {
            var metadata = CreateResizeTraceMetadata(root, GetStringProperty(root, "reason", "unknown"));
            metadata["event"] = "TerminalScreenStateSnapshot";
            metadata["rendererIdentity"] = GetStringProperty(root, "rendererIdentity", "unknown");
            metadata["bufferType"] = GetStringProperty(root, "bufferType", "unknown");
            metadata["bufferLength"] = GetIntProperty(root, "bufferLength", 0);
            metadata["scrollbackExtent"] = GetIntProperty(root, "scrollbackExtent", 0);
            metadata["scrollPosition"] = GetIntProperty(root, "scrollPosition", 0);
            metadata["atBottom"] = GetBooleanProperty(root, "atBottom");
            metadata["selectionPresent"] = GetBooleanProperty(root, "selectionPresent");
            metadata["rendererDisposed"] = GetBooleanProperty(root, "rendererDisposed");
            metadata["promptBoundary"] = GetBooleanProperty(root, "promptBoundary");
            metadata["outputSequence"] = GetLongProperty(root, "outputSequence", 0);
            metadata["submissionId"] = GetLongProperty(root, "submissionId", 0);

            var markerRowsJson = root.TryGetProperty("markerRowsJson", out var markerRowsJsonProperty) &&
                markerRowsJsonProperty.ValueKind == JsonValueKind.String
                ? markerRowsJsonProperty.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(markerRowsJson) &&
                root.TryGetProperty("markerRows", out var legacyRows) &&
                legacyRows.ValueKind == JsonValueKind.Array)
            {
                markerRowsJson = legacyRows.GetRawText();
            }

            if (!TerminalMarkerSnapshotDiagnostics.TryNormalize(
                    markerRowsJson,
                    out var normalizedMarkerRowsJson,
                    out var markerSummary))
            {
                normalizedMarkerRowsJson = "[]";
                markerSummary = TerminalMarkerSnapshotDiagnostics.MarkerSnapshotSummary.Empty;
            }

            // Keep the normalized payload as a string. Passing a nested .NET
            // collection through diagnostic metadata collapses it to a type name.
            metadata["markerRowsJson"] = normalizedMarkerRowsJson;
            metadata["hasStart"] = markerSummary.HasStart;
            metadata["lowestNumberedMarker"] = markerSummary.LowestNumberedMarker;
            metadata["highestNumberedMarker"] = markerSummary.HighestNumberedMarker;
            metadata["numberedMarkerCount"] = markerSummary.NumberedMarkerCount;
            metadata["numberedSequenceJson"] = markerSummary.NumberedSequenceJson;
            metadata["hasEnd"] = markerSummary.HasEnd;
            metadata["markerRowCount"] = markerSummary.MarkerRowCount;
            metadata["contentOmitted"] = true;
            TerminalCriticalTrace.LogStage("TerminalScreenStateSnapshot", metadata);
            DeveloperDiagnostics.LogDebug("Terminal", "Captured bounded xterm screen-state snapshot.", metadata);
        }

        private static string GetStringProperty(JsonElement root, string name, string fallback)
        {
            return root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? fallback
                : fallback;
        }

        private static string GetBoundedJsonArrayProperty(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
            {
                return "[]";
            }

            var raw = property.GetRawText();
            return raw.Length <= 8_192 ? raw : "[]";
        }

        private static int GetIntProperty(JsonElement root, string name, int fallback)
        {
            return root.TryGetProperty(name, out var property) &&
                   property.ValueKind == JsonValueKind.Number &&
                   property.TryGetInt32(out var value)
                ? value
                : fallback;
        }

        private static long GetLongProperty(JsonElement root, string name, long fallback)
        {
            return root.TryGetProperty(name, out var property) &&
                   property.ValueKind == JsonValueKind.Number &&
                   property.TryGetInt64(out var value)
                ? value
                : fallback;
        }

        private static double GetDoubleProperty(JsonElement root, string name, double fallback)
        {
            return root.TryGetProperty(name, out var property) &&
                   property.ValueKind == JsonValueKind.Number &&
                   property.TryGetDouble(out var value)
                ? value
                : fallback;
        }

        private static bool? GetBooleanProperty(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var property))
            {
                return null;
            }

            return property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }

        private static string GetWindowsPtyBuildNumber()
        {
            if (!OperatingSystem.IsWindows())
            {
                return "0";
            }

            var buildNumber = Environment.OSVersion.Version.Build;
            return buildNumber > 0
                ? buildNumber.ToString(CultureInfo.InvariantCulture)
                : "0";
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (sender is not CoreWebView2 coreWebView2)
            {
                return;
            }

            if (!IsCurrentCoreWebView2(coreWebView2))
            {
                DeveloperDiagnostics.LogDebug(
                    "Terminal",
                    "Ignored stale WebView2 terminal callback.",
                    new Dictionary<string, object?>
                    {
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["staleCallbackIgnored"] = true,
                        ["contentOmitted"] = true
                    });
                return;
            }

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
                        StartupLifecycleTrace.Write("TerminalControl.Xterm", "READY");
                        var readySource = root.TryGetProperty("source", out var readySourceProp)
                            ? readySourceProp.GetString()
                            : "unknown";
                        TerminalStartupTrace.Write("XTERM_READY", $"source={readySource}; uiThread={Dispatcher.CheckAccess()}");
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

                    case "output_ack":
                        if (root.TryGetProperty("rendererGeneration", out var outputAckRendererProp) &&
                            outputAckRendererProp.TryGetInt32(out var outputAckRendererGeneration) &&
                            outputAckRendererGeneration == _rendererInstanceGeneration &&
                            root.TryGetProperty("generation", out var generationProp) &&
                            generationProp.TryGetInt32(out var generation) &&
                            root.TryGetProperty("sequence", out var sequenceProp) &&
                            sequenceProp.TryGetInt64(out var sequence))
                        {
                            var scheduleFlush = _outputFlowController.Acknowledge(generation, sequence);
                            TerminalOutputCorrelationTrace.RecordRendererAcknowledgement(generation, outputAckRendererGeneration, sequence);
                            PerformanceTrace.Record(
                                "instant",
                                "TerminalC2CCorrelation",
                                "RendererAcknowledged",
                                generation: generation,
                                properties: new Dictionary<string, object?>
                                {
                                    ["rendererSequence"] = sequence,
                                    ["rendererGeneration"] = _rendererInstanceGeneration,
                                    ["scheduleFlush"] = scheduleFlush,
                                    ["hasOutstandingOutput"] = _outputFlowController.HasOutstandingOutput,
                                    ["contentOmitted"] = true
                                });
                            TerminalStartupTrace.Write("JS_WRITE_COMPLETED", $"sequence={sequence}; generation={generation}; replayScheduled={scheduleFlush}");
                            TerminalCriticalTrace.LogStage(
                                "TerminalControl.RendererAcknowledgement",
                                new Dictionary<string, object?>
                                {
                                    ["rendererGeneration"] = _rendererInstanceGeneration,
                                    ["terminalSessionGeneration"] = generation,
                                    ["outputSequence"] = sequence,
                                    ["scheduleFlush"] = scheduleFlush
                            });
                            RequestOutputFlush(scheduleFlush);
                            if (!_outputFlowController.HasOutstandingOutput &&
                                _rendererRecovery.ReplayCompleted(_rendererInstanceGeneration))
                            {
                                TerminalCriticalTrace.LogStage(
                                    "RendererRecovery.ReplayCompleted",
                                    new Dictionary<string, object?>
                                    {
                                        ["rendererGeneration"] = _rendererInstanceGeneration,
                                        ["terminalSessionGeneration"] = generation,
                                        ["recoveryCycle"] = _rendererRecovery.Cycle,
                                        ["recoveryPhase"] = _rendererRecovery.Phase.ToString(),
                                        ["contentOmitted"] = true
                                    });
                            }
                            TryStartPendingResizeIfRendererIdle();
                        }
                        else
                        {
                            TerminalCriticalTrace.LogStage(
                                "TerminalControl.RendererAcknowledgement.Rejected",
                                new Dictionary<string, object?>
                                {
                                    ["rendererGeneration"] = _rendererInstanceGeneration,
                                    ["ackRendererGeneration"] = root.TryGetProperty("rendererGeneration", out var rejectedRendererProp) && rejectedRendererProp.TryGetInt32(out var rejectedRenderer) ? rejectedRenderer : 0,
                                    ["terminalSessionGeneration"] = root.TryGetProperty("generation", out var rejectedGenerationProp) && rejectedGenerationProp.TryGetInt32(out var rejectedGeneration) ? rejectedGeneration : 0,
                                    ["reason"] = "stale-or-invalid-renderer-generation",
                                    ["contentOmitted"] = true
                                });
                        }
                        break;

                    case "resize_commit_ack":
                        HandleResizeCommitAcknowledgement(root);
                        break;

                    case "terminal_compatibility":
                        {
                            var screenReaderMode = root.TryGetProperty("screenReaderMode", out var screenReaderProp) &&
                                screenReaderProp.ValueKind == JsonValueKind.True;
                            var unicodeWidthProvider = root.TryGetProperty("unicodeWidthProvider", out var unicodeWidthProp)
                                ? unicodeWidthProp.GetString()
                                : "unknown";
                            var binaryInputBridge = root.TryGetProperty("binaryInputBridge", out var binaryInputProp) &&
                                binaryInputProp.ValueKind == JsonValueKind.True;
                            var mousePasteGesture = root.TryGetProperty("mousePasteGesture", out var mousePasteProp)
                                ? mousePasteProp.GetString()
                                : "unknown";
                            var leaveTerminalShortcut = root.TryGetProperty("leaveTerminalShortcut", out var leaveShortcutProp)
                                ? leaveShortcutProp.GetString()
                                : "unknown";
                            var xtermVersion = root.TryGetProperty("xtermVersion", out var xtermVersionProp)
                                ? xtermVersionProp.GetString()
                                : "unknown";
                            var windowsPtyBackend = root.TryGetProperty("windowsPtyBackend", out var windowsPtyBackendProp)
                                ? windowsPtyBackendProp.GetString()
                                : "unknown";
                            var windowsPtyBuildNumber = root.TryGetProperty("windowsPtyBuildNumber", out var windowsPtyBuildProp) &&
                                windowsPtyBuildProp.TryGetInt32(out var parsedWindowsPtyBuildNumber)
                                ? parsedWindowsPtyBuildNumber
                                : 0;
                            AppLogger.Info(
                                "Terminal",
                                $"xterm compatibility configured. Version={xtermVersion}, WindowsPtyBackend={windowsPtyBackend}, WindowsPtyBuildNumber={windowsPtyBuildNumber}, ScreenReaderMode={screenReaderMode}, UnicodeWidthProvider={unicodeWidthProvider}, BinaryInputBridge={binaryInputBridge}, MousePasteGesture={mousePasteGesture}, LeaveTerminalShortcut={leaveTerminalShortcut}.");
                            DeveloperDiagnostics.LogInfo(
                                "Terminal",
                                "xterm compatibility capabilities were configured.",
                                new Dictionary<string, object?>
                                {
                                    ["screenReaderMode"] = screenReaderMode,
                                    ["unicodeWidthProvider"] = unicodeWidthProvider,
                                    ["binaryInputBridge"] = binaryInputBridge,
                                    ["mousePasteGesture"] = mousePasteGesture,
                                    ["leaveTerminalShortcut"] = leaveTerminalShortcut,
                                    ["xtermVersion"] = xtermVersion,
                                    ["windowsPtyBackend"] = windowsPtyBackend,
                                    ["windowsPtyBuildNumber"] = windowsPtyBuildNumber
                                });
                        }
                        break;

                    case "terminal_theme_applied":
                        {
                            var themeName = root.TryGetProperty("theme", out var themeProp)
                                ? themeProp.GetString()
                                : "unknown";
                            var background = root.TryGetProperty("background", out var backgroundProp)
                                ? backgroundProp.GetString()
                                : "unknown";
                            var foreground = root.TryGetProperty("foreground", out var foregroundProp)
                                ? foregroundProp.GetString()
                                : "unknown";
                            var selectionBackground = root.TryGetProperty("selectionBackground", out var selectionProp)
                                ? selectionProp.GetString()
                                : "unknown";
                            AppLogger.Info(
                                "Terminal",
                                $"xterm visual theme applied. Theme={themeName}, Background={background}, Foreground={foreground}, SelectionBackground={selectionBackground}.");
                            DeveloperDiagnostics.LogInfo(
                                "Terminal",
                                "xterm visual theme was applied.",
                                new Dictionary<string, object?>
                                {
                                    ["theme"] = themeName,
                                    ["background"] = background,
                                    ["foreground"] = foreground,
                                    ["selectionBackground"] = selectionBackground
                                });
                        }
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

                    case "xterm_resize_trace_error":
                        {
                            var traceReason = root.TryGetProperty("message", out var traceMsgProp) ? traceMsgProp.GetString() : "unknown";
                            var traceSource = root.TryGetProperty("source", out var traceSourceProp) ? traceSourceProp.GetString() : "unknown";
                            var traceStage = root.TryGetProperty("stage", out var traceStageProp) ? traceStageProp.GetString() : "unknown";
                            AppLogger.Warning("Terminal", $"xterm.js resize trace failed but resize commit handling continued. Source={traceSource}, Stage={traceStage}, Reason={traceReason}");
                            DeveloperDiagnostics.LogWarning(
                                "Terminal",
                                "xterm.js resize trace failed but resize commit handling continued.",
                                new Dictionary<string, object?>
                                {
                                    ["source"] = traceSource,
                                    ["stage"] = traceStage,
                                    ["reason"] = traceReason,
                                    ["contentOmitted"] = true
                                });
                        }
                        break;

                    case "xterm_output_cursor_trace":
                        LogXtermOutputCursorTrace(root);
                        break;

                    case "xterm_screen_state_snapshot":
                        LogXtermScreenStateSnapshot(root);
                        break;

                    case "xterm_screen_state_snapshot_error":
                        DeveloperDiagnostics.LogWarning(
                            "Terminal",
                            "xterm screen-state snapshot failed; terminal behavior continued.",
                            new Dictionary<string, object?>
                            {
                                ["reason"] = GetStringProperty(root, "reason", "unknown"),
                                ["exception"] = GetStringProperty(root, "message", "unknown"),
                                ["contentOmitted"] = true
                            });
                        break;

                    case "xterm_output_cursor_trace_error":
                        {
                            var traceReason = root.TryGetProperty("message", out var traceMsgProp) ? traceMsgProp.GetString() : "unknown";
                            var traceSource = root.TryGetProperty("source", out var traceSourceProp) ? traceSourceProp.GetString() : "unknown";
                            var traceStage = root.TryGetProperty("stage", out var traceStageProp) ? traceStageProp.GetString() : "unknown";
                            AppLogger.Warning("Terminal", $"xterm.js output cursor trace failed but terminal output handling continued. Source={traceSource}, Stage={traceStage}, Reason={traceReason}");
                            DeveloperDiagnostics.LogWarning(
                                "Terminal",
                                "xterm.js output cursor trace failed but terminal output handling continued.",
                                new Dictionary<string, object?>
                                {
                                    ["source"] = traceSource,
                                    ["stage"] = traceStage,
                                    ["reason"] = traceReason,
                                    ["contentOmitted"] = true
                                });
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
                            AppLogger.Debug("Terminal", $"xterm activation message received. Source={source}, WebViewFocused={TryGetWebViewKeyboardFocusWithin()}.");
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
                                $"xterm focus reported. Source={source}, DocumentHasFocus={documentHasFocus}, ActiveElement={activeElement}, WebViewFocused={TryGetWebViewKeyboardFocusWithin()}.");
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
                            AppLogger.Debug("Terminal", $"xterm blur reported. Source={source}, WebViewFocused={TryGetWebViewKeyboardFocusWithin()}.");
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
                            var copyLength = copyText?.Length ?? 0;
                            var copyLineCount = copyText is null ? 0 : copyText.Split('\n').Length;
                            DeveloperDiagnostics.LogUserAction(
                                "Terminal",
                                "TerminalSelectionCopyRequested",
                                "xterm requested clipboard copy for its current selection.",
                                new Dictionary<string, object?>
                                {
                                    ["selectionLength"] = copyLength,
                                    ["selectionLineCount"] = copyLineCount,
                                    ["contentOmitted"] = true
                                });

                            if (copyText is not null)
                            {
                                try
                                {
                                    System.Windows.Clipboard.SetText(copyText, System.Windows.TextDataFormat.UnicodeText);
                                    ResetClipboardFailureEpisode(ClipboardCopyOperation);
                                    AppLogger.Debug("Terminal", $"Terminal selection copied to the Windows clipboard. Length={copyLength}, Lines={copyLineCount}, ContentOmitted=True.");
                                    DeveloperDiagnostics.LogStateTransition(
                                        "Terminal",
                                        "TerminalSelectionClipboard",
                                        "Requested",
                                        "Copied",
                                        "xterm selection was accepted by the host clipboard bridge.",
                                        new Dictionary<string, object?>
                                        {
                                            ["selectionLength"] = copyLength,
                                            ["selectionLineCount"] = copyLineCount,
                                            ["contentOmitted"] = true
                                        });
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine("[TerminalControl] Clipboard.SetText failed.");
                                    LogClipboardFailure(ClipboardCopyOperation, ex);
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
                                ResetClipboardFailureEpisode(ClipboardPasteReadOperation);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine("[TerminalControl] Clipboard paste read failed.");
                                LogClipboardFailure(ClipboardPasteReadOperation, ex);
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
                                    _firstInputObservedForDiagnostics = true;
                                    AppLogger.Info("Terminal", $"Received first xterm.js input message from WebView2. Length={data.Length}, ContentOmitted=True.");
                                }
                                else if (_inputInfoLogCount < 4)
                                {
                                    _inputInfoLogCount++;
                                    AppLogger.Info("Terminal", $"Received additional xterm.js input message from WebView2. Index={_inputInfoLogCount + 1}, Length={data.Length}, ContentOmitted=True.");
                                }

                                AppLogger.Debug("Terminal", $"xterm input received from WebView2. Length={data.Length}, ContentOmitted=True.");
                                DeveloperDiagnostics.LogUserAction(
                                    "Terminal",
                                    "TerminalInput",
                                    "xterm input received from WebView2.",
                                    new Dictionary<string, object?>(DeveloperDiagnostics.CreatePrivateTextMetadata(data)));
                                RaiseTerminalActivated("xterm.onData");
                                UserInput?.Invoke(data);
                            }
                        }
                        break;

                    case "xterm_resize_trace":
                        LogXtermResizeTrace(root);
                        break;

                    case "resize":
                    case "resize_request":
                        HandleResizeRequest(root);
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

        private void LogClipboardFailure(string operation, Exception exception)
        {
            if (!TryBeginClipboardFailureEpisode(operation))
            {
                return;
            }

            var message = operation == ClipboardCopyOperation
                ? "Terminal clipboard copy failed; clipboard content was omitted."
                : "Terminal clipboard paste read failed; clipboard content was omitted.";
            var metadata = new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["exceptionType"] = exception.GetType().FullName,
                ["hResult"] = exception.HResult,
                ["dispatcherCheckAccess"] = Dispatcher.CheckAccess(),
                ["managedThreadId"] = Environment.CurrentManagedThreadId,
                ["contentOmitted"] = true
            };

            AppLogger.Warning("Terminal", message);
            DeveloperDiagnostics.LogWarning("Terminal", message, metadata);
        }

        private bool TryBeginClipboardFailureEpisode(string operation)
        {
            if (operation == ClipboardCopyOperation)
            {
                if (_clipboardCopyFailureEpisodeActive)
                {
                    return false;
                }

                _clipboardCopyFailureEpisodeActive = true;
                return true;
            }

            if (_clipboardPasteReadFailureEpisodeActive)
            {
                return false;
            }

            _clipboardPasteReadFailureEpisodeActive = true;
            return true;
        }

        private void ResetClipboardFailureEpisode(string operation)
        {
            if (operation == ClipboardCopyOperation)
            {
                _clipboardCopyFailureEpisodeActive = false;
                return;
            }

            _clipboardPasteReadFailureEpisodeActive = false;
        }

        private void FlushOutputQueue()
        {
            _isReady = true;
            TerminalStartupTrace.Write("RENDERER_READY", $"controlId={GetHashCode():X8}; uiThread={Dispatcher.CheckAccess()}");
            var activeGeneration = _outputFlowController.ActiveGeneration;
            var replayedHistoryCharacters = 0;
            var replacingRenderer = _rendererRecovery.RendererReady(_rendererInstanceGeneration);
            if (replacingRenderer)
            {
                TerminalCriticalTrace.LogStage(
                    "TerminalReplayBegin",
                    new Dictionary<string, object?>
                    {
                        ["rendererGeneration"] = _rendererInstanceGeneration,
                        ["terminalSessionGeneration"] = activeGeneration,
                        ["recoveryCycle"] = _rendererRecovery.Cycle,
                        ["contentOmitted"] = true
                    });
                _outputFlowController.RestoreRenderer();
            }

            if (replacingRenderer && activeGeneration is { } generation)
            {
                foreach (var historyChunk in _persistentOutputHistory.Snapshot(generation))
                {
                    var replayResult = _outputFlowController.Enqueue(generation, historyChunk);
                    replayedHistoryCharacters += replayResult.AcceptedCharacters;
                }

                if (!_outputFlowController.HasOutstandingOutput)
                {
                    _rendererRecovery.ReplayCompleted(_rendererInstanceGeneration);
                    TerminalCriticalTrace.LogStage(
                        "TerminalReplayEnd",
                        new Dictionary<string, object?>
                        {
                            ["rendererGeneration"] = _rendererInstanceGeneration,
                            ["terminalSessionGeneration"] = generation,
                            ["replayedHistoryCharacters"] = replayedHistoryCharacters,
                            ["rawOutputNotReplayed"] = true,
                            ["contentOmitted"] = true
                        });
                }
            }

            var scheduleFlush = _outputFlowController.SetRendererReady();
            _rendererReadyReplayPending = scheduleFlush;
            TerminalStartupTrace.Write("RENDERER_READY_REPLAY_START", $"controlId={GetHashCode():X8}; replayScheduled={scheduleFlush}; outstandingOutput={_outputFlowController.HasOutstandingOutput}");

            AppLogger.Info("Terminal", $"xterm.js renderer is ready; bounded terminal output delivery is enabled. ReplayedHistoryCharacters={replayedHistoryCharacters}.");
            DeveloperDiagnostics.LogInfo("Terminal", "xterm.js renderer is ready; bounded terminal output delivery is enabled.", new Dictionary<string, object?>
            {
                ["rendererGeneration"] = _rendererInstanceGeneration,
                ["sessionGeneration"] = activeGeneration,
                ["replayedHistoryCharacters"] = replayedHistoryCharacters,
                ["historyOwnership"] = "terminal-session",
                ["recoveryPhase"] = _rendererRecovery.Phase.ToString(),
                ["recoveryCycle"] = _rendererRecovery.Cycle,
                ["contentOmitted"] = true
            });
            RequestOutputFlush(scheduleFlush);
            TerminalStartupTrace.Write("RENDERER_READY_REPLAY_END", $"controlId={GetHashCode():X8}; replayScheduled={scheduleFlush}");

            // Auto-focus so the user can type immediately.
            ActivateTerminalHost("FlushOutputQueue");

            // Notify subscribers (e.g. MainWindow) that the terminal is ready.
            // This is the signal to start the ConPTY session so output has
            // somewhere to go as soon as it arrives.
            TerminalReady?.Invoke();
        }

        private void WebView_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not WebView2 renderer ||
                !TryGetCurrentRenderer(renderer, out var lifecycle) ||
                lifecycle.IsRetired)
            {
                return;
            }

            AppLogger.Debug("Terminal", $"Terminal host received mouse activation. Button={e.ChangedButton}, WebViewFocused={TryGetWebViewKeyboardFocusWithin()}.");
            DeveloperDiagnostics.LogUserAction("Terminal", "TerminalMouseActivation", "Terminal host received mouse activation.", new Dictionary<string, object?> { ["button"] = e.ChangedButton.ToString() });
            RaiseTerminalActivated($"WebView.{e.ChangedButton}MouseDown");
            ActivateTerminalHost($"WebView.{e.ChangedButton}MouseDown");
        }

        private void WebView_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            if (sender is not WebView2 renderer ||
                !TryGetCurrentRenderer(renderer, out var lifecycle) ||
                lifecycle.IsRetired)
            {
                return;
            }

            AppLogger.Debug("Terminal", $"WebView2 host received keyboard focus. NewFocus={e.NewFocus?.GetType().Name ?? "(null)"}.");
            DeveloperDiagnostics.LogUserAction("Terminal", "TerminalKeyboardFocus", "WebView2 host received keyboard focus.", new Dictionary<string, object?> { ["newFocus"] = e.NewFocus?.GetType().FullName });
            RaiseTerminalActivated("WebView.GotKeyboardFocus");
        }

        private void WebView_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            if (sender is not WebView2 renderer ||
                !TryGetCurrentRenderer(renderer, out var lifecycle) ||
                lifecycle.IsRetired)
            {
                return;
            }

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
                if (!_webView2Available ||
                    !TryGetCurrentRenderer(out var renderer, out var lifecycle) ||
                    !TryGetCoreWebView2($"ActivateTerminalHost:{source}", out _, renderer, lifecycle))
                {
                    return;
                }

                bool focusResult;
                bool isKeyboardFocused;
                bool isKeyboardFocusWithin;
                try
                {
                    focusResult = renderer.Focus();
                    isKeyboardFocused = renderer.IsKeyboardFocused;
                    isKeyboardFocusWithin = renderer.IsKeyboardFocusWithin;
                }
                catch (Exception ex) when (IsWebView2LifecycleException(ex))
                {
                    RetireWebView2Renderer($"ActivateTerminalHost:{source}", ex, renderer, lifecycle);
                    return;
                }

                AppLogger.Debug(
                    "Terminal",
                    $"Terminal host focus requested. Source={source}, FocusResult={focusResult}, IsKeyboardFocused={isKeyboardFocused}, IsKeyboardFocusWithin={isKeyboardFocusWithin}, CoreReady=True.");
                DeveloperDiagnostics.LogUiThreadDispatch("Terminal", "TerminalFocusDispatch", "Terminal host focus requested.", Dispatcher.CheckAccess(), new Dictionary<string, object?> { ["source"] = source, ["focusResult"] = focusResult });
                PostToWebView("focus", string.Empty);
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void RaiseTerminalActivated(string source)
        {
            TerminalActivated?.Invoke(source);
        }

        private static string SummarizeVtControls(string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                return "(none)";
            }

            var controls = new List<string>();
            for (var index = 0; index + 2 < data.Length && controls.Count < 12; index++)
            {
                if (data[index] != '\x1b' || data[index + 1] != '[')
                {
                    continue;
                }

                var end = index + 2;
                while (end < data.Length && (data[end] < '@' || data[end] > '~'))
                {
                    end++;
                }

                if (end >= data.Length)
                {
                    break;
                }

                controls.Add($"CSI {data[(index + 2)..(end + 1)]}");
                index = end;
            }

            return controls.Count == 0 ? "(none)" : string.Join(", ", controls);
        }

    }
}
