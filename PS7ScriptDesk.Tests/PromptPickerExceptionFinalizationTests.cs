namespace PS7ScriptDesk.Tests;

public sealed class PromptPickerExceptionFinalizationTests
{
    [Theory]
    [InlineData(true, true, false, "Save")]
    [InlineData(false, false, true, "Discard")]
    [InlineData(false, false, false, "Cancel")]
    [InlineData(null, false, false, "Cancel")]
    public void UnsavedPromptResultMapping_DistinguishesSaveDiscardCancelAndClose(bool? dialogResult, bool primaryAccepted, bool secondaryAccepted, string expected)
    {
        var decision = PS7ScriptDesk.Shell.Services.UserPromptService.ResolveUnsavedChangesDecision(dialogResult, primaryAccepted, secondaryAccepted);
        Assert.Equal(expected, decision.ToString());
    }

    [Fact]
    public void ExitCommandAndWindowClose_ConvergeOnOneShutdownCoordinator()
    {
        var source = Read("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        Assert.Contains("private void Exit_Click", source, StringComparison.Ordinal);
        Assert.Contains("Close();", source, StringComparison.Ordinal);
        Assert.Contains("private async void Window_Closing", source, StringComparison.Ordinal);
        Assert.Contains("TryPrepareForWindowClose()", source, StringComparison.Ordinal);
        Assert.Contains("TryPrepareForApplicationClose()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PrefixSuffixWorkflow_UsesScriptDeskTextInputDialog()
    {
        var source = Read("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        Assert.Contains("new TextInputDialog", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Interaction.InputBox", source, StringComparison.Ordinal);
        Assert.Contains("dialog.ShowDialog() != true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TextInputDialog_HasKeyboardAndAccessibleContract()
    {
        var xaml = Read("PS7ScriptDesk.Shell", "Dialogs", "TextInputDialog.xaml");
        var code = Read("PS7ScriptDesk.Shell", "Dialogs", "TextInputDialog.xaml.cs");
        Assert.Contains("IdeSecondaryWindowStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Input value\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsDefault=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsCancel=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("InputBox.SelectAll()", code, StringComparison.Ordinal);
        Assert.Contains("Key.Escape", code, StringComparison.Ordinal);
        Assert.Contains("Result = InputText", code, StringComparison.Ordinal);
    }

    [Fact]
    public void CentralMessagePrompts_UseScriptDeskDialogAndPreserveNativeFileDialogs()
    {
        var service = Read("PS7ScriptDesk.Shell", "Services", "UserPromptService.cs");
        Assert.Contains("new IdeMessageDialog", service, StringComparison.Ordinal);
        Assert.Contains("ShowUnsavedChangesPrompt", service, StringComparison.Ordinal);
        Assert.Contains("ShowWarningMessage", service, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Win32.SaveFileDialog", service, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Win32.OpenFileDialog", service, StringComparison.Ordinal);
    }

    [Fact]
    public void FolderPickers_UseBuiltInOpenFolderDialogWithoutWinForms()
    {
        var service = Read("PS7ScriptDesk.Shell", "Services", "UserPromptService.cs");
        var wizard = Read("PS7ScriptDesk.Shell", "Dialogs", "RestApiPublishWizardWindow.xaml.cs");
        var project = Read("PS7ScriptDesk.Shell", "PS7ScriptDesk.Shell.csproj");
        Assert.Contains("Microsoft.Win32.OpenFolderDialog", service, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Win32.OpenFolderDialog", wizard, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderBrowserDialog", service, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderBrowserDialog", wizard, StringComparison.Ordinal);
        Assert.DoesNotContain("UseWindowsForms", project, StringComparison.Ordinal);
    }

    [Fact]
    public void MessageDialog_HasAccessibleModalContract()
    {
        var xaml = Read("PS7ScriptDesk.Shell", "Dialogs", "IdeMessageDialog.xaml");
        var code = Read("PS7ScriptDesk.Shell", "Dialogs", "IdeMessageDialog.xaml.cs");
        Assert.Contains("IdeSecondaryWindowStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"ScriptDesk message dialog\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Message content\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsDefault=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Key.Escape", code, StringComparison.Ordinal);
        Assert.Contains("PrimaryAccepted", code, StringComparison.Ordinal);
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
