using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.CompilerServices;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class GitWorkspaceSelectorDisplayTests
{
    [Fact]
    public void RemoteSelectorUsesExplicitNamePresentationForUrlLocalAndMultipleRemotes()
    {
        RunOnStaThread(() =>
        {
            var originUrl = new GitRemote("origin", "https://github.com/octocat/Hello-World.git", "https://github.com/octocat/Hello-World.git");
            var originLocal = new GitRemote("origin", "Z:\\Test_Git\\ScriptDesk-Safety-Remote.git", "Z:\\Test_Git\\ScriptDesk-Safety-Remote.git");
            var upstream = new GitRemote("upstream", "https://example.invalid/upstream.git", "https://example.invalid/upstream.git");
            var selector = new ComboBox
            {
                ItemsSource = new[] { originLocal, upstream },
                ItemTemplate = CreateNameTemplate()
            };
            TextSearch.SetTextPath(selector, nameof(GitRemote.Name));
            selector.SelectedItem = originLocal;

            Assert.Same(originLocal, selector.SelectedItem);
            Assert.Equal(["origin", "upstream"], selector.Items.Cast<GitRemote>().Select(remote => remote.Name).ToArray());
            Assert.Equal("origin", originUrl.Name);
            Assert.DoesNotContain("GitRemote {", originLocal.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("Z:\\Test_Git", originLocal.Name, StringComparison.Ordinal);
            Assert.NotNull(selector.ItemTemplate);
        });
    }

    [Fact]
    public void GitWorkspaceXamlUsesExplicitFriendlyPropertiesForObjectBackedSelectors()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "PS7ScriptDesk.Shell", "GitWorkspaceWindow.xaml"));
        var appXaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "PS7ScriptDesk.Shell", "App.xaml"));
        var branch = ExtractControlBlock(xaml, "BranchSelector");
        var remote = ExtractControlBlock(xaml, "RemoteSelector");

        Assert.Contains("TextSearch.TextPath=\"DisplayName\"", branch, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DisplayName}\"", branch, StringComparison.Ordinal);
        Assert.Contains("TextSearch.TextPath=\"Name\"", remote, StringComparison.Ordinal);
        Assert.Contains("<ComboBox.ItemTemplate>", remote, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Name}\"", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("DisplayMemberPath=\"Name\"", remote, StringComparison.Ordinal);
        Assert.Contains("ContentTemplate=\"{TemplateBinding ItemTemplate}\"", appXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteSelectionDefaultsAndRefreshesByLogicalName()
    {
        var runner = new GitCommandRunner();
        var coordinator = new GitWorkspaceCoordinator(new GitService(runner, new GitRepositoryLocator(runner)));
        var host = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        using var remotes = new GitRemotesViewModel(coordinator, host);
        coordinator.PublishState(new GitRepositoryState(
            new GitEnvironmentInfo(true, "git.exe", "2.x", null, null),
            new GitRepositoryInfo(true, "C:\\repo", "C:\\repo", "master", false, "sha", false, false, false, null, null),
            "C:\\repo", DateTimeOffset.UtcNow));
        coordinator.PublishRemoteState(new GitRemoteState(new[]
        {
            new GitRemote("origin", "Z:\\Test_Git\\ScriptDesk-Safety-Remote.git", "Z:\\Test_Git\\ScriptDesk-Safety-Remote.git"),
            new GitRemote("upstream", "https://example.invalid/upstream.git", "https://example.invalid/upstream.git")
        }));

        Assert.Equal("origin", remotes.SelectedRemote?.Name);
        coordinator.PublishRemoteState(new GitRemoteState(new[]
        {
            new GitRemote("origin", "https://github.com/octocat/Hello-World.git", "https://github.com/octocat/Hello-World.git"),
            new GitRemote("upstream", "https://example.invalid/upstream.git", "https://example.invalid/upstream.git")
        }));

        Assert.Equal("origin", remotes.SelectedRemote?.Name);
        Assert.Equal("origin", remotes.Remotes.First().Name);
    }

    [Fact]
    public void EmptyRemoteCollectionHasNoPlaceholderObjectText()
    {
        RunOnStaThread(() =>
        {
            var selector = new ComboBox
            {
                ItemsSource = Array.Empty<GitRemote>(),
                ItemTemplate = CreateNameTemplate()
            };

            Assert.Empty(selector.Items);
            Assert.Null(selector.SelectedItem);
            Assert.Equal(string.Empty, selector.SelectionBoxItem);
        });
    }

    private static DataTemplate CreateNameTemplate()
    {
        var template = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(TextBlock)) };
        template.VisualTree.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(GitRemote.Name)));
        return template;
    }

    private static string ExtractControlBlock(string xaml, string controlName)
    {
        var nameIndex = xaml.IndexOf($"x:Name=\"{controlName}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Expected to find {controlName}.");
        var start = xaml.LastIndexOf("<ComboBox", nameIndex, StringComparison.Ordinal);
        var end = xaml.IndexOf("</ComboBox>", nameIndex, StringComparison.Ordinal);
        Assert.True(start >= 0 && end >= 0);
        return xaml[start..(end + "</ComboBox>".Length)];
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); } catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
