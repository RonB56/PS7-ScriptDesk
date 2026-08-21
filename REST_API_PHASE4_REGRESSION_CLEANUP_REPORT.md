# REST API Publisher - Phase 4.1 Regression Cleanup Report

## Summary

The reported EXE export diagnostics failure was investigated as a narrow Phase 4.1 regression cleanup. The issue was not reproduced.

Classification:

`INTERMITTENT / NOT REPRODUCED`

No production code or test assertions were changed. The complete repository suite now passes with 317 passed, 0 failed, 0 skipped.

## Original Failure

Original failing test:

`PS7ScriptDesk.Tests.ExeExportServiceDiagnosticsTests.TemporaryDirectoryFailure_IsServiceOwnedAndPreservesOriginalException`

Original assertion symptom:

- Expected captured developer diagnostics text to contain `CreateTemporaryDirectories`.
- Captured diagnostic string was empty.
- The previous full-suite result was Passed: 316, Failed: 1, Skipped: 0, Total: 317.

## Investigation

Inspected paths:

- `PS7ScriptDesk.Tests/ExeExportServiceDiagnosticsTests.cs`
- `PS7ScriptDesk.PowerShell/Services/ExeExportService.cs`
- `PS7ScriptDesk.Application/Diagnostics/DeveloperDiagnostics.cs`
- `PS7ScriptDesk.Tests/DiagnosticReliabilityCollection.cs`
- `PS7ScriptDesk.Tests/DeveloperDiagnosticsReliabilityTests.cs`
- `PS7ScriptDesk.Tests/AppLoggerReliabilityTests.cs`
- `PS7ScriptDesk.Tests/DetachedTaskDiagnosticsTests.cs`
- `PS7ScriptDesk.Tests/PsesDebugSessionReliabilityTests.cs`

Findings:

- The failing test enables developer diagnostics, captures the current session file path, runs `ExeExportService.ExportScriptAsExeAsync`, disables diagnostics to stop/drain the writer, then reads the appended diagnostics slice.
- `ExeExportService` initializes `failureStage` to `ExeExportFailureStage.CreateTemporaryDirectories` before `Directory.CreateDirectory(projectDirectory)` and `Directory.CreateDirectory(publishDirectory)`.
- On the temporary-directory failure path, `ExeExportService` catches the original exception, calls `LogExportException`, records the failure stage in diagnostic metadata, writes an application error log, and returns an `ExeExportResult` whose detailed log preserves `ex.ToString()`.
- `DeveloperDiagnostics` writes asynchronously through a bounded channel and drains on `ConfigureFromSettings(disabled)` by completing the channel and waiting for the writer task.
- Diagnostic reliability tests use the `DiagnosticReliability` xUnit collection with `DisableParallelization = true`.
- Reflection-based diagnostics/logger seam tests restore modified static delegates in `finally` blocks.
- No REST Phase 4 code was found interacting with `DeveloperDiagnostics`.
- REST proof-host order checks did not reproduce the failure.

One production edge was noted but not proven as the root cause of this failure: `DeveloperDiagnostics.Shutdown()` stops the current session writer without clearing `_sessionState`. No test call site for `DeveloperDiagnostics.Shutdown()` was found, so this remains only an observed edge, not the proven cause of the original full-suite failure.

## Reproduction Results

Originally failing test, isolated repetitions:

- Run 1: Passed 1, Failed 0, Skipped 0, Total 1.
- Run 2: Passed 1, Failed 0, Skipped 0, Total 1.
- Run 3: Passed 1, Failed 0, Skipped 0, Total 1.
- Run 4: Passed 1, Failed 0, Skipped 0, Total 1.
- Run 5: Passed 1, Failed 0, Skipped 0, Total 1.
- Additional post-sequence runs 6-10: Passed 1, Failed 0, Skipped 0, Total 1 each.

Total observed named-test repetitions:

- Passed: 10
- Failed: 0
- Skipped: 0
- Total executions: 10

EXE diagnostics class repetitions:

- Run 1: Passed 6, Failed 0, Skipped 0, Total 6.
- Run 2: Passed 6, Failed 0, Skipped 0, Total 6.
- Run 3: Passed 6, Failed 0, Skipped 0, Total 6.
- Additional ordered sequence class run: Passed 6, Failed 0, Skipped 0, Total 6.

Diagnostic reliability set:

- Command filter: `FullyQualifiedName~ReliabilityTests`
- Passed: 43
- Failed: 0
- Skipped: 0
- Total: 43

Test-order/state-interaction results:

