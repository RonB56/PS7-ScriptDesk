namespace PS7ScriptDesk.Shell.Help;

/// <summary>Wizard-local help text kept separate from export and API execution logic.</summary>
internal static class ExportWizardHelpDefinitions
{
    public static IReadOnlyDictionary<string, string> Exe { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ValidationText"] = "Validation and export status\nShows configuration problems or the current export result. Resolve errors here before relying on the generated artifact.",
        ["ImplicationsText"] = "Deployment implications\nSummarizes prerequisites and tradeoffs implied by the selected runtime, architecture, and package options. Review these constraints against the target computers.",
        ["ReviewApplicationText"] = "Application review\nRead-only summary of the selected application identity and execution behavior.",
        ["ReviewPlatformText"] = "Platform review\nRead-only summary of architecture, .NET deployment, PowerShell runtime, package format, and optimization selections.",
        ["ReviewAdvancedText"] = "Advanced settings review\nRead-only summary of profile, fatal-error dialog, logging, and metadata settings.",
        ["ReviewDependencyText"] = "Dependency review\nRead-only summary of detected dependencies and their packaging considerations.",
        ["ReviewOutputText"] = "Output review\nRead-only resolved destination and artifact summary. Confirm the path before starting export.",
        ["PresetList"] = "Deployment preset\nChoose a starting point for the later settings. Portable Windows EXE selects self-contained .NET and bundled PowerShell; Console and GUI select their window behavior; ARM64 targets Windows ARM64; Small EXE requires installed .NET; Custom leaves choices for you to set. Presets are editable and do not prove compatibility with every destination computer.",
        ["ApplicationNameBox"] = "Application and executable name\nInitially suggested from the source script filename. This value supplies the generated EXE filename and application/product display name. Use a filename-safe name that users will recognize.",
        ["ApplicationTypeBox"] = "Application type\nDefault: Auto Detect. It chooses Console when the wizard cannot confidently identify a GUI script. Console keeps a terminal for output and interactive input such as Read-Host. Windows GUI starts without a console, which suits graphical scripts but hides ordinary console output. Choose explicitly when the script's interaction model is known.",
        ["DescriptionBox"] = "Application description\nOptional descriptive metadata for the generated application. Keep it concise and avoid secrets or user data.",
        ["IconBox"] = "Application icon\nOptional path to a Windows .ico file embedded as the executable icon. The selected file must remain available while exporting.",
        ["AdministratorBox"] = "Require administrator\nDefault: Off (Normal User). Enabling requests administrator elevation through a Windows manifest, so Windows may show a UAC prompt at launch. Elevated execution changes access and drag-and-drop behavior. Enable only when the script must perform an administrative task; elevation is not a general requirement.",
        ["ArchitectureBox"] = "Target architecture\nDefault: Windows x64. Select Windows x64 or Windows ARM64. The executable and any bundled runtime must match the destination architecture. Use ARM64 only for supported Windows ARM devices and an available matching runtime.",
        ["DeploymentBox"] = ".NET deployment mode\nDefault: Self-contained. Self-contained includes the .NET runtime and increases output size while reducing the .NET prerequisite. Framework-dependent is smaller but requires a compatible .NET runtime installed on the destination. This choice does not determine whether PowerShell itself is bundled.",
        ["PowerShellBox"] = "PowerShell runtime source\nDefault: Bundle complete PowerShell 7 runtime. Bundling packages a selected runtime with the export, increasing size and reducing reliance on separately installed PowerShell. Use selected installed PowerShell 7 is smaller but requires a configured compatible runtime on the destination. Bundling .NET alone does not bundle PowerShell; neither option guarantees clean-machine compatibility without deployment testing.",
        ["PackageBox"] = "Package format\nDefault: Single EXE. Single EXE embeds the PowerShell runtime archive in one launcher file and materializes it into a local cache at startup. Application folder keeps the runtime and supporting files beside the launcher. A single file is easier to copy; a folder can be easier to inspect and can avoid extraction work, but the whole folder must be deployed. Both can be large when PowerShell is bundled.",
        ["OptimizationBox"] = "Startup and compatibility optimization\nDefault: Balanced. In the current generator, Maximum Compatibility and Balanced both leave ReadyToRun disabled. Fast Startup enables ReadyToRun and can increase output size; framework-dependent Fast Startup produces a validation warning. Measure on intended targets because startup and compatibility depend on the machine and script.",
        ["DependencyList"] = "Detected dependencies\nReview files and modules the wizard detected for this script. This list informs packaging choices; it does not automatically guarantee every dynamically loaded module or external file is included. Check the script's runtime behavior after export.",
        ["LoadProfileBox"] = "Load user PowerShell profile\nDefault: Off. When enabled, the launched PowerShell process loads the selected user's profile. Profiles can add commands and settings but make behavior depend on that user's machine. Leave off for more predictable deployment unless the script requires profile configuration.",
        ["ErrorDialogBox"] = "Fatal error dialog\nDefault: On. For Windows GUI applications, show a dialog when the launcher reports a fatal startup or execution failure. Console applications report errors to their console. This does not guarantee that every non-terminating PowerShell error is fatal.",
        ["WriteLogBox"] = "Application log\nDefault: On. Writes timestamped launcher errors and, for GUI applications, captured PowerShell output and error lines to %LOCALAPPDATA%\\PS7ScriptDesk\\ExportedApps\\<application>.log. Logs can contain paths and script output; inspect them before sharing. This is not a complete PowerShell transcript.",
        ["CompanyBox"] = "Company metadata\nOptional publisher/company name embedded in application version metadata. Use the organization name intended for users.",
        ["ProductVersionBox"] = "Product version\nDefault: 1.0.0. Version string recorded as product metadata. Keep it consistent with your release numbering.",
        ["FileVersionBox"] = "File version\nDefault: 1.0.0.0. Version string recorded for the generated file. Windows file properties display this separately from product version.",
        ["OutputBox"] = "Export destination\nFolder and executable name for the generated artifact. Review the resolved path before export; replacing an existing destination is subject to the export's ownership and replacement checks.",
        ["BrowseIconButton"] = "Browse for application icon\nChoose an existing Windows .ico file to use as the executable icon.",
        ["BrowseOutputButton"] = "Browse for export destination\nChoose the output folder and executable filename. The dialog asks before replacing an existing file.",
        ["BackButton"] = "Back\nReturn to the previous wizard step. Current configuration values remain available for review.",
        ["NextButton"] = "Next or return export settings\nContinue to the next wizard step. On the final Review step, validate the configuration and return it to the export workflow; this button does not itself compile the executable. Validation may prevent completion when required values are invalid.",
        ["CancelButton"] = "Cancel export\nClose the wizard without starting the final export operation.",
        ["PresetStepButton"] = "Preset step\nChoose a starting deployment profile. Preset values can be changed in later steps.",
        ["ApplicationStepButton"] = "Application step\nSet application identity, window type, icon, and elevation behavior.",
        ["PlatformStepButton"] = "Platform step\nChoose architecture, .NET deployment, PowerShell runtime source, package format, and optimization.",
        ["DependenciesStepButton"] = "Dependencies step\nReview dependency findings before exporting.",
        ["AdvancedStepButton"] = "Advanced step\nSet profile, error display, logging, and version metadata.",
        ["ReviewStepButton"] = "Review step\nConfirm output settings and start export after reviewing the summary.",
    };

