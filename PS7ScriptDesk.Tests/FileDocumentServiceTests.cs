using System.Text;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Infrastructure.Services;

namespace PS7ScriptDesk.Tests;

public sealed class FileDocumentServiceTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk.Tests-{Guid.NewGuid():N}");
    private readonly FileDocumentService _service = new();

    public FileDocumentServiceTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public void WriteAllText_NewFile_PreservesUtf8WithoutBomAndLineEndings()
    {
        var path = Path.Combine(_testDirectory, "unicode.ps1");
        const string content = "Write-Output 'héllo'\r\nWrite-Output '世界'\n";

        _service.WriteAllText(path, content, DocumentFileState.Missing, "test-new");

        Assert.Equal(content, File.ReadAllText(path));
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Empty(GetTemporaryFiles());
    }

    [Fact]
    public void WriteAllText_ExistingFile_ReplacesOnlyExpectedVersion()
    {
        var path = CreateFile("existing.ps1", "old");
        var expected = _service.GetFileState(path);

        _service.WriteAllText(path, "new", expected, "test-replace");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Empty(GetTemporaryFiles());
    }

    [Fact]
    public void WriteAllText_StaleExpectedState_PreservesCurrentFileAndCleansTemporaryFile()
    {
        var path = CreateFile("stale.ps1", "original");
        var staleState = _service.GetFileState(path);
        File.WriteAllText(path, "external-change");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));

        Assert.Throws<DocumentFileChangedException>(() =>
            _service.WriteAllText(path, "editor-change", staleState, "test-stale"));

        Assert.Equal("external-change", File.ReadAllText(path));
        Assert.Empty(GetTemporaryFiles());
    }

    [Fact]
    public void WriteAllText_ReplacementFailure_PreservesOriginalAndCleansTemporaryFile()
    {
        var path = CreateFile("locked.ps1", "original");
        var expected = _service.GetFileState(path);
        using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() =>
                _service.WriteAllText(path, "replacement", expected, "test-locked"));
        }

        Assert.Equal("original", File.ReadAllText(path));
        Assert.Empty(GetTemporaryFiles());
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_testDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string[] GetTemporaryFiles()
    {
        return Directory.GetFiles(_testDirectory, "*.ps7scriptdesk-*.tmp", SearchOption.TopDirectoryOnly);
    }
}
