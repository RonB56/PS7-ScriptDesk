namespace PS7ScriptDesk.Tests;

public sealed class WizardVisualReconciliationTests
{
    [Fact]
    public void WizardStyleDictionary_InheritsSharedDialogContractAndKeepsSpecializedKeys()
    {
        var xaml = Read("PS7ScriptDesk.Shell", "Dialogs", "ExportWizardStyles.xaml");

        Assert.Contains("BasedOn=\"{StaticResource IdeDialogPrimaryButtonStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource IdeDialogSecondaryButtonStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource IdeDialogTertiaryButtonStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource IdeDialogPanelStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource IdeDialogResultPanelStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"WizardStepTabItemStyle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"WizardPresetListBoxItemStyle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"WizardPageHostStyle\"", xaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ExportWizardWindow.xaml", "Export PowerShell script as EXE", "Export as EXE title")]
    [InlineData("RestApiPublishWizardWindow.xaml", "Publish PowerShell script as REST API", "Publish as API title")]
    public void WizardWindows_UseSharedFrameAndAccessibleHeader(string fileName, string windowName, string titleName)
    {
        var xaml = Read("PS7ScriptDesk.Shell", "Dialogs", fileName);

        Assert.Contains("Style=\"{DynamicResource WizardWindowStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains($"AutomationProperties.Name=\"{windowName}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{DynamicResource WizardTitleStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains($"AutomationProperties.Name=\"{titleName}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{DynamicResource WizardDescriptionStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{DynamicResource WizardFooterStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsDefault=\"True\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RestWizard_RetainsAdvancedWorkflowSurfaceAndAccessibleLiveAreas()
    {
        var xaml = Read("PS7ScriptDesk.Shell", "Dialogs", "RestApiPublishWizardWindow.xaml");

        foreach (var marker in new[] { "SecurityModeBox", "OpenApi", "StartTestButton", "StopInvocationButton", "LocalTestEventGrid", "PublishApiButton", "BuildPublishStatusText", "AutomationProperties.LiveSetting=\"Polite\"" })
        {
            Assert.Contains(marker, xaml, StringComparison.Ordinal);
        }
    }

    private static string Read(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.slnx")))
            {
                return File.ReadAllText(Path.Combine(new[] { current.FullName }.Concat(parts).ToArray()));
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PowerShellStudio repository root.");
    }
}