    public static IReadOnlyDictionary<string, string> Api { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SectionList"] = "API wizard sections\nChoose API configuration, endpoints, runtime limits, local testing, or build and publish operations. Your current configuration remains available when changing sections.",
        ["SourceText"] = "Source script\nShows the PowerShell script whose discovered functions and parameters are available for API configuration.",
        ["ValidationText"] = "Validation and operation status\nShows configuration errors, warnings, or operation results. A structurally successful build or publish does not by itself prove deployment runtime behavior.",
        ["UnsupportedSecurityModeText"] = "Unavailable authentication mode\nExplains why a selected authentication mode cannot be used with the current REST V1 configuration. Choose one of the enabled modes before saving or publishing.",
        ["SecurityGuidanceText"] = "Authentication guidance\nSummarizes the restrictions and guidance for the selected security mode. An API key is one control and does not make unrestricted public network exposure safe.",
        ["LocalTestStatusText"] = "Local host status\nShows whether the loopback Local Test host is stopped, starting, ready, or reporting an error.",
        ["LocalTestResultsSplitter"] = "Resize Local Test panels\nDrag to change the relative space used by the request and response areas. This changes the current view layout only.",
        ["LocalTestResponseSummaryText"] = "Local response summary\nSummarizes the most recent local request result. Inspect the Response or Live Events view for details.",
        ["BuildPublishStatusText"] = "Build and publish status\nReports project generation, packaging, and verification results. Structural checks are not a substitute for running the artifact on its intended target.",
        ["ApiKeyEnvironmentVariableLabel"] = "API key environment variable\nName of the environment variable that supplies the API key to the host. The secret value is never stored in this setting or shown in help.",
        ["LocalTestApiKeyLabel"] = "Local test API key\nCredential sent only for local test requests that require API-key authentication. Do not share screenshots or copied data containing secrets.",
        ["ApiTitleBox"] = "API title\nDisplay name used in API metadata and the local OpenAPI document. Initially derived from the source script filename; the fallback is PowerShell API. Choose a stable name for clients and operators.",
        ["ApiVersionBox"] = "API version\nVersion label included in generated API documentation. Default: 1.0.0. Use a version clients can use to identify contract changes.",
        ["RoutePrefixBox"] = "Route prefix\nPath prefix placed before REST routes. Default: /api. It is normalized to begin with one slash. This is a URL path, not a network address.",
        ["DescriptionBox"] = "API description\nOptional human-readable description used by generated API documentation. Do not put credentials or private data here.",
        ["SecurityModeBox"] = "API authentication mode\nDefault: API key. REST V1 supports API key or Local test only without authentication. Other listed modes are disabled and cannot be saved or published. API keys are supplied through the configured environment variable. API-key authentication does not make public exposure safe; use network controls and HTTPS termination appropriate to your deployment.",
        ["ApiKeyEnvironmentVariableBox"] = "API key environment variable\nDefault: PS7API_API_KEY. Name of the environment variable that supplies the API key to the host. The secret value is never stored in this setting or shown in help. Keep the variable available to the host process and protect it as a credential.",
        ["AllowNoAuthBox"] = "Allow unauthenticated local test\nDefault: Off. Allows the Local Test host to run without API-key authentication on its loopback listener. This setting is for local testing and does not authorize exposing an unauthenticated API to other machines.",
        ["SwaggerBox"] = "Local OpenAPI Viewer\nDefault: On. Enables the interactive documentation viewer for local testing. The viewer sends requests to the local host; it does not publish or secure the API.",
        ["PublishedOpenApiAuthBox"] = "Protect published OpenAPI access\nDefault: On when API-key mode is selected. Require the API key when clients retrieve the published OpenAPI document. This is separate from authentication on each endpoint.",
        ["EndpointGrid"] = "Endpoint configuration\nEach row maps a discovered PowerShell function to one API operation. Enable only functions intended for callers. REST handles a request and response; SSE streams server events; WebSocket supports a persistent two-way connection. Route and method apply to REST; API key requirements are enforced for protected operations.",
        ["ParameterGrid"] = "Endpoint parameter bindings\nMaps HTTP request values to the selected PowerShell function's named parameters. Query, Route, Body, and Header are supported request sources; ServerDefined values are supplied by configuration. Required behavior and type guide validation and conversion before invocation.",
        ["EndpointSummaryTextBlock"] = "Endpoint summary\nRead-only summary of discovered functions and configured API operations. Select a row to inspect its parameters below.",
        ["ReconciliationResolutionBox"] = "Apply configuration reconciliation\nReview the listed changes before checking this control. Confirming applies the current metadata updates and excludes removed functions or parameters from the saved effective configuration. Canceling the wizard leaves the companion file unchanged.",
        ["SelectedEndpointParameterTextBlock"] = "Selected endpoint parameters\nRead-only summary of the selected function's parameters and binding requirements.",
        ["RunspaceMinBox"] = "Minimum PowerShell runspaces\nMinimum runspaces kept available by the pool. Default: 1. Must be a positive whole number and no greater than the maximum; no additional upper bound is enforced here. A higher minimum can keep capacity warm but uses more resources while idle.",
        ["RunspaceMaxBox"] = "Maximum PowerShell runspaces\nMaximum execution environments in the pool. Default: 4. Must be a positive whole number and at least the minimum; no additional upper bound is enforced here. Increasing it can improve concurrent throughput but raises CPU and memory use; requests still share the process and are not isolated from trusted script state.",
        ["ConcurrencyBox"] = "Maximum concurrent requests\nMaximum PowerShell invocations allowed at once. Default: 4. Must be a positive whole number and cannot exceed maximum runspaces; no additional upper bound is enforced here. Increase only after measuring workload; each active invocation consumes resources.",
        ["QueueLimitBox"] = "Request queue limit\nMaximum requests waiting for an execution slot. Default: 32. The wizard requires a positive whole number, although the model can represent zero. A larger queue can absorb bursts but waiting calls may take longer; it does not increase concurrent execution.",
        ["TimeoutBox"] = "Request timeout (seconds)\nDefault invocation timeout. Default: 30 seconds. The wizard accepts positive whole seconds; values above five minutes produce a validation warning. A timeout cancels the invocation path after the configured interval; it is not a hard process memory or CPU sandbox.",
        ["ResponseByteLimitBox"] = "Response byte limit\nMaximum serialized result size checked for each PowerShell invocation after result normalization. Default: 5,242,880 bytes (5 MiB); the wizard accepts positive whole bytes. This limits response payload handling, not total process memory: a large PowerShell object may already exist before serialization checks.",
        ["OutputDirectoryBox"] = "Generated project folder\nDestination for editable source generated by Generate Project and used by build/publish workflows. Existing content is protected by generated-project ownership checks; only recognized owned files may be replaced. Choose a dedicated folder and do not place valuable unrelated files there.",
        ["BrowseOutputDirectoryButton"] = "Browse for generated project folder\nChoose the destination directory for the editable API project. Existing files are subject to generated-project ownership and replacement protections.",
        ["PreserveGeneratedProjectBox"] = "Keep generated project files after publishing\nDefault: Off. This checkbox value is saved in the API configuration. Current Build/Publish service source does not read it, so it currently does not change cleanup or retention behavior. The generated editable project is managed separately from published output; inspect the folder before removing it.",
        ["SaveButton"] = "Save configuration\nValidate and save the current API settings beside the source project configuration. Saving does not generate or publish a runnable host.",
        ["StartTestButton"] = "Start Local Test\nGenerate the local test project and start its host on the supported loopback listener. Successful local startup confirms this test environment only, not deployment compatibility or safe public exposure.",
        ["RestartTestButton"] = "Restart local host\nStop and start the Local Test host using the current saved configuration.",
        ["StopTestButton"] = "Stop host\nShut down the complete Local Test API host and its listener. This is different from Stop Test, which cancels one request or test invocation.",
        ["StopInvocationButton"] = "Stop test invocation\nCancel the currently running local request or streaming test. The API host remains available for later requests.",
        ["BaseUrlBox"] = "Local base URL\nLoopback address where the local test host is listening. This is a read-only result of host startup.",
        ["DiscoveryUrlBox"] = "Discovery URL\nLocal discovery document URL for the running test host. Use it to inspect available operations.",
        ["OpenApiUrlBox"] = "OpenAPI document URL\nURL of the machine-readable OpenAPI document for the local host. Open JSON displays that document.",
        ["OpenOpenApiButton"] = "Open OpenAPI JSON\nOpen the local OpenAPI document in a browser for inspection.",
        ["SwaggerUrlBox"] = "OpenAPI Viewer URL\nLocal address of the optional interactive viewer. It is available only when the viewer is enabled.",
        ["OpenViewerButton"] = "Open OpenAPI Viewer\nOpen interactive API documentation for the local test host. Requests made there use the local host and its configured authentication.",
        ["LocalTestEndpointBox"] = "Test endpoint\nSelect the configured operation to preview and run a local request. The available request fields depend on its transport and bindings.",
        ["LocalTestParameterGrid"] = "Local test parameter values\nEnter sample values used by Run Test. Values are converted according to the binding type where supported; route values fill route placeholders, query values become query parameters, body values become JSON, and header values become request headers.",
        ["LocalTestApiKeyBox"] = "Local test API key\nEnter the API key only when testing an endpoint that requires it. The value is sent in the X-API-Key header and is not included in tooltip text or normal help output.",
        ["TestRequestUrlBox"] = "Request URL\nResolved local request URL preview. REST and SSE use HTTP URLs; WebSocket uses ws:// or wss:// as appropriate.",
        ["TestRequestBodyBox"] = "Request payload\nJSON request body or WebSocket invocation envelope preview. Review it before sending; secret fields should not be copied into shared diagnostics.",
        ["ExecuteRequestButton"] = "Run Test\nSend one request using the selected endpoint and current sample values. REST returns a response; SSE and WebSocket may produce a sequence of live events.",
        ["LocalTestResponseBox"] = "Response\nDisplays the completed HTTP response or final request result. Live SSE and WebSocket messages appear in Live Events instead.",
        ["LocalTestEventGrid"] = "Live events\nShows incremental SSE or WebSocket events received during the current test. This is a client-side event display, not a durable server log.",
        ["LocalTestEventDetailsBox"] = "Selected event details\nShows the full details for the selected live event. Review before copying because event data can contain script output or sensitive values.",
        ["CopySelectedEventButton"] = "Copy selected event\nCopy the selected live event details to the clipboard.",
        ["CopyAllEventsButton"] = "Copy live events\nCopy the events currently retained in the Local Test view to the clipboard.",
        ["CopyResponseButton"] = "Copy response\nCopy the displayed response to the clipboard.",
        ["ClearResultsButton"] = "Clear local results\nClear response and event details from the wizard view. This does not change API configuration or server state.",
        ["GeneratedProjectPathBox"] = "Generated project path\nShows the editable source project folder used by Generate Project, Build API, or Publish API. Replacement is subject to the generated-project ownership manifest.",
        ["GenerateProjectButton"] = "Generate editable project\nCreate or update the C# source project and runtime source files from the current API configuration. This operation does not compile the generated source or create a runnable artifact.",
        ["TargetArchitectureBox"] = "Publish target architecture\nDefault: Windows x64. Choose Windows x64, Windows ARM64, or Both. The packaged runtime must match the target. Cross-architecture validation is structural and does not prove the artifact runs on that architecture.",
        ["BuildApiButton"] = "Build API\nPackage the existing bundled API runtime and generated project into a runnable build output. This packages a prebuilt runtime; it does not compile the generated C# source project.",
        ["PublishApiButton"] = "Publish API\nCreate a deployment-oriented package from the existing bundled runtime and generated project. Publishing is distinct from generating editable source and does not establish clean-machine compatibility by itself.",
        ["BuildPublishOutputPathBox"] = "Build or publish output\nShows the output folder produced by the last build or publish operation. Confirm the target before distributing it.",
        ["OpenPublishFolderButton"] = "Open output folder\nOpen the last build or publish output directory in File Explorer.",
        ["BackButton"] = "Previous section\nReturn to the prior API wizard section without discarding the current in-memory configuration.",
        ["NextButton"] = "Next section\nMove to the next API wizard section. Validation may report invalid configuration before an operation can run.",
        ["CancelButton"] = "Close API wizard\nClose the wizard. Unsaved configuration changes may be lost.",
    };

