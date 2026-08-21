# PS7 ScriptDesk REST API Publisher Architecture Audit

## 0. Scope and Evidence

This is an analysis and architecture deliverable only. No application source, XAML, tests, packages, prototypes, or project files were changed.

Evidence was gathered from the current repository in `C:\Users\rbarn\source\repos\PowerShellStudio`. The supporting documents named by `AGENTS.md` under `docs/` were not present in this checkout, so this audit relies on direct repository inspection rather than those documents.

Important version note: `AGENTS.md` describes the product baseline as .NET 8, but the checked-in projects currently target `net10.0` and `net10.0-windows`. Examples:

- `Directory.Build.props`, lines 5-6, configures `win-x64` restore assets and references `net10.0/win-x64`.
- `PS7ScriptDesk.Shell/PS7ScriptDesk.Shell.csproj`, lines 4-12, targets `net10.0-windows`, uses WPF and Windows Forms, and references AvalonEdit, WebView2, and `System.Management.Automation` 7.6.2.
- `PS7ScriptDesk.PowerShell/PS7ScriptDesk.PowerShell.csproj`, lines 4-15, targets `net10.0` and references only `Application` and `Domain`.
- `PS7ScriptDesk.Domain/Models/ExeExportConfiguration.cs`, line 86, uses `net10.0` as the generated EXE host target.

The REST API publisher should follow the repository as it exists while leaving final target-framework decisions for implementation planning.

## 1. Audit of Current PS7 ScriptDesk Architecture

### Verified Project and Layer Boundaries

The current solution is layered:

| Project | Existing responsibility | Evidence |
| --- | --- | --- |
| `PS7ScriptDesk.Domain` | Durable models and simple result/configuration types. | `ApplicationSettings`, `ExeExportConfiguration`, `ExeExportRequest`, `PowerShellRuntimeInfo` live under `PS7ScriptDesk.Domain/Models`. |
| `PS7ScriptDesk.Application` | Interfaces, validators, diagnostics, app utilities. | `Application/Interfaces/IExeExportService.cs`, lines 7-12; `Application/Services/ExeExportConfigurationValidator.cs`, lines 9-50; `Application/Diagnostics/DeveloperDiagnostics.cs`, lines 24-130. |
| `PS7ScriptDesk.Infrastructure` | File and settings persistence services. | `Infrastructure/Services/ApplicationSettingsService.cs`, lines 12-156. |
| `PS7ScriptDesk.PowerShell` | PowerShell runtime discovery, terminal execution process services, EXE project generation/publish. | `RuntimeService`, `ScriptExecutionService`, `LiveConsoleService`, `ExeExportService`, `ExeHostProjectGenerator`. |
| `PS7ScriptDesk.UI` | View models and UI commands independent of WPF windows where practical. | `UI/ViewModels/MainWindowViewModel.cs`, lines 135-183 and 2603-2792. |
| `PS7ScriptDesk.Shell` | WPF app, windows, dialogs, debugger, editor integrations, composition root. | `Shell/Composition/AppBootstrapper.cs`, lines 13-52; `Shell/MainWindow.xaml`, line 202; `Shell/Dialogs/ExportWizardWindow.xaml`, lines 1-127. |
| `PS7ScriptDesk.Tests` | xUnit regression and reliability tests. | Test classes for EXE export, terminal, diagnostics, settings, and logger reliability. |

The REST publisher should preserve this separation. Durable REST/API models should start in `Domain`. Application-facing contracts and validation should live in `Application`. Generated project creation and static PowerShell metadata analysis can live in `PowerShell` if they have no WPF dependency. WPF wizard and local test UX should remain in `Shell` and `UI`.

### WPF Shell Integration

The current export entry point is a File menu item:

- `PS7ScriptDesk.Shell/MainWindow.xaml`, line 202: `Export as E_XE...` binds to `ExportAsExeCommand` and uses help key `Command.ExportAsExe`.
- `PS7ScriptDesk.UI/ViewModels/MainWindowViewModel.cs`, lines 181-182, creates `ExportAsExeCommand`.
- `MainWindowViewModel.OnExportAsExeAsync`, lines 2603-2792, owns command validation, save-before-export, wizard invocation, progress reporting, and service dispatch.
- `Shell/Composition/AppBootstrapper.cs`, lines 15-23 and 33-43, manually constructs services and injects them into `MainWindowViewModel`.

Recommended integration point for the future UI: add a new File menu branch shaped like `Publish as API -> REST API`, backed by a new `PublishAsRestApiCommand` or a generic `PublishAsApiCommand` with transport-specific targets. Because WebSocket and SSE are future transports, the visible menu should not bake REST into the top-level service boundary.

### Existing Export as EXE Implementation

The EXE export implementation is useful evidence but should not be copied wholesale.

Reusable patterns:

- `MainWindowViewModel.OnExportAsExeAsync`, lines 2603-2792, shows the command lifecycle: validate selection, ensure saved source, open wizard, create request, report progress, call service, publish completion.
- `ExportWizardService.ShowWizard`, `Shell/Services/ExportWizardService.cs`, lines 12-24, adapts the request, analyzes dependencies, opens a WPF wizard, and persists last stable choices.
- `ExportWizardWindow.xaml`, lines 39-44 and 48-114, demonstrates the existing step-button plus tab-host wizard style.
- `ExeExportService.ExportConfiguredScriptAsync`, lines 199-307, validates, prepares an isolated temp workspace, generates a host project, runs `dotnet publish`, verifies output, logs diagnostics, handles cancellation, and cleans temp files.
- `ExeExportService.CreateDotNetPublishStartInfo`, lines 734-766, uses `ProcessStartInfo.ArgumentList` and avoids shell-built publish commands.
- `ExeExportService.CreateExportDiagnosticMetadata`, lines 896-908, logs filenames and runtime metadata without full script text.

Pieces that should remain separate:

- `ExeHostProjectGenerator` is EXE-specific. It embeds a full script and either invokes it through `System.Management.Automation` or starts `pwsh.exe`; REST requires endpoint configuration, an ASP.NET Core host, request binding, JSON output, and runspace-pool execution.
- `PowerShellDependencyAnalyzer` is intentionally regex-based and dependency-focused, not a publishable function metadata parser.
- `ExeExportConfiguration` contains executable packaging concepts such as application type, icon, EXE target architecture, and administrator manifest. Those should not become the API endpoint model.

### PowerShell Runtime Discovery

`RuntimeService` is the strongest reusable current component for finding PowerShell:

- `RuntimeService.DiscoverRuntimes`, lines 41-157, enumerates candidate paths, uses a metadata fast path unless launch validation is required, consolidates duplicates, chooses a preferred PowerShell 7 runtime, and logs `DeveloperDiagnostics`.
- `RuntimeService.TryBuildRuntimeFromFileMetadata`, lines 724-783, rejects non-`pwsh.exe`, enforces PowerShell 7 or later, and builds a trusted metadata-based runtime.
- `RuntimeService.ProbeRuntimeCandidate`, lines 913-937, can launch `pwsh.exe` with `-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ...` to verify runtime identity.
- `RuntimeService.CreateRuntimeInfo`, lines 1261-1289, computes `IsPowerShell7OrLater` from edition/version.

The REST publisher should reuse runtime discovery for UI/runtime selection and for deciding the PowerShell SDK/runtime compatibility story. The generated REST app itself should not depend on the editor's live terminal process or selected runtime state after generation; it should carry explicit runtime configuration.

