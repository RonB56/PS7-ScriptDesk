using System.Reflection;
using System.Diagnostics;
using System.Windows;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Shell.Dialogs;

namespace PS7ScriptDesk.Shell.Help;

public partial class AboutWindow : Window
{
    private readonly Action? _openLogsFolder;
    private readonly Action? _createSupportPackage;
    private readonly Action? _copyDiagnostics;

    public AboutWindow(string? powerShellRuntimeVersion = null, Action? openLogsFolder = null,
        Action? createSupportPackage = null, Action? copyDiagnostics = null)
    {
        InitializeComponent();

        _openLogsFolder = openLogsFolder;
        _createSupportPackage = createSupportPackage;
        _copyDiagnostics = copyDiagnostics;
        var presentation = CreatePresentation(powerShellRuntimeVersion);
        ProductNameText.Text = presentation.ProductName;
        VersionText.Text = presentation.VersionText;
        DescriptionText.Text = presentation.Description;
        PackageMetadataText.Text = presentation.PackageMetadataText;
        PowerShellRuntimeText.Text = presentation.PowerShellRuntimeText;
    }

    internal static AboutWindowPresentation CreatePresentation(string? powerShellRuntimeVersion = null)
    {
        return new AboutWindowPresentation(
            ApplicationBranding.PublicName,
            $"Version {GetRunningVersionText()}",
            ApplicationBranding.Tagline,
            GetPackageMetadataText(),
            string.IsNullOrWhiteSpace(powerShellRuntimeVersion)
                ? "PowerShell runtime version: Unavailable"
                : $"PowerShell runtime version: {powerShellRuntimeVersion}");
    }

    internal static string GetRunningVersionText()
    {
        var assembly = typeof(AboutWindow).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var normalizedInformationalVersion = NormalizeVersion(informationalVersion);
        if (!string.IsNullOrWhiteSpace(normalizedInformationalVersion))
        {
            return normalizedInformationalVersion;
        }

        var fileVersion = assembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>()?
            .Version;
        var normalizedFileVersion = NormalizeVersion(fileVersion);
        if (!string.IsNullOrWhiteSpace(normalizedFileVersion))
        {
            return normalizedFileVersion;
        }

        var version = assembly.GetName().Version;
        return version is null
            ? "Unavailable"
            : $"{Math.Max(version.Major, 0)}.{Math.Max(version.Minor, 0)}.{Math.Max(version.Build, 0)}";
    }

    private static string? NormalizeVersion(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return null;
        }

        var normalized = versionText.Trim();
        var metadataSeparatorIndex = normalized.IndexOf('+');
        if (metadataSeparatorIndex >= 0)
        {
            normalized = normalized[..metadataSeparatorIndex];
        }

        return normalized.TrimStart('v', 'V');
    }

    private static string GetPackageMetadataText()
    {
        return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPX_PACKAGE_FAMILY_NAME"))
            ? "Package metadata is not available for this unpackaged build."
            : "This is a Windows-packaged build; package metadata is managed by Windows.";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RepositoryLink_Click(object sender, RoutedEventArgs e) => OpenPublicLink("https://github.com/RonB56/PS7-ScriptDesk");

    private void IssueLink_Click(object sender, RoutedEventArgs e) => OpenPublicLink("https://github.com/RonB56/PS7-ScriptDesk/issues");

    private static void OpenPublicLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _ = new IdeMessageDialog(null, "Unable to Open Support Link",
                $"Open this address manually:\n{url}\n\n{exception.Message}").ShowDialog();
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e) => _openLogsFolder?.Invoke();

    private void CreateSupportPackageButton_Click(object sender, RoutedEventArgs e) => _createSupportPackage?.Invoke();

    private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e) => _copyDiagnostics?.Invoke();
}

internal sealed record AboutWindowPresentation(
    string ProductName,
    string VersionText,
    string Description,
    string PackageMetadataText,
    string PowerShellRuntimeText);
