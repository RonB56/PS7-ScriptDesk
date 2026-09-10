using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;

namespace PS7ScriptDesk.Tests;

public sealed class GitDiffParserTests
{
    [Fact]
    public void ParsesContextAdditionsRemovalsAndHunks()
    {
        const string output = "--- a/test.ps1\n+++ b/test.ps1\n@@ -1,3 +1,4 @@\n keep\n-old\n+new\n+added\n";

        var diff = GitDiffParser.Parse(output, "test.ps1", GitDiffScope.Unstaged);

        Assert.Equal("test.ps1", diff.OldPath);
        Assert.Equal("test.ps1", diff.NewPath);
        Assert.Contains(diff.Lines, line => line.Kind == GitDiffLineKind.Removed && line.Text == "old");
        Assert.Contains(diff.Lines, line => line.Kind == GitDiffLineKind.Added && line.Text == "new");
        Assert.Contains(diff.Lines, line => line.Kind == GitDiffLineKind.HunkHeader);
    }

    [Fact]
    public void PreservesStagedScopeAndDetectsDeletedAndBinaryOutput()
    {
        var deleted = GitDiffParser.Parse("--- a/old.ps1\n+++ /dev/null\n@@ -1 +0,0 @@\n-gone\n", "old.ps1", GitDiffScope.Staged);
        var binary = GitDiffParser.Parse("Binary files a/image.bin and b/image.bin differ\n", "image.bin", GitDiffScope.Unstaged);

        Assert.Equal(GitDiffScope.Staged, deleted.Scope);
        Assert.True(deleted.IsDeleted);
        Assert.True(binary.IsBinary);
        Assert.Empty(binary.Lines);
    }

    [Fact]
    public void UntrackedDiffUsesAnExplicitNewFileBase()
    {
        var diff = new GitDiff("/dev/null", "new.ps1", GitDiffScope.Unstaged,
            new[] { new GitDiffLine(GitDiffLineKind.Added, "Write-Output hi", null, 1) }, false, true, false, false);

        Assert.True(diff.IsUntracked);
        Assert.Equal("/dev/null", diff.OldPath);
        Assert.Equal("new.ps1", diff.DisplayName);
    }

    [Fact]
    public void ParsesRenamePaths()
    {
        var diff = GitDiffParser.Parse("similarity index 100%\nrename from old.ps1\nrename to new.ps1\n", "new.ps1", GitDiffScope.Staged);

        Assert.True(diff.IsRenamed);
        Assert.Equal("old.ps1", diff.OldPath);
        Assert.Equal("new.ps1", diff.NewPath);
        Assert.Equal(100, diff.SimilarityIndex);
        Assert.True(diff.IsRenameOnly);
    }

    [Fact]
    public void AlignsUnequalReplacementBlocksAndKeepsHunksSeparate()
    {
        var diff = GitDiffParser.Parse("--- a/a.ps1\n+++ b/a.ps1\n@@ -1,3 +1,4 @@\n keep\n-old1\n-old2\n+new1\n+new2\n+new3\n@@ -8,1 +9,1 @@\n-old\n+new\n", "a.ps1", GitDiffScope.Unstaged);

        Assert.Equal(2, diff.Lines.Count(line => line.Kind == GitDiffLineKind.HunkHeader));
        Assert.Equal(2, diff.Hunks.Count);
        var rows = diff.Hunks[0].AlignedRows;
        Assert.Equal(4, rows.Count);
        Assert.Equal("new3", rows[^1].RightText);
        Assert.Single(diff.Hunks[1].AlignedRows);
    }

    [Fact]
    public void EmptyPureRenameHasMeaningfulStatus()
    {
        var diff = GitDiffParser.Parse("similarity index 100%\nrename from old file.ps1\nrename to new file.ps1\n", "new file.ps1", GitDiffScope.Unstaged);
        Assert.True(diff.IsRenameOnly);
        Assert.Equal("old file.ps1 → new file.ps1", diff.RenameText);
        Assert.Empty(diff.Hunks);
    }
}
