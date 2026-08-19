using System.Reflection;
using System.Windows;
using PS7ScriptDesk.Application.Utilities;

namespace PS7ScriptDesk.Shell.Help;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var presentation = CreatePresentation();
        ProductNameText.Text = presentation.ProductName;
        VersionText.Text = presentation.VersionText;
        DescriptionText.Text = presentation.Description;
        PackageMetadataText.Text = presentation.PackageMetadataText;
    }

    internal static AboutWindowPresentation CreatePresentation()
    {
        return new AboutWindowPresentation(
            ApplicationBranding.PublicName,
            $"Version {GetRunningVersionText()}",
            ApplicationBranding.Tagline,
            GetPackageMetadataText());
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
}

internal sealed record AboutWindowPresentation(
    string ProductName,
    string VersionText,
    string Description,
    string PackageMetadataText);
