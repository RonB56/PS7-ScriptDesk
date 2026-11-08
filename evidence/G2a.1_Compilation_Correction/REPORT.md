# G2a.1 Focused Compilation Correction Evidence

Date: 2026-11-06
Phase: B — authorized minimal source repair

## Exact source change

Changed `PS7ScriptDesk.Shell/MainWindow.xaml.cs` only for product source:

- Removed the unused `selectionStart` and `selectionLength` locals from `TryCopySelectedEditorTextToClipboard`.
- Declared `selectionStart` and `selectionLength` immediately after `selectedText` is read in `RunSelectionFromEditorAsync`, initialized from the editor's current selection.
- Kept the current-line fallback assignments (`caretLine.Offset` and `caretLine.Length`) and the existing `EditorExecutionSourceInfo` metadata construction unchanged.

No execution, syntax recovery, or document-session semantics were changed. The newly changed methods were inspected for other obvious compile-time errors; none were identified.

## Diagnostics and support-package integration verification

`PS7ScriptDesk.Application/Diagnostics/DeveloperDiagnostics.cs` contains the Run Selection evidence package integration. The capture source, `PS7ScriptDesk.PowerShell/Services/RunSelectionEvidenceCapture.cs`, writes under `DeveloperDiagnosticsRootDirectory/RunSelectionEvidence`. `DeveloperDiagnostics.CreateSupportPackage()` reads that same local directory and stores it under `DeveloperDiagnostics/RunSelectionEvidence` in the ZIP. This matches the new capture directory. These diagnostics/support-package source files were verified and not modified in this correction.

## Build results

Existing project configurations and defaults were used; no framework or dependency settings were changed.

- `dotnet build PS7ScriptDesk.PowerShell/PS7ScriptDesk.PowerShell.csproj --no-restore` — succeeded; 0 warnings, 0 errors; target `net10.0` as currently configured.
- `dotnet build PS7ScriptDesk.Shell/PS7ScriptDesk.Shell.csproj --no-restore` — succeeded; 0 warnings, 0 errors; target `net10.0-windows10.0.26100.0` / `win-x64` as currently configured.

## Focused test attempt

One bounded attempt was made:

`dotnet test PS7ScriptDesk.Tests/PS7ScriptDesk.Tests.csproj --no-restore --filter FullyQualifiedName~RunSelectionEvidenceCaptureTests --blame-hang-timeout 2m`

The execution tool returned no console output or exit code, and no new `.trx` result was created. The test result is therefore indeterminate. No second attempt was made.

## Remaining blockers

- Focused test outcome cannot be confirmed from the captured command result.
- Repository-listed supporting documents (`CODEBASE_OVERVIEW.md`, `BUILD_AND_PACKAGING.md`, `DEBUGGER_AND_TERMINAL.md`, `RELEASE_READINESS.md`, `KNOWN_FRAGILE_AREAS.md`, and `CODEX_WORKFLOW.md`) were absent from `docs/`; only `docs/LocalOnly_NotForGitHub` was present.

## Acceptance criteria

- [x] Unused locals removed from clipboard helper.
- [x] Selection locals declared in Run Selection immediately after reading selected text.
- [x] Current-line fallback and selection metadata preserved.
- [x] No other obvious compile-time errors found in the two changed methods.
- [x] PowerShell and Shell builds passed.
- [ ] Focused test result confirmed (single attempt was indeterminate).
- [x] Diagnostics capture directory and support ZIP integration verified.
