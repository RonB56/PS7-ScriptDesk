# KNOWN_FRAGILE_AREAS

This list is based on repo inspection and inline code comments, not on executed end-to-end testing.

## 1. Debugger toolbar and session state sync

Files:

- `PowerShellStudio.Shell/MainWindow.xaml.cs`
- `PowerShellStudio.Shell/Debug/PsesDebugSession.cs`

Why fragile:

- The shell manually enables/disables debug commands based on session existence and paused state.
- The view model also depends on `IsDebugSessionActive` to block Run during debugging.

Risk:

- UI buttons can become misleading if session state changes but toolbar/menu state does not refresh in lockstep.

## 2. Integrated console prompt return and clear behavior

Files:

- `PowerShellStudio.UI/ViewModels/MainWindowViewModel.cs`
- `PowerShellStudio.PowerShell/Services/LiveConsoleService.cs`
- `PowerShellStudio.Shell/Controls/TerminalControl.xaml.cs`

Why fragile:

- The code explicitly documents prior prompt corruption when using display-only clears.
- The current implementation prefers sending `cls` through the real PowerShell session to keep PSReadLine, ConsoleHost, ConPTY, and xterm in agreement.

Risk:

- Seemingly harmless terminal UX changes can break prompt redraw, leave continuation prompts behind, or desynchronize visible output from actual shell state.

## 3. Run Selection / F8 behavior

Files:

- `PowerShellStudio.Shell/MainWindow.xaml.cs`
- `PowerShellStudio.UI/ViewModels/MainWindowViewModel.cs`

Why fragile:

- `F8` is expected to run the current selection, or the current line if nothing is selected.
- The code exists partly to avoid AvalonEdit selection/focus edge cases that made the command appear to do nothing.

Risk:

- Refactors around editor focus, command routing, or selection state can easily regress this legacy-ISE-style behavior.

## 4. Metadata/completion loading and cache health

Files:

- `PowerShellStudio.Shell/Editor/PowerShellCompletionService.cs`
- `PowerShellStudio.Shell/Editor/EditorMetadataCacheStore.cs`
- `PowerShellStudio.Shell/Editor/EditorMetadataBuilderHost.cs`

Why fragile:

- Metadata is loaded asynchronously, cached per runtime, and refreshed through helper processes.
- The repo contains cache validation, quarantine, migration, deletion, warmup status UI, and fallback behavior, which is a sign this area has already needed defensive handling.

Risk:

- Runtime/version changes, cache mismatches, helper-launch failures, or protocol drift can degrade IntelliSense and quick info without obviously breaking the editor.

## 5. Execution policy dependent subprocesses

Files:

- `PowerShellStudio.PowerShell/Services/LiveConsoleService.cs`
- `PowerShellStudio.PowerShell/Services/ScriptExecutionService.cs`
- `PowerShellStudio.Shell/Debug/PsesDebugSession.cs`
- `PowerShellStudio.Package/Installer/Install.cmd`
- `PowerShellStudio.Package/Installer/Install.ps1`

Why fragile:

- Multiple app subsystems start PowerShell with `-ExecutionPolicy Bypass`.

Risk:

- Changes here can break terminal startup, Run, Debug, metadata helpers, installer behavior, or EXE export flows.

## 6. MSIX/Desktop Bridge packaging

Files:

- `PowerShellStudio.Package/PowerShellStudio.Package.wapproj`
- `PowerShellStudio.Package/Package.appxmanifest`

Why fragile:

- Packaging depends on Windows-specific SDK/Desktop Bridge tooling, signing, installer script copying, and warning suppressions tied to dependency compatibility.

Risk:

- Packaging can fail independently of app compilation.
- Signing or WAP environment differences can block release work on another machine.

## 7. WPF / WinForms boundary

Files:

- `PowerShellStudio.Shell/PowerShellStudio.Shell.csproj`
- `PowerShellStudio.Shell/Services/UserPromptService.cs`

Why fragile:

- The shell is a WPF app but also enables WinForms and uses `FolderBrowserDialog` from WinForms.

Risk:

- Dialog behavior, threading assumptions, or future UI cleanup work can accidentally break folder-selection flows.

## 8. Window title, version text, and branding consistency

Files:

- `PowerShellStudio.Application/Utilities/ApplicationBranding.cs`
- `PowerShellStudio.UI/ViewModels/MainWindowViewModel.cs`
- `PowerShellStudio.Shell/PowerShellStudio.Shell.csproj`
- `PowerShellStudio.Package/Package.appxmanifest`

Why fragile:

- Public branding is `PS7 ScriptDesk`, but legacy/internal naming remains `PowerShellStudio`.
- Shell project version and package manifest version currently differ.

Risk:

- End-user branding, title bar text, package identity, installer output, and Local AppData migration behavior can drift out of sync.

## 9. SDK default-glob guardrails

Files:

- `PowerShellStudio.UI/PowerShellStudio.UI.csproj`
- `PowerShellStudio.TerminalSpike/PowerShellStudio.TerminalSpike.csproj`

Why fragile:

- Both projects contain explicit `Compile Remove` / resource exclusion rules to avoid accidentally compiling nested copied source trees.

Risk:

- Folder moves or copied content can silently change build inputs if those guards are removed or not updated.