    public static IReadOnlyDictionary<string, string> ApiGridColumns { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Use"] = "Use endpoint\nDefault: On for created endpoint configurations. Enable or disable this function as an API operation. Disabled functions are not exposed as endpoints.",
        ["Function"] = "PowerShell function\nDiscovered function name. This cell is read-only; choose a function in the source script before discovery.",
        ["Transport"] = "Transport\nDefault: REST when no endpoint override is set. REST sends a discrete request and response. SSE streams server-to-client events over HTTP. WebSocket keeps a two-way connection open for interactive messages. Choose REST for ordinary operations, SSE for incremental output, and WebSocket for bidirectional exchange.",
        ["Method"] = "HTTP method\nDefault: GET. REST V1 supports GET and POST. GET is appropriate for retrieval and query/route inputs; POST is suited to request bodies or actions. This setting applies to REST operations.",
        ["Route"] = "Route template\nREST path for the operation. Placeholders such as {id} bind to Route parameters. The path is combined with the API route prefix.",
        ["Operation"] = "Operation identifier\nStable identifier used in API documentation and generated operation metadata. Keep it unique and suitable for client tooling.",
        ["Auth"] = "Require authentication\nDefault: On for created endpoint configurations. When enabled, the endpoint requires the configured API key in the X-API-Key header if API-key mode is active. Local test-only mode has no API key enforcement; authentication does not replace network security.",
        ["PowerShell"] = "PowerShell parameter\nOriginal parameter name from the discovered function. This cell is read-only to prevent accidental binding to a different script parameter.",
        ["HTTP/API"] = "HTTP/API parameter name\nExternal name used by API clients. It maps request data to the selected PowerShell parameter.",
        ["Source"] = "Parameter source\nDefault: Query. Query reads a URL query value; Route reads a route placeholder; Body reads a JSON request property; Header reads an HTTP header; ServerDefined supplies a configured value rather than caller input. Only these sources are supported.",
        ["Required"] = "Required behavior\nDefault: Inherit from PowerShell. Inherit uses PowerShell mandatory metadata where available. Required always requires the value; Optional allows omission. Omitted values remain absent from invocation while explicitly supplied false values are preserved.",
        ["Type"] = "Parameter type\nType used to convert incoming text or JSON values before calling PowerShell. Use supported primitive types matching the function parameter; unsupported complex conversion is not implied.",
        ["Validation"] = "PowerShell validation summary\nRead-only summary of discovered mandatory and validation attributes. The API validates supported constraints before invocation; inspect the source function for the full rule.",
        ["Parameter"] = "Test parameter\nName of a configured API input used in the Local Test request.",
        ["Value"] = "Event or test value\nFor request rows, this is the sample value sent to the API. For event rows, this is output received from the stream. Review for sensitive data before copying.",
        ["#"] = "Event sequence\nOrder in which the Local Test client received and retained the event.",
        ["Event"] = "Event type\nKind of SSE or WebSocket event recorded by the local client.",
        ["Stream"] = "PowerShell stream\nStream classification associated with an event or output record.",
        ["Value / Message"] = "Event value or message\nPayload or message captured for this event. Content can include script-generated data.",
        ["Time"] = "Event time\nLocal timestamp when the test client recorded the event.",
        ["Terminal"] = "Terminal status\nIndicates the final status reported for the streaming invocation when available.",
    };

