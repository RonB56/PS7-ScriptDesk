# REST API Publisher - Phase 4 Implementation Report

## Summary

Phase 4 replaces the Phase 3 single-runspace proof executor with a reusable, bounded `RunspacePool` execution core. The new core is transport-neutral: it accepts an `ApiInvocationRequest`, performs admission/concurrency/timeout/cancellation handling through `PowerShellInvocationCoordinator`, invokes an allowlisted PowerShell function through a request-scoped `PowerShell` instance attached to a shared `RunspacePool`, and returns an `ApiInvocationResult` without HTTP concepts.

The Phase 3 REST proof host now uses this core while preserving GET binding, POST JSON binding, selected function invocation, `AddCommand`, `AddParameter`, proof output normalization, injection resistance, and sanitized REST errors.

## Files Added

- `PS7ScriptDesk.RestApiProofHost/PowerShell/ApiInvocationRequest.cs`
- `PS7ScriptDesk.RestApiProofHost/PowerShell/ApiInvocationResult.cs`
- `PS7ScriptDesk.RestApiProofHost/PowerShell/PowerShellInvocationMetrics.cs`
- `PS7ScriptDesk.RestApiProofHost/PowerShell/RunspacePoolManager.cs`
- `PS7ScriptDesk.RestApiProofHost/PowerShell/PowerShellInvocationCoordinator.cs`
- `REST_API_PHASE4_IMPLEMENTATION_REPORT.md`

## Files Modified

- `PS7ScriptDesk.RestApiProofHost/PowerShell/IPowerShellFunctionInvoker.cs`
- `PS7ScriptDesk.RestApiProofHost/PowerShell/PowerShellFunctionInvoker.cs`
- `PS7ScriptDesk.RestApiProofHost/Hosting/RestApiProofHostFactory.cs`
- `PS7ScriptDesk.RestApiProofHost/Api/RestEndpointMapper.cs`
- `PS7ScriptDesk.RestApiProofHost/Scripts/TestApi.ps1`
- `PS7ScriptDesk.RestApiProofHost/Config/TestApi.ps7api.json`
- `PS7ScriptDesk.Tests/RestApiProofHostTests.cs`

## Final Architecture

The execution flow is:

```text
REST endpoint mapper
    -> ApiInvocationRequest
    -> PowerShellInvocationCoordinator
    -> bounded admission slot
    -> bounded execution slot
    -> RunspacePoolManager lease
    -> PowerShellFunctionInvoker
    -> request-scoped PowerShell instance
    -> AddCommand(allowlisted function)
    -> AddParameter(request values)
    -> ApiInvocationResult
    -> REST response mapping and proof normalization
```

Responsibilities are separated as follows:

- `ApiInvocationRequest`: transport-neutral function name, parameters, and optional timeout.
- `ApiInvocationResult`: transport-neutral status, output, safe message, stream records, elapsed time, and pool generation.
- `PowerShellInvocationCoordinator`: admission, queue wait, execution throttling, timeout/cancellation orchestration, metrics, shutdown, and pool rebuild requests.
- `RunspacePoolManager`: initial session state creation, function loading, allowlist validation, shared pool leasing, rebuild coordination, and disposal.
- `PowerShellFunctionInvoker`: one `PowerShell` instance per invocation, `RunspacePool` assignment, `AddCommand`, `AddParameter`, async invocation, stream capture, stop/cleanup, and disposal.
- `RestEndpointMapper`: HTTP binding, coordinator invocation, status-to-HTTP mapping, sanitized errors, and Phase 3 proof normalization.

The new core classes do not reference `HttpContext`, `HttpRequest`, `HttpResponse`, ASP.NET Core routing types, WebSocket, or SSE types.

## Runspace Pool

- Minimum runspaces: configured from Phase 2 runtime options; proof config uses `1`.
- Maximum runspaces: configured from Phase 2 runtime options; default remains bounded by runtime configuration and proof config uses `4`.
- Initialization: `RunspacePoolManager.InitializeAsync` builds an `InitialSessionState`, creates a `RunspacePool`, sets min/max runspaces, opens the pool, and verifies configured functions.
- Function/script loading: the proof host parses `Scripts/TestApi.ps1` and installs top-level function definitions into `InitialSessionState` using `SessionStateFunctionEntry`.
- Allowlist: the manager stores the configured function set and rejects unknown functions before invocation.
- Disposal: the manager disposes the current pool during shutdown and rebuild. Direct `RunspacePool.Dispose()` is used for bounded cleanup because `RunspacePool.Close()` was observed to hang in the proof-host cancellation path.

## Invocation Lifecycle

