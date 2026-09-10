using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class DocumentLanguageGatingTests
{
    [Theory]
    [InlineData("script.ps1", DocumentLanguage.PowerShell)]
    [InlineData("module.PSM1", DocumentLanguage.PowerShell)]
    [InlineData("manifest.Psd1", DocumentLanguage.PowerShell)]
    [InlineData("App.xaml.cs", DocumentLanguage.PlainText)]
    [InlineData("settings.json", DocumentLanguage.PlainText)]
    [InlineData("README.md", DocumentLanguage.PlainText)]
    [InlineData(".gitignore", DocumentLanguage.PlainText)]
    [InlineData("unknown", DocumentLanguage.PlainText)]
    public void ClassifiesOnlyPowerShellExtensionsAsPowerShell(string path, DocumentLanguage expected)
    {
        Assert.Equal(expected, DocumentLanguageClassifier.ClassifyPath(path));
    }

    [Fact]
    public void EditorTabUsesFileClassificationAndKeepsUntitledPs1PowerShell()
    {
        var source = new EditorTabViewModel("App.xaml.cs", "<Window />", "C:\\repo\\App.xaml.cs");
        var script = new EditorTabViewModel("Untitled1.ps1", "Get-Date");

        Assert.False(source.IsPowerShellDocument);
        Assert.True(script.IsPowerShellDocument);
    }

    [Fact]
    public void SaveAsReclassifiesTheExistingEditorTab()
    {
        var tab = new EditorTabViewModel("notes.txt", "Get-Date", "C:\\repo\\notes.txt");

        tab.SetFilePath("C:\\repo\\script.ps1");

        Assert.Equal(DocumentLanguage.PowerShell, tab.Language);
        Assert.True(tab.IsPowerShellDocument);
    }

    [Fact]
    public void ShellReactivatesPowerShellServicesWhenASelectedTabReturns()
    {
        var shellPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var shellSource = File.ReadAllText(Path.GetFullPath(shellPath));

        Assert.Contains("ActivatePowerShellEditorServices(activeEditor, selectedTab)", shellSource, StringComparison.Ordinal);
        Assert.Contains("StartCompletionEngineWarmup(runtimeInfo)", shellSource, StringComparison.Ordinal);
    }
}
