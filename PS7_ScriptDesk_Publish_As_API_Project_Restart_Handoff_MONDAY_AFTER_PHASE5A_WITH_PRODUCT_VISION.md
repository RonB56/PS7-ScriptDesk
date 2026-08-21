# PS7 ScriptDesk — Publish as API Project Restart / Handoff Guide
## Updated after REST Phase 5A

**Project:** PS7 ScriptDesk — PowerShell-to-API Publishing  
**Primary repository:** `C:\Users\rbarn\source\repos\PowerShellStudio`  
**Purpose:** Upload this file to ChatGPT when restarting the project on Monday. It restores the current project state, architecture decisions, completed work, testing baseline, and exact next step.  
**Status date:** August 21, 2026  
**Current transport:** REST API  
**Future transports:** WebSocket API, then Server-Sent Events (SSE)

---

# 1. Executive Restart Summary

The PS7 ScriptDesk “Publish as API” feature is being implemented in phases.

Transport order:

1. REST API
2. WebSocket API
3. Server-Sent Events (SSE)

REST is being built first, but the execution/result infrastructure is deliberately transport-independent so WebSocket and SSE can reuse it later.

Current status:

- Phase 1 — COMPLETE
- Phase 2 — COMPLETE
- Phase 3 — COMPLETE / ACCEPTED
- Phase 4 — COMPLETE / ACCEPTED
- Phase 4.1 regression cleanup — COMPLETE
- Phase 5A — COMPLETE / ACCEPTED
- Phase 5B — NEXT
- Phase 6+ — NOT STARTED

Current clean baseline:

- Full repository suite: **329 passed / 0 failed / 0 skipped**
- REST proof-host suite: **49 passed / 0 failed / 0 skipped**
- Phase 1 parser tests: **36/36**
- Phase 2 configuration tests: **46/46**
- EXE diagnostics tests: **6/6**
- Full solution restore/build: **0 warnings / 0 errors**

Codex allowance was down to approximately 2%, so stop here until the allowance refreshes.

The next implementation task is:

# **Phase 5B — Stream Policy, Error Taxonomy, and REST ProblemDetails Mapping**

Do not redo completed phases unless a real regression is found.

---

# 2. Critical Workflow Rules

For each Codex phase/sub-phase:

1. Inspect the actual repository first.
2. Implement only the requested scope.
3. Run targeted tests.
4. Run the complete repository suite.
5. Run a full solution build.
6. Self-review.
7. Write a detailed `.md` implementation report into the repository.
8. Upload that report to ChatGPT.
9. Have ChatGPT review it before authorizing the next phase.

Do not rely on Codex chat output as the permanent implementation record.

Existing reports:

- `REST_API_PUBLISHER_ARCHITECTURE.md`
- `REST_API_PHASE1_IMPLEMENTATION_REPORT.md`
- `REST_API_PHASE2_IMPLEMENTATION_REPORT.md`
- `REST_API_PHASE3_IMPLEMENTATION_REPORT.md`
- `REST_API_PHASE4_IMPLEMENTATION_REPORT.md`
- `REST_API_PHASE4_REGRESSION_CLEANUP_REPORT.md`
- `REST_API_PHASE5A_IMPLEMENTATION_REPORT.md`

Expected next report:

- `REST_API_PHASE5B_IMPLEMENTATION_REPORT.md`

Do not combine Phase 5B, Phase 6, and Phase 7 into one task.

---

# 3. Main Architecture Decisions

The key architecture file is:

`REST_API_PUBLISHER_ARCHITECTURE.md`

Preserve these decisions:

- Running APIs execute in a **separate ASP.NET Core process**.
- REST uses ASP.NET Core Minimal APIs.
- PowerShell execution uses `System.Management.Automation`.
- Function discovery is AST-first/static and does not execute scripts merely to discover endpoints.
- Do not use ScriptDesk’s interactive terminal/debugger execution systems as the API engine.
- API configuration is associated with scripts through `.ps7api.json` companion configuration.
- Remote clients never select arbitrary function names.
- Invocation uses trusted configured function names with:
  - `AddCommand(configuredFunction)`
  - `AddParameter(name, value)`
- Never build executable PowerShell from request values.
- REST is an adapter over a reusable invocation/result core.
- WebSocket and SSE should reuse that shared core later.
- `RunspacePool` is **not** a security sandbox.

Repository:

`C:\Users\rbarn\source\repos\PowerShellStudio`

Current project TFMs are .NET 10-era (`net10.0` / `net10.0-windows`). Do not downgrade to .NET 8 because older documents mentioned it.

Standalone REST proof host:

`PS7ScriptDesk.RestApiProofHost`

---

# 4. Phase 1 — COMPLETE

**PowerShell Function Metadata Parser**

Implemented static AST-based discovery without executing the script.

Capabilities include:

- named function discovery
- advanced functions
- typed parameters
- mandatory flags
- aliases
- validation attributes
- default expression text
- parse-error detection

Current targeted test baseline:

**36/36 passed**

---

# 5. Phase 2 — COMPLETE

**REST/API Configuration Model**

Implemented durable REST/API publishing models and validation.

Important decisions:

- transport-neutral concepts are separated from REST-specific settings
- script companion configuration uses a form such as:
  `MyScript.ps7api.json`
- runtime/resource/output settings are represented in durable configuration

Current targeted test baseline:

**46/46 passed**

---

# 6. Phase 3 — COMPLETE / ACCEPTED

**Standalone REST Proof Host**

Created:

`PS7ScriptDesk.RestApiProofHost`

Proved the end-to-end path:

```text
HTTP request
    -> REST binding
    -> trusted configured PowerShell function
    -> AddCommand / AddParameter
    -> PowerShell output
    -> JSON response
```

GET example:

`GET /api/systeminfo?computerName=SERVER01`

POST example:

```json
{
  "computerName": "SERVER02"
}
```

Important injection regression:

Input:

`SERVER01; Get-Process`

was treated as literal parameter data. `Get-Process` was not executed.

This invariant must never regress.

---

# 7. Phase 4 — COMPLETE / ACCEPTED

**PowerShell Runspace Host**

Phase 4 replaced the single-runspace proof executor with a bounded, transport-neutral `RunspacePool` execution core.

Components introduced include:

- `ApiInvocationRequest`
- `ApiInvocationResult`
- `PowerShellInvocationMetrics`
- `RunspacePoolManager`
- `PowerShellInvocationCoordinator`

Current execution flow:

```text
REST endpoint mapper
    -> ApiInvocationRequest
    -> PowerShellInvocationCoordinator
    -> bounded admission slot
    -> bounded execution slot
    -> RunspacePoolManager lease
    -> request-scoped PowerShell instance
    -> AddCommand(allowlisted function)
    -> AddParameter(request values)
    -> ApiInvocationResult
```

The shared execution core has no HTTP, WebSocket, or SSE dependency.

## Runtime behavior

Proof configuration:

- minimum runspaces: 1
- maximum runspaces: 4
- queue capacity: 32
- queue wait timeout: 10 seconds

Each invocation uses its own request-scoped `PowerShell` instance attached to the shared pool.

## Timeout/cancellation

PowerShell invocation is asynchronous.

Active timeout/cancellation uses:

`PowerShell.StopAsync(...)`

Timeout/cancellation must actually stop work rather than merely abandoning the caller.

## Conservative pool recovery

A timed-out or actively canceled pooled runspace is not silently trusted.

Phase 4 rebuilds the whole pool after:

- invocation timeout
- caller cancellation during active execution
- explicit recovery request

Rebuilds are synchronized.

## Cross-request state

Tests confirmed:

- ordinary request parameters remain caller-specific
- concurrent calls return correct IDs
- function-local values do not normally leak
- deliberately written global/session state can persist in a reused runspace
- rebuilding the pool clears explicit global state

Therefore, pooled runspaces are not hostile-script isolation.

---

# 8. Phase 4.1 — COMPLETE

The initial full-suite run after Phase 4 had one EXE diagnostics failure:

`ExeExportServiceDiagnosticsTests.TemporaryDirectoryFailure_IsServiceOwnedAndPreservesOriginalException`

A narrowly scoped cleanup investigation found:

- named test passed 10/10 repeated runs
- EXE diagnostics class passed repeatedly
- REST-before-EXE test ordering did not reproduce it
- broader diagnostics reliability tests did not reproduce it
- no source/test code was changed

Classification:

`INTERMITTENT / NOT REPRODUCED`

The cleanup pass restored a fully green baseline at that time.

Do not invent a root cause for the old one-off failure.

One observed but unproven edge was that `DeveloperDiagnostics.Shutdown()` may stop the writer without clearing `_sessionState`. It was not proven related and was not changed.

---

# 9. Phase 5A — COMPLETE / ACCEPTED

**Production-safe JSON normalization foundation**

Phase 5A replaced the Phase 3 proof-only result normalizer with a bounded, transport-neutral PowerShell result normalizer.

Current successful-output path:

```text
REST endpoint
    -> ApiInvocationRequest
    -> PowerShellInvocationCoordinator
    -> ApiInvocationResult.Output as raw IReadOnlyList<PSObject>
    -> PowerShellResultNormalizer.Normalize(...)
    -> NormalizedApiResult
    -> Results.Json(normalized.Value)
```

`ApiInvocationResult` still represents execution and captured stream data.

Normalization occurs after execution and before REST serialization.

That keeps the execution core reusable and avoids HTTP dependencies in the normalizer.

---

# 10. Phase 5A Supported Scalars

The normalizer supports:

- `null`
- string
- char -> one-character string
- bool
- byte / sbyte
- short / ushort
- int / uint
- long / ulong
- float
- double
- decimal
- DateTime
- DateTimeOffset
- Guid
- enum -> stable enum name string

Enums intentionally serialize as text rather than underlying numeric values.

---

# 11. Phase 5A PSObject / PSCustomObject Policy

Raw `PSObject` values are explicitly inspected rather than passed blindly to `System.Text.Json`.

Behavior:

1. reject known formatting objects first
2. return scalar base objects as scalars
3. route dictionary/enumerable base objects through bounded collection handling
4. otherwise select gettable PowerShell properties
5. sort property names deterministically
6. recursively normalize values

Relevant PowerShell members include:

- `NoteProperty`
- `Property`
- `ScriptProperty`

Case-insensitive duplicate property names fail deterministically.

PowerShell adaptation/formatting internals are not blindly exposed.

---

# 12. Phase 5A .NET Object Policy

Ordinary .NET objects are normalized conservatively using:

- public
- instance
- readable
- non-indexer

properties.

Ignored:

- static properties
- indexers
- write-only properties

Objects with no usable readable properties are rejected as unsupported.

Property getter exceptions become typed normalization failures.

Raw exception messages/stacks are not returned to clients.

---

# 13. Phase 5A Dictionaries / Collections

## Dictionaries

`IDictionary` values become string-key dictionaries.

Simple string/scalar keys may be converted with invariant/stable formatting.

Complex keys are rejected rather than passed through arbitrary `ToString()`.

Keys are sorted deterministically.

Case-insensitive duplicate keys fail.

## Collections

`IEnumerable` becomes a bounded list.

Strings remain scalar strings.

Enumeration is bounded by configured item limits.

---

# 14. Phase 5A Pipeline Cardinality

Top-level behavior:

- zero pipeline results -> `null`
- one result -> one normalized value/object
- multiple results -> array/list

This preserves Phase 3 behavior.

---

# 15. Phase 5A Depth Protection

Configuration:

`ApiRuntimeOptions.SerializationDepth`

Proof value:

`8`

Semantics:

- root value = depth 1
- nested property/dictionary/list value increments depth
- multiple-result synthetic result list = depth 1
- its individual pipeline items start at depth 2

Exceeding the limit gives:

`NormalizationFailureKind.DepthExceeded`

No silent truncation.

---

# 16. Phase 5A Cycle Detection

Cycle tracking is request-scoped and reference-identity based.

Implementation concept:

- create tracking state for each normalization call
- add a reference when entering
- remove it when exiting
- if the same active reference is entered again:
  `NormalizationFailureKind.CycleDetected`

Tests include:

- self-reference
- two-object cycle

No tracking state is retained globally.

---

# 17. Phase 5A Item Limits

Configuration:

`ApiRuntimeOptions.ResponseItemLimit`

Proof value:

`1000`

The top-level pipeline count is checked.

Nested dictionary entries and enumerable elements consume from the same per-call normalization item budget.

Exceeding it produces:

`NormalizationFailureKind.ItemLimitExceeded`

No silent truncation/discard.

---

# 18. Phase 5A Exact Byte Limit

Configuration:

`ApiRuntimeOptions.ResponseByteLimit`

Proof value:

`5,242,880` bytes

After normalization, exact UTF-8 JSON size is measured with:

`JsonSerializer.SerializeToUtf8Bytes(...)`

Oversize produces:

`NormalizationFailureKind.ByteLimitExceeded`

This protects both large collections and one oversized string.

Do not replace this with character-count estimates.

---

# 19. Phase 5A Formatting Object Rejection

PowerShell formatting output is not valid API data.

Detection checks PowerShell type information for:

`Microsoft.PowerShell.Commands.Internal.Format.`

Rejected output produces:

`NormalizationFailureKind.FormattingObjectRejected`

Important test-environment deviation:

Actual `Format-Table` execution could not be used because `System.Management.Automation` attempted to load `Microsoft.PowerShell.Commands.Diagnostics.dll`, which was absent from test output.

The test therefore uses a deterministic object marked with the same internal formatting type-name prefix.

The actual detection logic is namespace-prefix based and not hard-coded only to that one test object.

---

# 20. Phase 5A Getter Failure Security

Getter failures produce:

`NormalizationFailureKind.PropertyGetterFailed`

Safe failure messages do not expose:

- exception text
- stack traces
- object dumps
- property values
- script source
- filesystem paths
- arbitrary unknown-object `ToString()` output

Keep this behavior.

---

# 21. Current Temporary REST Normalization Error Mapping

On normalization failure, REST currently returns a temporary sanitized HTTP 500.

Title:

`PowerShell output could not be serialized.`

Detail:

`The configured PowerShell operation returned output that could not be converted safely.`

This is intentionally temporary.

The final external error taxonomy and final `ProblemDetails` behavior belong to **Phase 5B**.

---

# 22. Phase 5A Test Results