```text
accepted
    The coordinator attempts to take a bounded admission slot.

queued
    If all execution slots are busy, admitted work waits on the bounded execution semaphore.

executing
    The coordinator leases the current pool generation and delegates to the invoker.

completed
    Successful output is returned as `ApiInvocationStatus.Success`.

failed
    Invalid functions, PowerShell failures, host unavailable, and internal failures return distinct transport-neutral statuses.

canceled / timed out
    Caller cancellation and invocation timeout stop the active PowerShell pipeline, release slots, mark the lease as poisoned, and request pool rebuild.
```

Cleanup uses `try/finally` paths for admission slots, execution slots, active counters, linked token sources, cancellation registrations, and request-scoped `PowerShell` disposal.

## Concurrency Control

- Execution limit: `SemaphoreSlim _executionSlots`, configured to match runtime max concurrency/pool maximum.
- Queue capacity: `SemaphoreSlim _admissionSlots`, sized as `maxConcurrency + queueLimit`, so only bounded executing plus waiting work is accepted.
- Default proof queue capacity: `32`.
- Default proof queue wait timeout: `10 seconds`.
- Queue full: admission slot cannot be taken immediately.
- Queue wait timeout: admitted work cannot obtain an execution slot within the configured queue wait.
- Caller canceled before execution: caller cancellation while waiting returns `ApiInvocationStatus.CallerCanceled`.

Metrics track active invocations, queued invocations, configured maximum concurrency, observed maximum active count, completed count, queue-full count, queue-timeout count, caller-canceled count, invocation-timeout count, PowerShell failure count, internal failure count, and pool rebuild count.

## Timeout Handling

The coordinator creates a linked invocation token with the endpoint override or default runtime timeout. The invoker starts the pipeline asynchronously with `BeginInvoke`, waits for completion without blocking ASP.NET request handling, and calls `PowerShell.StopAsync(callback: null, state: null)` when timeout/cancellation wins the race.

Stop cleanup is bounded with a short wait. Timed-out invocations return `ApiInvocationStatus.InvocationTimedOut`, release execution/admission slots, and trigger pool rebuild because the affected runspace may have uncertain state.

## Cancellation Handling

REST passes `HttpContext.RequestAborted` into the transport-neutral coordinator. If the caller token is canceled before execution, the coordinator returns `ApiInvocationStatus.CallerCanceled` without starting PowerShell. If cancellation occurs during execution, the invoker stops the active request-scoped `PowerShell` object with `StopAsync`, waits a bounded time for cleanup, disposes the object, marks the lease for rebuild, and returns `CallerCanceled`.

Tests verify that a normal request succeeds immediately after cancellation and that no execution slot remains leaked.

## Pool Recovery

Canceled and timed-out runspaces are not trusted for silent reuse. Phase 4 uses a conservative whole-pool rebuild policy for suspected poisoned state.

Rebuild triggers:

- invocation timeout
- caller cancellation during active PowerShell execution
- explicit test/requested recovery through `RequestPoolRebuildAsync`

Synchronization:

- `RunspacePoolManager` guards pool state and rebuild with a single async lock.
- Only one rebuild runs at a time.
- New leases see either the current open pool or the rebuilt generation.
- Shutdown sets coordinator state to unavailable, cancels queued/running work, waits a bounded period, then disposes the manager.

Requests already executing on an old generation finish through their leased pool reference; requests entering during shutdown return `HostUnavailable` or cancellation statuses.

## Cross-Request State

Tested behavior:

- Normal parameterized calls with distinct request IDs return caller-specific values under stress.
- Concurrent delay calls return their own IDs.
- Function-local values and parameters do not leak into later normal calls.
- An explicit global variable written by `Set-Phase4GlobalState` can persist in a reused runspace when max concurrency is `1`.
- A pool rebuild clears that explicit global state.

Conclusion:

- Normal request parameters are isolated by using a request-scoped `PowerShell` instance and `AddCommand(..., useLocalScope: true)`.
- Pooled PowerShell runspaces are not complete sandboxes. Global variables and other runspace-level state can persist when scripts intentionally write there.
- Phase 4 mitigation is conservative pool rebuild after cancellation/timeout and an explicit recovery path tested for global state cleanup.
- Full state reset policy, process isolation, and hostile script containment remain future work.

## Security

HTTP clients cannot provide script text, arbitrary commands, arbitrary script paths, or arbitrary function names. The REST mapper selects the function from trusted configuration. The coordinator checks that function against the allowlist before invocation. The invoker calls:

```text
AddCommand(configuredFunction, useLocalScope: true)
AddParameter(parameterName, value)
```

