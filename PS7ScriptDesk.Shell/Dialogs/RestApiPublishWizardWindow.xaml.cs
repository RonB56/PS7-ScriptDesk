using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell.Help;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace PS7ScriptDesk.Shell.Dialogs;

public partial class RestApiPublishWizardWindow : Window, INotifyPropertyChanged
{
    private readonly ApiPublishWizardRequest _request;
    private readonly ApiMetadataResult _metadata;
    private readonly IApiPublishConfigurationStore _configurationStore;
    private readonly IApiLocalTestHostService _localTestHostService;
    private readonly IApiBuildPublishService _buildPublishService;
    private ApiPublishConfiguration _configuration;
    private RestApiEndpointRow? _selectedEndpointRow;
    private CancellationTokenSource? _buildPublishCancellation;
    private string _lastBuildPublishOutputPath = string.Empty;
    private bool _isInitializing;
    private bool _isLocalTestBusy;
    private bool _isBuildPublishBusy;

    public RestApiPublishWizardWindow(
        ApiPublishWizardRequest request,
        ApiMetadataResult metadata,
        ApiPublishConfiguration configuration,
        IApiPublishConfigurationStore configurationStore,
        IApiLocalTestHostService localTestHostService,
        IApiBuildPublishService buildPublishService)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _configurationStore = configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
        _localTestHostService = localTestHostService ?? throw new ArgumentNullException(nameof(localTestHostService));
        _buildPublishService = buildPublishService ?? throw new ArgumentNullException(nameof(buildPublishService));
        _localTestHostService.StatusChanged += LocalTestHostService_StatusChanged;
        _isInitializing = true;
        try
        {
            InitializeComponent();
            EnsureInitialPageSelection(Pages, SectionList);
            SourceText.Text = $"Source script: {_request.SourceScriptPath}";
            LoadConfiguration();
            RefreshMetadataSummary();
            EndpointGrid.SelectedIndex = Endpoints.Count > 0 ? 0 : -1;
            LocalTestEndpointBox.ItemsSource = Endpoints;
            LocalTestEndpointBox.SelectedIndex = Endpoints.Count > 0 ? 0 : -1;
            TargetArchitectureBox.ItemsSource = CreatePublishTargetOptions();
            TargetArchitectureBox.SelectedValue = ApiPublishTargetArchitecture.WinX64;
            RefreshBuildPublishStatus("Ready to generate the REST API project.");
            ApplyLocalTestStatus(_localTestHostService.CurrentStatus);
        }
        finally
        {
            _isInitializing = false;
        }

        SelectEndpoint(Endpoints.FirstOrDefault());
        UpdateNavigation();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RestApiEndpointRow> Endpoints { get; } = new();

    public ObservableCollection<RestApiParameterBindingRow> EndpointParameterBindings { get; } = new();

    public ApiPublishConfiguration? Configuration { get; private set; }

    public string EndpointSummaryText { get; private set; } = string.Empty;

    public string SelectedEndpointParameterText { get; private set; } = "Select an endpoint to review its parameter bindings.";

    internal static IReadOnlyList<ApiSecurityMode> SupportedRestV1SecurityModes { get; } =
    [
        ApiSecurityMode.LocalTestNoAuthentication,
        ApiSecurityMode.ApiKey
    ];

    internal static int ResolveInitialPageIndex(int selectedIndex, int itemCount)
        => selectedIndex >= 0 || itemCount <= 0 ? selectedIndex : 0;

    internal static bool IsRestV1SecurityModeSupported(ApiSecurityMode mode)
        => mode is ApiSecurityMode.LocalTestNoAuthentication or ApiSecurityMode.ApiKey;

    internal static IReadOnlyList<RestApiSecurityModeOption> CreateRestV1SecurityModeOptions(ApiSecurityMode currentMode)
    {
        var options = new List<RestApiSecurityModeOption>
        {
            new(ApiSecurityMode.LocalTestNoAuthentication, "Local test only, no authentication", IsSelectable: true),
            new(ApiSecurityMode.ApiKey, "API key", IsSelectable: true)
        };

        if (!IsRestV1SecurityModeSupported(currentMode))
        {
            options.Add(new RestApiSecurityModeOption(
                currentMode,
                $"{GetSecurityModeDisplayName(currentMode)} (Not available in REST V1)",
                IsSelectable: false));
        }

        return options;
    }

    internal static IReadOnlyList<ApiPublishTargetArchitectureOption> CreatePublishTargetOptions()
        =>
        [
            new(ApiPublishTargetArchitecture.WinX64, "win-x64"),
            new(ApiPublishTargetArchitecture.WinArm64, "win-arm64"),
            new(ApiPublishTargetArchitecture.Both, "Both")
        ];

    internal static RestApiSecurityModeUiState CreateSecurityModeUiState(ApiSecurityMode mode)
        => mode switch
        {
            ApiSecurityMode.LocalTestNoAuthentication => new RestApiSecurityModeUiState(
                ShowApiKeyControls: false,
                ShowLocalNoAuthControls: true,
                ShowUnsupportedModeMessage: false,
                GuidanceText: "Local no-auth mode is for loopback local testing only. Use API key mode before publishing an API beyond this machine.",
                UnsupportedModeText: string.Empty),
            ApiSecurityMode.ApiKey => new RestApiSecurityModeUiState(
                ShowApiKeyControls: true,
                ShowLocalNoAuthControls: false,
                ShowUnsupportedModeMessage: false,
                GuidanceText: "Published APIs read keys from the named environment variable. Use HTTPS and an explicit hosting URL before exposing an API beyond this machine.",
                UnsupportedModeText: string.Empty),
            _ => new RestApiSecurityModeUiState(
                ShowApiKeyControls: false,
                ShowLocalNoAuthControls: false,
                ShowUnsupportedModeMessage: true,
                GuidanceText: "Choose a supported REST V1 security mode before saving or publishing this API.",
                UnsupportedModeText: CreateUnsupportedSecurityModeMessage(mode))
        };

