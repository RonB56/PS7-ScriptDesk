using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell.Dialogs;
using PS7ScriptDesk.Shell.Services;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;

namespace PS7ScriptDesk.Tests;

public sealed class RestApiPublishWizardServiceTests
{
    [Fact]
    public void CreateDefaultEndpoints_UsesPublishableFunctionsAndQueryBindings()
    {
        var metadata = CreateMetadata();

        var endpoints = RestApiPublishWizardService.CreateDefaultEndpoints(metadata, "/api");

        var endpoint = Assert.Single(endpoints);
        Assert.Equal("Get-Widget", endpoint.PowerShellFunctionName);
        Assert.Equal(ApiHttpMethod.Get, endpoint.Rest.Method);
        Assert.Equal("/api/get-widget", endpoint.Rest.RouteTemplate);
        Assert.Equal("getWidget", endpoint.Rest.OperationId);
        Assert.False(endpoint.RequiresAuthentication);
        Assert.Equal(2, endpoint.ParameterBindings.Count);
        Assert.Contains(endpoint.ParameterBindings, binding =>
            binding.PowerShellParameterName == "Name" &&
            binding.Source == ApiParameterSource.Query &&
            binding.Name == "name" &&
            binding.Required == ApiRequiredBehavior.Required &&
            binding.TypeName == "string");
        Assert.Contains(endpoint.ParameterBindings, binding =>
            binding.PowerShellParameterName == "IncludeInactive" &&
            binding.Source == ApiParameterSource.Query &&
            binding.Name == "includeInactive" &&
            binding.Required == ApiRequiredBehavior.Optional &&
            binding.TypeName == "bool");
    }