Phase 5A added 12 new tests.

Current `RestApiProofHostTests` total:

- **49 passed**
- **0 failed**
- **0 skipped**

Coverage now includes:

- scalars
- enum-to-string
- null
- PSCustomObject
- nested objects
- ordinary .NET objects
- dictionaries
- hashtables
- arrays/lists
- zero/one/many pipeline results
- depth limit
- self-cycle/two-object cycle
- item limits
- exact UTF-8 byte limit
- oversized string
- formatting-object rejection
- sanitized REST formatting failure
- getter failure
- duplicate property/key rejection
- normalization stress

---

# 23. Current Authoritative Clean Baseline

After Phase 5A:

## REST proof-host

`RestApiProofHostTests`

**49 passed / 0 failed / 0 skipped**

## Phase 1

`PowerShellApiMetadataServiceTests`

**36 passed / 0 failed / 0 skipped**

## Phase 2

`ApiPublishConfiguration`

**46 passed / 0 failed / 0 skipped**

## EXE diagnostics

`ExeExportServiceDiagnosticsTests`

**6 passed / 0 failed / 0 skipped**

## Entire repository

**329 passed / 0 failed / 0 skipped / 329 total**

## Builds

- proof host: **0 warnings / 0 errors**
- tests project: **0 warnings / 0 errors**
- full solution restore/build: **0 warnings / 0 errors**

This is the baseline Phase 5B must preserve.

---

# 24. Phase 5A Stress Sanity

A test normalized 750 small nested payload items twice.

It verified request-scoped tracking and a bounded runtime under 5 seconds.

The test passed.

This is a sanity check, not a benchmark.

---

# 25. Intentionally Deferred After Phase 5A

Not yet implemented:

- final PowerShell stream response policy
- public stream exposure policy
- final Phase 5 error taxonomy
- final REST `ProblemDetails`
- OpenAPI / Swagger
- production project generator
- WPF publish wizard
- local-test UI
- authentication
- WebSocket
- SSE
- streaming responses
- process sandboxing
- worker-process isolation
- cloud deployment
- final File-menu integration

These are not Phase 5A bugs.

---

# 26. EXACT NEXT TASK — PHASE 5B

On Monday, implement:

# **REST API Publisher Phase 5B**
## **Stream Policy, Error Taxonomy, and REST ProblemDetails Mapping**

Phase 5B completes the remaining half of Phase 5.

Do not begin Phase 6 OpenAPI in the same Codex task.

---

# 27. Phase 5B Objectives

Establish a transport-neutral error/result model covering at minimum:

- request/binding failure
- invalid configured function
- queue full
- queue wait timeout
- caller cancellation
- invocation timeout
- PowerShell terminating failure
- PowerShell non-terminating errors
- normalization failures
- serialization/output-limit failure
- host unavailable
- internal failure

REST should map these classifications into consistent HTTP statuses and sanitized `ProblemDetails`.

The core error model must remain independent of HTTP.

---

# 28. Phase 5B PowerShell Stream Policy

Phase 4 already captures PowerShell stream information.

Phase 5B should define explicit handling for:

- Error
- Warning
- Verbose
- Debug
- Information

Recommended direction:

## Warning

A success result with warnings should generally remain successful.

Warnings should be:

- retained only up to a bounded cap
- logged safely
- not automatically included in bare production REST responses

Optional response envelope behavior can remain later/opt-in.

## Verbose / Debug / Information

Do not expose them in production REST responses by default.

They may support future local-test/developer views.

Keep storage bounded.

## Non-terminating Error stream

Default architecture direction:

Treat non-terminating PowerShell errors as a failed invocation unless endpoint configuration explicitly allows partial success later.

Phase 5B should establish a safe default.

## Terminating PowerShell error

Return transport-neutral PowerShell failure.

REST maps to a sanitized server error unless the failure is confidently a parameter binding/validation problem.

---

# 29. Stream Caps

Stream retention must remain bounded.

Architecture recommendation was approximately:

`100 entries`

per relevant stream or an equivalent bounded aggregate.

Reuse existing configuration if already present.

Do not allow scripts producing enormous Warning/Error/etc. streams to create unbounded memory growth.

Do not return all captured stream text to clients.

---

# 30. Phase 5B Error Taxonomy

Exact names should follow repository conventions, but the model needs to distinguish meaningful categories.

Possible categories include:

- ValidationFailure
- InvalidFunction
- QueueFull
- QueueWaitTimedOut
- CallerCanceled
- InvocationTimedOut
- PowerShellBindingFailure
- PowerShellValidationFailure
- PowerShellFailure
- NonTerminatingPowerShellError
- NormalizationDepthExceeded
- NormalizationCycleDetected
- NormalizationItemLimitExceeded
- NormalizationByteLimitExceeded
- FormattingObjectRejected
- PropertyGetterFailed
- HostUnavailable
- InternalFailure

