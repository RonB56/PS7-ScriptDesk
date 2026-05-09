# CODEBASE_OVERVIEW

## What this app is

`PS7 ScriptDesk` is a Windows-only WPF desktop application for PowerShell 7.x. It behaves like an ISE-style scripting tool: users can open and edit scripts, run a full script, run a selection, work in an integrated PowerShell terminal, set breakpoints, and step through debugging sessions.

Public branding is `PS7 ScriptDesk`. Internal/repo names still commonly use `PowerShellStudio`.

## Solution and project layout

Observed root solution entry point:

- `PowerShellStudio.slnx`

Also present:

- `PowerShellStudio.UI/PowerShellStudio.slnx`
  - Appears to be a smaller project subset and does not include the packaging project.

Projects seen in the repo:

- `PowerShellStudio.Shell`
  - Main WPF shell project.
  - Targets `net10.0-windows`.
  - Uses WPF and also enables WinForms.
- `PowerShellStudio.UI`
  - View models and commands.
- `PowerShellStudio.PowerShell`
  - Runtime discovery, live terminal session, script execution, EXE export.
- `PowerShellStudio.Infrastructure`
  - Settings persistence, file/workspace services.
- `PowerShellStudio.Application`
  - Shared interfaces, logging, branding, temp storage helpers.
- `PowerShellStudio.Domain`
  - Domain models such as settings and runtime metadata.
- `PowerShellStudio.Package`
  - MSIX/Desktop Bridge packaging project.
- `PowerShellStudio.Editor`
  - Separate project exists, but the visible editor-heavy implementation currently appears to live mostly under `PowerShellStudio.Shell/Editor`.
- `PowerShellStudio.TerminalSpike`
  - Experimental/spike project. Present in the root solution. It appears non-production, but that is an inference from naming and project contents, not a guaranteed release rule.

## Where important code lives

### App startup and composition

- `PowerShellStudio.Shell/App.xaml`
- `PowerShellStudio.Shell/App.xaml.cs`
- `PowerShellStudio.Shell/Composition/AppBootstrapper.cs`

`AppBootstrapper` manually wires the concrete services and creates `MainWindow`.

### Main shell and UI behavior

- `PowerShellStudio.Shell/MainWindow.xaml`
- `PowerShellStudio.Shell/MainWindow.xaml.cs`
- `PowerShellStudio.UI/ViewModels/MainWindowViewModel.cs`

Current behavior is split across these two layers:

- Shell layer: WPF event handling, AvalonEdit wiring, debugger UI, terminal host hookup, help wiring, theme application.
- UI view model: tab/workspace state, run/stop orchestration, console session status, runtime selection, persisted state restoration.

### Editor and authoring

Primary editor-related code observed under:

- `PowerShellStudio.Shell/Editor/`

Notable files:

- `BindableTextEditor.cs`
- `BreakpointGlyphMargin.cs`
- `BreakpointLineBackgroundRenderer.cs`
- `DiagnosticGlyphMargin.cs`
- `PowerShellIntelliSenseService.cs`
- `PowerShellCompletionService.cs`
- `PowerShellDiagnosticsService.cs`
- `PowerShellSyntaxColorizer.cs`
- `EditorMetadataCacheStore.cs`
- `EditorMetadataBuilderHost.cs`

This area owns syntax highlighting, diagnostics, IntelliSense, metadata warmup, cached command/help metadata, and breakpoint visuals.

### Terminal / console

- `PowerShellStudio.Shell/Controls/TerminalControl.xaml`
- `PowerShellStudio.Shell/Controls/TerminalControl.xaml.cs`
- `PowerShellStudio.PowerShell/Services/LiveConsoleService.cs`

Observed architecture:

- WPF hosts a WebView2-based `xterm.js` terminal surface.
- `LiveConsoleService` manages the single shared ConPTY-backed PowerShell session.
- Raw VT/ANSI output is forwarded to the terminal host.

### Run / script execution

- `PowerShellStudio.UI/ViewModels/MainWindowViewModel.cs`
- `PowerShellStudio.PowerShell/Services/LiveConsoleService.cs`
- `PowerShellStudio.PowerShell/Services/ScriptExecutionService.cs`

The current app appears to favor dispatching runs into the shared live terminal session rather than using a completely separate output-only runner.

### Debugger and breakpoints

- `PowerShellStudio.Shell/Debug/`
- `PowerShellStudio.Shell/MainWindow.xaml.cs`

Notable files:

- `IDebugSession.cs`
- `PsesDebugSession.cs`
- `DebugSessionState.cs`
- `DebugBreakpointInfo.cs`

The shell appears to own breakpoint collection, toolbar/menu enablement, current-line highlight, and debug panels. `PsesDebugSession` owns the background PowerShell debug process and its marker-based protocol.

### Settings and persistence

- `PowerShellStudio.Infrastructure/Services/ApplicationSettingsService.cs`
- `PowerShellStudio.Domain/Models/ApplicationSettings.cs`

Settings are stored under Local AppData using the new internal name `PS7ScriptDesk`, with migration logic from legacy `PowerShellStudio`.

### Runtime discovery

- `PowerShellStudio.PowerShell/Services/RuntimeService.cs`

Runtime discovery inspects known install roots, registry locations, `PATH`, and Windows PowerShell fallback locations.

### Metadata / completion / help

- `PowerShellStudio.Shell/Editor/PowerShellCompletionService.cs`
- `PowerShellStudio.Shell/Editor/EditorMetadataCacheStore.cs`
- `PowerShellStudio.Shell/Help/ContextHelp.cs`
- `PowerShellStudio.Shell/Help/HelpTopicCatalog.cs`

The repo contains a substantial built-in help catalog and a metadata cache/warmup system for command catalog, quick info, parameter metadata, and `Get-Help`-derived detail.

### Logging and branding

- `PowerShellStudio.Application/AppLogger.cs`
- `PowerShellStudio.Application/Utilities/ApplicationBranding.cs`

Branding constants, log file naming, and Local AppData roots are centralized here.

### Packaging / MSIX

- `PowerShellStudio.Package/PowerShellStudio.Package.wapproj`
- `PowerShellStudio.Package/Package.appxmanifest`
- `PowerShellStudio.Package/Installer/Install.ps1`
- `PowerShellStudio.Package/Installer/Install.cmd`
- `PowerShellStudio.Package/Images/`

## Uncertain areas

- No top-level README was found during this pass, so repo intent beyond the code itself is uncertain.
- `PowerShellStudio.Editor` exists as a project, but much of the active editor behavior is implemented in `PowerShellStudio.Shell/Editor`. The long-term boundary between those two projects is not fully clear from inspection alone.
- `PowerShellStudio.TerminalSpike` appears to be experimental, but the repo does not explicitly mark it as excluded from future work.
- The exact preferred day-to-day build entry point for contributors is not documented in the repo; the root `PowerShellStudio.slnx` is the broadest observed solution file.
