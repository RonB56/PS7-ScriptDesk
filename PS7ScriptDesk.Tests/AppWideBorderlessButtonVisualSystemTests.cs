namespace PS7ScriptDesk.Tests;

public sealed class AppWideBorderlessButtonVisualSystemTests
{
    [Fact]
    public void SharedButtonStylesProvideBorderlessInteractionStatesAndSemanticVariants()
    {
        var app = Read("PS7ScriptDesk.Shell", "App.xaml");
        var workspace = Read("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml");
        var wizard = Read("PS7ScriptDesk.Shell", "Dialogs", "ExportWizardStyles.xaml");

        Assert.Contains("x:Key=\"IdeButtonFocusVisualStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeActionButtonStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdePrimaryButtonStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeDestructiveButtonStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"BorderThickness\" Value=\"0\" />", app, StringComparison.Ordinal);
        Assert.Contains("Theme.Button.HoverBackground", app, StringComparison.Ordinal);
        Assert.Contains("Theme.Button.PressedBackground", app, StringComparison.Ordinal);
        Assert.Contains("Theme.Button.DisabledForeground", app, StringComparison.Ordinal);
        Assert.Contains("Theme.Button.FocusAccent", app, StringComparison.Ordinal);
        Assert.Contains("FocusVisualStyle", app, StringComparison.Ordinal);
        Assert.Contains("IsChecked\" Value=\"True\"", app, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeSecondaryButtonStyle}\"", workspace, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeDestructiveButtonStyle}\"", workspace, StringComparison.Ordinal);
        Assert.Contains("IdeDestructiveButtonStyle", Read("PS7ScriptDesk.Shell", "BranchPickerWindow.xaml"), StringComparison.Ordinal);
        Assert.Contains("IdeDestructiveButtonStyle", Read("PS7ScriptDesk.Shell", "Dialogs", "DocumentRecoveryDialog.xaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("<ControlTemplate TargetType=\"Button\">", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("<Setter Property=\"BorderThickness\" Value=\"1\" />", wizard, StringComparison.Ordinal);
    }

    [Fact]
    public void AllThemesProvideTheSharedButtonResourceContract()
    {
        var keys = new[]
        {
            "Theme.Button.RestingBackground",
            "Theme.Button.HoverBackground",
            "Theme.Button.PressedBackground",
            "Theme.Button.Foreground",
            "Theme.Button.DisabledForeground",
            "Theme.Button.FocusAccent",
            "Theme.Button.PrimaryBackground",
            "Theme.Button.PrimaryForeground",
            "Theme.Button.DestructiveBackground",
            "Theme.Button.DestructiveForeground",
            "Theme.Button.SelectedBackground"
        };

        foreach (var theme in new[] { "DarkTheme.xaml", "LightTheme.xaml", "IseBlueTheme.xaml" })
        {
            var xaml = Read("PS7ScriptDesk.Shell", "Themes", theme);
            foreach (var key in keys)
            {
                Assert.Contains($"x:Key=\"{key}\"", xaml, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void KeyButtonSurfacesUseSemanticSharedStylesInsteadOfLocalChrome()
    {
        var xamlFiles = new[]
        {
            Read("PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml"),
            Read("PS7ScriptDesk.Shell", "FindReplaceWindow.xaml"),
            Read("PS7ScriptDesk.Shell", "Help", "AboutWindow.xaml"),
            Read("PS7ScriptDesk.Shell", "Dialogs", "ExportWizardWindow.xaml"),
            Read("PS7ScriptDesk.Shell", "Dialogs", "RestApiPublishWizardWindow.xaml")
        };

        foreach (var xaml in xamlFiles)
        {
            Assert.DoesNotContain("TargetType=\"Button\"", xaml, StringComparison.Ordinal);
        }

        var app = Read("PS7ScriptDesk.Shell", "App.xaml");
        Assert.Contains("x:Key=\"IdeDialogPrimaryButtonStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeDialogSecondaryButtonStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolbarButtonStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeToolbarIconButtonStyle\"", app, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeBottomPaneTabToggleButtonStyle\"", app, StringComparison.Ordinal);
    }

    private static string Read(params string[] pathParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.slnx")))
            {
                return File.ReadAllText(Path.Combine(new[] { current.FullName }.Concat(pathParts).ToArray()));
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PowerShellStudio repository root.");
    }
}