Do not unnecessarily create dozens of public types if a smaller coherent hierarchy works.

Preserve the detailed Phase 5A `NormalizationFailureKind` internally where useful.

---

# 31. Binding / Validation Classification

Parameter binding/validation errors may map to HTTP 400.

But only classify an error as client-caused when confidently identified.

Examples:

- missing mandatory parameter
- known type conversion failure
- PowerShell parameter binding failure
- `[ValidateSet]` / other validation attribute failure

Do not turn arbitrary PowerShell execution exceptions into HTTP 400.

Prefer reliable SMA exception types/error IDs over fragile string matching.

Investigate actual runtime behavior before implementing mappings.

---

# 32. REST Status Mapping Direction

Recommended V1 mappings:

| Condition | HTTP status |
|---|---:|
| Missing/invalid input | 400 |
| Confident PowerShell binding/validation failure | 400 |
| Queue full | 429 |
| Queue wait timeout | 429 |
| Host unavailable | 503 |
| Invocation timeout | 504 |
| PowerShell terminating failure | 500 |
| Non-terminating PowerShell error | 500 by default |
| Normalization failure | 500 |
| Unexpected/internal failure | 500 |

Authentication 401/403 belongs to the later security phase.

Caller disconnect may not have a response because the client is gone.

Avoid presenting HTTP 499 as a standard public response unless deliberately justified.

---

# 33. REST ProblemDetails

Replace ad hoc REST error shapes with consistent sanitized `ProblemDetails`.

Conceptual shape:

```json
{
  "type": "https://ps7scriptdesk/errors/...",
  "title": "...",
  "status": 500,
  "detail": "...",
  "requestId": "..."
}
```

Requirements:

- deterministic type/title/status
- safe detail
- request/correlation ID when practical
- no stack trace
- no local paths
- no script source
- no complete parameters
- no secret headers/tokens
- no raw PowerShell `ErrorRecord`
- no arbitrary object dumps

---

# 34. Success Envelope Boundary

Bare normalized output remains the default success response.

Do not automatically break API output by changing every success response to:

```json
{
  "data": ...,
  "warnings": ...
}
```

The architecture direction is:

- bare result by default
- envelope later or opt-in

Warnings/debug streams should not force a V1 response-shape change.

---

# 35. Normalization Failure Mapping in Phase 5B

Map the Phase 5A normalization failures into the common error architecture:

- `DepthExceeded`
- `CycleDetected`
- `ItemLimitExceeded`
- `ByteLimitExceeded`
- `FormattingObjectRejected`
- `PropertyGetterFailed`
- unsupported value/duplicate-property cases where represented

REST may still use 500 for these in V1, but:

- the internal taxonomy should remain precise
- `ProblemDetails` should stay sanitized
- logs may record the safe typed failure kind

Do not expose internal object content.

---

# 36. Phase 5B Required Tests

Codex should add tests for at least:

## PowerShell stream policy

- warning captured
- verbose captured
- debug captured
- information captured
- stream caps enforced
- streams not included in default bare REST success body

## Non-terminating error

- deterministic test function produces output plus non-terminating PowerShell error
- default policy yields failure
- raw error details do not leak

## Terminating error

- maps to stable transport-neutral error
- REST returns sanitized `ProblemDetails`

## Binding/validation errors

- confidently identified validation/binding failure maps to 400
- generic script failure remains 500

## Capacity/lifecycle errors

- queue full -> 429
- queue wait timeout -> 429
- invocation timeout -> 504
- host unavailable -> 503

## Phase 5A failures

Verify standardized ProblemDetails for:

- depth
- cycle
- item limit
- byte limit
- formatting object
- getter failure

## Sanitization

Assert REST error bodies do not expose:

- stack traces
- script paths
- source
- raw exception messages where unsafe

## Request ID

If request IDs are introduced, test their presence/format without exposing internals.

## Regression

All current REST proof behavior remains green.

---

# 37. Phase 5B Build / Test Requirements

After implementation:

1. build proof host
2. build tests
3. run Phase 5B-specific tests
4. run all `RestApiProofHostTests`
5. run Phase 1 parser tests
6. run Phase 2 configuration tests
7. run EXE diagnostics tests
8. run complete repository suite
9. run full solution restore/build

Starting baseline:

**329 passed / 0 failed / 0 skipped**

The test count should increase after Phase 5B.

No unexplained regression is acceptable.

---

# 38. Phase 5B Codex Report Requirements

Create:

`REST_API_PHASE5B_IMPLEMENTATION_REPORT.md`

Required sections:

- Summary
- Files Added
- Files Modified
- Final Error Architecture
- Error Taxonomy
- Stream Policy
- Stream Caps
- Non-Terminating Error Policy
- Binding/Validation Classification
- Normalization Failure Mapping
- REST Status Mapping
- ProblemDetails Shape
- Sanitization / Privacy
- Request/Correlation IDs
- Tests Added
- Phase 5B Test Results
- REST Phase 3/4/5A Regression Results
- Phase 1/2 Results
- EXE Diagnostics Results
- Complete Repository Suite
- Full Build
- Deviations
- Known Limitations
- Final Assessment

