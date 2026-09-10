using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Tests;

public sealed class SourceControlViewModelTests
{
    [Fact]
    public void ProjectsStagedAndUnstagedChangesIntoLogicalGroups()
    {
        var viewModel = new SourceControlViewModel();
        var changes = new[]
        {
            Create("both.ps1", 'M', 'M'),
            Create("staged.ps1", 'A', ' '),
            Create("untracked.ps1", ' ', '?', tracked: false, untracked: true),
            Create("conflict.ps1", 'U', 'U', conflicted: true)
        };
        var state = new GitRepositoryState(
            new GitEnvironmentInfo(true, "git.exe", "2.0", null, null),
            new GitRepositoryInfo(true, "C:\\repo", "C:\\repo", "main", false, "abc", false, false, false, null, null),
            "C:\\repo",
            DateTimeOffset.UtcNow)
        { Changes = changes };

        viewModel.ApplyState(state, "C:\\repo", null);

        Assert.Equal(new[] { "CHANGES", "STAGED CHANGES", "UNTRACKED", "CONFLICTS" }, viewModel.Groups.Select(group => group.Title));
        Assert.Equal(1, viewModel.Groups.Single(group => group.Title == "CHANGES").Count);
        Assert.Equal(2, viewModel.Groups.Single(group => group.Title == "STAGED CHANGES").Count);
        Assert.Equal(1, viewModel.Groups.Single(group => group.Title == "UNTRACKED").Count);
        Assert.Equal(1, viewModel.Groups.Single(group => group.Title == "CONFLICTS").Count);
    }

    [Fact]
    public void FiltersWithoutRerunningGitAndSupportsCurrentFileScope()
    {
        var viewModel = new SourceControlViewModel();
        var changes = new[] { Create("one.ps1", ' ', 'M'), Create("two.ps1", ' ', 'M') };
        var state = new GitRepositoryState(
            new GitEnvironmentInfo(true, "git.exe", "2.0", null, null),
            new GitRepositoryInfo(true, "C:\\repo", "C:\\repo", "main", false, "abc", false, false, false, null, null),
            "C:\\repo",
            DateTimeOffset.UtcNow)
        { Changes = changes };
        viewModel.ApplyState(state, "C:\\repo", "C:\\repo\\one.ps1");

        viewModel.Scope = SourceControlScope.CurrentFile;
        viewModel.FilterText = "one";

        Assert.Single(viewModel.Groups);
        Assert.Single(viewModel.Groups[0].Items);
        Assert.Equal("one.ps1", viewModel.Groups[0].Items[0].RelativePath);
    }

    private static GitFileStatus Create(string relativePath, char index, char working, bool tracked = true, bool untracked = false, bool conflicted = false)
        => new($"C:\\repo\\{relativePath}", relativePath, null, index, working, tracked, untracked, conflicted,
            index == 'R' || working == 'R', index == 'D' || working == 'D', index == 'A' || working == 'A', index == 'M' || working == 'M', false);
}