Request strings are passed as parameter data, not executable source. The injection regression for `SERVER01; Get-Process` remains covered by tests and manual verification.

## REST Integration

The Phase 3 REST proof host now resolves a `PowerShellInvocationCoordinator` from the host factory. `RestEndpointMapper` still owns query/body binding and proof normalization, but delegates execution to the coordinator.

Minimal Phase 4 HTTP mappings:

- `QueueFull` -> `429`
- `QueueWaitTimedOut` -> `429`
- `InvocationTimedOut` -> `504`
- `CallerCanceled` -> `499` when a response can still be produced
- `HostUnavailable` -> `503`
- `InvalidFunction`, `PowerShellFailure`, and `InternalFailure` -> sanitized `500`

Full error taxonomy remains Phase 5 work.

## Metrics/Diagnostics

Added thread-safe snapshots through `PowerShellInvocationMetricsSnapshot`:

- active invocation count
- queued invocation count
- configured max concurrency
- pool generation
- pool rebuild count
- total accepted count
- total completed count
- rejected queue-full count
- queue timeout count
- caller cancellation count
- invocation timeout count
- PowerShell failure count
- internal failure count
- max observed active invocation count

The proof host also logs safe lifecycle events through ASP.NET Core `ILogger`: pool initialization/open, function verification, invocation start/completion, timeout/cancellation, PowerShell failure, rebuild, and shutdown. Logs avoid request bodies, script source, full parameter sets, and full output.

## Tests Added

Phase 4-specific additions in `RestApiProofHostTests`:

- `RunspacePoolLifecycle_RepeatedInitializeDispose_WorksCleanly`
- `PowerShellFailure_CapturesErrorStreamSafely`
- `RunspacePool_AllowsConcurrentExecution`
- `ConcurrencyBound_DoesNotExceedConfiguredMaximum`
- `QueueCapacity_ReturnsQueueFullWhenExecutionAndQueueAreOccupied`
- `QueueWaitTimeout_ReturnsTimedOutWaitWithoutStartingPowerShell`
- `InvocationTimeout_StopsPowerShellRebuildsPoolAndAllowsRecovery`
- `CallerCancellation_StopsPowerShellRebuildsPoolAndAllowsRecovery`
- `CrossRequestState_NormalParametersRemainIndependentAndGlobalStateRequiresPoolRecovery`
- `ShutdownDuringActivity_CompletesWithinBoundedDuration`
- `Stress_ManyShortInvocationsKeepCallerSpecificResults`
- `CancellationStress_MixedTimeoutCancelAndSuccessLeavesEngineUsable`

Existing Phase 3 proof-host tests were preserved and continue to run in the same test class.

## Stress Results

- Concurrency proof: four 500 ms invocations with pool max `4` overlap and complete substantially faster than serial execution.
- Concurrency bound: with max concurrency `2`, observed active invocation count remains at or below `2`.
- Reliability stress: 60 short calls with pool max `4` and queue limit `100` return caller-specific results without deadlock or leaked slots.
- Cancellation stress: mixed success, timeout, and caller-canceled invocations complete, then normal recovery requests succeed and active invocation count returns to `0`.

## Test Results

Builds:

- `dotnet build PS7ScriptDesk.RestApiProofHost\PS7ScriptDesk.RestApiProofHost.csproj`
  - Passed, 0 warnings, 0 errors.
- `dotnet build PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj`
  - Passed, 0 warnings, 0 errors.
- `& 'C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe' PS7ScriptDesk.slnx /restore /m /p:Configuration=Debug /p:Platform=x64`
  - Passed, 0 warnings, 0 errors.
  - The initial sandboxed attempt failed on access to `C:\Users\rbarn\AppData\Local\Microsoft SDKs`; the escalated build passed.

Filtered tests:

- Phase 4 tests: 12 Phase 4-specific test cases are included in `RestApiProofHostTests`; passed as part of the proof-host filtered run.
- Phase 3 tests: 25 retained Phase 3 proof-host test cases are included in `RestApiProofHostTests`; passed as part of the proof-host filtered run.
- Phase 3 plus Phase 4 proof-host command:
  - `dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --filter FullyQualifiedName~RestApiProofHostTests`
  - Passed: 37, Failed: 0, Skipped: 0, Total: 37, Duration: 11 s.
- Phase 2 configuration command:
  - `dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --filter FullyQualifiedName~ApiPublishConfiguration`
  - Passed: 46, Failed: 0, Skipped: 0, Total: 46, Duration: 519 ms.
- Phase 1 parser command:
  - `dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --filter FullyQualifiedName~PowerShellApiMetadataServiceTests`
  - Passed: 36, Failed: 0, Skipped: 0, Total: 36, Duration: 541 ms.