Final assessment:

`PHASE 5B COMPLETE`

or:

`PHASE 5B INCOMPLETE`

Do not start Phase 6.

---

# 39. Remaining Roadmap After Phase 5B

## Phase 6 — OpenAPI

Expected work:

- route/method metadata
- parameter source/type/required status
- JSON body schema
- known response schemas where possible
- generic JSON schema where unknown
- common error responses
- Swagger UI for local test
- configurable Swagger exposure for published APIs

## Phase 7 — Project Generator

Generate the production ASP.NET Core API host project.

## Phase 8 — Local Test Host

PS7 ScriptDesk should generate/build/start/stop the child API and show status/logs.

## Phase 9 — Wizard UI

Add:

`Publish as API -> REST API`

and the WPF publishing workflow.

## Phase 10 — Publishing

Self-contained Windows x64 and ARM64.

## Phase 11 — Security Hardening

API key auth, network exposure warnings, HTTPS guidance, secret handling, limits, elevation warnings.

## Phase 12 — Regression and Documentation

Final REST V1 regression/testing/docs.

Then:

- WebSocket transport
- SSE transport

---

# 40. Security Invariants — Preserve Forever

1. Remote clients cannot submit arbitrary PowerShell source.
2. Remote clients cannot select arbitrary function names.
3. Functions come from trusted generated/server configuration.
4. Invocation uses `AddCommand` and `AddParameter`.
5. Request strings never become executable PowerShell source.
6. API execution does not use ScriptDesk terminal/debugger services.
7. API runs separately from the WPF application.
8. `RunspacePool` is not a sandbox.
9. Do not leak stack traces/local secrets through REST.
10. Keep concurrency/queue/output limits bounded.
11. Timeout/cancellation must actually stop execution.
12. Normalization remains depth/cycle/item/byte bounded.
13. Do not silently truncate output.
14. Do not expose PowerShell formatting infrastructure as API data.
15. Do not expose Warning/Verbose/Debug/Information streams by default in production responses.

---

# 44. Product Vision, Market Positioning, and Target Use Cases

This section preserves the product rationale behind the API work so future implementation decisions are guided not only by technical architecture, but by the customer value the feature is intended to create.

## 44.1 Core Product Thesis

The strongest product concept is:

> **Write an ordinary PowerShell function in PS7 ScriptDesk, then turn it into a deployable application or service without requiring the user to become a .NET/web-platform developer.**

PS7 ScriptDesk should ultimately support two distinct deployment paths from the same PowerShell development environment:

```text
WRITE
  ↓
DEBUG
  ↓
TEST
  ↓
CHOOSE DEPLOYMENT TARGET
  ├── Export as EXE
  └── Publish as API
        ├── REST API
        ├── WebSocket API
        └── Server-Sent Events (SSE)
```

This is a broader product proposition than simply being a PowerShell editor or an API wrapper.

The goal is to make PS7 ScriptDesk a **PowerShell application and service generation environment**.

---

## 44.2 Why This Is Marketable

PowerShell is already heavily used for:

- systems administration
- enterprise automation
- cloud administration
- Microsoft 365
- Exchange
- Active Directory
- Azure
- Windows Server
- VMware
- Hyper-V
- SQL Server administration
- Intune / SCCM
- certificate management
- provisioning
- help-desk automation
- legacy-system integration
- operational tooling
- scheduled maintenance
- infrastructure automation

Organizations often possess substantial existing PowerShell script libraries.

A recurring enterprise problem is:

> “We already have a PowerShell script/function that performs the task. How do we let another application safely invoke it?”

Today, common answers include:

- rewrite the logic in C#
- have a developer build a web service around it
- install/configure a separate automation platform
- manually host PowerShell behind another framework
- launch scripts indirectly from another system

PS7 ScriptDesk can potentially reduce that to:

```text
Open existing PowerShell script
        ↓
Select function
        ↓
Publish as API
        ↓
Configure endpoint
        ↓
Test locally
        ↓
Publish/deploy
```

That is the key customer value.

---

## 44.3 Primary Target Market

The initial market should be **enterprise and internal automation**, not “build general public websites with PowerShell.”

Strong target users include:

- Windows administrators
- PowerShell developers
- infrastructure engineers
- cloud administrators
- Microsoft 365 administrators
- DevOps engineers
- help-desk automation teams
- enterprise integration teams
- small internal development teams
- organizations with large existing PowerShell libraries

The most compelling use cases are services that expose trusted internal automation functions to:

- ServiceNow
- internal web applications
- monitoring systems
- orchestration platforms
- ticketing systems
- dashboards
- other internal APIs
- scheduled jobs
- mobile/internal tools
- line-of-business systems

