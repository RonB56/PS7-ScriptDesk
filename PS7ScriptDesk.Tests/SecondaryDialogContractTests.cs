using System;
using System.IO;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class SecondaryDialogContractTests
{
    private static string ReadShellFile(string fileName) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "PS7ScriptDesk.Shell", fileName));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PS7ScriptDesk.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    [Fact]
    public void AppResourcesDefineTheSharedSecondaryDialogContract()
    {
        var app = ReadShellFile("App.xaml");

        foreach (var key in new[]
        {
            "IdeSecondaryWindowStyle",
            "IdeDialogTitleStyle",
            "IdeDialogDescriptionStyle",
            "IdeDialogSectionStyle",
            "IdeDialogFieldLabelStyle",
            "IdeDialogFieldHelpStyle",
            "IdeDialogFooterStyle",
            "IdeDialogResultPanelStyle"
        })
        {
            Assert.Contains($"x:Key=\"{key}\"", app, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AllThemeDictionariesRetainTheSemanticKeysUsedByTheContract()
    {
        foreach (var theme in new[] { "DarkTheme.xaml", "LightTheme.xaml", "IseBlueTheme.xaml" })
        {
            var text = ReadShellFile(Path.Combine("Themes", theme));
            foreach (var key in new[]
            {
                "Theme.App.Background",
                "Theme.Text.Primary",
                "Theme.Text.Secondary",
                "Theme.Border.Subtle",
                "Theme.Surface.Secondary",
                "Theme.Status.Error.Foreground"
            })
            {
                Assert.Contains($"x:Key=\"{key}\"", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void FindReplaceRetainsInteractionContractAndNamesItsControls()
    {
        var xaml = ReadShellFile("FindReplaceWindow.xaml");
        var code = ReadShellFile("FindReplaceWindow.xaml.cs");

        Assert.Contains("Style=\"{StaticResource IdeSecondaryWindowStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeDialogResultPanelStyle}\"", xaml, StringComparison.Ordinal);
        foreach (var name in new[] { "Find text", "Replace text", "Match case", "Whole word", "Use regular expression", "Find results", "Find result count", "Find next", "Find previous", "Replace one", "Replace all", "Close Find and Replace" })
        {
            Assert.Contains($"AutomationProperties.Name=\"{name}\"", xaml, StringComparison.Ordinal);
        }

        foreach (var marker in new[] { "IsDefault=\"True\"", "PreviewKeyDown=\"Window_PreviewKeyDown\"", "Key.F3", "Key.F", "Key.H", "Key.Escape", "Key.F1", "Owner = ownerWindow", "e.Cancel = true", "Hide();" })
        {
            Assert.True(xaml.Contains(marker, StringComparison.Ordinal) || code.Contains(marker, StringComparison.Ordinal), $"Missing interaction marker: {marker}");
        }
    }
}
