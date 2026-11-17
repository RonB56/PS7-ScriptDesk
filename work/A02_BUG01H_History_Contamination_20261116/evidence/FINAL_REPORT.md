# A02-BUG-01H — Final Report

## Decision: FIX IMPLEMENTED — BUILD PASSED; FOCUSED RETEST STALLED

Run File and Run Selection use the PSES DAP-stage plus LSP foreground bridge. The LSP `evaluate` submits a short generated invocation through PSES's interactive foreground path. The existing structured echo filter operates after host output reaches ConPTY, so it cannot remove a command already accepted by PSReadLine.

The repair adds a narrow `AddToHistoryHandler` wrapper to the private DAP staging expression before the LSP foreground invocation. It skips only lines matching the reserved ScriptDesk launcher form (`&` or `.` followed by `$global:__ps7sd_editor_` and exactly 32 hexadecimal request-id characters). It chains the currently installed handler for every other line and returns `MemoryAndFile` when no prior handler exists. A process-unique internal global state object tracks the wrapper reference and prior handler so repeated requests do not stack callbacks; it is not written to the console or history. The repair does not change `HistorySaveStyle`, history paths, unrelated history rows, execution authority, prompts, output filtering, or debugger transport.

## Demonstrated root cause of the focused test failures

The failure was in production-generated handler initialization, not in the harness's `&` syntax. The generated global state starts with `Handler = $null`. The initial PSReadLine option also has `AddToHistoryHandler = $null` in both failing scenarios. The registration guard compared those two values with `ReferenceEquals`; because null is reference-equal to null, it treated an uninitialized state as an already-installed wrapper and skipped creating/storing the scriptblock. The harness then correctly attempted `& $global:mockHistoryHandler`, where the value was still null, producing the reported PowerShell error.

The guard now treats a null stored `Handler` as uninitialized and installs the production-generated wrapper. A deterministic regression asserts that registration stores an actual `ScriptBlock`, then invokes that handler for an internal launcher (`SkipAdding`) and a normal command with no prior handler (`MemoryAndFile`). The test harness also explicitly retrieves and type-checks the installed handler before invoking it through the runspace.

## Evidence and limits

- Source confirms the generated variable name and Run File/Selection invocation operators. Source comments state that DAP stages the script and the foreground LSP request is written by PSES to ConPTY.
- The PSReadLine handler uses the documented supported `AddToHistoryHandler` option; Microsoft documents `SkipAdding` and the normal history outcomes. See [Set-PSReadLineOption](https://learn.microsoft.com/en-us/powershell/module/psreadline/set-psreadlineoption) and [about_PSReadLine](https://learn.microsoft.com/en-us/powershell/module/psreadline/about/about_psreadline).
- The focused tests execute the production-generated registration script and handler in a PowerShell SDK runspace with mocked `Get-PSReadLineOption`/`Set-PSReadLineOption` commands. They verify the wrapper's decision values and lifecycle behavior. These are simulated-history tests, not tests of an interactive PSReadLine reader, its in-memory history queue, or its history file.
- Serialized build passed after the repair with 0 errors and one existing CS0414 warning in `UiScaleWpfBehaviorTests.cs`.
- The focused external retest was launched under PowerShell 7.6.10. VSTest discovered the test assembly but returned no test results after 45 seconds; the process was stopped. No focused pass/fail result is claimed for this repaired revision. See `AUTOMATED_TEST_RESULTS.md` for the exact command.
- No PSES/ConPTY live UAT was performed. Actual PSReadLine Up/Down history and persistence behavior remain unverified; live session verification is distinct from the simulated handler decision tests.
- No PSES/ConPTY live UAT was performed. PSReadLine Up/Down history, persistence/restart, prompts, Read-Host, secure input, Ctrl+C, debug breakpoints, and shared session state remain pending live verification.
- Whether the PowerShell engine's `Get-History` records the LSP invocation is not established by the current source evidence. `AddToHistoryHandler` controls PSReadLine's interactive history queue/file; it is not presented as an engine-history API. Existing `Get-History` probe code is in the separate legacy `LiveConsoleService` dispatch path.

## Changed files

- `PS7ScriptDesk.PowerShell/Services/PsesInteractiveHistoryExclusion.cs`
- `PS7ScriptDesk.PowerShell/Services/PsesOwnedInteractiveSessionService.cs`
- `PS7ScriptDesk.Tests/PsesInteractiveHistoryExclusionTests.cs`
- This task's local-only report folder.

**Developer diagnostics not changed:** the existing content-free event accurately records that the guard registration script is included in the private DAP stage; this patch corrects the no-handler initialization condition and does not change the diagnostic event's represented stage or runtime telemetry.

See `ROOT_CAUSE_ANALYSIS.md`, `HISTORY_OWNERSHIP_TRACE.md`, `REPAIR_DESIGN.md`, `AUTOMATED_TEST_RESULTS.md`, `MANUAL_UAT.md`, `REGRESSION_RISK.md`, and `A02_BUG_01_STATUS.md` for the detailed evidence and next validation steps.
