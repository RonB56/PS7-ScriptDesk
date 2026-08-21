# REST API Publisher - Phase 5A Implementation Report

## Summary

Phase 5A replaced the Phase 3 proof-only output normalizer with a production-safe, transport-neutral PowerShell result normalizer. Successful PowerShell pipeline output is now converted into a bounded JSON-safe object graph before the REST proof host serializes it. The normalizer has no HTTP dependency and is suitable as the foundation for later REST, WebSocket, SSE, and generated-host transports.

## Files Added

- `REST_API_PHASE5A_IMPLEMENTATION_REPORT.md`

## Files Modified

- `PS7ScriptDesk.RestApiProofHost/PowerShell/PowerShellResultNormalizer.cs`
- `PS7ScriptDesk.RestApiProofHost/Api/RestEndpointMapper.cs`
- `PS7ScriptDesk.RestApiProofHost/Scripts/TestApi.ps1`
- `PS7ScriptDesk.RestApiProofHost/Config/TestApi.ps7api.json`
- `PS7ScriptDesk.Tests/RestApiProofHostTests.cs`

## Final Normalization Architecture

The successful-output path is:

```text
REST endpoint
    -> ApiInvocationRequest
    -> PowerShellInvocationCoordinator
    -> ApiInvocationResult.Output as raw IReadOnlyList<PSObject>
    -> PowerShellResultNormalizer.Normalize(...)
    -> NormalizedApiResult
    -> Results.Json(normalized.Value)
```

`ApiInvocationResult` continues to represent the internal execution result and stream data. Normalization happens after execution and before REST serialization, keeping the execution core reusable and avoiding HTTP dependencies in the normalizer.

## Supported Scalars

The normalizer supports:

- `null` -> `null`
- `string` -> same string
- `char` -> one-character string
- `bool` -> same Boolean
- `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong` -> same numeric value
- `float`, `double`, `decimal` -> same numeric value
- `DateTime` -> same `DateTime` value for `System.Text.Json`
- `DateTimeOffset` -> same `DateTimeOffset` value for `System.Text.Json`
- `Guid` -> same `Guid` value for `System.Text.Json`
- enum values -> stable enum name string

Enums intentionally normalize to text instead of underlying numeric values for more predictable API JSON.

## PSObject / PSCustomObject Policy

`PSObject` values are inspected explicitly rather than passed through to `System.Text.Json`. Formatting objects are rejected first. Scalar base objects are returned as scalars. Dictionary and enumerable base objects route through the bounded dictionary/collection logic. Otherwise, gettable PowerShell `NoteProperty`, `Property`, and `ScriptProperty` members are selected, sorted by ordinal property name, and normalized recursively.

PowerShell infrastructure metadata and formatting/adaptation internals are not blindly serialized. Case-insensitive duplicate property names fail deterministically.

## .NET Object Policy

Ordinary .NET objects are normalized conservatively through public instance readable non-indexer properties. Static properties, indexers, and write-only members are ignored. Property names are sorted ordinally and preserved as declared. Objects with no usable public readable properties are rejected as unsupported values. Getter failures return a typed normalization failure.

## Collections / Dictionaries

`IDictionary` values become dictionaries with string keys. String-like and simple scalar keys are converted using invariant or stable string formatting. Complex keys are rejected rather than converted through arbitrary `ToString()` output. Keys are sorted ordinally and case-insensitive duplicates are rejected.

`IEnumerable` values become lists, except strings, which remain scalar strings. Enumeration is bounded by the configured item budget to prevent lazy or very large sequences from running indefinitely.

## Pipeline Cardinality

Top-level pipeline behavior remains compatible with Phase 3:

- zero pipeline results -> `null`
- one pipeline result -> the normalized single value/object
- multiple pipeline results -> a normalized array/list

## Depth Limit

The default depth is sourced from `ApiRuntimeOptions.SerializationDepth`; the proof configuration remains `8`.

Root values are counted as depth `1`. Nested object properties, dictionary values, and collection items increment depth by one. For multiple pipeline results, the synthetic top-level result array is treated as depth `1`, so each pipeline item begins at depth `2`.

When normalization attempts to enter a value deeper than the configured depth, it fails with `NormalizationFailureKind.DepthExceeded`. No truncation is performed.

## Cycle Detection

Cycle detection uses a request-scoped active reference stack backed by `ReferenceEqualityComparer.Instance`. Reference-type objects are added on entry and removed on exit. Re-entering an active reference fails with `NormalizationFailureKind.CycleDetected`. Tracking state is created per normalization call and is not retained globally.

## Item Limits

The item limit is sourced from `ApiRuntimeOptions.ResponseItemLimit`; the proof configuration remains `1000`.

The top-level pipeline count is checked before normalization. For multiple pipeline results, each top-level item also consumes from the recursive item budget. Dictionary entries and enumerable items consume the same per-call recursive budget. A single top-level scalar or object does not consume the budget merely for being the only pipeline result, but any nested dictionary or enumerable items inside it do.

Exceeding the limit fails with `NormalizationFailureKind.ItemLimitExceeded`. No silent discard or truncation is performed.

## Byte Limit

The byte limit is sourced from `ApiRuntimeOptions.ResponseByteLimit`; the proof configuration remains `5,242,880` bytes.

After successful normalization, the normalizer measures the exact serialized UTF-8 JSON size with `JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions)`. If the serialized payload exceeds the configured byte limit, normalization fails with `NormalizationFailureKind.ByteLimitExceeded`. Oversized single strings are covered by this exact measurement.

## Formatting Objects

