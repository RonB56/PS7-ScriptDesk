# REST API Publisher Phase 1 Implementation Report

## Files Added

- `PS7ScriptDesk.Domain/Models/ApiMetadataResult.cs`
- `PS7ScriptDesk.Application/Interfaces/IApiMetadataService.cs`
- `PS7ScriptDesk.PowerShell/Services/PowerShellApiMetadataService.cs`
- `PS7ScriptDesk.Tests/PowerShellApiMetadataServiceTests.cs`
- `REST_API_PHASE1_IMPLEMENTATION_REPORT.md`

## Files Modified

- `PS7ScriptDesk.PowerShell/PS7ScriptDesk.PowerShell.csproj`
  - Added `System.Management.Automation` 7.6.2 so the non-WPF PowerShell layer can use `System.Management.Automation.Language.Parser` and AST types directly.

## Architecture Implemented

Phase 1 implements only the static PowerShell function metadata parser described in `REST_API_PUBLISHER_ARCHITECTURE.md`.

Implemented layering:

- Domain: structured metadata/result models for parser results, source extents, syntax errors, functions, parameters, validation attributes, comment help, and warnings.
- Application: `IApiMetadataService` interface.
- PowerShell: `PowerShellApiMetadataService`, an AST-based parser using `System.Management.Automation.Language.Parser.ParseInput`.
- Tests: xUnit tests focused on static parser behavior and regression safety.

No WPF, menu, wizard, REST host, ASP.NET Core server, Swagger/OpenAPI, runspace pool, project generator, publish pipeline, WebSocket, or SSE implementation was added.

## Metadata Supported

The parser reports:

- Parse success/failure.
- Structured syntax errors with error ID, message, and source extent.
- AST-visible PowerShell functions.
- Function/filter classification.
- Top-level versus nested functions.
- Parent function name for nested functions.
- Publishable flag for top-level ordinary functions.
- Function source extent with line/column/offset/text.
- CmdletBinding/advanced-function detection.
- Parameter metadata:
  - name
  - declared type name
  - explicit type presence
  - switch detection
  - array detection
  - nullable detection where statically represented
  - mandatory state: mandatory, not mandatory, unknown
  - default-value expression text without evaluation
  - aliases
  - validation attributes
  - source extent
  - metadata completeness flag
- Validation attributes:
  - `ValidateSet`
  - `ValidateRange`
  - `ValidateLength`
  - `ValidatePattern`
  - `ValidateNotNull`
  - `ValidateNotNullOrEmpty`
- Literal validation/alias argument values where statically resolvable.
- Statically declared `OutputType` values.
- Conservative adjacent line-comment help sections for synopsis, description, parameter descriptions, and examples.
- Warnings for nested functions, filters, PowerShell classes, dynamic execution, dynamic function creation, and partially unknown metadata.

## Unsupported Metadata and Limitations

- Dynamic functions created by `Invoke-Expression`, `New-Item Function:`, script generation, module side effects, or runtime code are not returned as static functions.
- PowerShell class methods are ignored for Phase 1 and surfaced only through class warnings.
- Filters are discovered and classified separately, but are not marked publishable for REST V1.
- Default values are stored as expression text only; they are never evaluated.
- Runtime-only attribute values are marked partially unknown when legal syntax reaches the AST.
- Comment-based help extraction is conservative and currently handles adjacent line comments before a function; it is not a full PowerShell help engine.
- Actual runtime pipeline output is not inferred; only static `OutputType` attributes are reported.

## Test Coverage

Added `PowerShellApiMetadataServiceTests` covering:

- basic function discovery
- advanced functions and `CmdletBinding`
- typed parameters
- mandatory parameter forms
- explicit `Mandatory = $false`
- unknown mandatory expressions
- literal defaults
- expression defaults without execution
- aliases
- `ValidateSet`
- `ValidateRange`
- `ValidateLength`
- `ValidatePattern`
- `ValidateNotNull`
- `ValidateNotNullOrEmpty`
- unresolved validation arguments
- arrays
- nullable values
- hashtable, pscustomobject, guid, datetime, datetimeoffset, enum-like names
- `OutputType`
- multiple functions
- nested functions
- no-function scripts
- syntax-error scripts
- malicious-looking scripts
- dynamic function creation
- unsupported metadata
- conservative comment help
- PowerShell classes
- filters

## Exact Tests Run and Results

1. `dotnet build PS7ScriptDesk.PowerShell\PS7ScriptDesk.PowerShell.csproj`
   - Passed.
   - 0 warnings, 0 errors.

2. `dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --filter FullyQualifiedName~PowerShellApiMetadataServiceTests`
   - Passed.
   - 36 passed, 0 failed, 0 skipped.
   - Required elevated sandbox approval because the WPF test project evaluation reads the local Windows SDK cache under `C:\Users\rbarn\AppData\Local\Microsoft SDKs`.

3. `dotnet build PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj`
   - Passed.
   - 0 warnings, 0 errors.

4. `dotnet test PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --no-build`
   - Passed.
   - 234 passed, 0 failed, 0 skipped.
   - Required elevated sandbox approval for the same Windows SDK cache access.

5. `dotnet build PS7ScriptDesk.slnx`
   - Failed in packaging project because `Microsoft.DesktopBridge.props` was not available under the dotnet SDK path.
   - Non-packaging projects built successfully in that run.

6. `& 'C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe' PS7ScriptDesk.slnx /restore /m /p:Configuration=Debug /p:Platform=x64`
   - Passed with elevated sandbox approval.
   - 4 warnings, 0 errors.
   - Warnings were pre-existing Shell warnings in `MainWindow.xaml.cs`:
     - CS4014 at line 5243.
     - CS8604 at line 7129.

## Risks Discovered

- PowerShell class methods can appear as `FunctionDefinitionAst` nodes; the parser now explicitly filters function ASTs with a `TypeDefinitionAst` ancestor so class methods are not exposed as API functions.
- Some dynamic-looking attribute expressions are rejected by PowerShell syntax itself before metadata parsing; syntax errors are returned in the parser result rather than thrown.
- Comment help association can be fragile if made too ambitious, so Phase 1 keeps it conservative.

## Static Analysis Confirmation

`PowerShellApiMetadataService` only calls `Parser.ParseInput` and inspects AST nodes. It does not:

- execute the script
- dot-source the script
- launch `pwsh.exe`
- create runspaces
- invoke functions
- evaluate default expressions
- load modules
- write source text to logs
- modify source files

The malicious-looking script test verifies that source containing destructive/network/process/file commands is parsed without executing those commands.

## Phase Boundary Confirmation

No later REST API Publisher phases were implemented. This change adds no REST server functionality, no ASP.NET Core host, no OpenAPI/Swagger support, no local test API process, no project generator, no publishing workflow, no UI, no WebSocket support, and no SSE support.