### System.Management.Automation Usage

Current direct use is split:

- `PS7ScriptDesk.Shell` references `System.Management.Automation` 7.6.2 in the shell project file, line 27.
- `ExeHostProjectGenerator` emits generated source that references `System.Management.Automation` and `System.Management.Automation.Runspaces`, lines 151-152, and invokes the embedded script through `InitialSessionState.CreateDefault2()` and `PowerShell.Create(initialSessionState)`, lines 174-183.
- `PowerShellDiagnosticsService` builds parser scripts using `System.Management.Automation.Language.Parser.ParseInput`, lines 570-574, and finds `FunctionDefinitionAst` nodes, lines 590-596.
- `PowerShellCompletionService` uses `CommandMetadata` and command parameter metadata in a helper PowerShell script, lines 1767-1810 and 1873-1903.

REST publishing should use `System.Management.Automation` in the generated host and in static metadata services, but should not move REST execution into the WPF shell process.

### Runspaces, Terminal Execution, and Debugger Execution

Existing execution paths are intentionally interactive/process-oriented:

- `MainWindowViewModel.OnRunAsync`, lines 3007-3041, sends the visible editor content or saved file path into the terminal.
- `MainWindowViewModel.EnsureConsoleSessionAsync`, lines 3633-3703, gates and starts/restarts the shared terminal session.
- `LiveConsoleService.StartSessionAsync`, lines 296-342, owns a long-lived interactive terminal process and ensures only the correct runtime session is running.
- `LiveConsoleService.StartPseudoConsoleSession`, lines 1359-1468, creates a Windows ConPTY session and launches `pwsh.exe` in STA mode.
- `LiveConsoleService.StartRedirectedSession`, lines 1578-1630, is a fallback redirected process session.
- `ScriptExecutionService.ExecuteScriptAsync`, lines 35-149, is an older or alternate per-script process snapshot path. It writes a temp `.ps1`, starts `pwsh.exe`, redirects output/error, and kills the process tree on stop.
- `PsesDebugSession`, lines 16-90 and 147-211, manages a separate debug `pwsh.exe` process, bootstrap markers, request gates, timeouts, and stream readers.

The generated REST API must not share `LiveConsoleService`, `ScriptExecutionService`, or `PsesDebugSession`. Those services are optimized for editor/terminal/debugger workflows, not multi-request HTTP execution. The REST host should have its own PowerShell execution engine with a runspace pool, request-scoped invocations, bounded concurrency, cancellation, and cleanup.

### Settings and Persistence

Current settings behavior:

- `ApplicationSettings` stores shell layout, recent/reopen files, selected runtime, diagnostics settings, and last EXE export configuration. Lines 35-90 are the relevant fields.
- `ApplicationSettingsService.LoadSettings`, lines 31-99, loads JSON from `%LOCALAPPDATA%\PS7ScriptDesk\appsettings.json`, applies safe defaults, and logs settings diagnostics.
- `ApplicationSettingsService.SaveSettings`, lines 101-156, writes a temporary file and replaces the settings file.
- `ExeExportConfiguration` comments explicitly keep source text out of persisted export settings, lines 69-72.

REST API publishing configurations should not be stored only in global app settings. They should be script-side companion files so they are portable and source-control friendly.

### Logging and Developer Diagnostics

Current logging and diagnostics provide a strong pattern:

- `AppLogger`, lines 14-88, is a bounded asynchronous app logger under `%LOCALAPPDATA%\PS7ScriptDesk\Logs`.
- `DeveloperDiagnostics`, lines 24-74, manages optional developer diagnostics sessions under `%LOCALAPPDATA%\PS7ScriptDesk\DeveloperDebugging`.
- `DeveloperDiagnostics.SanitizePreview` and `CreateTextMetadata`, lines 273-327, provide redacted preview/hash metadata.
- `DeveloperDiagnostics.CreatePrivateTextMetadata`, lines 329-350, records only length and line count for sensitive text.
- EXE export logs user actions and progress through `DeveloperDiagnostics.LogUserAction`, `LogStateTransition`, `LogInfo`, `LogError`, and `LogException`; see `MainWindowViewModel` lines 2731-2741 and 5100-5114, and `ExeExportService` lines 212-218, 284-290, 860-877, and 886-893.

REST publisher generation/test/publish activity should use the same app logger and developer diagnostics categories, with a new category such as `ApiPublish`. The generated API application should have its own structured logs and must not write request bodies, secrets, full scripts, or full PowerShell output by default.

### Packaging and Publish Services

The EXE exporter already has a project generation and `dotnet publish` path:

- `ExeHostProjectGenerator.Generate`, lines 13-44, writes script, settings, program source, manifest, and project file.
- `ExeExportService.ExportConfiguredScriptAsync`, lines 221-240, prepares temp project/publish directories and invokes the generator/publisher.
- `ExeExportService.RunDotNetPublishAsync`, lines 677-731, starts `dotnet`, captures stdout/stderr, kills the process tree on cancellation, and returns a result.
- `ExecutableVerifier.Verify`, lines 9-60, verifies Windows PE output and target architecture.

REST publishing should reuse this pattern conceptually, but in new API-specific classes. Direct code sharing should be through extracted common project-generation/publish helpers only if implementation discovers meaningful duplication. Avoid coupling REST to EXE manifest/icon/application-type assumptions.

### Tests

Existing relevant test coverage includes:

- EXE export presets, validation, dependency scan, workflow, integration, diagnostics: `AdvancedExeExportTests`, `ExeExportWorkflowTests`, `ExeExportIntegrationTests`, `ExeExportServiceDiagnosticsTests`.
- Terminal lifecycle, focus, input, output isolation, architecture policy, diagnostics privacy: `TerminalLifecycleViewModelTests`, `TerminalInputRouterTests`, `TerminalOutputBridgeTests`, `TerminalOutputIsolationTests`, `TerminalDiagnosticPrivacyTests`, `TerminalArchitecturePolicyTests`.
- Diagnostics/logger/settings reliability: `DeveloperDiagnosticsReliabilityTests`, `DetachedTaskDiagnosticsTests`, `ApplicationSettingsServiceReliabilityTests`, `AppLoggerReliabilityTests`.

The API publisher needs new parser, model, generation, runspace execution, binding, serialization, security, concurrency, and regression tests. It should not weaken existing terminal/debugger/export assumptions.

## 2. Proposed Feature Definition

The feature should allow a user to publish selected PowerShell functions as a generated ASP.NET Core REST API.

Target workflow:

1. Active saved PowerShell script.
2. Analyze publishable functions.
3. User selects functions.
4. User configures REST endpoints.
5. User maps PowerShell parameters to route, query, body, header, or server-defined values.
6. User configures runtime, authentication, timeouts, concurrency, and OpenAPI behavior.
7. User tests the API locally in a child process.
8. User publishes a self-contained generated API project/executable.

Example:

```powershell
function Get-SystemInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ComputerName
    )

    Get-CimInstance Win32_OperatingSystem -ComputerName $ComputerName |
        Select-Object CSName, Caption, Version
}
```

Possible endpoint:

```text
GET /api/systeminfo?computerName=SERVER01
```

The generated REST host should bind `computerName` to `-ComputerName`, invoke `Get-SystemInfo` in a request-scoped PowerShell pipeline, and serialize pipeline output as JSON.

