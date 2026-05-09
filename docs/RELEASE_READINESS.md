# RELEASE_READINESS

## Purpose

This checklist is for deciding whether a build is:

- tester-ready
- Microsoft Store-ready

It is based on repo inspection only. It is not a record of a successful build or package validation.

## Tester-ready gate

A tester-ready build should pass all of these checks on native Windows:

### Install and launch

- App installs cleanly from the intended package or dev build path.
- App launches without startup exception dialogs.
- Window title and visible branding show `PS7 ScriptDesk` consistently enough for the intended test build.

### Editor

- Open file, new file, save, save as, close tab all work.
- Drag/drop opening still works for expected file types.
- Syntax coloring, diagnostics, and breakpoint glyphs still render.
- Find/replace and go-to-line still open and navigate correctly.

### Run

- Run Full Script sends the current visible script to the shared terminal session.
- Unsaved/dirty content runs as the visible editor content, not stale disk content.
- Run Selection works from selection.
- `F8` also works.
- `F8` with no selection still runs the current line.

### Debug

- Breakpoints can be added and removed.
- Starting debug with breakpoints pauses execution when hit.
- Paused state highlights the current line.
- Continue, Step Over, Step Into, Step Out, and Stop Debug all work.
- Toolbar/menu enabled state matches actual debug state.

### Console / terminal

- Embedded terminal starts successfully with the selected runtime.
- Console input, resize, copy/paste, interrupt, restart, and clear all behave correctly.
- Prompt returns cleanly after Run, Run Selection, Ctrl+C, and Clear Console.
- Helper markers and internal app messages do not leak into the visible console.

### Help

- F1/context help opens.
- Built-in help windows display expected content for common surfaces.

### Settings persistence

- Window size/position, explorer visibility, workspace path, runtime selection, reopened tabs, theme, zoom, and context-help enablement persist across restart.

### Logging / privacy

- App logs are written only to the expected Local AppData path.
- No unexpected telemetry path was identified in this repo pass, but this should still be checked at release time.
- Logs should be reviewed to ensure they do not expose more user script content or system details than intended.

## Microsoft Store-ready gate

In addition to tester-ready checks, a Store-ready build should pass:

### MSIX / packaging

- `PowerShellStudio.Package` builds successfully on the intended release machine.
- Package identity, display name, version, icons, and signing details are correct.
- Generated installer assets are correct if they are still part of release distribution.

### WACK and Store validation

- WACK or equivalent Microsoft package validation passes.
- Store metadata and app manifest values align with the package being submitted.
- `runFullTrust` capability remains intentional and acceptable for the release target.

### Branding and versioning

- App version shown in the shell and package version in the MSIX are intentionally aligned or intentionally documented if different.
- `PS7 ScriptDesk` branding is consistent across shell, manifest, installer, and Store assets.
- Legacy `PowerShellStudio` naming is not accidentally shown in end-user-facing places unless intentionally retained.

### Execution policy and runtime assumptions

- The app still works on a clean target Windows environment with the intended PowerShell runtime installed.
- Execution-policy-dependent flows are explicitly validated:
  - app terminal startup
  - Run
  - Debug
  - metadata/completion helper processes
  - installer path if used

## Release blockers worth extra attention

- Package signing and certificate setup
- WAP/Desktop Bridge build prerequisites
- Debugger toolbar state drift
- Terminal prompt corruption after clear/interrupt/run
- Metadata cache warmup failures on first launch
- Branding/version mismatch between shell and package