- REST proof-host tests, then failing EXE test:
  - `RestApiProofHostTests`: Passed 37, Failed 0, Skipped 0, Total 37.
  - Failing EXE test afterward: Passed 1, Failed 0, Skipped 0, Total 1.
- EXE diagnostics tests, REST proof-host tests, failing EXE test again:
  - `ExeExportServiceDiagnosticsTests`: Passed 6, Failed 0, Skipped 0, Total 6.
  - `RestApiProofHostTests`: Passed 37, Failed 0, Skipped 0, Total 37.
  - Failing EXE test afterward: Passed 1, Failed 0, Skipped 0, Total 1.
- Diagnostic reliability set, then failing EXE test:
  - Reliability set: Passed 43, Failed 0, Skipped 0, Total 43.
  - Failing EXE test afterward: Passed 1, Failed 0, Skipped 0, Total 1.

## Root Cause

`ROOT CAUSE NOT PROVEN`

The original failure did not reproduce in isolation, after the EXE diagnostics class, after REST Phase 4 proof-host tests, after the broader diagnostic reliability set, or in the complete repository suite.

## Files Modified

Source files modified: none.

Test files modified: none.

Report file added:

- `REST_API_PHASE4_REGRESSION_CLEANUP_REPORT.md`

## Fix

No source or test correction was made.

The evidence did not justify changing production diagnostics, EXE export behavior, REST Phase 4 code, or test assertions.

## Exception Preservation

The original temporary-directory exception behavior remains intact.

Evidence:

- The named test passed 10 observed repetitions.
- `ExeExportService` still returns `BuildFailureResult(..., ex.ToString())` from the catch path.
- The test still asserts the original exception type `IOException` appears in diagnostics.

## Diagnostics Behavior

Intended behavior for this failure path:

- `failureStage` is initialized to `CreateTemporaryDirectories`.
- Directory creation throws when the test points `TEMP`/`TMP` at a file path.
- The catch path calls `LogExportException`.
- Diagnostic metadata includes `stage = CreateTemporaryDirectories` and `exceptionType`.
- `DeveloperDiagnostics.ConfigureFromSettings(disabled)` completes the diagnostics channel and waits for the writer task before the test reads the file.

Observed behavior in this cleanup pass:

- Diagnostics were recorded deterministically in all reproduced runs.
- No empty diagnostic slice was observed.

## REST Phase 4 Impact

No REST Phase 4 code changed.

REST Phase 4 proof-host tests were run specifically as an ordering/state-interaction check and remained green.

## Repeated Test Results

Command:

`dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --no-build --filter FullyQualifiedName~ExeExportServiceDiagnosticsTests.TemporaryDirectoryFailure_IsServiceOwnedAndPreservesOriginalException`

Result across 10 observed executions:

- Passed: 10
- Failed: 0
- Skipped: 0

## EXE Diagnostics Test Results

Command:

`dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --no-build --filter FullyQualifiedName~ExeExportServiceDiagnosticsTests`

Repeated result:

- Four observed class executions.
- Each execution passed 6, failed 0, skipped 0.

Related EXE export subset:

Command:

`dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --no-build --filter FullyQualifiedName~ExeExport`

Result:

- Passed: 22
- Failed: 0
- Skipped: 0
- Total: 22

## REST Phase 4 Test Results

Command:

`dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --no-build --filter FullyQualifiedName~RestApiProofHostTests`

Observed results:

- Ordered REST-before-EXE run: Passed 37, Failed 0, Skipped 0, Total 37.
- Ordered EXE-REST-EXE run: Passed 37, Failed 0, Skipped 0, Total 37.

## Complete Repository Test Results

Command:

`dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --no-build`

Result:

- Passed: 317
- Failed: 0
- Skipped: 0
- Total: 317
- Duration: 55 s

## Full Build

Command:

`& 'C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe' PS7ScriptDesk.slnx /restore /m /p:Configuration=Debug /p:Platform=x64`

Result:

- Build succeeded.
- Warnings: 0
- Errors: 0

The MSIX packaging build rewrote `PS7ScriptDesk.Package/BundleArtifacts/x64.txt` to Debug artifact paths during validation; that generated build-output churn was restored to the checked-in Release artifact entries afterward.

## Remaining Issues

- The original one-off empty diagnostics capture was not reproduced, so no proven root cause is available.
- The observed `DeveloperDiagnostics.Shutdown()` dead-session edge remains unmodified because no test or investigated sequence invoked it and it was not proven to cause the reported failure.
- No REST Phase 5 work was started.

## Final Assessment

REGRESSION CLEANUP COMPLETE - INTERMITTENT ISSUE NOT REPRODUCED
