# DEBUGGER_AND_TERMINAL

## Terminal model

The current app uses one shared interactive PowerShell terminal session.

Observed pieces:

- `PowerShellStudio.Shell/Controls/TerminalControl.xaml.cs`
- `PowerShellStudio.PowerShell/Services/LiveConsoleService.cs`
- `PowerShellStudio.UI/ViewModels/MainWindowViewModel.cs`

The terminal host is:

- WebView2
- `xterm.js`
- backed by a real PowerShell process through Windows ConPTY when available

If ConPTY startup fails, `LiveConsoleService` has a fallback redirected-session path.

## Expected run behavior

### Run Full Script

Observed in `MainWindowViewModel`:

- Full Run dispatches the active tab into the shared terminal session.
- If the tab is clean and still matches its saved `.ps1` file, the saved path can be used as the execution identity.
- If disk content no longer matches the visible editor content, the visible editor content is used instead and the tab is marked stale.

### Run Selection / F8

Observed in `MainWindow.xaml.cs` and `MainWindowViewModel.cs`:

- `F8` is wired to Run Selection.
- If text is selected, that selection is sent.
- If nothing is selected, the current line is sent instead.
- Run Selection intentionally executes in the shared terminal scope so variables, modules, and working directory carry forward.

This current-line fallback is important and should be preserved unless the user explicitly wants a behavior change.

## Expected debugger behavior

Observed pieces:

- `PowerShellStudio.Shell/Debug/PsesDebugSession.cs`
- `PowerShellStudio.Shell/MainWindow.xaml.cs`

### Breakpoints

- Breakpoints are tracked per open tab.
- The shell collects enabled breakpoints from open tabs before starting a debug session.
- Breakpoint glyphs and background rendering are owned by shell/editor integration code.

### Starting debug

- If the selected tab has enabled breakpoints, plain Run currently redirects into debug startup instead of a normal run.
- Debug can also be started explicitly from the debug command path.
- If a saved file cannot be trusted, debug falls back to a temporary snapshot of the visible editor content.

### Paused breakpoint state

When PowerShell hits a breakpoint, the expected paused state is:

- debug session state becomes paused
- current location highlight is updated in the editor
- status text changes to indicate breakpoint hit
- variables/call stack refresh can run
- stepping/continue controls become enabled
- Run should remain blocked while the debug session is active

### Continue / stepping / stop

Observed commands:

- Continue
- Step Over
- Step Into
- Step Out
- Stop Debug

These are issued only when the debug session is paused, except Stop Debug which is available while a debug session exists.

## Toolbar/menu state must match debugger state

`MainWindow.xaml.cs` explicitly synchronizes toolbar/menu enablement with debug state through `RefreshDebugCommandAvailability(...)`.

Future changes in this area must preserve:

- Start Debug enabled only when a new debug session can start
- Step Into / Step Over / Step Out / Continue enabled only when paused
- Stop Debug enabled whenever a session exists
- `ViewModel.IsDebugSessionActive` kept in sync so Run can be disabled while debugging

If toolbar state and debug state drift apart, users will see misleading controls even when the underlying session is correct.

## Console visibility rules

The codebase is trying to keep app/helper protocol details out of the visible PowerShell terminal:

- `LiveConsoleService` strips execution sentinels before forwarding terminal output.
- `TerminalControl` comments explicitly say app diagnostics should go to the app log, not the visible terminal.
- `MainWindowViewModel` comments note that host lifecycle/status messages should not be interleaved into the live terminal stream.

Future changes must preserve this rule:

- helper commands, sentinels, metadata markers, and app diagnostics should not leak into the user-visible console output

## Prompt return and clear behavior

Prompt restoration is fragile enough that the current code documents it inline.

Observed behavior:

- Clear Console prefers sending `cls` through the live session.
- The code avoids mixing display-only clears with input tricks because that previously left broken prompt state such as stray continuation prompts.

Future terminal fixes should preserve real PowerShell prompt recovery, not just clear pixels in xterm.

## Execution policy concerns

Several PowerShell subprocess paths use `-ExecutionPolicy Bypass`, including:

- live terminal startup
- debug session startup
- metadata/completion helper processes
- installer scripts
- script execution/EXE export paths

Implications:

- execution-policy behavior is a real runtime dependency in this app
- changes here can break Run, Debug, metadata loading, packaging install, or exported EXE behavior
- do not remove or tighten these paths casually without real Windows verification
