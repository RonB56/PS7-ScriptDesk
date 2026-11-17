# Automated Test Results

## Build

Command:

```powershell
dotnet build '.\PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj' --no-restore -m:1 /nodeReuse:false -v:minimal
```

Result: **PASS**. Build succeeded with 0 errors and one existing warning: `UiScaleWpfBehaviorTests.cs(421,60)`, CS0414 (`FakeApiLocalTestHostService.StatusChanged` assigned but never used). The new production and test files compiled.

## Focused test execution after initialization repair

Exact command run under external PowerShell 7.6.10:

```powershell
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoProfile -NoLogo -Command 'Set-Location "C:\Users\rbarn\source\repos\PowerShellStudio"; $PSVersionTable.PSVersion.ToString(); dotnet test ".\PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj" --no-build --no-restore -m:1 /nodeReuse:false --filter "FullyQualifiedName~PsesInteractiveHistoryExclusionTests" -v:minimal'
```

The command confirmed PowerShell `7.6.10`. VSTest discovered the test assembly but produced no test results after 45 seconds. The process was stopped. This is **NO RESULT**, not a pass or test failure. The command was run again after restoring the harness to invoke the installed scriptblock through the runspace; the same discovery stall occurred.

## Test coverage added

- Run File `& $global:__ps7sd_editor_<id>` and Run Selection `. $global:__ps7sd_editor_<id>` both return `SkipAdding` for distinct IDs.
- A pre-existing handler returning `MemoryOnly` still supplies that decision for an ordinary command.
- A pre-existing .NET delegate handler returning `MemoryOnly` still supplies that decision.
- Repeated registration leaves the installed handler stable and does not stack wrappers.
- With no prior handler, ordinary commands retain the normal `MemoryAndFile` decision.
- With no prior handler, registration stores a real `ScriptBlock`; the test invokes that actual production-generated handler and checks both an internal launcher (`SkipAdding`) and an ordinary command (`MemoryAndFile`).
- Mocked history save style and path remain unchanged.

These are simulated-history tests: they execute the generated PowerShell registration and callback in an SDK runspace with mocked PSReadLine cmdlets. They verify callback decisions, chaining, idempotence, and unaffected mocked settings. They do not exercise a live interactive PSReadLine reader, its in-memory queue, persistence to the history file, or PSES/ConPTY input behavior.

Request completion, debugger ownership, shadow telemetry, and echo filtering implementations were not changed. Existing focused regression coverage remains in `PsesAvDeterministicTransportTests` (foreground response/idle completion), `ExecutionCorrelationTelemetryTests` (diagnostic-only shadow and debugger classification), `AuthoritativeSessionStructuredExecutionTests` (structured result and authoritative request identity), and `StructuredEchoFilterContractTests`/`StructuredInvocationPresentationTests` (echo filtering). Those tests were not executed in this run because the VSTest host stalled; no nonregression result is claimed.

## External PowerShell commands

From the repository root, the exact external PowerShell 7.6.10 retest command is:

```powershell
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoProfile -NoLogo -Command 'Set-Location "C:\Users\rbarn\source\repos\PowerShellStudio"; dotnet test ".\PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj" --no-build --no-restore -m:1 /nodeReuse:false --filter "FullyQualifiedName~PsesInteractiveHistoryExclusionTests" -v:minimal'
```

Then run the combined regression suite:

```powershell
dotnet test '.\PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj' --no-build --no-restore -m:1 /nodeReuse:false -v:minimal
```

The reported 105/105 combined tests are the user's pre-task baseline. They were not re-run for this change and must not be counted as validation of this patch.
