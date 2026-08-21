# REST API Publisher Phase 3 Implementation Report

## Summary

Phase 3 implements a standalone ASP.NET Core Minimal API proof host in `PS7ScriptDesk.RestApiProofHost`. The host proves this complete path:

HTTP request -> REST parameter binding -> configured PowerShell function invocation -> PowerShell output collection -> JSON HTTP response.

The proof exposes a deterministic `Get-SystemInfo` PowerShell function through:

- `GET /api/systeminfo?computerName=SERVER01`
- `POST /api/systeminfo` with `{ "computerName": "SERVER02" }`

No PS7 ScriptDesk WPF UI, publishing wizard, terminal, debugger, EXE export flow, WebSocket, SSE, OpenAPI generator, project generator, production authentication, or Phase 4 runspace pool was added.

## Files Added

- `PS7ScriptDesk.RestApiProofHost/PS7ScriptDesk.RestApiProofHost.csproj`
- `PS7ScriptDesk.RestApiProofHost/Program.cs`
- `PS7ScriptDesk.RestApiProofHost/Config/TestApi.ps7api.json`
- `PS7ScriptDesk.RestApiProofHost/Scripts/TestApi.ps1`
- `PS7ScriptDesk.RestApiProofHost/Hosting/RestApiProofHostFactory.cs`
- `PS7ScriptDesk.RestApiProofHost/Api/RestEndpointMapper.cs`
- `PS7ScriptDesk.RestApiProofHost/Api/RestParameterBinder.cs`
- `PS7ScriptDesk.RestApiProofHost/Models/SystemInfoRequest.cs`
- `PS7ScriptDesk.RestApiProofHost/PowerShell/IPowerShellFunctionInvoker.cs`
- `PS7ScriptDesk.RestApiProofHost/PowerShell/PowerShellFunctionInvoker.cs`
- `PS7ScriptDesk.RestApiProofHost/PowerShell/PowerShellResultNormalizer.cs`
- `PS7ScriptDesk.Tests/RestApiProofHostTests.cs`
- `REST_API_PHASE3_IMPLEMENTATION_REPORT.md`

## Files Modified

- `PS7ScriptDesk.slnx`
- `PS7ScriptDesk.Tests/PS7ScriptDesk.Tests.csproj`

## Architecture

The proof host reads the Phase 2 `ApiPublishConfiguration` model from `Config/TestApi.ps7api.json`. The REST mapper registers enabled configured endpoints using ASP.NET Core Minimal APIs. The REST layer converts HTTP inputs into a transport-neutral dictionary of PowerShell parameters, then calls the invoker with:

- configured function name
- parameter dictionary
- `HttpContext.RequestAborted`

The invoker returns collected `PSObject` pipeline output. `PowerShellResultNormalizer` converts the output to JSON-safe objects before ASP.NET Core serializes the response with `System.Text.Json`.

## PowerShell Execution

`PowerShellFunctionInvoker` creates one isolated in-process PowerShell runspace with `InitialSessionState.CreateDefault2()`. Startup loads `Scripts/TestApi.ps1` into that runspace and verifies each configured function with `Get-Command -CommandType Function`.

Invocation uses `System.Management.Automation.PowerShell.Create()`, assigns the proof runspace, calls `AddCommand(functionName)`, and supplies values with `AddParameter(parameterName, value)`. It never builds a command string from request data.

The invoker also keeps an allowlist of verified configured functions. Attempts to invoke an unconfigured function fail deterministically with `ProofPowerShellInvocationException`.

## Injection Protection

Request values are never concatenated into executable PowerShell source. `SERVER01; Get-Process`, `$(Get-Process)`, and similar values are supplied only as parameter values through `AddParameter`. There is no endpoint that accepts arbitrary script text, arbitrary command text, script paths, or client-selected function names.

## GET Test

Tested request:

```text
GET http://127.0.0.1:5087/api/systeminfo?computerName=SERVER01
```

Summarized response:

```json
{
  "ComputerName": "SERVER01",
  "Message": "System information requested for SERVER01"
}
```

## POST Test

Tested request:

```http
POST http://127.0.0.1:5087/api/systeminfo
Content-Type: application/json

{ "computerName": "SERVER02" }
```

Summarized response:

```json
{
  "ComputerName": "SERVER02",
  "Message": "System information requested for SERVER02"
}
```

The POST proof uses the strongly typed `SystemInfoRequest` DTO before creating the PowerShell parameter dictionary.

## Error Tests

Missing required GET parameter returns 400:

```json
{
  "title": "Invalid request.",
  "status": 400,
  "detail": "Required parameter 'computerName' is missing."
}
```

Malformed JSON and missing/empty POST `computerName` also return 400.

PowerShell terminating failure returns sanitized 500:

```json
{
  "title": "PowerShell invocation failed.",
  "status": 500,
  "detail": "The configured PowerShell operation could not be completed."
}
```

Server error responses do not include stack traces, script paths, script source, runspace internals, or raw PowerShell exception text.

## Serialization

The proof normalizer handles:

- scalar strings
- integers and numeric primitives
- booleans
- no output as JSON `null`
- explicit `$null` output as JSON `null`
- one `PSCustomObject` as a clean property dictionary
- multiple pipeline objects as a JSON array
- dictionaries and simple enumerable values

This is intentionally limited and replaceable. Full production JSON/error mapping remains Phase 5 work.

## Tests Added

`RestApiProofHostTests` verifies:

