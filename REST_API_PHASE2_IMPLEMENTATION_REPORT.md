# REST API Publisher Phase 2 Implementation Report

## Files Added

- `PS7ScriptDesk.Domain/Models/ApiPublishConfiguration.cs`
- `PS7ScriptDesk.Application/Interfaces/IApiPublishConfigurationValidator.cs`
- `PS7ScriptDesk.Application/Interfaces/IApiPublishConfigurationStore.cs`
- `PS7ScriptDesk.Application/Services/ApiPublishConfigurationValidator.cs`
- `PS7ScriptDesk.Infrastructure/Services/ApiPublishConfigurationStore.cs`
- `PS7ScriptDesk.Tests/ApiPublishConfigurationTests.cs`
- `PS7ScriptDesk.Tests/ApiPublishConfigurationStoreTests.cs`
- `REST_API_PHASE2_IMPLEMENTATION_REPORT.md`

## Files Modified

- None beyond adding the Phase 2 files above.

## Model Structure

Phase 2 adds a durable `ApiPublishConfiguration` root with schema version, source script identity, transport, API metadata, endpoint configurations, runtime/resource limits, security, OpenAPI settings, and publish-output preferences.

Transport-neutral models include API metadata, endpoints, parameter bindings, runtime/resource options, security options, response behavior, validation diagnostics, and publish output preferences. REST-specific endpoint concerns are isolated in `ApiRestEndpointOptions`.

The configuration stores the source script filename rather than a full absolute path when saved as a companion file next to the `.ps1`. It never stores PowerShell source text.

## Enums

Added durable enum values for:

- `ApiTransport`: REST, WebSocket, ServerSentEvents.
- `ApiHttpMethod`: GET, POST, and deferred future verbs.
- `ApiParameterSource`: Route, Query, Body, Header, ServerDefined.
- `ApiRequiredBehavior`.
- `ApiArrayBindingBehavior`.
- `ApiSecurityMode`.
- `ApiServerDefinedValueKind`.
- `ApiNoOutputBehavior`.

WebSocket, SSE, PUT, PATCH, DELETE, JWT, and Windows authentication are represented for future compatibility but rejected by Phase 2/REST V1 validation.

## Validator Behavior

`ApiPublishConfigurationValidator` performs static validation only. It validates schema version, supported transport, security mode, resource limits, endpoint IDs, REST methods, route syntax, duplicate method/route pairs, route token/binding correspondence, duplicate parameter bindings, GET body binding rejection, POST body binding shape, server-defined values, function existence, publishability, mandatory parameter bindings, static parameter metadata completeness, and supported REST V1 parameter types.

Validation diagnostics include stable codes, messages, JSON-style paths where practical, endpoint IDs, and parameter names.

## Persistence Behavior

`ApiPublishConfigurationStore` implements deterministic companion-file paths, save, load, and existence checks. It writes indented UTF-8 JSON, uses a temporary file in the destination directory, then replaces or moves into place. Temporary file cleanup is best-effort.

The store rejects unsaved/non-`.ps1` source paths. It also refuses to save secret-sensitive server-defined literal values and refuses to overwrite malformed existing JSON without surfacing an error.

## Companion-File Format

For `Inventory.ps1`, the companion path is `Inventory.ps7api.json`. Uppercase `.PS1`, filenames with dots, Unicode filenames, relative paths, and absolute paths are covered by tests.

Persisted JSON uses readable enum strings, stable camel-case property names from the model names, schema version `1`, and no unsafe `$type` metadata. Unknown future fields are ignored during deserialization.

## Schema Version Behavior

The current schema version is `1`. Loading rejects missing, invalid, or unsupported future schema versions with `InvalidDataException`. No migrations are implemented in Phase 2.

## Selected Resource Defaults

Defaults are deterministic:

- runspace pool minimum: 1
- runspace pool maximum: 4
- maximum concurrent executions: 4
- queue limit: 32
- queue wait timeout: 10 seconds
- default invocation timeout: 30 seconds
- request body limit: 1 MB
- response item limit: 1,000
- response byte limit: 5 MB
- serialization depth: 8
- retained warning/error stream entries: 100

## Secret Handling Design

API key authentication stores only an environment-variable name such as `PS7API_API_KEY`; there is no plaintext API-key value property. Server-defined values can be literal non-secret values or environment-variable references. Secret-sensitive literal values are validation errors and are refused by the persistence layer.

## Exact Tests Run

1. `dotnet build PS7ScriptDesk.Infrastructure\PS7ScriptDesk.Infrastructure.csproj`
   - Passed.
   - 0 warnings, 0 errors.

2. `dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --filter "FullyQualifiedName~ApiPublishConfiguration"`
   - Passed.
   - 46 passed, 0 failed, 0 skipped.
   - Required elevated sandbox approval because the Windows-targeted test graph reads the local Windows SDK cache under `C:\Users\rbarn\AppData\Local\Microsoft SDKs`.

3. `dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --filter FullyQualifiedName~PowerShellApiMetadataServiceTests`
   - Passed.
   - 36 passed, 0 failed, 0 skipped.
   - Required elevated sandbox approval for the same Windows SDK cache access.

4. `dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --no-build`
   - Passed.
   - 280 passed, 0 failed, 0 skipped.
   - Required elevated sandbox approval for the same Windows SDK cache access.

5. `dotnet build PS7ScriptDesk.slnx`
   - Failed in `PS7ScriptDesk.Package.wapproj` because the dotnet SDK path does not contain `Microsoft.DesktopBridge.props`.
   - Non-packaging projects built before the packaging failure.

6. `& 'C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe' PS7ScriptDesk.slnx /restore /m /p:Configuration=Debug /p:Platform=x64`
   - Passed.
   - 4 warnings, 0 errors.
   - Warnings are the existing Shell warnings in `MainWindow.xaml.cs`: CS4014 at line 5243 and CS8604 at line 7129, emitted for both the temporary WPF project and the Shell project.

## Known Limitations

No REST host, request binding, runspace pool, OpenAPI generation, local test process, publishing workflow, UI, WebSocket, or SSE protocol implementation is present. Enum type validation is static and conservative; unknown enum-like types produce warnings because Phase 2 does not execute PowerShell or load assemblies.

## Risks Discovered

The current repository does not include the `docs/` files named by `AGENTS.md`; only `docs/LocalOnly_NotForGitHub` is present. Phase 2 therefore follows the REST architecture and Phase 1 reports plus direct repository conventions.

## Static Validation Confirmation

Phase 2 validation does not execute PowerShell, launch `pwsh`, create runspaces, inspect network ports, read secrets, contact external services, or start ASP.NET Core.

## Phase Boundary Confirmation

No Phase 3+ functionality was implemented. There is no HTTP server, ASP.NET Core host, Minimal API mapping, request handling, JSON response serialization, `ProblemDetails`, authentication handler, Swagger/OpenAPI generation, local Test API process, wizard/menu/XAML work, project generator, publish command, WebSocket, or SSE implementation.
