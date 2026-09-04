using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class Wave3UtilityDialogContractTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRoot() }.Concat(parts).ToArray()));

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PS7ScriptDesk.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    [Fact]
    public void Wave3DialogsAdoptTheSharedSecondaryWindowContract()
    {
        foreach (var path in new[]
        {
            new[] { "PS7ScriptDesk.Shell", "Editor", "GoToLineDialog.xaml" },
            new[] { "PS7ScriptDesk.Shell", "Dialogs", "ExternalFileConflictDialog.xaml" },
            new[] { "PS7ScriptDesk.Shell", "Help", "AboutWindow.xaml" },
            new[] { "PS7ScriptDesk.Shell", "RuntimeResolverWindow.xaml" },
            new[] { "PS7ScriptDesk.Shell", "Dialogs", "ExportProgressWindow.xaml" },
            new[] { "PS7ScriptDesk.Shell", "Dialogs", "DocumentRecoveryDialog.xaml" }
        })
        {
            var xaml = Read(path);
            Assert.True(
                xaml.Contains("Style=\"{StaticResource IdeSecondaryWindowStyle}\"", StringComparison.Ordinal) ||
                xaml.Contains("Style=\"{DynamicResource IdeSecondaryWindowStyle}\"", StringComparison.Ordinal),
                $"Shared secondary-window style was not adopted by {string.Join("/", path)}.");
        }
    }

    [Fact]
    public void Wave3DialogsRetainPrimaryCancelHelpAndAccessibilityContracts()
    {
        var goToLine = Read("PS7ScriptDesk.Shell", "Editor", "GoToLineDialog.xaml");
        Assert.Contains("IsDefault=\"True\"", goToLine, StringComparison.Ordinal);
        Assert.Contains("IsCancel=\"True\"", goToLine, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Line number\"", goToLine, StringComparison.Ordinal);

        var conflict = Read("PS7ScriptDesk.Shell", "Dialogs", "ExternalFileConflictDialog.xaml");
        Assert.Contains("ReloadButton_Click", conflict, StringComparison.Ordinal);
        Assert.Contains("OverwriteButton_Click", conflict, StringComparison.Ordinal);
        Assert.Contains("SaveAsButton_Click", conflict, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Overwrite disk file\"", conflict, StringComparison.Ordinal);

        var runtime = Read("PS7ScriptDesk.Shell", "RuntimeResolverWindow.xaml");
        Assert.Contains("IsDefault=\"True\"", runtime, StringComparison.Ordinal);
        Assert.Contains("IsCancel=\"True\"", runtime, StringComparison.Ordinal);
        Assert.Contains("ContextHelp.Key=\"Runtime.Resolver\"", runtime, StringComparison.Ordinal);

        var progress = Read("PS7ScriptDesk.Shell", "Dialogs", "ExportProgressWindow.xaml");
        Assert.Contains("AutomationProperties.Name=\"Export progress\"", progress, StringComparison.Ordinal);
        Assert.Contains("CloseButton_Click", progress, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryUsesDedicatedDialogAndPreservesActionMapping()
    {
        var service = Read("PS7ScriptDesk.Shell", "Services", "UserPromptService.cs");
        var dialog = Read("PS7ScriptDesk.Shell", "Dialogs", "DocumentRecoveryDialog.xaml.cs");
        var dialogXaml = Read("PS7ScriptDesk.Shell", "Dialogs", "DocumentRecoveryDialog.xaml");

        Assert.Contains("new DocumentRecoveryDialog", service, StringComparison.Ordinal);
        Assert.DoesNotContain("AddRecoveryButton", service, StringComparison.Ordinal);
        foreach (var action in new[] { "Restore", "Discard", "SaveAs", "KeepForLater" })
        {
            Assert.True(dialog.Contains(action, StringComparison.Ordinal) || dialogXaml.Contains(action, StringComparison.Ordinal));
        }
    }
}