    [Fact]
    public void LoadOrCreateConfiguration_WhenExistingConfigurationHasNoEndpoints_AddsDetectedEndpoints()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "RestApiWizardServiceTests.ps1");
        var existing = ApiPublishConfiguration.CreateDefaultForScriptPath(sourcePath);
        existing.Endpoints.Clear();
        var store = new InMemoryConfigurationStore(existing);
        var service = new RestApiPublishWizardService(store, localTestHostServiceFactory: () => throw new InvalidOperationException("Local host service should not be created."));

        var configuration = service.LoadOrCreateConfiguration(
            new ApiPublishWizardRequest("Widget API", sourcePath, "function Get-Widget {}"),
            CreateMetadata());

        Assert.Equal("RestApiWizardServiceTests.ps1", configuration.SourceScript);
        Assert.Single(configuration.Endpoints);
        Assert.Equal("/api/get-widget", configuration.Endpoints[0].Rest.RouteTemplate);
    }

    [Theory]
    [InlineData(-1, 4, 0)]
    [InlineData(0, 4, 0)]
    [InlineData(2, 4, 2)]
    [InlineData(-1, 0, -1)]
    public void InitialPageSelection_NormalizesMissingSelectionToFirstPage(int selectedIndex, int itemCount, int expected)
    {
        Assert.Equal(expected, RestApiPublishWizardWindow.ResolveInitialPageIndex(selectedIndex, itemCount));
    }

    [Fact]
    public void PublishTargetOptions_DefaultToSupportedSelfContainedTargets()
    {
        var options = RestApiPublishWizardWindow.CreatePublishTargetOptions();

        Assert.Equal(
            [ApiPublishTargetArchitecture.WinX64, ApiPublishTargetArchitecture.WinArm64, ApiPublishTargetArchitecture.Both],
            options.Select(option => option.Architecture).ToArray());
        Assert.Contains(options, option => option.DisplayName == "win-x64");
        Assert.Contains(options, option => option.DisplayName == "win-arm64");
    }

    [Fact]
    public void ParameterBindingRow_ProjectsConfigurationAndMetadataForEndpointMappingUi()
    {
        var metadata = CreateMetadata().Functions[0].Parameters[0];
        var binding = new ApiParameterBindingConfiguration
        {
            PowerShellParameterName = "Name",
            Source = ApiParameterSource.Query,
            Name = "name",
            Required = ApiRequiredBehavior.Required,
            TypeName = "string"
        };

        var row = RestApiParameterBindingRow.FromConfiguration(binding, metadata);
        var roundTripped = row.ToConfiguration();

        Assert.Equal("Name", row.PowerShellParameterName);
        Assert.Equal("name", row.Name);
        Assert.Equal(ApiParameterSource.Query, row.Source);
        Assert.Equal(ApiRequiredBehavior.Required, row.Required);
        Assert.Contains("Mandatory", row.ValidationSummary, StringComparison.Ordinal);
        Assert.Equal("Name", roundTripped.PowerShellParameterName);
        Assert.Equal(ApiParameterSource.Query, roundTripped.Source);
        Assert.Equal("name", roundTripped.Name);
    }

    [Fact]
    public void RestV1SecurityModeOptions_ExposeOnlySupportedModesForNewConfigurations()
    {
        var options = RestApiPublishWizardWindow.CreateRestV1SecurityModeOptions(ApiSecurityMode.LocalTestNoAuthentication);

        Assert.Equal(
            [ApiSecurityMode.LocalTestNoAuthentication, ApiSecurityMode.ApiKey],
            options.Select(option => option.Mode).ToArray());
        Assert.All(options, option => Assert.True(option.IsSelectable));
        Assert.DoesNotContain(options, option => option.Mode == ApiSecurityMode.JwtBearer);
        Assert.DoesNotContain(options, option => option.Mode == ApiSecurityMode.WindowsAuthentication);
    }

    [Fact]
    public void RestV1SecurityModeOptions_KeepUnsupportedExistingModeVisibleButDisabled()
    {
        var options = RestApiPublishWizardWindow.CreateRestV1SecurityModeOptions(ApiSecurityMode.JwtBearer);

        var unsupported = Assert.Single(options, option => option.Mode == ApiSecurityMode.JwtBearer);
        Assert.False(unsupported.IsSelectable);
        Assert.Contains("Not available in REST V1", unsupported.DisplayName, StringComparison.Ordinal);
        Assert.Contains(options, option => option.Mode == ApiSecurityMode.LocalTestNoAuthentication && option.IsSelectable);
        Assert.Contains(options, option => option.Mode == ApiSecurityMode.ApiKey && option.IsSelectable);
    }

    [Theory]
    [InlineData(ApiSecurityMode.LocalTestNoAuthentication)]
    [InlineData(ApiSecurityMode.ApiKey)]
    public void LocalNoAuthAndApiKey_AreRestV1SelectableModes(ApiSecurityMode mode)
    {
        Assert.Contains(mode, RestApiPublishWizardWindow.SupportedRestV1SecurityModes);
        Assert.True(RestApiPublishWizardWindow.IsRestV1SecurityModeSupported(mode));
    }

    [Theory]
    [InlineData(ApiSecurityMode.JwtBearer)]
    [InlineData(ApiSecurityMode.WindowsAuthentication)]
    public void UnsupportedSecurityModes_AreNotPublishableInRestV1(ApiSecurityMode mode)
    {
        var configuration = CreateValidConfiguration(mode);
        var result = new ApiPublishConfigurationValidator().Validate(configuration, CreateMetadata());

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, error => error.Code == "API042");
        Assert.Contains("not supported by REST V1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SecurityModeUiState_IsModeAware()
    {
        var local = RestApiPublishWizardWindow.CreateSecurityModeUiState(ApiSecurityMode.LocalTestNoAuthentication);
        Assert.False(local.ShowApiKeyControls);
        Assert.True(local.ShowLocalNoAuthControls);
        Assert.False(local.ShowUnsupportedModeMessage);

        var apiKey = RestApiPublishWizardWindow.CreateSecurityModeUiState(ApiSecurityMode.ApiKey);
        Assert.True(apiKey.ShowApiKeyControls);
        Assert.False(apiKey.ShowLocalNoAuthControls);
        Assert.False(apiKey.ShowUnsupportedModeMessage);

        var jwt = RestApiPublishWizardWindow.CreateSecurityModeUiState(ApiSecurityMode.JwtBearer);
        Assert.False(jwt.ShowApiKeyControls);
        Assert.False(jwt.ShowLocalNoAuthControls);
        Assert.True(jwt.ShowUnsupportedModeMessage);
        Assert.Contains("not available in REST V1", jwt.UnsupportedModeText, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingUnsupportedSecurityMode_GetsDisabledOptionAndValidationState()
    {
        var options = RestApiPublishWizardWindow.CreateRestV1SecurityModeOptions(ApiSecurityMode.WindowsAuthentication);
        var state = RestApiPublishWizardWindow.CreateSecurityModeUiState(ApiSecurityMode.WindowsAuthentication);

        var unsupported = Assert.Single(options, option => option.Mode == ApiSecurityMode.WindowsAuthentication);
        Assert.False(unsupported.IsSelectable);
        Assert.False(state.ShowApiKeyControls);
        Assert.False(state.ShowLocalNoAuthControls);
        Assert.True(state.ShowUnsupportedModeMessage);
        Assert.Contains("not available in REST V1", state.UnsupportedModeText, StringComparison.Ordinal);
    }

    [Fact]
    public void WizardXaml_BindsModeAwareSecurityControlsAndCancelButton()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "PS7ScriptDesk.Shell", "Dialogs", "RestApiPublishWizardWindow.xaml"));

        Assert.Contains("x:Name=\"SecurityModeBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DisplayMemberPath=\"DisplayName\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedValuePath=\"Mode\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"JwtBearer\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"WindowsAuthentication\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ApiKeyEnvironmentVariableLabel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ApiKeyEnvironmentVariableBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PublishedOpenApiAuthBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LocalTestApiKeyLabel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LocalTestApiKeyBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UnsupportedSecurityModeText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CancelButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"CloseButton_Click\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void WizardXaml_ExposesBuildPublishWorkflowControls()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "PS7ScriptDesk.Shell", "Dialogs", "RestApiPublishWizardWindow.xaml"));

        Assert.Contains("Build / Publish", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GeneratedProjectPathBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TargetArchitectureBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GenerateProjectButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BuildApiButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PublishApiButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BuildPublishStatusText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BuildPublishOutputPathBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"OpenPublishFolderButton\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Step 1", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Step 2", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WizardConstructor_InitializesBuildPublishFirstDisplayState()
    {
        var code = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "PS7ScriptDesk.Shell", "Dialogs", "RestApiPublishWizardWindow.xaml.cs"));

        Assert.Contains("EnsureInitialPageSelection(Pages, SectionList)", code, StringComparison.Ordinal);
        Assert.Contains("TargetArchitectureBox.ItemsSource = CreatePublishTargetOptions()", code, StringComparison.Ordinal);
        Assert.Contains("TargetArchitectureBox.SelectedValue = ApiPublishTargetArchitecture.WinX64", code, StringComparison.Ordinal);
        Assert.Contains("RefreshBuildPublishStatus(\"Ready to generate the REST API project.\")", code, StringComparison.Ordinal);
    }

    [Fact]
    public void WizardCancelHandler_DoesNotSaveStartOrPublish()
    {
        var code = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "PS7ScriptDesk.Shell", "Dialogs", "RestApiPublishWizardWindow.xaml.cs"));
        var marker = "private void CloseButton_Click";
        var start = code.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = code.IndexOf("private void Window_Loaded", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var handler = code[start..end];

        Assert.Contains("DialogResult = false", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveConfiguration", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("StartLocalTestAsync", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiProjectGenerator", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("RunBuildPublishOperationAsync", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("_buildPublishService", handler, StringComparison.Ordinal);
    }

    private static ApiMetadataResult CreateMetadata()
        => new(
            parsedSuccessfully: true,
            sourcePath: null,
            syntaxErrors: [],
            functions:
            [
                new ApiFunctionMetadata(
                    "Get-Widget",
                    ApiFunctionConstructKind.Function,
                    isAdvancedFunction: true,
                    isTopLevel: true,
                    parentFunctionName: null,
                    isPublishable: true,
                    CreateExtent(),
                    [
                        new ApiParameterMetadata(
                            "Name",
                            "string",
                            hasExplicitType: true,
                            isSwitch: false,
                            isArray: false,
                            isNullable: false,
                            ApiParameterMandatoryState.Mandatory,
                            defaultValueExpression: null,
                            aliases: [],
                            validationAttributes: [],
                            CreateExtent(),
                            isMetadataComplete: true,
                            warnings: []),
                        new ApiParameterMetadata(
                            "IncludeInactive",
                            null,
                            hasExplicitType: false,
                            isSwitch: true,
                            isArray: false,
                            isNullable: null,
                            ApiParameterMandatoryState.NotMandatory,
                            defaultValueExpression: null,
                            aliases: [],
                            validationAttributes: [],
                            CreateExtent(),
                            isMetadataComplete: true,
                            warnings: [])
                    ],
                    new ApiCommentHelpMetadata(string.Empty, "Gets a widget.", null, null, null, isPartial: false),
                    declaredOutputTypes: [],
                    warnings: []),
                new ApiFunctionMetadata(
                    "Nested-Helper",
                    ApiFunctionConstructKind.Function,
                    isAdvancedFunction: false,
                    isTopLevel: false,
                    parentFunctionName: "Get-Widget",
                    isPublishable: false,
                    CreateExtent(),
                    parameters: [],
                    commentHelp: null,
                    declaredOutputTypes: [],
                    warnings: [])
            ],
            warnings: []);

    private static ApiPublishConfiguration CreateValidConfiguration(ApiSecurityMode securityMode)
    {
        var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath(@"C:\Scripts\Widget.ps1");
        configuration.Security.Mode = securityMode;
        configuration.Security.AllowNoAuthenticationForLocalTest = securityMode == ApiSecurityMode.LocalTestNoAuthentication;
        configuration.Security.ApiKeyEnvironmentVariableName = "PS7API_TEST_KEY";
        configuration.OpenApi.EnableSwaggerUiForLocalTest = true;
        configuration.Endpoints = RestApiPublishWizardService.CreateDefaultEndpoints(CreateMetadata(), "/api");
        return configuration;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PowerShellStudio repository root.");
    }

    private static ApiSourceExtent CreateExtent()
        => new(1, 1, 1, 1, 0, 0, string.Empty);

    private sealed class InMemoryConfigurationStore : IApiPublishConfigurationStore
    {
        private readonly ApiPublishConfiguration _configuration;

        public InMemoryConfigurationStore(ApiPublishConfiguration configuration)
        {
            _configuration = configuration;
        }

        public string? GetCompanionPath(string? sourceScriptPath) => sourceScriptPath is null ? null : Path.ChangeExtension(sourceScriptPath, ".ps7api.json");

        public bool ConfigurationExists(string sourceScriptPath) => true;

        public ApiPublishConfiguration Load(string sourceScriptPath) => _configuration;

        public void Save(string sourceScriptPath, ApiPublishConfiguration configuration)
        {
            SaveCount++;
        }

        public int SaveCount { get; private set; }
    }

}
