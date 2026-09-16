using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class GitWorkspaceWpfBindingTests
{
    [Fact]
    public void BranchAndCommitMessageBindingsSurviveSnapshotReplacementOnStaDispatcher()
    {
        RunOnSta(dispatcher =>
        {
            if (System.Windows.Application.Current is null)
            {
                var app = new PS7ScriptDesk.Shell.App();
                app.InitializeComponent();
            }
            var coordinator = new GitWorkspaceCoordinator(new GitService(new GitCommandRunner(), new GitRepositoryLocator(new GitCommandRunner())));
            var host = CreateHost();
            using var workspace = new GitWorkspaceViewModel(coordinator, host);
            var branches = workspace.Branches!;

            var combo = new ComboBox { SelectedValuePath = "OperationName" };
            combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(branches.Branches)) { Source = branches });
            combo.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(branches.SelectedBranchName)) { Source = branches, Mode = BindingMode.OneWay });
            var template = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(TextBlock)) };
            template.VisualTree.SetBinding(TextBlock.TextProperty, new Binding(nameof(GitBranch.DisplayName)));
            combo.ItemTemplate = template;

            var changes = workspace.Changes!;
            var messageBox = new TextBox();
            messageBox.SetBinding(TextBox.TextProperty, new Binding(nameof(changes.CommitMessage))
            {
                Source = changes,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });

            var first = new GitBranch("main", "refs/heads/main", true, false, null, null, null, "one");
            coordinator.PublishBranchState(new GitBranchState(new[] { first }, "main", false, "one"));
            Pump(dispatcher);
            Assert.Same(first, combo.SelectedItem);
            Assert.Equal("main", combo.SelectedValue);
            Assert.Equal("main", ((GitBranch)combo.SelectedItem).Name);

            changes.CommitMessage = "Batch 5 UAT commit";
            Pump(dispatcher);
            Assert.Equal("Batch 5 UAT commit", messageBox.Text);

            var replacement = new GitBranch("main", "refs/heads/main", true, false, null, null, null, "two");
            coordinator.PublishBranchState(new GitBranchState(new[] { replacement }, "main", false, "two"));
            host.CommitMessage = string.Empty;
            Pump(dispatcher);

            Assert.Same(replacement, combo.SelectedItem);
            Assert.Equal("main", combo.SelectedValue);
            Assert.Equal("main", ((GitBranch)combo.SelectedItem).Name);
            Assert.Equal(string.Empty, changes.CommitMessage);
            Assert.Equal(string.Empty, messageBox.Text);

            host.Dispose();
        });
    }

    [Fact]
    public void DedicatedGitWorkspaceBranchSelector_RendersShortNamesForLocalRefs()
    {
        RunOnSta(dispatcher =>
        {
            var coordinator = new GitWorkspaceCoordinator(new GitService(new GitCommandRunner(), new GitRepositoryLocator(new GitCommandRunner())));
            var host = CreateHost();
            using var workspace = new GitWorkspaceViewModel(coordinator, host);
            var combo = new ComboBox { SelectedValuePath = "OperationName" };
            combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(GitBranchesViewModel.Branches)) { Source = workspace.Branches });
            combo.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(GitBranchesViewModel.SelectedBranchName)) { Source = workspace.Branches, Mode = BindingMode.OneWay });
            var template = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(TextBlock)) };
            template.VisualTree.SetBinding(TextBlock.TextProperty, new Binding(nameof(GitBranch.DisplayName)));
            combo.ItemTemplate = template;
            var state = new GitBranchState(new[]
            {
                new GitBranch("refs/heads/main", "refs/heads/main", true, false, null, null, null, "one"),
                new GitBranch("refs/heads/uat-branch", "refs/heads/uat-branch", false, false, null, null, null, "two"),
                new GitBranch("refs/heads/uat-create-only", "refs/heads/uat-create-only", false, false, null, null, null, "three")
            }, "refs/heads/main", false, "one");

            coordinator.PublishBranchState(state);
            Pump(dispatcher);

            var visible = combo.Items.Cast<GitBranch>().Select(branch => branch.DisplayName).ToArray();
            Assert.Equal(["main", "uat-branch", "uat-create-only"], visible);
            Assert.DoesNotContain(visible, name => name.StartsWith("refs/heads/", StringComparison.Ordinal));
            Assert.Equal("main", combo.SelectedValue);
            host.Dispose();
        });
    }

    [Fact]
    public void DedicatedGitWorkspace_RendersBranchActionsAndGatesSelectedBranchState()
    {
        RunOnSta(dispatcher =>
        {
            if (System.Windows.Application.Current is null)
            {
                var app = new PS7ScriptDesk.Shell.App();
                app.InitializeComponent();
            }
            var coordinator = new GitWorkspaceCoordinator(new GitService(new GitCommandRunner(), new GitRepositoryLocator(new GitCommandRunner())));
            var host = CreateHost();
            using var workspace = new GitWorkspaceViewModel(coordinator, host);
            coordinator.PublishState(new GitRepositoryState(
                new GitEnvironmentInfo(true, "git.exe", "2.x", null, null),
                new GitRepositoryInfo(true, "C:\\repo", "C:\\repo", "main", false, "one", false, false, false, null, null),
                "C:\\repo",
                DateTimeOffset.UtcNow));
            coordinator.PublishBranchState(new GitBranchState(new[]
            {
                new GitBranch("main", "refs/heads/main", true, false, null, null, null, "one"),
                new GitBranch("uat-branch", "refs/heads/uat-branch", false, false, null, null, null, "two"),
                new GitBranch("origin/uat-branch", "refs/remotes/origin/uat-branch", false, true, null, null, null, "three")
            }, "main", false, "one"));
            var window = new PS7ScriptDesk.Shell.GitWorkspaceWindow(workspace);
            dispatcher.Invoke(() => { window.ApplyTemplate(); }, DispatcherPriority.Loaded);
            var actionsButton = (Button)window.FindName("BranchActionsButton")!;
            var switchButton = (Button)window.FindName("SwitchBranchButton")!;
            var menu = actionsButton.ContextMenu!;
            actionsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(dispatcher);

            var rename = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Rename"));
            var delete = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Delete"));
            Assert.True(rename.IsEnabled);
            Assert.False(delete.IsEnabled);
            Assert.False(switchButton.IsEnabled);

            workspace.Branches!.SelectedBranch = workspace.Branches.Branches.Single(branch => branch.OperationName == "uat-branch");
            Pump(dispatcher);
            Assert.True(rename.IsEnabled);
            Assert.True(delete.IsEnabled);
            Assert.True(switchButton.IsEnabled);

            menu.IsOpen = false;
            workspace.Branches.SelectedBranch = workspace.Branches.Branches.Single(branch => branch.IsRemote);
            Pump(dispatcher);
            actionsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(dispatcher);
            Assert.False(rename.IsEnabled);
            Assert.False(delete.IsEnabled);
            Assert.False(switchButton.IsEnabled);
            menu.IsOpen = false;
            coordinator.PublishBranchState(new GitBranchState(Array.Empty<GitBranch>(), null, true, "detached"));
            Pump(dispatcher);
            actionsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(dispatcher);
            Assert.True(menu.IsOpen);
            Assert.False(rename.IsEnabled);
            Assert.False(delete.IsEnabled);
            menu.IsOpen = false;
            host.Dispose();
        });
    }

    private static MainWindowViewModel CreateHost()
        => new(
            new FakeWorkspaceService(),
            new FakeRuntimeService(),
            new FileDocumentService(),
            new FakeWorkspaceFolderService(),
            new FakeUserPromptService(),
            new FakeLiveConsoleService(),
            new FakeExeExportService());

    private static void Pump(Dispatcher dispatcher)
        => dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

    private static void RunOnSta(Action<Dispatcher> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try { action(dispatcher); }
            catch (Exception ex) { failure = ex; }
            finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