PowerShell formatting output is rejected before object-property normalization. Detection checks `PSObject.TypeNames` and the base object full type name for the prefix `Microsoft.PowerShell.Commands.Internal.Format.`. Rejected formatting data fails with `NormalizationFailureKind.FormattingObjectRejected`.

The proof endpoint `/api/phase5/formatting` returns a deterministic object marked with the same PowerShell internal formatting type-name prefix so the REST path can validate sanitized failure behavior.

## Property Getter Failures

PowerShell and .NET property getter exceptions are caught by the normalizer and converted to `NormalizationFailureKind.PropertyGetterFailed`. The safe failure message does not include the thrown exception message, stack trace, property value, or object dump.

## Security / Privacy

Failure messages are deliberately generic. They do not include script source, full object dumps, stack traces, filesystem paths, parameter values, or arbitrary `ToString()` output from unknown objects. The REST adapter logs only the endpoint, function, path, elapsed time, serialized byte count on success, and typed failure kind on normalization failure.

## REST Integration

The proof host registers `PowerShellResultNormalizer.Shared` in DI. Successful invocations call `Normalize(result.Output, configuration.Runtime, RestApiProofHostFactory.JsonOptions)`. Normalization success returns `Results.Json(normalized.Value, options: JsonOptions)`. Normalization failure returns a temporary sanitized HTTP 500:

- title: `PowerShell output could not be serialized.`
- detail: `The configured PowerShell operation returned output that could not be converted safely.`

The final public Phase 5 error taxonomy and `ProblemDetails` mapping remain deferred to Phase 5B.

## Tests Added

The Phase 5A coverage added these major tests:

- required scalar coverage, including enum-to-string and null
- PSObject, nested PSCustomObject, .NET object, dictionary, hashtable, list, and array normalization
- pipeline cardinality for zero, one, and many results
- deterministic depth-limit success/failure
- self-reference and two-object cycle detection
- top-level and nested item-limit enforcement
- exact UTF-8 byte-limit enforcement, including an oversized string
- formatting-object rejection through the invocation path
- REST sanitized 500 for formatting output
- property getter failure safety
- case-insensitive duplicate property/key rejection
- bounded repeated normalization stress sanity

## Phase 5A Test Results

The 12 Phase 5A-specific tests passed as part of `RestApiProofHostTests`.

Command:

```text
dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --no-build --filter FullyQualifiedName~RestApiProofHostTests
```

Result: 49 passed / 0 failed / 0 skipped / 49 total.

## REST Phase 3/4 Regression Results

The same `RestApiProofHostTests` run covered the Phase 3/4 proof-host regressions, including GET/POST behavior, command-injection safety, timeout/recovery, cancellation/recovery, concurrency, queue bounds, shutdown, and repeated start/stop behavior.

Result: 49 passed / 0 failed / 0 skipped / 49 total.

## Phase 1/2 Results

Configuration/parser and metadata coverage remained green.

Commands:

```text
dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --no-build --filter FullyQualifiedName~PowerShellApiMetadataServiceTests
dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --no-build --filter FullyQualifiedName~ApiPublishConfiguration
```

Results:

- `PowerShellApiMetadataServiceTests`: 36 passed / 0 failed / 0 skipped / 36 total.
- `ApiPublishConfiguration`: 46 passed / 0 failed / 0 skipped / 46 total.

## EXE Diagnostics Results

Command:

```text
dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --no-build --filter FullyQualifiedName~ExeExportServiceDiagnosticsTests
```

Result: 6 passed / 0 failed / 0 skipped / 6 total.

## Complete Repository Suite

Command:

```text
dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --no-build
```

Result: 329 passed / 0 failed / 0 skipped / 329 total.

## Full Build

Commands:

```text
dotnet build PS7ScriptDesk.RestApiProofHost\PS7ScriptDesk.RestApiProofHost.csproj
dotnet build PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj
& 'C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe' PS7ScriptDesk.slnx /restore /m /p:Configuration=Debug /p:Platform=x64
```

Results:

- Proof host build: 0 warnings / 0 errors.
- Tests project build: 0 warnings / 0 errors.
- Full solution restore/build: build succeeded, 0 warnings / 0 errors.

## Stress / Performance Sanity

`ResultNormalizer_Stress_NormalizesModerateNestedPayloadWithoutRetainedState` normalizes 750 small nested payload items twice with request-scoped tracking and a bounded assertion under 5 seconds. The test passed, confirming no retained state across calls and no obvious pathological slowdown for a moderate response.

## Deviations

Actual `Format-Table` execution could not be used in the current proof-host test environment because `System.Management.Automation` attempted to load `Microsoft.PowerShell.Commands.Diagnostics.dll`, which is not present in the test output. Phase 5A therefore uses a deterministic proof function that returns an object marked with the same internal formatting type-name prefix used by real PowerShell formatting output. The normalizer detection itself is not hard-coded to that one test object; it rejects the PowerShell internal formatting namespace prefix.

The repository-level supporting documents named in `AGENTS.md` were not present under `docs/`; only `docs/LocalOnly_NotForGitHub` exists in this checkout.

## Known Limitations

The following remain explicitly deferred:

- stream response policy and public stream exposure
- final Phase 5 error taxonomy
- final REST `ProblemDetails` design
- OpenAPI/Swagger work
- project generator
- WPF publishing UI, local-test UI, authentication, and broader publishing/security work
- WebSocket, SSE, and streaming responses
- process sandboxing, worker processes, cloud publishing, and REST menu integration

## Final Assessment

PHASE 5A COMPLETE
