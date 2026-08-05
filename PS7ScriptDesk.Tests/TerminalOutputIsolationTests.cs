using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalOutputIsolationTests
{
    [Fact]
    public void AboutMessage_IsRoutedToApplicationActivity_NotDebuggerOrTerminalDisplay()
    {
        var viewModel = CreateViewModel();
        var debuggerWrites = new List<string>();
        var initialTerminalDisplay = viewModel.TerminalDisplayText;

        viewModel.SetTerminalSessionControls(() => { }, () => { });
        viewModel.SetDebuggerOutputSink(debuggerWrites.Add);

        viewModel.AboutCommand.Execute(null);

        Assert.Empty(debuggerWrites);
        Assert.Equal(initialTerminalDisplay, viewModel.TerminalDisplayText);
        Assert.Contains("About requested", viewModel.ApplicationActivityText, StringComparison.Ordinal);
    }

    [Fact]
    public void DebuggerOutput_UsesExplicitDebuggerSink_NotApplicationActivity()
    {
        var viewModel = CreateViewModel();
        var debuggerWrites = new List<string>();

        viewModel.SetDebuggerOutputSink(debuggerWrites.Add);
        viewModel.AppendDebugOutput("debugger text");

        var write = Assert.Single(debuggerWrites);
        Assert.Contains("[debug] debugger text", write, StringComparison.Ordinal);
        Assert.DoesNotContain("debugger text", viewModel.ApplicationActivityText, StringComparison.Ordinal);
    }

    private static MainWindowViewModel CreateViewModel()
    {
        return new MainWindowViewModel(
            new FakeWorkspaceService(),
            new FakeRuntimeService(),
            new FileDocumentService(),
            new FakeWorkspaceFolderService(),
            new FakeUserPromptService(),
            new FakeLiveConsoleService(),
            new FakeExeExportService());
    }
}