---

## 44.4 Competitive Validation

Existing technologies such as:

- PowerShell Universal
- Pode

demonstrate that there is already a real use case for hosting PowerShell through REST/web-service infrastructure.

That is positive market validation.

PS7 ScriptDesk should not try to copy these products feature-for-feature.

The differentiation should be:

> **Integrated PowerShell development, debugging, testing, API generation, packaging, and publishing inside one desktop IDE.**

The user should not need to become an expert in:

- ASP.NET Core
- controllers
- Minimal API route plumbing
- runspaces
- JSON serialization
- cancellation infrastructure
- HTTP status mapping
- Swagger/OpenAPI
- deployment project structure

PS7 ScriptDesk should generate that infrastructure from the PowerShell code and the user's wizard selections.

---

## 44.5 Key Differentiator: Existing PowerShell Functions Become APIs

Example source:

```powershell
function Get-EmployeeInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$EmployeeId
    )

    # Existing enterprise logic
}
```

The intended user experience should allow this to become something like:

```text
GET /api/employees/{employeeId}
```

without the user manually writing an ASP.NET Core application.

The wizard should discover:

- function name
- parameter names
- parameter types
- mandatory state
- aliases
- validation metadata
- default values where statically known

and use that metadata to propose API definitions.

---

## 44.6 Automatic Endpoint and OpenAPI Generation Is a Major Selling Feature

A PowerShell function such as:

```powershell
function Get-ComputerStatus {
    param(
        [Parameter(Mandatory)]
        [string]$ComputerName,

        [ValidateSet('Basic','Full')]
        [string]$Detail = 'Basic'
    )

    # Existing logic
}
```

could allow ScriptDesk to suggest:

```text
GET /api/computer/status

Query parameters:

computerName
  type: string
  required: yes

detail
  type: string
  required: no
  allowed:
    Basic
    Full
  default:
    Basic
```

The same metadata can drive:

- endpoint configuration
- request validation
- generated documentation
- Swagger/OpenAPI
- test UI

This is a significant product differentiator.

The user should feel that ScriptDesk **understands the PowerShell function and builds the service contract around it**.

---

## 44.7 Export as EXE and Publish as API Are Complementary

These should remain separate File-menu workflows.

Recommended eventual structure:

```text
File
  ...
  Export as EXE...
  Publish as API
      REST API...
      WebSocket API...
      Server-Sent Events (SSE)...
```

Do not merge API publishing into the EXE wizard.

They serve different deployment goals.

### Export as EXE

Best for:

- standalone utilities
- administrative tools
- desktop/console deployment
- users who need a local executable

### Publish as API

Best for:

- integration
- automation services
- remote invocation
- service-to-service use
- web/mobile/internal application backends
- exposing controlled enterprise operations

The ability to choose either from the same PowerShell source is a core product advantage.

---

## 44.8 REST + WebSocket + SSE Product Story

REST alone is useful.

REST + WebSocket + SSE makes the feature substantially more powerful.

### REST

Useful for:

- request/response operations
- CRUD-style automation
- service integration
- one-shot function calls

Example:

```text
POST /api/deploy
```

### SSE

Useful for:

- progress
- status events
- logs
- long-running operation updates
- one-way server-to-client streaming

Example:

```text
event: progress
data: {"percent":35,"message":"Installing components"}
```

### WebSocket

Useful for:

- interactive two-way control
- long-lived sessions
- bidirectional command/status exchange
- interactive management applications

Together, these transports can position PS7 ScriptDesk as a **PowerShell application-service generator**, rather than simply a script IDE with an HTTP wrapper.

---

## 44.9 Keep the Shared-Core Architecture Because It Supports the Product Vision

The technical architecture already supports the product positioning.

REST, WebSocket, and SSE should share:

- function metadata
- configuration models
- allowlisting
- request/invocation model
- runspace pool
- concurrency
- timeout/cancellation
- result normalization
- stream handling
- error taxonomy
- logging
- security concepts
- generated-project infrastructure

Only transport-specific concerns should differ.

This is strategically important because it lets PS7 ScriptDesk add new deployment types without rebuilding the execution engine.

---

## 44.10 Generated Output Should Remain Inspectable

A strong differentiator should be that PS7 ScriptDesk generates a normal, inspectable ASP.NET Core project rather than forcing users into a proprietary opaque runtime.

Advanced users should be able to:

- keep the generated project
- inspect the generated C#
- inspect endpoint configuration
- modify the project if desired
- build it independently
- place it under source control

This improves:

- trust
- enterprise acceptance
- debugging
- extensibility
- portability

Avoid unnecessary lock-in.

---

## 44.11 Recommended Product Message

A concise future marketing direction could be:

> **Build in PowerShell. Deploy as an app or an API.**

Another possible positioning:

> **Turn trusted PowerShell automation into deployable applications and services—without rewriting it in C#.**