## 3. Recommended V1 Scope

V1 should be deliberately constrained.

Supported:

- Saved `.ps1` source files only.
- Named functions declared in the script, including `function Name { ... }` and advanced functions with `[CmdletBinding()]`.
- Function-level `param()` blocks.
- Deterministic static parameter metadata:
  - parameter name
  - type name where statically declared
  - mandatory state from `[Parameter(Mandatory)]`
  - default value expression text where statically available
  - aliases from `[Alias(...)]`
  - common validation attributes where literal/static: `ValidateSet`, `ValidateRange`, `ValidateLength`, `ValidatePattern`, `ValidateNotNull`, `ValidateNotNullOrEmpty`
  - switch parameters
- REST methods: GET and POST in V1, with PUT/PATCH/DELETE deferred unless there is a compelling API design reason.
- Parameter sources: route, query, JSON body object, HTTP header, and server-defined value.
- Authentication: localhost/no-auth for test only, API key for V1 publish.
- Publishing: Windows x64 and Windows ARM64 self-contained outputs.
- OpenAPI generation for configured endpoints.

Excluded from V1:

- Arbitrary free-form scripts without selected function entry points.
- Dynamically created functions that require script execution to discover.
- Parameter sets as first-class endpoint variants unless a single unambiguous set can be selected.
- Interactive PowerShell input, host prompts, credentials prompts, progress UI, `Read-Host`, and console UI.
- Persistent runspace state intentionally shared across requests.
- Streaming response bodies, WebSocket, SSE, bidirectional invocation, and long-lived subscriptions.
- Windows Service, IIS, Docker, Linux publish targets.
- Automatic bundling of all modules, native binaries, certificates, or script-adjacent resources.
- Running generated APIs elevated by default.

Free-form `.ps1` scripts should be excluded unless they expose a well-defined named function entry point. This is necessary for repeatable metadata, stable endpoint contracts, predictable OpenAPI, and safer invocation.

## 4. REST Host Architecture

Recommended generated host:

- .NET current repository target at implementation time, preferably aligned to the product target.
- ASP.NET Core Minimal APIs.
- `System.Management.Automation` for PowerShell invocation.
- Generated configuration file plus copied source scripts.
- A transport-independent PowerShell invocation core used by REST now and future transports later.

Major layers:

```text
PS7 ScriptDesk WPF Shell
  -> API publish wizard/view model
  -> API metadata parser and configuration validator
  -> generated project service
  -> local test process manager / publish service

Generated REST API application
  -> ASP.NET Core host
  -> REST endpoint mapper
  -> request parameter binder
  -> transport-independent invocation coordinator
  -> PowerShell runspace pool
  -> PowerShell function invoker
  -> output normalizer and JSON serializer
  -> error mapper
  -> auth/logging/config/OpenAPI
```

Generated REST app components:

| Component | Responsibility |
| --- | --- |
| `Program.cs` | Configure ASP.NET Core, auth, logging, OpenAPI, endpoints, limits, health/status route. |
| `ApiEndpointDefinition` | Durable endpoint contract: function, route, verb, binding map, auth, timeout, response behavior. |
| `RestEndpointMapper` | Converts configured endpoint definitions into Minimal API routes. REST-only. |
| `RestParameterBinder` | Reads route/query/body/header values and creates normalized invocation inputs. REST-only. |
| `PowerShellInvocationCoordinator` | Transport-independent request lifecycle, timeout/cancellation, queueing, concurrency, diagnostics. |
| `RunspacePoolManager` | Creates and owns a bounded `RunspacePool`, initializes scripts/modules, disposes safely. |
| `PowerShellFunctionInvoker` | Invokes one function by name with bound parameters and captures streams/errors. |
| `PowerShellResultNormalizer` | Converts `PSObject`/streams/errors to safe response DTOs. |
| `ApiErrorMapper` | Maps validation, binding, PowerShell, timeout, cancellation, and serialization failures. |
| `ApiSecurityOptions` | API key/JWT/Windows auth-ready configuration, with V1 API key support. |
| `ApiLogging` | Structured logs with request IDs, route/function, duration, status, error class, no request body by default. |

Recommended ASP.NET Core use:

- Minimal APIs for generated route registration.
- `System.Text.Json` for HTTP JSON serialization.
- `IOptions<ApiHostOptions>` for config.
- `ILogger` for generated host logs.
- `CancellationToken` from `HttpContext.RequestAborted`.
- `IHostedService` or singleton initialization service for runspace-pool startup/warmup.

## 5. Shared Future Transport Architecture

REST should be one transport over a shared API invocation core.

Transport-independent:

- Script/function metadata model.
- Function selection model.
- Parameter metadata model.
- Endpoint/invocation logical model, excluding HTTP-specific binding details.
- PowerShell execution engine.
- Runspace pool management.
- Invocation lifecycle: accepted, queued, bound, executing, completed, failed, canceled, timed out.
- Cancellation and timeout handling.
- Resource limits and concurrency.
- Auth policy concepts and claims/principal abstraction.
- Input validation results.
- Output normalization and serialization-ready DTOs.
- Error taxonomy.
- Logging and developer diagnostics metadata shape.
- Project generation orchestration.
- Local test process lifecycle.

REST-specific:

- HTTP verbs.
- Route templates.
- Route/query/body/header binding sources.
- HTTP status codes.
- `ProblemDetails` response mapping.
- OpenAPI/Swagger.
- HTTP request size limits and content-type handling.
- HTTP authentication middleware.

Future WebSocket-specific, deferred:

- Connection lifecycle.
- Message envelope.
- Bidirectional request/response correlation.
- Connection authorization.
- Backpressure and streaming output protocol.

Future SSE-specific, deferred:

- Event stream format.
- Replay/event IDs.
- Long-lived response lifecycle.
- Keepalive and reconnect behavior.

The key architectural decision is to make `PowerShellInvocationCoordinator` accept a transport-neutral `ApiInvocationRequest` and return an `ApiInvocationResult`. REST should adapt HTTP to that model.

## 6. PowerShell Parsing and Metadata Discovery

Recommended approach: AST-first static analysis using `System.Management.Automation.Language.Parser`.

Rationale:

- Current PS7 ScriptDesk already uses AST parsing safely for editor diagnostics without executing user scripts: `PowerShellDiagnosticsService`, lines 570-596.
- AST parsing can find function definitions, extents, parameter ASTs, attributes, type constraints, default expressions, comments/help blocks, and syntax errors.
- Static analysis avoids executing untrusted scripts merely to discover publishable functions.

Recommended service:

- `IPowerShellApiMetadataService` in `Application`.
- `PowerShellApiMetadataService` in `PowerShell`.
- It should parse source text with `Parser.ParseInput`.
- It should return syntax errors and publishable function candidates.
- It should treat parse errors as blocking for publishing unless the user selects a function whose extent is demonstrably valid, which is likely too complex for V1. V1 should block on parse errors.

For each function, discover:

- Function name from `FunctionDefinitionAst.Name`.
- Start/end extents for UI selection.
- Whether it is advanced via `[CmdletBinding()]` or parameter attributes.
- Parameters from function body `ParamBlock`.
- Parameter name from `ParameterAst.Name`.
- Parameter type from `StaticType`, type constraint text, or declared type extent.
- Mandatory state from `[Parameter(Mandatory=$true)]` or `[Parameter(Mandatory)]`.
- Default values from parameter default expression extent text, marked as expression text rather than evaluated value.
- Validation attributes from attribute ASTs with literal arguments where possible.
- Aliases from `[Alias('name')]`.
- Help text from comment-based help immediately associated with the function.
- Output information only where statically detectable, such as `[OutputType(...)]`; otherwise unknown.