    private void LoadConfiguration()
    {
        ApiTitleBox.Text = _configuration.Api.Title;
        ApiVersionBox.Text = _configuration.Api.Version;
        RoutePrefixBox.Text = _configuration.Api.DefaultRoutePrefix;
        DescriptionBox.Text = _configuration.Api.Description;
        ApiKeyEnvironmentVariableBox.Text = string.IsNullOrWhiteSpace(_configuration.Security.ApiKeyEnvironmentVariableName)
            ? "PS7API_API_KEY"
            : _configuration.Security.ApiKeyEnvironmentVariableName;
        AllowNoAuthBox.IsChecked = _configuration.Security.AllowNoAuthenticationForLocalTest;
        SwaggerBox.IsChecked = _configuration.OpenApi.EnableSwaggerUiForLocalTest;
        PublishedOpenApiAuthBox.IsChecked = _configuration.OpenApi.RequireAuthenticationForPublishedSwagger;
        LoadSecurityModeOptions(_configuration.Security.Mode);
        UpdateSecurityModeUi();
        if (!IsRestV1SecurityModeSupported(_configuration.Security.Mode))
        {
            ValidationText.Text = $"API042: {CreateUnsupportedSecurityModeMessage(_configuration.Security.Mode)}";
        }

        RunspaceMinBox.Text = _configuration.Runtime.RunspacePoolMinimum.ToString(CultureInfo.InvariantCulture);
        RunspaceMaxBox.Text = _configuration.Runtime.RunspacePoolMaximum.ToString(CultureInfo.InvariantCulture);
        ConcurrencyBox.Text = _configuration.Runtime.MaximumConcurrentExecutions.ToString(CultureInfo.InvariantCulture);
        QueueLimitBox.Text = _configuration.Runtime.QueueLimit.ToString(CultureInfo.InvariantCulture);
        TimeoutBox.Text = _configuration.Runtime.DefaultInvocationTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
        ResponseByteLimitBox.Text = _configuration.Runtime.ResponseByteLimit.ToString(CultureInfo.InvariantCulture);
        OutputDirectoryBox.Text = _configuration.Output.OutputDirectory;
        PreserveGeneratedProjectBox.IsChecked = _configuration.Output.PreserveGeneratedProject;

        Endpoints.Clear();
        foreach (var endpoint in _configuration.Endpoints)
        {
            Endpoints.Add(RestApiEndpointRow.FromConfiguration(endpoint));
        }
    }

    private bool TryReadConfiguration()
    {
        PersistSelectedParameterRows();
        ValidationText.Text = string.Empty;
        _configuration.Api.Title = ApiTitleBox.Text.Trim();
        _configuration.OpenApi.Title = _configuration.Api.Title;
        _configuration.Api.Version = ApiVersionBox.Text.Trim();
        _configuration.OpenApi.Version = _configuration.Api.Version;
        _configuration.Api.DefaultRoutePrefix = NormalizeRoutePrefix(RoutePrefixBox.Text);
        _configuration.Api.Description = DescriptionBox.Text.Trim();
        _configuration.OpenApi.Description = _configuration.Api.Description;
        if (!TryReadSelectedSecurityMode(out var securityMode))
        {
            ValidationText.Text = "Select a supported REST V1 security mode.";
            return false;
        }

        _configuration.Security.Mode = securityMode;
        _configuration.Security.ApiKeyEnvironmentVariableName = string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariableBox.Text)
            ? "PS7API_API_KEY"
            : ApiKeyEnvironmentVariableBox.Text.Trim();
        _configuration.Security.AllowNoAuthenticationForLocalTest = securityMode == ApiSecurityMode.LocalTestNoAuthentication && AllowNoAuthBox.IsChecked == true;
        _configuration.OpenApi.EnableSwaggerUiForLocalTest = SwaggerBox.IsChecked == true;
        _configuration.OpenApi.RequireAuthenticationForPublishedSwagger = securityMode == ApiSecurityMode.ApiKey && PublishedOpenApiAuthBox.IsChecked == true;
        _configuration.Output.OutputDirectory = OutputDirectoryBox.Text.Trim();
        _configuration.Output.PreserveGeneratedProject = PreserveGeneratedProjectBox.IsChecked == true;

        if (!TryReadPositiveInt(RunspaceMinBox, "Runspace minimum", out var runspaceMinimum) ||
            !TryReadPositiveInt(RunspaceMaxBox, "Runspace maximum", out var runspaceMaximum) ||
            !TryReadPositiveInt(ConcurrencyBox, "Maximum concurrent requests", out var concurrency) ||
            !TryReadPositiveInt(QueueLimitBox, "Queue limit", out var queueLimit) ||
            !TryReadPositiveInt(TimeoutBox, "Request timeout seconds", out var timeoutSeconds) ||
            !TryReadPositiveInt(ResponseByteLimitBox, "Response byte limit", out var responseByteLimit))
        {
            return false;
        }

