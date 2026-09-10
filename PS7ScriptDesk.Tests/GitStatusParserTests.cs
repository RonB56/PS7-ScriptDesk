using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;

namespace PS7ScriptDesk.Tests;

public sealed class GitStatusParserTests
{
    [Fact]
    public void ParsesOrdinaryStagedAndUnstagedRecordsWithoutOpeningDocuments()
    {
        var output = string.Join('\0',
            "1 M. N... 100644 100644 100644 abc def Scripts/Changed File.ps1",
            "1 .M N... 100644 100644 100644 abc def Scripts/Working File.ps1",
            "1 MM N... 100644 100644 100644 abc def Scripts/Both.ps1") + '\0';

        var statuses = GitStatusParser.Parse(output, "C:\\repo");

        Assert.Equal(3, statuses.Count);
        Assert.True(statuses[0].IsStaged);
        Assert.False(statuses[0].HasUnstagedChanges);
        Assert.True(statuses[1].HasUnstagedChanges);
        Assert.False(statuses[1].IsStaged);
        Assert.True(statuses[2].IsStaged);
        Assert.True(statuses[2].HasUnstagedChanges);
        Assert.All(statuses, status => Assert.DoesNotContain("EditorTabViewModel", status.FullPath, StringComparison.Ordinal));
    }

    [Fact]
    public void ParsesAddedDeletedUntrackedAndConflictRecords()
    {
        var output = string.Join('\0',
            "1 A. N... 000000 100644 100644 000 111 Added.ps1",
            "1 .D N... 100644 100644 000 111 000 Deleted.ps1",
            "? Notes and scratch.txt",
            "u UU N... 100644 100644 100644 100644 111 222 333 Conflict.ps1") + '\0';

        var statuses = GitStatusParser.Parse(output, "C:\\repo");

        Assert.Equal(4, statuses.Count);
        Assert.Contains(statuses, status => status.IsAdded && !status.IsUntracked);
        Assert.Contains(statuses, status => status.IsDeleted);
        Assert.Contains(statuses, status => status.IsUntracked && status.StatusGlyph == "?");
        Assert.Contains(statuses, status => status.IsConflicted && status.StatusGlyph == "U");
    }

    [Theory]
    [InlineData("DD")]
    [InlineData("AU")]
    [InlineData("UD")]
    [InlineData("UA")]
    [InlineData("DU")]
    [InlineData("AA")]
    [InlineData("UU")]
    public void PreservesEverySupportedUnmergedStatusCode(string xy)
    {
        var output = $"u {xy} N... 100644 100644 100644 100644 111 222 333 Conflict {xy}.ps1\0";

        var status = Assert.Single(GitStatusParser.Parse(output, "C:\\repo"));

        Assert.True(status.IsConflicted);
        Assert.Equal(xy[0], status.IndexStatus);
        Assert.Equal(xy[1], status.WorkingTreeStatus);
        Assert.False(status.IsStaged);
        Assert.False(status.IsUntracked);
    }

    [Fact]
    public void ParsesRenameCopySpacesAndUnicodePaths()
    {
        var output = string.Join('\0',
            "2 R. N... 100644 100644 100644 abc def R100 New Folder/renamed file.ps1",
            "Old Folder/original file.ps1",
            "2 C. N... 100644 100644 100644 abc def C100 Unicode/日本語.ps1",
            "Unicode/source.ps1") + '\0';

        var statuses = GitStatusParser.Parse(output, "C:\\repo");

        Assert.Equal(2, statuses.Count);
        Assert.True(statuses[0].IsRenamed);
        Assert.Equal("Old Folder/original file.ps1", statuses[0].OriginalPath);
        Assert.True(statuses[1].IsCopied);
        Assert.Equal("Unicode/source.ps1", statuses[1].OriginalPath);
        Assert.Contains("日本語", statuses[1].FullPath, StringComparison.Ordinal);
    }

    [Fact]
    public void IgnoresHeadersIgnoredAndMalformedRecords()
    {
        var output = string.Join('\0',
            "# branch.oid abc",
            "! ignored.log",
            "1 malformed",
            "x future format") + '\0';

        Assert.Empty(GitStatusParser.Parse(output, "C:\\repo"));
    }

    [Fact]
    public void HandlesLargeStatusSetsDeterministically()
    {
        var records = Enumerable.Range(0, 2000)
            .Select(index => $"1 .M N... 100644 100644 100644 abc def Folder{index % 20}/File{index}.ps1");

        var statuses = GitStatusParser.Parse(string.Join('\0', records) + '\0', "C:\\repo");

        Assert.Equal(2000, statuses.Count);
        Assert.Equal("Folder0/File0.ps1", statuses[0].RelativePath);
        Assert.Equal("Folder19/File1999.ps1", statuses[^1].RelativePath);
    }
}