- startup loads and verifies configured functions
- GET `/api/systeminfo` success
- POST `/api/systeminfo` success through `SystemInfoRequest`
- missing GET `computerName` returns 400
- empty GET `computerName` returns 400
- malformed POST JSON returns 400
- missing POST `computerName` returns 400
- empty POST `computerName` returns 400
- deterministic JSON response shape
- injection-like input remains literal data
- PowerShell terminating failure returns sanitized 500
- multiple pipeline objects return a JSON array
- unknown routes return 404
- request body cannot select an arbitrary function
- no arbitrary script/command endpoint exists
- host shutdown disposes PowerShell resources
- repeated start/stop works
- startup/request latency sanity
- direct PowerShell invoker loads and executes configured function
- direct invoker rejects unconfigured function
- normalizer handles scalars, no output, explicit `$null`, `PSCustomObject`, and multiple objects

## Test Results

Phase 3 proof host build:

- `dotnet build PS7ScriptDesk.RestApiProofHost\PS7ScriptDesk.RestApiProofHost.csproj`
- Passed, 0 warnings, 0 errors.

Phase 3 tests:

- `dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --filter FullyQualifiedName~RestApiProofHostTests`
- Passed: 25 passed, 0 failed, 0 skipped.
- Initial sandboxed attempt failed because the Windows-targeted test graph could not access `C:\Users\rbarn\AppData\Local\Microsoft SDKs`; rerun with approval passed.

Relevant REST Phase 1 tests:

- `dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --filter FullyQualifiedName~PowerShellApiMetadataServiceTests`
- Passed: 36 passed, 0 failed, 0 skipped.

Relevant REST Phase 2 tests:

- `dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --filter "FullyQualifiedName~ApiPublishConfiguration"`
- Passed: 46 passed, 0 failed, 0 skipped.

Complete repository test suite:

- `dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --no-build`
- Passed: 305 passed, 0 failed, 0 skipped.

Full solution restore/build:

- `& 'C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe' PS7ScriptDesk.slnx /restore /m /p:Configuration=Debug /p:Platform=x64`
- Initial sandboxed attempt failed on local Windows SDK cache access.
- Approved rerun passed: 0 warnings, 0 errors.

## Manual Smoke Test

Host start:

- `dotnet run --project PS7ScriptDesk.RestApiProofHost\PS7ScriptDesk.RestApiProofHost.csproj --no-build`
- Listening on `http://127.0.0.1:5087`.

GET:

- Request: `GET /api/systeminfo?computerName=SERVER01`
- Result: HTTP 200, `ComputerName = SERVER01`, `Message = System information requested for SERVER01`.

POST:

- Request: `POST /api/systeminfo` with `{ "computerName": "SERVER02" }`
- Result: HTTP 200, `ComputerName = SERVER02`, `Message = System information requested for SERVER02`.

Injection-like input:

- Request: `GET /api/systeminfo?computerName=SERVER01%3B%20Get-Process`
- Result: HTTP 200, `ComputerName = SERVER01; Get-Process`, `Message = System information requested for SERVER01; Get-Process`.
- The input was treated as literal data.

Missing parameter:

- Request: `GET /api/systeminfo`
- Result: HTTP 400 with ProblemDetails-style JSON.

Server shutdown:

- Stopped the standalone host with Ctrl+C.
- Follow-up request to `http://127.0.0.1:5087/health` did not reach a listening host.

## Deviations

- The proof host targets `net10.0`, matching the current repository project targets and the REST architecture audit. It does not downgrade to .NET 8.
- The POST systeminfo endpoint uses the dedicated `SystemInfoRequest` DTO for the requested proof, while the generic `RestParameterBinder` remains available for configuration-shaped bindings. Production configuration-driven endpoint generation is deferred.
- Cancellation is passed through from `HttpContext.RequestAborted` to startup gates and invocation entry points, but the synchronous Phase 3 PowerShell invocation is not force-stopped mid-pipeline. Full cancellation/runspace retirement remains Phase 4 work.
- The proof uses one isolated runspace protected by a semaphore. It does not implement the Phase 4 runspace pool.
- No optional `/health` endpoint was added.

The `docs/` files named by `AGENTS.md` are not present in this checkout, so implementation followed `REST_API_PUBLISHER_ARCHITECTURE.md`, Phase 1/2 reports, and direct repository inspection.

## Known Limitations

Deferred to Phase 4 - PowerShell Runspace Host:

- runspace pool
- bounded concurrency and queueing
- robust timeout and cancellation retirement
- cross-request state isolation hardening

Deferred to Phase 5 - JSON and Error Mapping:

- full serializer depth/cycle handling
- full stream capture and response policy
- complete error taxonomy
- output size limits

Deferred to Phase 6 - OpenAPI:

- generated OpenAPI document
- Swagger UI
- schema generation from endpoint metadata

Deferred to Phase 7 - Project Generator:

- production generated project layout
- copied user scripts/config generation
- generated README/appsettings

Deferred to later UI/publishing/security phases:

- WPF wizard
- `Publish as API` menu
- local-test UI
- Windows Service/IIS/Docker/Linux targets
- API-key/JWT/Windows authentication
- WebSocket and SSE transports
- production dependency bundling

## Regression Review

This implementation did not modify or integrate with:

- editor
- terminal
- debugger
- EXE export execution architecture
- WPF application startup
- settings persistence
- existing REST Phase 1 parser behavior
- existing REST Phase 2 configuration model, store, or validator behavior

The only solution-level wiring was adding the standalone proof host project to `PS7ScriptDesk.slnx` and referencing it from the test project.

## Final Assessment

PHASE 3 COMPLETE