        _configuration.Runtime.RunspacePoolMinimum = runspaceMinimum;
        _configuration.Runtime.RunspacePoolMaximum = runspaceMaximum;
        _configuration.Runtime.MaximumConcurrentExecutions = concurrency;
        _configuration.Runtime.QueueLimit = queueLimit;
        _configuration.Runtime.DefaultInvocationTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        _configuration.Runtime.ResponseByteLimit = responseByteLimit;
        _configuration.Endpoints = Endpoints.Select(row => row.ToConfiguration()).ToList();
        _configuration.SourceScript = Path.GetFileName(_request.SourceScriptPath);

        var validation = new ApiPublishConfigurationValidator().Validate(_configuration, _metadata);
        if (!validation.IsValid)
        {
            ValidationText.Text = string.Join(Environment.NewLine, validation.Errors.Select(error => $"{error.Code}: {error.Message}"));
            return false;
        }

        if (validation.Warnings.Count > 0)
        {
            ValidationText.Text = string.Join(Environment.NewLine, validation.Warnings.Select(warning => $"{warning.Code}: {warning.Message}"));
        }

        RefreshMetadataSummary();
        RefreshLocalTestPreview();
        return true;
    }

    private bool SaveConfiguration()
    {
        if (!TryReadConfiguration())
        {
            return false;
        }

        try
        {
            _configurationStore.Save(_request.SourceScriptPath, _configuration);
            Configuration = _configuration;
            DeveloperDiagnostics.LogInfo(
                "RestApiPublish",
                "REST API companion configuration saved.",
                new Dictionary<string, object?>
                {
                    ["sourceFileName"] = Path.GetFileName(_request.SourceScriptPath),
                    ["endpointCount"] = _configuration.Endpoints.Count
                });
            LocalTestStatusText.Text = $"Configuration saved: {_configurationStore.GetCompanionPath(_request.SourceScriptPath)}";
            return true;
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException(
                "RestApiPublish",
                ex,
                "REST API companion configuration save failed.",
                new Dictionary<string, object?> { ["sourceFileName"] = Path.GetFileName(_request.SourceScriptPath) });
            ValidationText.Text = ex.Message;
            return false;
        }
    }

    private async Task StartLocalTestAsync(bool restart)
    {
        if (_isLocalTestBusy || !SaveConfiguration())
        {
            return;
        }

        SetLocalTestBusy(true);
        try
        {
            var request = new ApiLocalTestHostRequest(
                _request.SourceScriptPath,
                _configuration,
                string.IsNullOrWhiteSpace(_configuration.Output.OutputDirectory) ? null : _configuration.Output.OutputDirectory,
                CreateProjectName(_configuration.Api.Title),
                overwriteExistingGeneratedProject: true);
            var result = restart
                ? await _localTestHostService.RestartAsync(request).ConfigureAwait(true)
                : await _localTestHostService.StartAsync(request).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                ValidationText.Text = string.IsNullOrWhiteSpace(result.DetailedLog)
                    ? result.SummaryMessage
                    : $"{result.SummaryMessage}{Environment.NewLine}{result.DetailedLog}";
            }
        }
        catch (OperationCanceledException)
        {
            ValidationText.Text = "Local API test startup was canceled.";
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException(
                "RestApiPublish",
                ex,
                "REST API local test failed from wizard.",
                new Dictionary<string, object?> { ["sourceFileName"] = Path.GetFileName(_request.SourceScriptPath) });
            ValidationText.Text = ex.Message;
        }
        finally
        {
            SetLocalTestBusy(false);
        }
    }

    private async Task StopLocalTestAsync()
    {
        if (_isLocalTestBusy)
        {
            return;
        }

        SetLocalTestBusy(true);
        try
        {
            await _localTestHostService.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException(
                "RestApiPublish",
                ex,
                "REST API local test stop failed from wizard.",
                new Dictionary<string, object?> { ["sourceFileName"] = Path.GetFileName(_request.SourceScriptPath) });
            ValidationText.Text = ex.Message;
        }
        finally
        {
            SetLocalTestBusy(false);
        }
    }

    private void ApplyLocalTestStatus(ApiLocalTestHostStatus status)
    {
        LocalTestStatusText.Text = status.StatusMessage;
        BaseUrlBox.Text = status.BaseUrl?.ToString() ?? string.Empty;
        OpenApiUrlBox.Text = status.OpenApiUrl?.ToString() ?? string.Empty;
        SwaggerUrlBox.Text = status.SwaggerUrl?.ToString() ?? string.Empty;
        if (status.Logs.Count > 0 && string.IsNullOrWhiteSpace(LocalTestResponseBox.Text))
        {
            LocalTestResponseBox.Text = string.Join(Environment.NewLine, status.Logs);
        }

        StopTestButton.IsEnabled = !_isLocalTestBusy && status.State is ApiLocalTestHostState.Running or ApiLocalTestHostState.Starting;
        RestartTestButton.IsEnabled = !_isLocalTestBusy && status.State == ApiLocalTestHostState.Running;
        StartTestButton.IsEnabled = !_isLocalTestBusy && status.State != ApiLocalTestHostState.Running;
        OpenOpenApiButton.IsEnabled = !_isLocalTestBusy && status.State == ApiLocalTestHostState.Running && status.OpenApiUrl is not null;
        OpenViewerButton.IsEnabled = !_isLocalTestBusy && status.State == ApiLocalTestHostState.Running && status.SwaggerUrl is not null;
        ExecuteRequestButton.IsEnabled = !_isLocalTestBusy && status.State == ApiLocalTestHostState.Running && _selectedEndpointRow is not null;
        RefreshLocalTestPreview();
    }

    private void RefreshMetadataSummary()
    {
        var publishableCount = _metadata.Functions.Count(function => function.IsPublishable);
        EndpointSummaryText = $"{Endpoints.Count(endpoint => endpoint.IsEnabled)} enabled endpoint(s), {publishableCount} publishable function(s) detected.";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EndpointSummaryText)));
    }

    private void SetLocalTestBusy(bool isBusy)
    {
        _isLocalTestBusy = isBusy;
        SaveButton.IsEnabled = !isBusy && !_isBuildPublishBusy;
        BackButton.IsEnabled = !isBusy && !_isBuildPublishBusy && Pages.SelectedIndex > 0;
        NextButton.IsEnabled = !isBusy && !_isBuildPublishBusy;
        GenerateProjectButton.IsEnabled = !isBusy && !_isBuildPublishBusy;
        BuildApiButton.IsEnabled = !isBusy && !_isBuildPublishBusy;
        PublishApiButton.IsEnabled = !isBusy && !_isBuildPublishBusy;
        TargetArchitectureBox.IsEnabled = !isBusy && !_isBuildPublishBusy;
        ApplyLocalTestStatus(_localTestHostService.CurrentStatus);
    }

    private void LocalTestHostService_StatusChanged(object? sender, ApiLocalTestHostStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ApplyLocalTestStatus(status));
            return;
        }

        ApplyLocalTestStatus(status);
    }

    private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || SectionList.SelectedIndex < 0)
        {
            return;
        }

        Pages.SelectedIndex = SectionList.SelectedIndex;
        UpdateNavigation();
    }

    private void EndpointGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        PersistSelectedParameterRows();
        SelectEndpoint(EndpointGrid.SelectedItem as RestApiEndpointRow);
        if (!ReferenceEquals(LocalTestEndpointBox.SelectedItem, _selectedEndpointRow))
        {
            LocalTestEndpointBox.SelectedItem = _selectedEndpointRow;
        }
    }

    private void LocalTestEndpointBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        PersistSelectedParameterRows();
        SelectEndpoint(LocalTestEndpointBox.SelectedItem as RestApiEndpointRow);
        if (!ReferenceEquals(EndpointGrid.SelectedItem, _selectedEndpointRow))
        {
            EndpointGrid.SelectedItem = _selectedEndpointRow;
        }
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (Pages.SelectedIndex >= Pages.Items.Count - 1)
        {
            if (SaveConfiguration())
            {
                DialogResult = true;
            }

            return;
        }

        Pages.SelectedIndex++;
        SectionList.SelectedIndex = Pages.SelectedIndex;
        UpdateNavigation();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Pages.SelectedIndex <= 0)
        {
            return;
        }

        Pages.SelectedIndex--;
        SectionList.SelectedIndex = Pages.SelectedIndex;
        UpdateNavigation();
    }

    private void UpdateNavigation()
    {
        BackButton.IsEnabled = !_isLocalTestBusy && !_isBuildPublishBusy && Pages.SelectedIndex > 0;
        NextButton.IsEnabled = !_isLocalTestBusy && !_isBuildPublishBusy;
        NextButton.Content = Pages.SelectedIndex == Pages.Items.Count - 1 ? "Save and Close" : "Next";
    }

    private void SelectEndpoint(RestApiEndpointRow? row)
    {
        _selectedEndpointRow = row;
        EndpointParameterBindings.Clear();
        if (row is null)
        {
            SelectedEndpointParameterText = "Select an endpoint to review its parameter bindings.";
        }
        else
        {
            var function = _metadata.Functions.FirstOrDefault(function =>
                string.Equals(function.Name, row.FunctionName, StringComparison.OrdinalIgnoreCase));
            foreach (var binding in row.ParameterBindings)
            {
                var parameter = function?.Parameters.FirstOrDefault(parameter =>
                    string.Equals(parameter.Name, binding.PowerShellParameterName, StringComparison.OrdinalIgnoreCase));
                EndpointParameterBindings.Add(RestApiParameterBindingRow.FromConfiguration(binding, parameter));
            }

            SelectedEndpointParameterText = EndpointParameterBindings.Count == 0
                ? $"{row.FunctionName} has no configured parameter bindings."
                : $"{row.FunctionName} parameter bindings.";
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedEndpointParameterText)));
        RefreshLocalTestPreview();
    }

    private void PersistSelectedParameterRows()
    {
        if (_selectedEndpointRow is null)
        {
            return;
        }

        _selectedEndpointRow.ParameterBindings = EndpointParameterBindings.Select(row => row.ToConfiguration()).ToList();
    }

    private void RefreshLocalTestPreview()
    {
        if (TestRequestUrlBox is null || TestRequestBodyBox is null)
        {
            return;
        }

        if (!TryCreateLocalTestRequest(out var preview, out var body, out _))
        {
            TestRequestUrlBox.Text = string.Empty;
            TestRequestBodyBox.Text = string.Empty;
            return;
        }

        TestRequestUrlBox.Text = preview.ToString();
        TestRequestBodyBox.Text = body ?? string.Empty;
    }

    private void ConfigurationEdited(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || !IsLoaded)
        {
            return;
        }

        UpdateSecurityModeUi();
        RefreshMetadataSummary();
    }

    private void BrowseOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose the generated REST API project folder",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            OutputDirectoryBox.Text = dialog.SelectedPath;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveConfiguration();

    private async void GenerateProjectButton_Click(object sender, RoutedEventArgs e)
        => await RunBuildPublishOperationAsync("Generate Project", _buildPublishService.GenerateProjectAsync);

    private async void BuildApiButton_Click(object sender, RoutedEventArgs e)
        => await RunBuildPublishOperationAsync("Build API", _buildPublishService.BuildAsync);

    private async void PublishApiButton_Click(object sender, RoutedEventArgs e)
        => await RunBuildPublishOperationAsync("Publish API", _buildPublishService.PublishAsync);

    private async void StartTestButton_Click(object sender, RoutedEventArgs e)
        => await StartLocalTestAsync(restart: false);

    private async void RestartTestButton_Click(object sender, RoutedEventArgs e)
        => await StartLocalTestAsync(restart: true);

    private async void StopTestButton_Click(object sender, RoutedEventArgs e)
        => await StopLocalTestAsync();

    private void OpenOpenApiButton_Click(object sender, RoutedEventArgs e)
        => OpenExternalUri(_localTestHostService.CurrentStatus.OpenApiUrl, "OpenAPI JSON");

    private void OpenViewerButton_Click(object sender, RoutedEventArgs e)
        => OpenExternalUri(_localTestHostService.CurrentStatus.SwaggerUrl, "OpenAPI Viewer");

    private void OpenPublishFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastBuildPublishOutputPath) || !Directory.Exists(_lastBuildPublishOutputPath))
        {
            RefreshBuildPublishStatus("The REST API output folder is not available yet.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_lastBuildPublishOutputPath) { UseShellExecute = true });
            DeveloperDiagnostics.LogUserAction(
                "RestApiPublish",
                "OpenRestApiPublishFolder",
                "Opening REST API build/publish output folder.",
                new Dictionary<string, object?> { ["outputDirectory"] = _lastBuildPublishOutputPath });
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException(
                "RestApiPublish",
                ex,
                "REST API output folder could not be opened.",
                new Dictionary<string, object?> { ["outputDirectory"] = _lastBuildPublishOutputPath });
            RefreshBuildPublishStatus($"Output folder could not be opened: {ex.Message}");
        }
    }

    private async void ExecuteRequestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLocalTestBusy || !TryCreateLocalTestRequest(out var requestUri, out var body, out var method))
        {
            return;
        }

        SetLocalTestBusy(true);
        try
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(method, requestUri);
            if (_configuration.Security.Mode == ApiSecurityMode.ApiKey &&
                _selectedEndpointRow?.RequiresAuthentication == true &&
                !string.IsNullOrWhiteSpace(LocalTestApiKeyBox.Password))
            {
                request.Headers.TryAddWithoutValidation("X-API-Key", LocalTestApiKeyBox.Password);
            }

            foreach (var header in EndpointParameterBindings.Where(row =>
                         row.Source == ApiParameterSource.Header &&
                         !string.IsNullOrWhiteSpace(row.Name) &&
                         !string.IsNullOrWhiteSpace(row.TestValue)))
            {
                request.Headers.TryAddWithoutValidation(header.Name, header.TestValue);
            }

            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request).ConfigureAwait(true);
            var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            LocalTestResponseBox.Text = $"{(int)response.StatusCode} {response.ReasonPhrase}{Environment.NewLine}{FormatJsonOrText(responseText)}";
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException(
                "RestApiPublish",
                ex,
                "REST API local test request failed from wizard.",
                new Dictionary<string, object?> { ["sourceFileName"] = Path.GetFileName(_request.SourceScriptPath) });
            LocalTestResponseBox.Text = ex.Message;
        }
        finally
        {
            SetLocalTestBusy(false);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CancelBuildPublishOperation();
        DialogResult = false;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => ContextHelp.ValidateWindowTopics(this);

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        CancelBuildPublishOperation();
        _localTestHostService.StatusChanged -= LocalTestHostService_StatusChanged;
        try
        {
            await _localTestHostService.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException("RestApiPublish", ex, "REST API local test host disposal failed while closing wizard.");
        }
    }

    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.F1)
        {
            e.Handled = true;
            ContextHelp.OpenForFocusedElement(this);
        }
    }

    private void OpenExternalUri(Uri? uri, string label)
    {
        if (uri is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            DeveloperDiagnostics.LogInfo(
                "RestApiPublish",
                $"REST API {label} opened in the default browser.",
                new Dictionary<string, object?> { ["uri"] = uri.ToString() });
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException(
                "RestApiPublish",
                ex,
                $"REST API {label} could not be opened.",
                new Dictionary<string, object?> { ["uri"] = uri.ToString() });
            ValidationText.Text = $"{label} could not be opened: {ex.Message}";
        }
    }

    private async Task RunBuildPublishOperationAsync(
        string actionName,
        Func<ApiBuildPublishRequest, CancellationToken, IProgress<ApiBuildPublishProgressUpdate>?, Task<ApiBuildPublishResult>> operation)
    {
        if (_isBuildPublishBusy || _isLocalTestBusy)
        {
            return;
        }

        if (!SaveConfiguration())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _buildPublishCancellation = cancellation;
        SetBuildPublishBusy(true);
        RefreshBuildPublishStatus($"{actionName} started.");
        var request = CreateBuildPublishRequest();
        var progress = new Progress<ApiBuildPublishProgressUpdate>(update => RefreshBuildPublishStatus(update.StatusMessage));

        try
        {
            var result = await Task.Run(
                () => operation(request, cancellation.Token, progress),
                cancellation.Token).ConfigureAwait(true);
            ApplyBuildPublishResult(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            RefreshBuildPublishStatus($"{actionName} was cancelled.");
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException(
                "RestApiPublish",
                ex,
                "REST API build/publish action failed from wizard.",
                new Dictionary<string, object?>
                {
                    ["actionName"] = actionName,
                    ["sourceFileName"] = Path.GetFileName(_request.SourceScriptPath)
                });
            RefreshBuildPublishStatus($"{actionName} failed: {DeveloperDiagnostics.SanitizePreview(ex.Message, 4096)}");
        }
        finally
        {
            if (ReferenceEquals(_buildPublishCancellation, cancellation))
            {
                _buildPublishCancellation = null;
            }

            SetBuildPublishBusy(false);
        }
    }

    private ApiBuildPublishRequest CreateBuildPublishRequest()
    {
        var projectDirectory = EnsureBuildPublishProjectDirectory();
        return new ApiBuildPublishRequest(
            _request.SourceScriptPath,
            _configuration,
            projectDirectory,
            CreateProjectName(_configuration.Api.Title),
            ResolveSelectedTargetArchitecture(),
            overwriteExistingGeneratedProject: true);
    }

    private string EnsureBuildPublishProjectDirectory()
    {
        var configured = OutputDirectoryBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var projectName = CreateProjectName(string.IsNullOrWhiteSpace(ApiTitleBox.Text) ? _configuration.Api.Title : ApiTitleBox.Text);
        var defaultDirectory = Path.Combine(ApplicationBranding.LocalApplicationDataRoot, "RestApiProjects", projectName);
        OutputDirectoryBox.Text = defaultDirectory;
        return defaultDirectory;
    }

    private ApiPublishTargetArchitecture ResolveSelectedTargetArchitecture()
        => TargetArchitectureBox.SelectedValue is ApiPublishTargetArchitecture architecture
            ? architecture
            : ApiPublishTargetArchitecture.WinX64;

    private void ApplyBuildPublishResult(ApiBuildPublishResult result)
    {
        GeneratedProjectPathBox.Text = result.ProjectDirectory ?? string.Empty;
        _lastBuildPublishOutputPath = result.OutputDirectory;
        BuildPublishOutputPathBox.Text = result.OutputDirectory;
        OpenPublishFolderButton.IsEnabled = result.Succeeded &&
                                            !string.IsNullOrWhiteSpace(result.OutputDirectory) &&
                                            Directory.Exists(result.OutputDirectory);

        var summary = result.SummaryMessage;
        if (!string.IsNullOrWhiteSpace(result.DetailedLog))
        {
            summary = $"{summary}{Environment.NewLine}{DeveloperDiagnostics.SanitizePreview(result.DetailedLog, 4096)}";
        }

        RefreshBuildPublishStatus(summary);
        if (!result.Succeeded)
        {
            ValidationText.Text = summary;
        }
    }

    private void SetBuildPublishBusy(bool isBusy)
    {
        _isBuildPublishBusy = isBusy;
        SaveButton.IsEnabled = !_isLocalTestBusy && !isBusy;
        BackButton.IsEnabled = !_isLocalTestBusy && !isBusy && Pages.SelectedIndex > 0;
        NextButton.IsEnabled = !_isLocalTestBusy && !isBusy;
        GenerateProjectButton.IsEnabled = !_isLocalTestBusy && !isBusy;
        BuildApiButton.IsEnabled = !_isLocalTestBusy && !isBusy;
        PublishApiButton.IsEnabled = !_isLocalTestBusy && !isBusy;
        TargetArchitectureBox.IsEnabled = !_isLocalTestBusy && !isBusy;
        UpdateNavigation();
    }

    private void RefreshBuildPublishStatus(string status)
    {
        if (BuildPublishStatusText is not null)
        {
            BuildPublishStatusText.Text = status;
        }
    }

    private void CancelBuildPublishOperation()
    {
        try
        {
            _buildPublishCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool TryCreateLocalTestRequest(out Uri requestUri, out string? body, out HttpMethod method)
    {
        requestUri = new Uri("http://127.0.0.1/");
        body = null;
        method = HttpMethod.Get;
        var status = _localTestHostService.CurrentStatus;
        if (status.BaseUrl is null || _selectedEndpointRow is null)
        {
            return false;
        }

        var row = _selectedEndpointRow;
        method = string.Equals(row.Method, ApiHttpMethod.Post.ToString(), StringComparison.OrdinalIgnoreCase)
            ? HttpMethod.Post
            : HttpMethod.Get;
        var route = string.IsNullOrWhiteSpace(row.RouteTemplate) ? "/" : row.RouteTemplate;
        var query = new List<string>();
        var bodyValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in EndpointParameterBindings)
        {
            if (parameter.Source == ApiParameterSource.ServerDefined)
            {
                continue;
            }

            var apiName = string.IsNullOrWhiteSpace(parameter.Name)
                ? parameter.PowerShellParameterName
                : parameter.Name;
            var value = parameter.TestValue ?? string.Empty;
            if (parameter.Source == ApiParameterSource.Route)
            {
                route = route.Replace("{" + apiName + "}", Uri.EscapeDataString(value), StringComparison.OrdinalIgnoreCase);
            }
            else if (parameter.Source == ApiParameterSource.Query)
            {
                query.Add($"{Uri.EscapeDataString(apiName)}={Uri.EscapeDataString(value)}");
            }
            else if (parameter.Source == ApiParameterSource.Body)
            {
                bodyValues[apiName] = ConvertTestValue(value, parameter.TypeName);
            }
        }

        var relative = route.StartsWith("/", StringComparison.Ordinal) ? route[1..] : route;
        if (query.Count > 0)
        {
            relative += "?" + string.Join("&", query);
        }

        requestUri = new Uri(status.BaseUrl, relative);
        if (bodyValues.Count > 0)
        {
            body = JsonSerializer.Serialize(bodyValues, new JsonSerializerOptions { WriteIndented = true });
            if (method == HttpMethod.Get)
            {
                method = HttpMethod.Post;
            }
        }

        return true;
    }

    private static object? ConvertTestValue(string? value, string? typeName)
    {
        var text = value ?? string.Empty;
        var normalizedType = string.IsNullOrWhiteSpace(typeName) ? "string" : typeName.Trim();
        if ((string.Equals(normalizedType, "int", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(normalizedType, "int32", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(normalizedType, "System.Int32", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        if ((string.Equals(normalizedType, "bool", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(normalizedType, "boolean", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(normalizedType, "System.Boolean", StringComparison.OrdinalIgnoreCase)) &&
            bool.TryParse(text, out var boolean))
        {
            return boolean;
        }

        return text;
    }

    private static string FormatJsonOrText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static void EnsureInitialPageSelection(System.Windows.Controls.TabControl pages, System.Windows.Controls.ListBox sectionList)
    {
        var pageIndex = ResolveInitialPageIndex(pages.SelectedIndex, pages.Items.Count);
        if (pageIndex >= 0)
        {
            pages.SelectedIndex = pageIndex;
            sectionList.SelectedIndex = pageIndex;
        }
    }

    private static bool TryReadPositiveInt(WpfTextBox textBox, string label, out int value)
    {
        if (int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0)
        {
            return true;
        }

        value = 0;
        textBox.Focus();
        var window = Window.GetWindow(textBox) as RestApiPublishWizardWindow;
        if (window is not null)
        {
            window.ValidationText.Text = $"{label} must be a positive whole number.";
        }

        return false;
    }

    private static string NormalizeRoutePrefix(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "/api" : value.Trim();
        return "/" + trimmed.Trim('/');
    }

    private static string CreateProjectName(string title)
    {
        var cleaned = new string((string.IsNullOrWhiteSpace(title) ? "PowerShellApi" : title)
            .Where(character => char.IsLetterOrDigit(character))
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "PowerShellApi" : cleaned;
    }

    private void LoadSecurityModeOptions(ApiSecurityMode currentMode)
    {
        SecurityModeBox.ItemsSource = CreateRestV1SecurityModeOptions(currentMode);
        SecurityModeBox.SelectedValue = currentMode;
        if (SecurityModeBox.SelectedItem is null)
        {
            SecurityModeBox.SelectedValue = ApiSecurityMode.LocalTestNoAuthentication;
        }
    }

    private void UpdateSecurityModeUi()
    {
        if (SecurityModeBox is null)
        {
            return;
        }

        var mode = TryReadSelectedSecurityMode(out var selectedMode)
            ? selectedMode
            : ApiSecurityMode.LocalTestNoAuthentication;
        var state = CreateSecurityModeUiState(mode);
        var apiKeyVisibility = state.ShowApiKeyControls ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyEnvironmentVariableLabel.Visibility = apiKeyVisibility;
        ApiKeyEnvironmentVariableBox.Visibility = apiKeyVisibility;
        ApiKeyEnvironmentVariableBox.IsEnabled = state.ShowApiKeyControls;
        PublishedOpenApiAuthBox.Visibility = apiKeyVisibility;
        PublishedOpenApiAuthBox.IsEnabled = state.ShowApiKeyControls;
        LocalTestApiKeyLabel.Visibility = apiKeyVisibility;
        LocalTestApiKeyBox.Visibility = apiKeyVisibility;
        LocalTestApiKeyBox.IsEnabled = state.ShowApiKeyControls;

        AllowNoAuthBox.Visibility = state.ShowLocalNoAuthControls ? Visibility.Visible : Visibility.Collapsed;
        AllowNoAuthBox.IsEnabled = state.ShowLocalNoAuthControls;
        UnsupportedSecurityModeText.Visibility = state.ShowUnsupportedModeMessage ? Visibility.Visible : Visibility.Collapsed;
        UnsupportedSecurityModeText.Text = state.UnsupportedModeText;
        SecurityGuidanceText.Text = state.GuidanceText;

        if (state.ShowUnsupportedModeMessage)
        {
            ValidationText.Text = $"API042: {state.UnsupportedModeText}";
        }
        else if (ValidationText.Text.StartsWith("API042:", StringComparison.Ordinal))
        {
            ValidationText.Text = string.Empty;
        }
    }

    private bool TryReadSelectedSecurityMode(out ApiSecurityMode mode)
    {
        if (SecurityModeBox.SelectedItem is RestApiSecurityModeOption option)
        {
            mode = option.Mode;
            return true;
        }

        if (SecurityModeBox.SelectedValue is ApiSecurityMode selectedMode)
        {
            mode = selectedMode;
            return true;
        }

        mode = default;
        return false;
    }

    private static string CreateUnsupportedSecurityModeMessage(ApiSecurityMode mode)
        => $"{GetSecurityModeDisplayName(mode)} authentication is not available in REST V1. Choose Local test only or API key before saving or publishing.";

    private static string GetSecurityModeDisplayName(ApiSecurityMode mode)
        => mode switch
        {
            ApiSecurityMode.LocalTestNoAuthentication => "Local test only, no authentication",
            ApiSecurityMode.ApiKey => "API key",
            ApiSecurityMode.JwtBearer => "JWT bearer",
            ApiSecurityMode.WindowsAuthentication => "Windows authentication",
            _ => mode.ToString()
        };
}

public sealed record RestApiSecurityModeOption(ApiSecurityMode Mode, string DisplayName, bool IsSelectable);

public sealed record ApiPublishTargetArchitectureOption(ApiPublishTargetArchitecture Architecture, string DisplayName);

public sealed record RestApiSecurityModeUiState(
    bool ShowApiKeyControls,
    bool ShowLocalNoAuthControls,
    bool ShowUnsupportedModeMessage,
    string GuidanceText,
    string UnsupportedModeText);

public sealed class RestApiParameterBindingRow
{
    public string PowerShellParameterName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ApiParameterSource Source { get; set; } = ApiParameterSource.Query;
    public ApiRequiredBehavior Required { get; set; } = ApiRequiredBehavior.InheritFromPowerShell;
    public string TypeName { get; set; } = string.Empty;
    public string ValidationSummary { get; set; } = string.Empty;
    public string TestValue { get; set; } = string.Empty;
    public ApiServerDefinedValue? ServerValue { get; set; }
    public bool IsSecretSensitive { get; set; }
    public ApiArrayBindingBehavior ArrayBinding { get; set; } = ApiArrayBindingBehavior.RepeatedValues;

    public static RestApiParameterBindingRow FromConfiguration(
        ApiParameterBindingConfiguration binding,
        ApiParameterMetadata? metadata)
        => new()
        {
            PowerShellParameterName = binding.PowerShellParameterName,
            Name = binding.Name,
            Source = binding.Source,
            Required = binding.Required,
            TypeName = string.IsNullOrWhiteSpace(binding.TypeName)
                ? metadata?.DeclaredTypeName ?? (metadata?.IsSwitch == true ? "bool" : "string")
                : binding.TypeName,
            ValidationSummary = BuildValidationSummary(metadata),
            ServerValue = binding.ServerValue,
            IsSecretSensitive = binding.IsSecretSensitive,
            ArrayBinding = binding.ArrayBinding
        };

    public ApiParameterBindingConfiguration ToConfiguration()
        => new()
        {
            PowerShellParameterName = PowerShellParameterName,
            Source = Source,
            Name = Name,
            Required = Required,
            ServerValue = ServerValue,
            IsSecretSensitive = IsSecretSensitive,
            ArrayBinding = ArrayBinding,
            TypeName = TypeName
        };

    private static string BuildValidationSummary(ApiParameterMetadata? metadata)
    {
        if (metadata is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (metadata.MandatoryState == ApiParameterMandatoryState.Mandatory)
        {
            parts.Add("Mandatory");
        }

        if (metadata.ValidationAttributes.Count > 0)
        {
            parts.AddRange(metadata.ValidationAttributes.Select(attribute => attribute.Name));
        }

        return string.Join(", ", parts);
    }
}

public sealed class RestApiEndpointRow
{
    public bool IsEnabled { get; set; }
    public string FunctionName { get; set; } = string.Empty;
    public string Method { get; set; } = ApiHttpMethod.Get.ToString();
    public string RouteTemplate { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool RequiresAuthentication { get; set; }
    public List<ApiParameterBindingConfiguration> ParameterBindings { get; set; } = new();

    public static RestApiEndpointRow FromConfiguration(ApiEndpointConfiguration endpoint)
        => new()
        {
            IsEnabled = endpoint.IsEnabled,
            FunctionName = endpoint.PowerShellFunctionName,
            Method = endpoint.Rest.Method.ToString(),
            RouteTemplate = endpoint.Rest.RouteTemplate,
            OperationId = endpoint.Rest.OperationId,
            Description = endpoint.Description,
            RequiresAuthentication = endpoint.RequiresAuthentication,
            ParameterBindings = endpoint.ParameterBindings
                .Select(binding => new ApiParameterBindingConfiguration
                {
                    PowerShellParameterName = binding.PowerShellParameterName,
                    Source = binding.Source,
                    Name = binding.Name,
                    Required = binding.Required,
                    ServerValue = binding.ServerValue,
                    IsSecretSensitive = binding.IsSecretSensitive,
                    ArrayBinding = binding.ArrayBinding,
                    TypeName = binding.TypeName
                })
                .ToList()
        };

    public ApiEndpointConfiguration ToConfiguration()
    {
        var method = Enum.TryParse<ApiHttpMethod>(Method, ignoreCase: true, out var parsedMethod)
            ? parsedMethod
            : ApiHttpMethod.Get;
        return new ApiEndpointConfiguration
        {
            EndpointId = ApiEndpointConfiguration.CreateStableEndpointId(FunctionName),
            IsEnabled = IsEnabled,
            PowerShellFunctionName = FunctionName,
            DisplayName = FunctionName,
            Description = Description,
            RequiresAuthentication = RequiresAuthentication,
            Rest =
            {
                Method = method,
                RouteTemplate = RouteTemplate,
                OperationId = string.IsNullOrWhiteSpace(OperationId) ? FunctionName : OperationId,
                Tags = ["PowerShell"],
                IncludeInOpenApi = true
            },
            ParameterBindings = ParameterBindings
        };
    }
}
