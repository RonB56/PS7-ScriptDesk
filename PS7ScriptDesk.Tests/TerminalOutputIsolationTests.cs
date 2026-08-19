using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalOutputIsolationTests
{
    [Fact]
    public async Task DebuggerOutput_UsesDedicatedBuffer_NotApplicationActivityOrTerminalDisplay()
    {
        var viewModel = CreateViewModel();
        var initialTerminalDisplay = viewModel.TerminalDisplayText;

        viewModel.AppendDebugOutput("debugger text");
        await WaitUntilAsync(
            () => viewModel.DebuggerOutputText.Contains("debugger text", StringComparison.Ordinal));

        Assert.Contains("[debug] debugger text", viewModel.DebuggerOutputText, StringComparison.Ordinal);
        Assert.DoesNotContain("debugger text", viewModel.ApplicationActivityText, StringComparison.Ordinal);
        Assert.Equal(initialTerminalDisplay, viewModel.TerminalDisplayText);

        viewModel.ClearDebugOutput();
        await WaitUntilAsync(() => viewModel.DebuggerOutputText.Length == 0);
        Assert.Equal(string.Empty, viewModel.DebuggerOutputText);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
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
