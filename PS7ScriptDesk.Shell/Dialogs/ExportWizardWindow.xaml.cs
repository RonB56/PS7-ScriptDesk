using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell.Help;

namespace PS7ScriptDesk.Shell.Dialogs;

public partial class ExportWizardWindow : Window
{
    private readonly ExeExportWizardRequest _request;
    private ExeExportConfiguration _configuration;
    private bool _isInitializing;
    private bool _isApplyingConfiguration;

    public ExportWizardWindow(ExeExportWizardRequest request, ExeExportConfiguration? lastConfiguration)
    {
        _request = request;
        _configuration = lastConfiguration?.Clone() ?? ExeExportConfiguration.CreatePreset(ExeExportPreset.PortableWindowsExe, request.SuggestedApplicationName);
        if (string.IsNullOrWhiteSpace(_configuration.ApplicationName)) _configuration.ApplicationName = request.SuggestedApplicationName;
        _isInitializing = true;
        try
        {
            InitializeComponent();
            EnsureInitialPageSelection(Pages);
            SelectPreset(_configuration.Preset);
            LoadConfiguration();
            DependencyList.ItemsSource = request.Dependencies.Select(dependency => $"{dependency.Kind}: {dependency.Value} — {dependency.Message}");
        }
        finally
        {
            _isInitializing = false;
        }

        UpdateWizardNavigation();
    }

    public ExeExportConfiguration? Configuration { get; private set; }

    private void LoadConfiguration()
    {
        _isApplyingConfiguration = true;
        try
        {
            ApplicationNameBox.Text = _configuration.ApplicationName;
            DescriptionBox.Text = _configuration.Description;
            IconBox.Text = _configuration.IconPath ?? string.Empty;
            CompanyBox.Text = _configuration.Company;
            FileVersionBox.Text = _configuration.FileVersion;
            ProductVersionBox.Text = _configuration.ProductVersion;
            OutputBox.Text = _configuration.OutputExecutablePath;
            AdministratorBox.IsChecked = _configuration.AdministratorMode == ExeAdministratorMode.RequireAdministrator;
            LoadProfileBox.IsChecked = _configuration.LoadPowerShellProfile;
            ErrorDialogBox.IsChecked = _configuration.ShowFatalErrorDialog;
            WriteLogBox.IsChecked = _configuration.WriteApplicationLog;
            Select(ApplicationTypeBox, _configuration.ApplicationType.ToString()); Select(ArchitectureBox, _configuration.Architecture.ToString());
            Select(DeploymentBox, _configuration.DeploymentModel.ToString()); Select(PowerShellBox, _configuration.PowerShellRuntimeModel.ToString());
            Select(PackageBox, _configuration.PackageFormat.ToString()); Select(OptimizationBox, _configuration.OptimizationProfile.ToString());
            RefreshSummary();
        }
        finally
        {
            _isApplyingConfiguration = false;
        }
    }

    private void ReadConfiguration()
    {
        _configuration.ApplicationName = ApplicationNameBox.Text.Trim(); _configuration.ProductName = _configuration.ApplicationName;
        _configuration.Description = DescriptionBox.Text.Trim(); _configuration.IconPath = string.IsNullOrWhiteSpace(IconBox.Text) ? null : IconBox.Text.Trim();
        _configuration.Company = CompanyBox.Text.Trim(); _configuration.FileVersion = FileVersionBox.Text.Trim(); _configuration.ProductVersion = ProductVersionBox.Text.Trim();
        _configuration.OutputExecutablePath = OutputBox.Text.Trim(); _configuration.AdministratorMode = AdministratorBox.IsChecked == true ? ExeAdministratorMode.RequireAdministrator : ExeAdministratorMode.NormalUser;
        _configuration.LoadPowerShellProfile = LoadProfileBox.IsChecked == true; _configuration.ShowFatalErrorDialog = ErrorDialogBox.IsChecked != false; _configuration.WriteApplicationLog = WriteLogBox.IsChecked != false;
        _configuration.ApplicationType = Parse<ExeApplicationType>(ApplicationTypeBox); _configuration.Architecture = Parse<ExeTargetArchitecture>(ArchitectureBox);
        _configuration.DeploymentModel = Parse<ExeDeploymentModel>(DeploymentBox); _configuration.PowerShellRuntimeModel = Parse<ExePowerShellRuntimeModel>(PowerShellBox);
        _configuration.PackageFormat = Parse<ExePackageFormat>(PackageBox); _configuration.OptimizationProfile = Parse<ExeOptimizationProfile>(OptimizationBox);
        ImplicationsText.Text = $"{(_configuration.IsPortable ? "Portable: .NET and PowerShell are embedded." : "Dependencies required: " + string.Join(", ", new[] { _configuration.RequiresDotNetRuntime ? ".NET runtime" : null, _configuration.RequiresInstalledPowerShell ? "PowerShell 7" : null }.Where(value => value is not null)))}";
    }