The exact marketing copy can change later, but the implementation should preserve the underlying value proposition.

---

## 44.12 Phase 9 UI Should Reflect This Product Vision

When the project reaches Phase 9 — Wizard UI — do not treat it as merely “add some controls.”

The wizard is a key part of the product experience.

The intended user should be able to move from:

```text
PowerShell function
```

to:

```text
working, documented, secure API
```

without needing to understand the generated ASP.NET Core plumbing.

The UI should:

- show discovered functions clearly
- explain which functions are publishable
- propose sensible endpoint defaults
- make parameter mappings understandable
- expose security choices clearly
- show runtime/concurrency limits in plain language
- provide local test controls
- preview endpoint URLs
- preview OpenAPI/Swagger behavior
- make publishing understandable
- avoid generic “AI-generated” visual styling
- use PS7 ScriptDesk's established modern design language

This should feel like a professional developer/admin tool.

---

## 44.13 Product Success Criterion

The feature should ultimately make this statement true:

> An experienced PowerShell administrator who does not know ASP.NET Core should be able to take an existing well-structured PowerShell function and safely publish it as a documented, testable API using PS7 ScriptDesk.

If the workflow still requires the user to manually understand runspaces, web-server plumbing, JSON serialization, or ASP.NET Core project structure, the product has not fully achieved its purpose.

---

# 45. Exact Monday Restart Prompt

Upload this file plus `REST_API_PHASE5A_IMPLEMENTATION_REPORT.md` and preferably `REST_API_PUBLISHER_ARCHITECTURE.md`.

Then say:

> We are restarting the PS7 ScriptDesk Publish as API project. Read the uploaded Monday restart/handoff document and the Phase 5A implementation report. Treat them as the authoritative current project state. Phases 1 through 5A are complete. The clean baseline is 329 passed, 0 failed, 0 skipped, and the full build has 0 warnings and 0 errors. Preserve the Product Vision, Market Positioning, and Target Use Cases section when making architecture or UI decisions. Review the state briefly, then prepare the Codex implementation task for Phase 5B only: Stream Policy, Error Taxonomy, and REST ProblemDetails Mapping. Do not redo earlier phases and do not begin Phase 6.

---

# 46. Files to Upload Monday

Recommended:

1. `PS7_ScriptDesk_Publish_As_API_Project_Restart_Handoff_MONDAY_AFTER_PHASE5A_WITH_PRODUCT_VISION.md`
2. `REST_API_PHASE5A_IMPLEMENTATION_REPORT.md`
3. `REST_API_PUBLISHER_ARCHITECTURE.md`

Helpful if available:

4. `REST_API_PHASE4_IMPLEMENTATION_REPORT.md`
5. `REST_API_PHASE4_REGRESSION_CLEANUP_REPORT.md`

Older Phase 1–3 reports are normally unnecessary if this restart file is available.

---

# 47. Current Status Table

| Phase | Status |
|---|---|
| 1 — Metadata Parser | COMPLETE |
| 2 — API Configuration Model | COMPLETE |
| 3 — Standalone REST Proof Host | COMPLETE / ACCEPTED |
| 4 — PowerShell Runspace Host | COMPLETE / ACCEPTED |
| 4.1 — Regression Cleanup | COMPLETE |
| 5A — JSON Normalization Foundation | COMPLETE / ACCEPTED |
| 5B — Stream Policy / Error Taxonomy / ProblemDetails | NEXT |
| 6 — OpenAPI | NOT STARTED |
| 7 — Project Generator | NOT STARTED |
| 8 — Local Test Host | NOT STARTED |
| 9 — Wizard UI | NOT STARTED |
| 10 — Publishing | NOT STARTED |
| 11 — Security Hardening | NOT STARTED |
| 12 — Regression & Documentation | NOT STARTED |

---

# 48. Final Handoff State

This remains an excellent stopping point.

The REST publisher already has:

- static PowerShell function discovery
- durable API configuration
- standalone working REST proof host
- trusted function allowlisting
- command-injection-resistant invocation
- bounded `RunspacePool` execution
- bounded queueing/concurrency
- real timeout/cancellation
- conservative pool recovery
- cross-request state characterization
- production-safe transport-neutral output normalization
- scalar/object/collection handling
- depth limits
- cycle detection
- item limits
- exact UTF-8 byte limits
- oversized response rejection
- formatting-object rejection
- safe property-getter failure handling
- sanitized temporary REST normalization failures
- a fully green repository baseline
- a defined product/market vision for the feature

Authoritative current baseline:

# **329 passed / 0 failed / 0 skipped**
# **Full solution build: 0 warnings / 0 errors**

Next step:

# **Phase 5B — Stream Policy, Error Taxonomy, and REST ProblemDetails Mapping**

Do not begin Phase 6 until Phase 5B has been implemented, tested, reported, uploaded, and reviewed.