    public static IReadOnlyDictionary<string, string> ApiPages { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["API"] = "API settings\nSet API identity, route prefix, authentication, and local OpenAPI options. The current listener policy is HTTP on loopback for local testing; remote bind and direct HTTPS are not supported.",
        ["Endpoints"] = "Endpoint and parameter configuration\nSelect exposed functions, choose transport and REST route details, then map incoming values to PowerShell parameters.",
        ["Runtime"] = "Runtime limits and generated project\nConfigure the runspace pool, concurrency, queue, invocation timeout, response-size check, and generated-project destination/retention.",
        ["Local Test"] = "Local API test\nGenerate and start a loopback host, send a selected endpoint request, and inspect ordinary responses or streaming events. Passing here does not prove a published artifact runs on another machine.",
        ["Response"] = "REST response view\nShows the completed response for an HTTP request. SSE and WebSocket messages appear in Live Events.",
        ["Live Events"] = "Streaming events view\nShows incremental events received over SSE or WebSocket during a local test.",
        ["Build / Publish"] = "Build and publish\nGenerate editable source, package a runnable artifact from the bundled runtime, or create a publish output. Review each operation's status and verification level.",
    };

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ExeOptions { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["PresetList"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PortableWindowsExe"] = "Portable Windows EXE\nStarts with self-contained .NET, bundled PowerShell, and a single-file package. This creates a large artifact and still requires target-machine verification.",
                ["WindowsConsoleApplication"] = "Windows Console Application\nStarts with a console for ordinary output and interactive input. Runtime and package choices remain editable.",
                ["WindowsGuiApplication"] = "Windows GUI Application\nStarts without a console, suitable for graphical scripts. Console output is not shown in a terminal.",
                ["Arm64PortableExe"] = "ARM64 Portable EXE\nTargets Windows ARM64. Bundled PowerShell currently supports x64 only, so select a compatible installed ARM64 PowerShell runtime or change architecture; verify the artifact on ARM64.",
                ["SmallExe"] = "Small EXE\nStarts framework-dependent on .NET to reduce output size. Destination computers need a compatible .NET runtime; PowerShell runtime selection is separate.",
                ["Custom"] = "Custom\nStarts from the model defaults and lets you choose each deployment option.",
            },
            ["ApplicationTypeBox"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AutoDetect"] = "Auto Detect\nChooses Console when the wizard cannot confidently detect a GUI application.",
                ["Console"] = "Console Application\nKeeps a terminal available for output and interactive PowerShell input.",
                ["WindowsGui"] = "Windows GUI Application\nRuns without a console window; scripts should present their own user interface.",
            },
            ["ArchitectureBox"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["X64"] = "Windows x64\nTarget 64-bit x86 Windows. This is the default target.",
                ["Arm64"] = "Windows ARM64\nTarget 64-bit ARM Windows. A matching PowerShell runtime source is required when bundling.",
            },
            ["DeploymentBox"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SelfContained"] = "Self-contained .NET\nInclude the .NET runtime, increasing output size and removing the separate .NET runtime prerequisite.",
                ["FrameworkDependent"] = "Framework-dependent .NET\nReduce output size but require a compatible .NET runtime installed on the destination.",
            },
            ["PowerShellBox"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Embedded"] = "Bundle complete PowerShell 7 runtime\nInclude a complete selected PowerShell distribution. This increases package size and avoids requiring separately installed PowerShell, subject to target compatibility.",
                ["InstalledPowerShell"] = "Use selected installed PowerShell 7\nRequire the configured PowerShell installation on the destination. This reduces package size but adds a runtime prerequisite.",
            },
            ["PackageBox"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SingleFile"] = "Single EXE\nEmbed the PowerShell runtime archive with the launcher; the runtime is materialized to a local cache at startup.",
                ["ApplicationFolder"] = "Application folder\nDeploy the launcher together with its runtime and support files in the generated folder.",
            },
            ["OptimizationBox"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MaximumCompatibility"] = "Maximum Compatibility\nLeaves ReadyToRun disabled in the current generator. The generated project setting currently matches Balanced.",
                ["Balanced"] = "Balanced\nDefault profile. The generated project currently also leaves ReadyToRun disabled, matching Maximum Compatibility.",
                ["FastStartup"] = "Fast Startup\nEnables ReadyToRun. Output may be larger and framework-dependent deployments receive a target-runtime support warning.",
            },
        };

    public static IReadOnlyDictionary<string, string> ApiSecurityOptions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["LocalTestNoAuthentication"] = "Local test only\nRuns without API-key checks on the supported loopback test listener. Do not expose an unauthenticated service to other machines.",
        ["ApiKey"] = "API key\nRequires the configured environment variable and sends a key check for protected endpoints. This does not replace network controls or HTTPS at the deployment edge.",
        ["Unsupported"] = "Unavailable in REST V1\nThis authentication mode is displayed to explain the saved setting but is disabled. Choose API key or Local test only without authentication.",
    };

    public static IReadOnlyDictionary<string, string> ApiTargetArchitectureOptions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["WinX64"] = "Windows x64\nPackage for 64-bit x86 Windows. This is the initial selection; target-machine execution still requires verification.",
        ["WinArm64"] = "Windows ARM64\nPackage for 64-bit ARM Windows. Build success on x64 does not prove ARM64 runtime compatibility.",
        ["Both"] = "Both architectures\nProduce outputs for x64 and ARM64 where supported. Each output needs runtime verification on its matching architecture.",
    };
}