Optional later runtime metadata:

- For functions that pass static screening, a sandboxed metadata probe could dot-source into an isolated process/runspace and use `Get-Command`/`CommandMetadata` to refine parameter sets, validation, and dynamic parameters. This is not required for V1 and must be opt-in because it executes script code.

Do not use the existing `PowerShellDependencyAnalyzer` for endpoint metadata. It is regex-based and explicitly reports portability uncertainty, `PowerShellDependencyAnalyzer.cs`, lines 9-18 and 24-40.

## 7. Endpoint Configuration Model

Recommended domain model, likely in `PS7ScriptDesk.Domain.Models.ApiPublishing`:

```csharp
public sealed class ApiPublishConfiguration
{
    public int SchemaVersion { get; set; } = 1;
    public string SourceScriptPath { get; set; } = "";
    public ApiTransportKind Transport { get; set; } = ApiTransportKind.Rest;
    public List<ApiEndpointConfiguration> Endpoints { get; set; } = new();
    public ApiRuntimeOptions Runtime { get; set; } = new();
    public ApiSecurityOptions Security { get; set; } = new();
    public ApiOpenApiOptions OpenApi { get; set; } = new();
    public ApiPublishOutputOptions Output { get; set; } = new();
}

public sealed class ApiEndpointConfiguration
{
    public string Id { get; set; } = "";
    public string FunctionName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public RestEndpointOptions Rest { get; set; } = new();
    public List<ApiParameterBindingConfiguration> ParameterBindings { get; set; } = new();
    public TimeSpan? Timeout { get; set; }
    public bool RequiresAuthentication { get; set; } = true;
    public string? AuthorizationPolicy { get; set; }
    public ApiResponseBehavior ResponseBehavior { get; set; } = new();
}
```

Parameter binding properties:

- PowerShell parameter name.
- Source kind: route, query, body, header, server-defined value.
- External name.
- Required override, if any.
- Default value override, if any.
- Type conversion behavior.
- Whether to bind arrays from repeated query keys or JSON arrays.
- Secret flag for headers/server-defined values.

Mapping rules:

- Route/path: value comes from route token, e.g. `/api/computers/{computerName}`.
- Query string: value comes from query key, e.g. `?computerName=SERVER01`.
- JSON body: value comes from body object property. V1 should require JSON object bodies, not arbitrary top-level arrays unless the function has one array/body parameter.
- Header: value comes from an HTTP header, normally for correlation or tenant-like values, not secrets unless explicitly marked.
- Server-defined value: value is configured in API settings or environment variables and is never client supplied.

REST-specific endpoint fields should be isolated in `RestEndpointOptions`: method, route template, content types, success status code, OpenAPI tags.

## 8. Parameter Binding

V1 supported PowerShell parameter types:

- `string`
- `int`
- `long`
- `decimal`
- `double`
- `bool`
- `DateTime`/`DateTimeOffset`
- enums
- `switch`
- arrays of supported scalar types
- nullable scalar types
- simple JSON objects to `PSCustomObject` or `hashtable`

V1 should avoid arbitrary .NET object graph binding. Complex objects should be passed as `PSCustomObject`/`Hashtable` unless a later version adds explicit DTO generation.

Binding behavior:

- Missing mandatory route/query/header/body property -> HTTP 400.
- Invalid scalar conversion -> HTTP 400.
- Invalid enum value -> HTTP 400 with allowed values if not sensitive.
- Invalid JSON -> HTTP 400.
- Unsupported type -> configuration validation error before publish; if reached at runtime, HTTP 500 with sanitized detail.
- Failed static validation attributes -> HTTP 400.
- Failed PowerShell runtime parameter validation -> HTTP 400 when identifiable as validation/binding, otherwise PowerShell error mapping.
- Null for non-nullable mandatory values -> HTTP 400.
- Extra JSON properties -> ignored by default in V1, with an option to reject later.

Recommended conversion implementation:

- Build an intermediate `BoundPowerShellParameter` dictionary.
- Use .NET conversion for primitives, enums, arrays, and nullable types.
- For body object values, use `JsonElement` conversion to `Hashtable`/`PSCustomObject`.
- Do not construct or evaluate PowerShell expressions from request strings.

## 9. PowerShell Execution Architecture

Recommended model: reusable bounded runspace pool with request-scoped `PowerShell` instances.

Why not one runspace per request:

- Better isolation, but high startup cost.
- Module/script loading per request would reduce throughput.
- Harder to bound process-wide initialization overhead.

Why not one global runspace:

- Poor concurrency.
- State leakage risk.
- Thread-safety and reentrancy issues.

Recommended V1 architecture:

- Generated host creates a `RunspacePool` on startup with min 1, max configurable.
- Each request creates a new `PowerShell` instance bound to the pool.
- The pool loads the source script/functions in initialization.
- Invocation uses `AddCommand(functionName).AddParameter(name, value)` rather than string-built commands.
- Request-scoped state should be stored in invocation parameters, not globals.
- The host should set conservative session state and execution policy, but should not try to implement a complete PowerShell sandbox. The threat model should say the API runs trusted user-selected scripts.
- For cancellation, call `PowerShell.Stop()`/`StopAsync()` when timeout or `RequestAborted` triggers and dispose the request `PowerShell`.
- If a runspace appears poisoned after cancellation/error, rebuild the pool or retire the runspace where feasible.

Script/function loading:

- Copy the source script into generated project content.
- On pool initialization, dot-source the script file into the session state or load it as a module if generated as a `.psm1`.
- Prefer generating a module wrapper for selected functions in V1 if practical, because modules provide a clearer export boundary.
- Validate that selected functions exist after load; fail startup with sanitized errors if missing.

Isolation:

- Do not share PS7 ScriptDesk editor terminal/debugger process.
- Do not execute in the WPF app process.
- Generate and run a child ASP.NET Core process for local testing.
- Published executable runs as its own OS process and privilege boundary.

## 10. Concurrency and Resource Control

Recommended V1 defaults:

- Runspace pool min: 1.
- Runspace pool max: 4 or `min(Environment.ProcessorCount, 4)`.
- Max concurrent PowerShell executions: equal to runspace pool max by default.
- Queue limit: 32 pending requests by default.
- Queue wait timeout: 10 seconds by default.
- Invocation timeout: 30 seconds default, configurable per endpoint with a sane upper warning.
- Request body size limit: 1 MB default for V1, configurable.
- Maximum serialized response items: configurable, default 1,000 pipeline items.
- Maximum serialized response bytes: configurable, default 5 MB.
- Maximum error/warning stream entries retained: configurable, default 100.
- Maximum log preview length: use existing diagnostics preview patterns.

Behavior:

- If queue full -> HTTP 429.
- If queue wait timed out -> HTTP 503 or 429; prefer 429 for capacity.
- If invocation timed out -> HTTP 504.
- If client disconnects -> cancel invocation and log as cancellation, generally do not treat as server error.
- If excessive output -> abort serialization or truncate only if explicitly configured. Default should return 500/507-like sanitized error rather than silently partial data. HTTP 500 with a typed error code is acceptable for V1.

