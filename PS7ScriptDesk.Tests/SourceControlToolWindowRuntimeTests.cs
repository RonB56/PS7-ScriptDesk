using System.Runtime.CompilerServices;
using System.Windows;
using PS7ScriptDesk.Shell;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class SourceControlToolWindowRuntimeTests
{
    [Fact]
    public void SourceControlToolWindowLoadsOnAnStaWpfThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new App();
                application.InitializeComponent();

                var viewModel = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
                var window = new SourceControlToolWindow(viewModel);
                var sharedRunner = new PS7ScriptDesk.Infrastructure.Services.GitCommandRunner();
                var sharedService = new PS7ScriptDesk.Infrastructure.Services.GitService(
                    sharedRunner,
                    new PS7ScriptDesk.Infrastructure.Services.GitRepositoryLocator(sharedRunner));
                var gitWorkspace = new GitWorkspaceWindow(
                    new GitWorkspaceViewModel(new PS7ScriptDesk.Application.Services.GitWorkspaceCoordinator(sharedService)));
                var branchPicker = new BranchPickerWindow(viewModel);
                var diffView = new DiffDocumentView
                {
                    DataContext = new PS7ScriptDesk.UI.ViewModels.DiffTabViewModel(
                        new PS7ScriptDesk.Domain.Models.GitDiff(
                            "a.ps1",
                            "a.ps1",
                            PS7ScriptDesk.Domain.Models.GitDiffScope.Unstaged,
                            Array.Empty<PS7ScriptDesk.Domain.Models.GitDiffLine>(),
                            false,
                            false,
                            false,
                            false))
                };
                var historyView = new HistoryDocumentView();

                Assert.Same(viewModel, window.DataContext);
                Assert.IsType<SourceControlView>(window.FindName("SourceControlContent"));
                Assert.Equal("Git Workspace", gitWorkspace.Title);
                Assert.NotNull(diffView);
                Assert.NotNull(historyView);
                Assert.Same(viewModel, branchPicker.DataContext);
                window.CloseForOwnerShutdown();
                gitWorkspace.Close();
                branchPicker.Close();
                application.Shutdown();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }
}
