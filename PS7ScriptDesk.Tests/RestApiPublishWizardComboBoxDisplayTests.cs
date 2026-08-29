using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell.Dialogs;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class RestApiPublishWizardComboBoxDisplayTests
{
    [Fact]
    public void ObjectBackedOptions_ReturnFriendlyDisplayTextWhenWpfFallsBackToToString()
    {
        var security = new RestApiSecurityModeOption(ApiSecurityMode.ApiKey, "API key", IsSelectable: true);
        var architecture = new ApiPublishTargetArchitectureOption(ApiPublishTargetArchitecture.WinArm64, "Windows ARM64");

        Assert.Equal("API key", security.ToString());
        Assert.Equal("Windows ARM64", architecture.ToString());
        Assert.Equal(ApiSecurityMode.ApiKey, security.Mode);
        Assert.Equal(ApiPublishTargetArchitecture.WinArm64, architecture.Architecture);
    }

    [Fact]
    public void ObjectBackedComboBoxes_SelectByTypedEnumValueAndDisplayFriendlyText()
    {
        RunOnStaThread(() =>
        {
            var securityBox = new ComboBox
            {
                ItemsSource = RestApiPublishWizardWindow.CreateRestV1SecurityModeOptions(ApiSecurityMode.ApiKey),
                SelectedValuePath = nameof(RestApiSecurityModeOption.Mode)
            };
            securityBox.SelectedValue = ApiSecurityMode.ApiKey;

            var securityOption = Assert.IsType<RestApiSecurityModeOption>(securityBox.SelectedItem);
            Assert.Equal(ApiSecurityMode.ApiKey, securityBox.SelectedValue);
            Assert.Equal(ApiSecurityMode.ApiKey, securityOption.Mode);
            Assert.Equal("API key", securityBox.SelectionBoxItem?.ToString());

            var architectureBox = new ComboBox
            {
                ItemsSource = RestApiPublishWizardWindow.CreatePublishTargetOptions(),
                SelectedValuePath = nameof(ApiPublishTargetArchitectureOption.Architecture)
            };
            architectureBox.SelectedValue = ApiPublishTargetArchitecture.WinX64;

            var architectureOption = Assert.IsType<ApiPublishTargetArchitectureOption>(architectureBox.SelectedItem);
            Assert.Equal(ApiPublishTargetArchitecture.WinX64, architectureBox.SelectedValue);
            Assert.Equal(ApiPublishTargetArchitecture.WinX64, architectureOption.Architecture);
            Assert.Equal("Windows x64", architectureBox.SelectionBoxItem?.ToString());
        });
    }

    [Fact]
    public void WizardXaml_UsesExplicitDisplayTemplatesForObjectBackedComboBoxes()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "PS7ScriptDesk.Shell", "Dialogs", "RestApiPublishWizardWindow.xaml"));
        var securityComboBox = ExtractControlBlock(xaml, "SecurityModeBox");
        var architectureComboBox = ExtractControlBlock(xaml, "TargetArchitectureBox");

        Assert.Contains("TextSearch.TextPath=\"DisplayName\"", securityComboBox, StringComparison.Ordinal);
        Assert.Contains("SelectedValuePath=\"Mode\"", securityComboBox, StringComparison.Ordinal);
        Assert.Contains("<ComboBox.ItemTemplate>", securityComboBox, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DisplayName}\"", securityComboBox, StringComparison.Ordinal);

        Assert.Contains("TextSearch.TextPath=\"DisplayName\"", architectureComboBox, StringComparison.Ordinal);
        Assert.Contains("SelectedValuePath=\"Architecture\"", architectureComboBox, StringComparison.Ordinal);
        Assert.Contains("<ComboBox.ItemTemplate>", architectureComboBox, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DisplayName}\"", architectureComboBox, StringComparison.Ordinal);
    }

    private static string ExtractControlBlock(string xaml, string controlName)
    {
        var nameIndex = xaml.IndexOf($"x:Name=\"{controlName}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Expected to find {controlName}.");

        var start = xaml.LastIndexOf("<ComboBox", nameIndex, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected {controlName} to be a ComboBox.");

        var explicitEnd = xaml.IndexOf("</ComboBox>", nameIndex, StringComparison.Ordinal);
        if (explicitEnd >= 0)
        {
            return xaml[start..(explicitEnd + "</ComboBox>".Length)];
        }

        var selfClosingEnd = xaml.IndexOf("/>", nameIndex, StringComparison.Ordinal);
        Assert.True(selfClosingEnd >= 0, $"Expected to find the end of {controlName}.");
        return xaml[start..(selfClosingEnd + 2)];
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

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