CPU and memory exhaustion cannot be fully solved in-process. Clear warnings are required: published APIs execute PowerShell with the privileges and resources of the API process. Later phases can explore job objects, child-worker isolation, or process-per-request for high-risk deployments.

## 11. Output Conversion and JSON Serialization

PowerShell output should be normalized before HTTP serialization.

Rules:

- `$null` single result -> JSON `null`.
- No pipeline output -> HTTP 204 or JSON `null`; choose JSON `null` for consistent OpenAPI unless user config says `204`.
- One scalar -> JSON scalar.
- Multiple pipeline results -> JSON array.
- Strings/numbers/bools/DateTime -> natural JSON values.
- `PSCustomObject`/`PSObject` -> object with public/adapted properties, excluding formatting/infrastructure properties.
- Dictionaries/hashtables -> JSON object.
- Arrays/enumerables -> JSON arrays.
- .NET objects -> serialize public readable properties after depth/cycle controls.
- Error, warning, verbose, debug, and information streams should be captured separately from success output.

Formatting objects:

- Strip or reject PowerShell formatting objects produced by `Format-Table`, `Format-List`, etc. They are console-rendering instructions, not API data. Return a warning in local test and an error in published API if output cannot be normalized cleanly.

Serialization:

- Use `System.Text.Json`.
- Default max depth: 8.
- Cycle handling: ignore cycles or produce a sanitized serialization failure. Prefer failure in V1 to avoid surprising incomplete data.
- Property naming: preserve PowerShell property names by default; optional camelCase later.
- Include an optional envelope mode:
  - Bare result by default for user-friendly APIs.
  - Envelope later or opt-in: `{ "data": ..., "warnings": ..., "requestId": ... }`.

Streams:

- Warning/verbose/debug/information streams should be logged with caps and optionally returned in an envelope for local test mode.
- Do not return debug/verbose streams by default in production responses.
- Non-terminating errors should be treated as failure by default unless endpoint config allows partial success.

## 12. Error Handling

Recommended response structure for errors:

```json
{
  "type": "https://ps7scriptdesk/errors/powershell-validation",
  "title": "Request parameter validation failed.",
  "status": 400,
  "detail": "The 'computerName' parameter is required.",
  "requestId": "..."
}
```

Map failures:

| Failure | HTTP status | Notes |
| --- | ---: | --- |
| Missing mandatory value | 400 | Include parameter name. |
| Invalid scalar/enum/date conversion | 400 | Include expected type. |
| Invalid JSON | 400 | Do not echo body. |
| Static validation attribute failure | 400 | Include safe validation message. |
| PowerShell parameter binding/validation exception | 400 | When confidently identified. |
| Missing configured function | 500 | Startup validation should usually catch this. |
| Module/script load failure | 500 | Sanitized detail; full diagnostics only in host logs. |
| Terminating PowerShell error | 500 | Use sanitized message; hide stack by default. |
| Non-terminating PowerShell error | 500 by default | Optional partial success later. |
| Timeout | 504 | Cancel invocation and log duration/function. |
| Client cancellation | 499-like internal log; no response if disconnected | ASP.NET Core cannot always send response. |
| Queue full | 429 | Include retry guidance without internal capacity details. |
| Unhandled .NET exception | 500 | Sanitized `ProblemDetails`. |
| Serialization failure | 500 | Include result type/depth reason if safe. |
| Unauthorized | 401 | Generated ASP.NET Core auth middleware. |
| Forbidden | 403 | Authorization policy failure. |

Sensitive stack traces should never be exposed remotely by default. Local test mode may expose more diagnostics only when the user explicitly enables it.

## 13. Authentication and Authorization

V1 recommendation:

- Local Test API:
  - HTTP localhost with no authentication allowed by explicit local-only setting.
  - API key optional for testing.
- Published API:
  - API key required by default.
  - API key accepted through header such as `X-API-Key`.
  - HTTPS warning if binding beyond localhost.

Later:

- JWT Bearer.
- Windows authentication.
- Per-endpoint authorization policies.
- Role/claim mapping.
- Secret store integration.

Authentication and authorization must be handled by the generated ASP.NET Core host. PowerShell functions should receive an optional sanitized principal/context object only if configured; they should not be responsible for validating raw tokens.

## 14. HTTPS

Local testing:

- Allow HTTP-only `localhost` by default.
- Allow HTTPS with .NET development certificate if available.
- Show clear URL and security mode in the test panel.

Publishing:

- Warn if publishing a non-localhost HTTP endpoint.
- Support configuration for user-supplied PFX path/password reference or Windows certificate store subject/thumbprint later.
- Do not implement certificate management in V1 beyond generated host configuration hooks.

Production guidance:

- HTTPS is required for API key auth over a network.
- API should not listen on public interfaces unless the user explicitly configures host URLs and acknowledges risk.

## 15. OpenAPI / Swagger

OpenAPI generation should come from endpoint configuration and parameter metadata.

Include:

- Route and HTTP method.
- Operation ID from endpoint ID/function name.
- Tags from source script or user configuration.
- Parameter names, sources, types, required state, descriptions.
- JSON request body schema for body-bound parameters.
- Response schema when statically known, otherwise generic JSON schema.
- Error responses: 400, 401, 403, 429, 500, 504 as applicable.

Swagger UI:

- Enabled by default for local test.
- Disabled by default in published production unless user enables it.
- If enabled in published output, require authentication unless explicitly public.

## 16. Local Test API Workflow

Recommended workflow:

1. Validate active script is saved and clean or prompt to save.
2. Parse publishable functions.
3. Validate endpoint configuration.
4. Generate an API project into a temp or user-selected working folder.
5. Start the generated API as a child process.
6. Display listening URLs, process ID, status, auth mode, log summary.
7. Offer Open Swagger UI.
8. Stream safe logs into a dedicated test panel.
9. Offer Stop API.

Recommended process model:

- Run as a child process, not inside PS7 ScriptDesk.
- Prefer generated executable for test after build, or `dotnet run` for early prototype. V1 should converge on generated host execution so test and publish behavior match.
- Capture stdout/stderr with bounded logs.
- Stop by graceful shutdown when possible, then kill process tree on timeout.
- Use a dedicated process manager service, not `LiveConsoleService`.

This must not interfere with:

- Existing ConPTY terminal.
- Debugger `PsesDebugSession`.
- Current editor execution flags.
- Runtime discovery refresh.
- EXE export progress window.

## 17. Publishing Architecture

V1 publish targets:

- Self-contained Windows x64 executable/folder.
- Self-contained Windows ARM64 executable/folder.

V1 generated project should be preserved by default or at least offer "Keep generated project" because advanced users will want to inspect/edit the ASP.NET Core host.

Later targets:

- Windows Service.
- IIS.
- Docker/container.
- Linux.
- Azure/App Service or cloud targets.

Publishing should follow a new API-specific generator and publish service:

- `ApiHostProjectGenerator`.
- `ApiPublishService`.
- `DotNetPublishRunner` if shared extraction is justified.
- `ApiHostVerifier` to verify output file/folder and generated config, not just PE headers.

Avoid inheriting EXE export elevation/application manifest behavior unless the user explicitly configures it for API output.

## 18. Generated Project Structure

Recommended generated layout:

```text
GeneratedApi/
  src/
    MyScript.ApiHost/
      MyScript.ApiHost.csproj
      Program.cs
      appsettings.json
      appsettings.Development.json
      Api/
        EndpointDefinitions.json
        ApiHostOptions.cs
        RestEndpointMapper.cs
        RestParameterBinder.cs
        ApiErrorMapper.cs
      PowerShell/
        PowerShellInvocationCoordinator.cs
        RunspacePoolManager.cs
        PowerShellFunctionInvoker.cs
        PowerShellResultNormalizer.cs
        PowerShellStreamCapture.cs
      Security/
        ApiKeyAuthenticationHandler.cs
      Scripts/
        MyScript.ps1
      OpenApi/
        OpenApiConfiguration.cs
      README.md
  publish/
```

The generated code should separate REST adapters from the transport-independent invocation core. Endpoint definitions should be data-driven so REST generation is not hard-coded into `Program.cs` beyond route registration.

## 19. Wizard/UI Architecture

The existing EXE wizard uses six steps: Preset, Application, Platform, Dependencies, Advanced, Review. Evidence: `ExportWizardWindow.xaml`, lines 39-44 and 48-114.

Recommended future REST wizard steps:

1. Source Script
2. Select Functions
3. Configure Endpoints
4. Configure Parameters
5. Security
6. Runtime and Limits
7. OpenAPI
8. Test
9. Publish

Notes:

- Source Script should show saved path, parse status, and whether script content is dirty.
- Select Functions should show AST-discovered functions and metadata confidence.
- Configure Endpoints should edit method, route, display name, description, enabled state.
- Configure Parameters should map each PowerShell parameter to route/query/body/header/server value and show validation.
- Security should choose local no-auth for test only or API key for publish.
- Runtime and Limits should set timeout, concurrency, queue, body size, output limits, runtime model.
- OpenAPI should configure title/version/tags and Swagger UI.
- Test should start/stop the local API and show URLs/logs.
- Publish should choose output folder, architecture, keep generated project, and run publish.

The UI should be consistent with current WPF styles and help integration. It should use `UI` view models for state and `Shell` dialogs/windows for presentation.

## 20. Persistence

Recommended persistence: companion JSON file next to the script.

Example:

```text
MyScript.ps1
MyScript.ps7api.json
```

Reasons:

- Portable with the script.
- Source-control friendly.
- Avoids bloating global app settings.
- Allows per-script endpoint definitions.
- Keeps generated API behavior reproducible.

Global application settings may store only last wizard preferences that are not source-specific, such as last output folder or preferred test URL mode. Do not store script text or secrets in global settings.

Secrets:

- API keys and certificate passwords should not be stored in the companion file in plaintext.
- Use environment variable references, user secrets for generated development projects, or later Windows credential store integration.

## 21. Security Threat Review

| Risk | Safeguard |
| --- | --- |
| Arbitrary PowerShell execution | V1 publishes only user-selected named functions from saved scripts; generated API is explicit and reviewed before publish. |
| Command injection | Invoke functions with `AddCommand`/`AddParameter`; never concatenate request values into PowerShell source. |
| Parameter injection | Type-convert and validate request values before invocation; do not evaluate expressions from clients. |
| Script injection | Do not accept uploaded scripts or function names at runtime; function names come from signed/generated config. |
| Unsafe expression evaluation | Default values are static metadata; do not evaluate arbitrary default expressions during discovery. |
| Path traversal | Generated host should read only configured script/config paths; route values are ordinary parameters, not file paths, unless function chooses to use them. |
| Unsafe file access | Warn that published functions run as the API process identity; optional allowlist policies later. |
| Authentication bypass | Use ASP.NET Core middleware; require API key for published non-local APIs by default. |
| Authorization bypass | Central endpoint auth policy before PowerShell invocation; defer per-role policies until later. |
| Secrets in configuration | Store references, not secret values; mark secret fields and redact in logs. |
| Secrets in logs | Never log request bodies, auth headers, API keys, full script text, or full output by default. |
| Exception leakage | Use sanitized `ProblemDetails`; full stack only in local test diagnostics when explicitly enabled. |
| Oversized HTTP requests | Configure request body and form limits; reject with 413. |
| Denial of service | Bound concurrency, queue length, timeout, body size, output size, and serialization depth. |
| Unbounded execution | Timeout plus cancellation; consider worker process isolation later. |
| Excessive concurrency | Semaphore/queue plus runspace-pool max. |
| Module loading from unsafe paths | Generated config should explicitly list module paths; warn on script-relative or absolute paths from dependency analysis. |
| Remote exposure of privileged scripts | Require explicit publish, show route/auth/privilege summary, default to localhost for test. |
| APIs running elevated | Detect elevation in PS7 ScriptDesk and generated test process; show warning. Do not silently inherit assumptions. |
| Unsafe object deserialization | Use `System.Text.Json`; bind JSON to primitives/hashtable/PSCustomObject, not arbitrary .NET types in V1. |

## 22. Elevation and Privileges

Current PS7 ScriptDesk has elevation awareness:

- `CurrentProcessElevation.TryGetIsElevated`, lines 18-57, checks current process token elevation.
- `AdministratorModeBannerState`, lines 10-18, warns that scripts launched from an elevated session may run elevated and that drag/drop/file access can be affected.
- EXE export has administrator manifest options in `ExeExportConfiguration`, lines 55-59 and 82.

Recommended API behavior:

- If PS7 ScriptDesk is elevated, local test should warn before starting an API child process because the child may inherit elevated privileges.
- Generated APIs should run non-elevated by default.
- Publishing should not default to `requireAdministrator`.
- If the user configures elevated launch or privileged functions, show a review warning: network clients may trigger privileged operations.
- Do not infer that because the editor is elevated, the published API should be elevated.
- If the generated API starts elevated, log that fact in host logs and expose it in local status UI, not in public API responses.

## 23. Logging and Developer Diagnostics

PS7 ScriptDesk generation/test/publish logging:

- Use `AppLogger` category `ApiPublish`.
- Use `DeveloperDiagnostics` category `ApiPublish`.
- Log user action accepted/rejected, parse start/stop, function count, endpoint count, validation results, generated project path, output file/folder names, runtime identity, publish RID, child process ID, local URLs, elapsed times, cancellation, and failures.
- Use existing text metadata policy: hashes/lengths/previews only where safe, private metadata for script text, request body, and host logs.

Generated REST application logging:

- Use ASP.NET Core `ILogger`.
- Separate access logs, PowerShell execution logs, errors, and developer traces.
- Include request ID/correlation ID, endpoint ID, function name, duration, status, error kind.
- Do not log request bodies or full parameter values by default.
- Redact authorization headers, API keys, tokens, passwords, certificates.
- Cap stream previews and output previews.

Developer diagnostics should not be written by the generated application into the editor's diagnostics directory by default. The generated host should have its own log directory under its app base or configured log path. PS7 ScriptDesk can capture child process logs during local test into its diagnostics with privacy filtering.

## 24. Testing Strategy

Parsing tests:

- Discover simple named functions.
- Discover advanced functions.
- Discover parameters, types, mandatory flags, aliases, validation attributes, default expression text.
- Reject parse errors.
- Reject dynamic/unsupported syntax.
- Do not execute scripts during metadata discovery.

Generation tests:

- Generated .NET project builds.
- Endpoint definition JSON is valid.
- Correct routes and verbs are emitted.
- Source script is copied without mutation.
- OpenAPI metadata reflects parameter sources and required flags.

Execution tests:

- Successful function call.
- GET query binding.
- POST body binding.
- Route/header/server-defined binding.
- PowerShell terminating error.
- PowerShell non-terminating error.
- Timeout and cancellation.
- Missing function after load.

Serialization tests:

- Scalar string/number/bool.
- Collection.
- `PSCustomObject`.
- `$null`.
- Nested object within depth.
- Formatting-object rejection.
- Serialization cycle/depth failure.

Security tests:

- Malformed JSON -> 400.
- Injection-like strings are passed as values, not executed.
- Oversized request -> 413.
- Missing/invalid API key -> 401.
- Unauthorized endpoint -> 403.
- Exception response hides stack trace.
- Logs redact secrets.

Concurrency tests:

- Multiple simultaneous requests.
- Queue limit and 429.
- Runspace reuse without cross-request state leakage.
- Cancellation does not poison subsequent requests.
- Resource cleanup on host shutdown.

Regression tests:

- New API publish command does not affect editor, terminal, debugger, EXE export, settings load/save, startup, or existing tests.
- Terminal output remains isolated, using existing terminal architecture policy tests as precedent.
- Developer diagnostics remain bounded and privacy-safe.

## 25. Reusable Current Code

| Existing file/class | Capability | Reuse directly | Adapt | Do not reuse | Reason |
| --- | --- | ---: | ---: | ---: | --- |
| `Shell/Composition/AppBootstrapper.cs` / `AppBootstrapper` | Composition root | No | Yes | No | Add new services following manual construction pattern. |
| `Shell/MainWindow.xaml` | File menu command location | No | Yes | No | Add `Publish as API -> REST API` later; do not change now. |
| `UI/ViewModels/MainWindowViewModel.cs` / export command flow | Command lifecycle, validation, progress pattern | No | Yes | No | API publish should mirror but not merge with EXE export state. |
| `Application/Interfaces/IExeExportService.cs` | Async service/progress contract shape | No | Yes | No | Define `IApiPublishService` with API-specific request/result/progress. |
| `Application/Interfaces/IExeExportWizardService.cs` | Wizard abstraction shape | No | Yes | No | Define `IApiPublishWizardService`. |
| `Shell/Services/ExportWizardService.cs` | WPF wizard adapter and last settings persistence | No | Yes | No | Useful pattern; API needs different analysis/config. |
| `Shell/Dialogs/ExportWizardWindow.xaml` | Multi-step wizard visual pattern | No | Yes | No | API wizard has different steps and denser endpoint UI. |
| `Domain/Models/ExeExportConfiguration.cs` | Stable persisted configuration pattern | No | Yes | No | API needs new model; avoid EXE concepts. |
| `Application/Services/ExeExportConfigurationValidator.cs` | Validator pattern | No | Yes | No | New API validator can follow result/errors/warnings style. |
| `PowerShell/Services/RuntimeService.cs` | PowerShell 7 runtime discovery | Yes | Yes | No | Reuse for UI/runtime selection and generated host options. |
| `PowerShell/Services/PowerShellDependencyAnalyzer.cs` | Portability scan | Maybe | Yes | No | Useful warnings, but not endpoint metadata. |
| `Shell/Editor/PowerShellDiagnosticsService.cs` | AST parsing precedent | No | Yes | No | Build real C# parser service rather than reusing editor helper strings. |
| `Shell/Editor/PowerShellCompletionService.cs` | Command metadata precedent | No | Yes | No | Optional later runtime probe only; not V1 static discovery. |
| `PowerShell/Services/ExeExportService.cs` | Temp workspace, dotnet publish, progress, cancellation, diagnostics | No | Yes | No | Extract common publish runner only if needed; API service remains separate. |
| `PowerShell/Services/ExeHostProjectGenerator.cs` | Generated project writer | No | Yes | No | Direct output is EXE wrapper-specific. |
| `PowerShell/Services/ExecutableVerifier.cs` | PE architecture verification | Maybe | Yes | No | Useful for self-contained executable output, but API folder/config verification differs. |
| `PowerShell/Services/LiveConsoleService.cs` | Interactive ConPTY terminal | No | No | Yes | State-heavy interactive terminal must not be used for HTTP requests. |
| `PowerShell/Services/ScriptExecutionService.cs` | One-shot process script snapshots | No | No | Yes | Process-per-script is not right for multi-request API execution. |
| `Shell/Debugger/PsesDebugSession.cs` | Debugger process/session control | No | No | Yes | Debugger protocol is separate and fragile. |
| `Application/AppLogger.cs` | Application logging | Yes | Yes | No | Use for PS7 ScriptDesk generation/test/publish events. |
| `Application/Diagnostics/DeveloperDiagnostics.cs` | Optional developer diagnostics | Yes | Yes | No | Add API publish category and privacy-safe metadata during implementation. |
| `Infrastructure/Services/ApplicationSettingsService.cs` | Global app settings JSON | No | Yes | No | Store only last UI prefs globally; API config should be companion file. |
| `Application/Utilities/CurrentProcessElevation.cs` | Elevation detection | Yes | Yes | No | Use for local test/elevation warnings. |

## 26. Proposed New Components

| Proposed name | Layer/project | Responsibility | Dependencies |
| --- | --- | --- | --- |
| `ApiPublishConfiguration` | Domain | Root companion-file model. | Domain only. |
| `ApiEndpointConfiguration` | Domain | Transport-neutral endpoint plus REST options. | Domain only. |
| `ApiParameterMetadata` | Domain | Function parameter metadata discovered from AST. | Domain only. |
| `ApiFunctionMetadata` | Domain | Publishable function metadata. | Domain only. |
| `ApiParameterBindingConfiguration` | Domain | Parameter source and external name mapping. | Domain only. |
| `ApiRuntimeOptions` | Domain | Timeout, concurrency, runspace pool, body/output limits. | Domain only. |
| `ApiSecurityOptions` | Domain | API key/JWT/Windows-auth-ready config. | Domain only. |
| `IApiMetadataService` | Application | Parse script and return function metadata. | Domain. |
| `IApiPublishConfigurationValidator` | Application | Validate endpoint/configuration model. | Domain. |
| `IApiPublishWizardService` | Application | UI abstraction for wizard. | Domain. |
| `IApiProjectGenerator` | Application | Generate API project from request/config. | Domain. |
| `IApiPublishService` | Application | Build/publish generated API. | Domain. |
| `IApiLocalTestService` | Application | Start/stop generated local test server. | Domain. |
| `PowerShellApiMetadataService` | PowerShell | AST-first function/parameter parser. | `System.Management.Automation`, Application, Domain. |
| `ApiHostProjectGenerator` | PowerShell | Writes generated ASP.NET Core host project. | Application, Domain. |
| `ApiPublishService` | PowerShell | Runs generation and `dotnet publish`. | Application, Domain, AppLogger, DeveloperDiagnostics. |
| `ApiLocalTestProcessService` | PowerShell or Infrastructure | Starts/stops generated API child process. | Application, Domain. |
| `ApiPublishViewModel` or wizard step VMs | UI | Wizard state, commands, validation display. | Application, Domain. |
| `ApiPublishWizardService` | Shell | Opens WPF wizard and persists last prefs. | Application, Domain, Shell. |
| `ApiPublishWizardWindow` | Shell | WPF wizard. | UI, Domain. |
| Generated `PowerShellInvocationCoordinator` | Generated host | Transport-neutral invocation lifecycle. | SMA, logging/options. |
| Generated `RunspacePoolManager` | Generated host | Owns runspace pool and script loading. | SMA. |
| Generated `RestEndpointMapper` | Generated host | Minimal API route mapping. | ASP.NET Core. |
| Generated `RestParameterBinder` | Generated host | HTTP value binding. | ASP.NET Core, System.Text.Json. |
| Generated `ApiKeyAuthenticationHandler` | Generated host | V1 auth. | ASP.NET Core auth. |