Complete repository suite:

- `dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --no-build`
  - Failed: 1, Passed: 316, Skipped: 0, Total: 317, Duration: 50 s.
  - Failure: `PS7ScriptDesk.Tests.ExeExportServiceDiagnosticsTests.TemporaryDirectoryFailure_IsServiceOwnedAndPreservesOriginalException`.
  - Failure detail: assertion expected diagnostic text containing `CreateTemporaryDirectories`, but the captured string was empty.
  - Phase 4 did not modify `ExeExportServiceDiagnosticsTests.cs` or the EXE export implementation. The failure is documented as outside the REST proof-host execution surface.

## Manual Test Results

Started proof host:

```text
$env:PS7SCRIPT_DESK_REST_POC_PORT='5087'
dotnet run --project PS7ScriptDesk.RestApiProofHost\PS7ScriptDesk.RestApiProofHost.csproj --no-build
```

Observed:

- Host listened on `http://127.0.0.1:5087/`.
- Pool generation 1 opened with min `1`, max `4`.
- Configured functions verified: `Get-SystemInfo`, `Invoke-TestFailure`, `Get-Numbers`, `Invoke-Phase4Delay`.

Manual endpoint checks:

- Normal GET: `GET /api/systeminfo?computerName=SERVER01`
  - HTTP 200; `ComputerName` was `SERVER01`.
- Normal POST: `POST /api/systeminfo` with `{"computerName":"SERVER02"}`
  - HTTP 200; `ComputerName` was `SERVER02`.
- Injection-like value: `GET /api/systeminfo?computerName=SERVER01%3B%20Get-Process`
  - HTTP 200; value remained literal `SERVER01; Get-Process`.
- Concurrent requests: four simultaneous `GET /api/phase4/delay?requestId=reqN&milliseconds=500`
  - Returned `req1,req2,req3,req4`.
  - Client elapsed time was 710 ms for four 500 ms calls.
  - Host logs showed overlapping execution and individual request durations around 505-551 ms after warmup.
- Timeout: `GET /api/phase4/timeout?requestId=timeout&milliseconds=1000`
  - HTTP 504.
  - Host logs showed `StopAsync`, `InvocationTimedOut`, and pool rebuild from generation 1 to generation 2.
- Post-timeout recovery: `GET /api/systeminfo?computerName=RECOVERY`
  - HTTP 200; `ComputerName` was `RECOVERY`.
- Shutdown: host stopped with Ctrl+C.
  - Follow-up probe to `http://127.0.0.1:5087/...` returned `NO_LISTENER`.

## Deviations

- `InitialSessionState.StartupScripts` was not used for function loading because it did not reliably expose the proof functions during pool verification. Phase 4 parses the deterministic proof script and installs top-level function definitions with `SessionStateFunctionEntry`.
- `RunspacePool.Close()` was not used during cleanup because it was observed to hang after stopped pooled invocations. The implementation uses `RunspacePool.Dispose()` for bounded cleanup.
- The deterministic delay test uses `[System.Threading.Thread]::Sleep(...)` rather than `Start-Sleep` because `Start-Sleep` triggered module/autoload issues in the proof-host runspace environment.
- Full per-runspace retirement is not implemented. Phase 4 conservatively rebuilds the whole pool after timeout/cancellation.
- Full stream policy and public error taxonomy are intentionally limited to a safe captured foundation and remain Phase 5 work.

## Known Limitations

- Phase 5: production JSON serialization, complete error taxonomy, stream/error response policy.
- Phase 6: Swagger/OpenAPI.
- Phase 7: generated-project service and production publishing pipeline.
- Later UI/publishing/security phases: publish wizard, local test UI, File menu integration, authentication, Windows Service/IIS/Docker hosting, process-level sandboxing.
- Future transport work: WebSocket, SSE, and streaming responses.
- Runspace pooling is not full sandbox isolation. Scripts that intentionally mutate global/session state can persist data until pool rebuild.

## Regression Review

- Editor: not affected.
- Terminal: not affected.
- Debugger: not affected.
- EXE export: not modified. One existing EXE export diagnostics test failed in the complete suite and is documented above.
- Startup: main WPF startup not affected; proof-host startup verified.
- Settings: not affected.
- Phase 1 parser: filtered parser tests passed, 36/36.
- Phase 2 configuration: filtered configuration tests passed, 46/46.
- Phase 3 REST host: retained proof-host tests passed and manual GET/POST/injection/error behavior remained operational.

## Final Assessment

PHASE 4 COMPLETE
