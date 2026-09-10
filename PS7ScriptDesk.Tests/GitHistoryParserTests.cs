using PS7ScriptDesk.Infrastructure.Services;

namespace PS7ScriptDesk.Tests;

public sealed class GitHistoryParserTests
{
    [Fact]
    public void ParsesCommitIdentityParentsAndDecorations()
    {
        var output = "\u001eabc123\0abc123\0parent1 parent2\0Ada\0ada@example.test\02026-09-09T10:00:00-04:00\02026-09-09T10:01:00-04:00\0Subject\0Subject\n\nBody\0(HEAD -> main, tag: v1)\u001e";
        var commit = Assert.Single(GitHistoryParser.ParseCommits(output));
        Assert.Equal(new[] { "parent1", "parent2" }, commit.ParentHashes);
        Assert.Equal("(HEAD -> main, tag: v1)", commit.Decoration);
        Assert.Equal("Body", commit.Body.Trim());
    }

    [Fact]
    public void ParsesModifiedAddedDeletedAndRenameStatusesWithSpaces()
    {
        var changes = GitHistoryParser.ParseNameStatus("M\0src/file.ps1\0A\0new file.ps1\0D\0old.ps1\0R100\0old name.ps1\0new name.ps1\0");
        Assert.Equal(4, changes.Count);
        Assert.Equal("new name.ps1", changes[^1].NewPath);
        Assert.True(changes[^1].IsRename);
    }
}