    private void RefreshSummary()
    {
        ReadConfiguration();
        ReviewApplicationText.Text = string.IsNullOrWhiteSpace(_configuration.Description)
            ? $"{_configuration.ApplicationName}.exe · {_configuration.ApplicationType}"
            : $"{_configuration.ApplicationName}.exe · {_configuration.ApplicationType}{Environment.NewLine}{_configuration.Description}";
        ReviewPlatformText.Text = $"{_configuration.RuntimeIdentifier} · {_configuration.DeploymentModel} · {_configuration.PowerShellRuntimeModel}{Environment.NewLine}{_configuration.PackageFormat} package · {_configuration.OptimizationProfile} optimization";
        ReviewAdvancedText.Text = $"Administrator: {_configuration.AdministratorMode} · Profile: {(_configuration.LoadPowerShellProfile ? "Load" : "Do not load")}{Environment.NewLine}Fatal error dialog: {(_configuration.ShowFatalErrorDialog ? "Enabled" : "Disabled")} · Application log: {(_configuration.WriteApplicationLog ? "Enabled" : "Disabled")}";
        ReviewDependencyText.Text = _request.Dependencies.Count == 0
            ? "No detected portability concerns."
            : $"{_request.Dependencies.Count} detected portability concern(s). Review the Dependencies step before exporting.";
        ReviewOutputText.Text = string.IsNullOrWhiteSpace(_configuration.OutputExecutablePath)
            ? "Choose an output executable path."
            : _configuration.OutputExecutablePath;
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshSummary();
        if (Pages.SelectedIndex == Pages.Items.Count - 1)
        {
            var validation = new ExeExportConfigurationValidator().Validate(_configuration);
            ValidationText.Text = string.Join(Environment.NewLine, validation.Errors.Concat(validation.Warnings));
            if (!validation.IsValid) return;
            Configuration = _configuration.Clone(); DialogResult = true; return;
        }
        Pages.SelectedIndex++;
    }
    private void BackButton_Click(object sender, RoutedEventArgs e) => Pages.SelectedIndex--;
    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Pages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || e.Source != Pages) return;

        BackButton.IsEnabled = Pages.SelectedIndex > 0;
        NextButton.Content = Pages.SelectedIndex == Pages.Items.Count - 1 ? "Export EXE" : "Next";
        UpdateWizardNavigation();
        RefreshSummary();
    }

    private void UpdateWizardNavigation()
    {
        var stepButtons = new[]
        {
            PresetStepButton,
            ApplicationStepButton,
            PlatformStepButton,
            DependenciesStepButton,
            AdvancedStepButton,
            ReviewStepButton
        };

        for (var index = 0; index < stepButtons.Length; index++)
        {
            stepButtons[index].Style = (Style)FindResource(index == Pages.SelectedIndex
                ? "WizardPrimaryButtonStyle"
                : "WizardSecondaryButtonStyle");
        }
    }

    internal static int ResolveInitialPageIndex(int selectedIndex, int itemCount)
        => selectedIndex >= 0 || itemCount <= 0 ? selectedIndex : 0;

    private static void EnsureInitialPageSelection(System.Windows.Controls.TabControl pages)
    {
        var pageIndex = ResolveInitialPageIndex(pages.SelectedIndex, pages.Items.Count);
        if (pageIndex >= 0)
        {
            pages.SelectedIndex = pageIndex;
        }
    }

    private void StepButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string tag }
            && int.TryParse(tag, out var pageIndex)
            && pageIndex >= 0
            && pageIndex < Pages.Items.Count)
        {
            Pages.SelectedIndex = pageIndex;
        }
    }
    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isApplyingConfiguration || PresetList.SelectedItem is not ListBoxItem item || !TryResolvePreset(item.Tag?.ToString(), out var preset))
            return;

        _configuration = ExeExportConfiguration.CreatePreset(preset, _request.SuggestedApplicationName);
        LoadConfiguration();
    }
    private void ConfigurationEdited(object sender, RoutedEventArgs e) { if (!_isInitializing && !_isApplyingConfiguration && IsLoaded) { _configuration.Preset = ExeExportPreset.Custom; RefreshSummary(); } }
    private void BrowseIcon_Click(object sender, RoutedEventArgs e) { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Icon Files (*.ico)|*.ico", CheckFileExists = true }; if (dialog.ShowDialog() == true) IconBox.Text = dialog.FileName; }
    private void BrowseOutput_Click(object sender, RoutedEventArgs e) { var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Executable Files (*.exe)|*.exe", DefaultExt = ".exe", AddExtension = true, OverwritePrompt = true, FileName = string.IsNullOrWhiteSpace(ApplicationNameBox.Text) ? "ExportedPowerShellScript.exe" : ApplicationNameBox.Text + ".exe" }; if (dialog.ShowDialog() == true) OutputBox.Text = dialog.FileName; }
    private void Window_Loaded(object sender, RoutedEventArgs e) => ContextHelp.ValidateWindowTopics(this);
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.F1) { e.Handled = true; ContextHelp.OpenForFocusedElement(this); } }
    private static T Parse<T>(System.Windows.Controls.ComboBox comboBox) where T : struct => comboBox.SelectedItem is ComboBoxItem item && Enum.TryParse<T>(item.Tag?.ToString(), out var value) ? value : default;
    private static void Select(System.Windows.Controls.ComboBox comboBox, string tag) => comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
    private void SelectPreset(ExeExportPreset preset) => PresetList.SelectedItem = PresetList.Items.OfType<ListBoxItem>().FirstOrDefault(item => string.Equals(item.Tag?.ToString(), preset.ToString(), StringComparison.Ordinal));
    internal static bool TryResolvePreset(string? presetTag, out ExeExportPreset preset) => Enum.TryParse(presetTag, out preset) && Enum.IsDefined(preset);
}