Avoid giant classes by making project generation write multiple generated source files instead of one enormous `Program.cs`.

## 27. Implementation Phases

### Phase 1: PowerShell Function Metadata Parser

Acceptance criteria:

- Parses saved script content without executing it.
- Finds publishable function definitions.
- Extracts parameter metadata, aliases, validation attributes, and parse errors.
- Tests cover simple, advanced, invalid, and unsupported cases.

### Phase 2: REST/API Configuration Model

Acceptance criteria:

- Domain models represent transport-neutral invocation and REST-specific options separately.
- Companion-file JSON round-trips with schema version.
- Validator catches invalid routes, duplicate endpoints, missing bindings, unsupported types, and unsafe defaults.

### Phase 3: Standalone Proof-of-Concept REST Host Design

Acceptance criteria:

- Generated or hand-created proof project can expose one selected function through Minimal APIs.
- No PS7 ScriptDesk UI integration yet.
- Demonstrates request binding, invocation, and JSON response.

### Phase 4: PowerShell Runspace Host

Acceptance criteria:

- Runspace pool initializes selected script/functions.
- Multiple request-scoped invocations work.
- Timeout/cancellation disposes request invocation safely.
- Tests verify no obvious cross-request state leakage.

### Phase 5: JSON and Error Mapping

Acceptance criteria:

- Normalizes scalars, arrays, `PSCustomObject`, dictionaries, `$null`.
- Captures PowerShell streams.
- Maps binding, validation, PowerShell, timeout, cancellation, and serialization failures to typed responses.

### Phase 6: OpenAPI

Acceptance criteria:

- Generated OpenAPI reflects routes, verbs, parameter sources, required flags, body schema, and common errors.
- Swagger UI enabled for local test by default and configurable for publish.

### Phase 7: Project Generator

Acceptance criteria:

- Generates structured ASP.NET Core project with copied scripts, endpoint config, host code, README, and appsettings.
- Generated project builds with repository-approved SDK target.
- No generated file contains plaintext secrets.

### Phase 8: Local Test Host

Acceptance criteria:

- PS7 ScriptDesk can generate/start/stop child API process.
- UI shows status, URLs, PID, auth mode, and logs.
- Local API does not affect terminal/debugger/script execution.

### Phase 9: Wizard UI

Acceptance criteria:

- Adds `Publish as API -> REST API` entry point.
- Wizard covers Source, Functions, Endpoints, Parameters, Security, Runtime/Limits, OpenAPI, Test, Publish.
- Preserves keyboard navigation, help, theme consistency, and diagnostics.

### Phase 10: Publishing

Acceptance criteria:

- Publishes self-contained Windows x64 and ARM64 outputs.
- Can preserve generated project.
- Verifies output and reports progress/failures.

### Phase 11: Security Hardening

Acceptance criteria:

- API key auth for published APIs.
- Request/body/output/concurrency limits enforced.
- Logs redact secrets.
- Elevated local test/publish warnings implemented.

### Phase 12: Regression and Documentation

Acceptance criteria:

- Existing EXE export, terminal, debugger, settings, startup, diagnostics tests pass.
- New architecture documentation is updated.
- Manual local test covers repeated start/stop, failed build, timeout, cancellation, and app shutdown.

## 28. Risks and Unknowns

| Risk/unknown | Severity | How to test |
| --- | --- | --- |
| Current repo target is `net10.0` while instructions say .NET 8 | Medium | Decide target before implementation; build generated host against selected TFM. |
| Runspace-pool cancellation may leave runspace state polluted | High | Prototype cancellation stress tests; consider pool rebuild after cancellation/timeouts. |
| Static AST cannot fully determine dynamic parameters/metadata | Medium | V1 excludes dynamic-only cases; optional isolated runtime metadata probe later. |
| Module loading behavior differs between editor machine and published machine | Medium | Generated README/config warnings; tests with module import failures. |
| PowerShell object serialization can expose too much or fail on complex graphs | High | Serialization depth/cycle tests and redaction review. |
| Long-running CPU-bound scripts may not stop promptly | High | Timeout tests with loops; evaluate process/job isolation later. |
| API key storage/secrets UX needs careful design | Medium | Prototype environment variable/user secret references. |
| Local test ports may collide or expose network surface | Medium | Bind localhost by default; test port selection and URL warnings. |
| Swagger schemas for PowerShell outputs may be weak | Low | Mark unknown outputs as generic JSON; improve only when `[OutputType]` exists. |
| Generated project preservation may create stale config/source copies | Low | Include regeneration metadata and clear UI warnings. |

## 29. Final Recommendation

### Recommended REST API Publisher Architecture

Build a generated ASP.NET Core Minimal API host with a transport-independent PowerShell invocation core. PS7 ScriptDesk should provide parsing, configuration, generation, local test, and publishing workflows, but the running REST API should be a separate child or published process with its own runspace pool and logs.

Use AST-first static metadata discovery, script-side companion JSON configuration, API-specific project generation, bounded runspace-pool execution, centralized ASP.NET Core authentication, typed request binding, sanitized `ProblemDetails`, and privacy-safe logging/diagnostics.

Do not reuse the interactive terminal, debugger session, or process snapshot execution services for HTTP request execution. Reuse runtime discovery, logging/diagnostics patterns, wizard workflow patterns, validation patterns, and the conceptual generated-project/publish pipeline.

### Recommended V1 Feature Set

V1 should support:

- Saved `.ps1` scripts with selected named functions.
- Static metadata for param blocks, advanced functions, typed parameters, mandatory flags, defaults as expression text, aliases, and common validation attributes.
- REST GET and POST.
- Route/query/body/header/server-defined parameter mappings.
- Scalar, enum, array, nullable, hashtable, and `PSCustomObject` style values.
- JSON responses with safe normalization.
- API key auth for published APIs; no-auth only for explicit localhost test.
- Local Test API as a separate child process.
- OpenAPI/Swagger for test and optional publish.
- Self-contained Windows x64 and ARM64 publishing.
- Companion `.ps7api.json` configuration files.

### Explicitly Deferred Features

Defer:

- WebSocket APIs and SSE transport-specific details.
- Arbitrary free-form script publishing without named function entry points.
- Dynamic parameter/runtime metadata execution by default.
- Streaming pipeline output.
- Interactive prompts/host UI.
- Windows Service/IIS/Docker/Linux targets.
- JWT Bearer and Windows authentication.
- Advanced authorization policy UI.
- Automatic module/native dependency bundling.
- Strong PowerShell sandboxing or worker-process isolation.
- Cloud deployment targets.

WebSocket and SSE should remain planned future transports over the same metadata, configuration, invocation, security, logging, limits, and project-generation core, but their protocol details should not be implemented in the REST V1.
