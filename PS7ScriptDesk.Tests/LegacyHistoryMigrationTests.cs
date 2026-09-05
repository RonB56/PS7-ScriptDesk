using System.Diagnostics;
using System.IO;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class LegacyHistoryMigrationTests
{
    private const string Root = @"C:\Users\tester\AppData\Local\PS7ScriptDesk\Temp\TerminalSnapshots";
    private const string GuidA = "0123456789abcdef0123456789abcdef";
    private const string GuidB = "fedcba9876543210fedcba9876543210";

    [Theory]
    [InlineData("&", false)]
    [InlineData(".", true)]
    public void ExactLegacyPsdWrapperIsMatched(string op, bool marked)
    {
        var marker = marked ? " #PS7SDi" : string.Empty;
        Assert.True(LegacyHistoryMigration.IsLegacyManagedLine($" {op} '{Root}\\psd-{GuidA}.ps1'{marker} ", Root));
    }

    [Fact]
    public void ExactLegacyHelperPairIsMatched()
    {
        Assert.True(LegacyHistoryMigration.IsLegacyManagedLine($"& '{Root}\\psh-{GuidA}.ps1' '{Root}\\psi-{GuidB}.ps1'", Root));
    }

    [Theory]
    [InlineData("Get-Process # ScriptDesk")]
    [InlineData("Get-ChildItem TerminalSnapshots")]
    [InlineData("& 'C:\\other\\psd-0123456789abcdef0123456789abcdef.ps1'")]
    [InlineData("& 'C:\\Users\\tester\\AppData\\Local\\PS7ScriptDesk\\Temp\\TerminalSnapshots\\psd-not-a-guid.ps1'")]
    [InlineData("& 'C:\\Users\\tester\\AppData\\Local\\PS7ScriptDesk\\Temp\\TerminalSnapshots\\pss-0123456789abcdef0123456789abcdef.ps1'")]
    [InlineData("& 'C:\\Users\\tester\\AppData\\Local\\PS7ScriptDesk\\Temp\\TerminalSnapshots\\psd-0123456789abcdef0123456789abcdef.ps1' -Extra")]
    public void SimilarOrNonWrapperCommandsArePreserved(string line)
    {
        Assert.False(LegacyHistoryMigration.IsLegacyManagedLine(line, Root));
    }

    [Fact]
    public void CurrentStartupContainsMigrationAndDoesNotUseClearHistoryCmdlet()
    {
        var command = LegacyHistoryMigration.BuildStartupCommand();
        Assert.Contains("ComputeHash", command, StringComparison.Ordinal);
        Assert.DoesNotContain("SHA256]::HashData", command, StringComparison.Ordinal);
        Assert.Contains("HistorySavePath", command, StringComparison.Ordinal);
        Assert.Contains("GetHistoryItems", command, StringComparison.Ordinal);
        Assert.Contains("PSREADLINE_INITIALIZED", command, StringComparison.Ordinal);
        Assert.Contains("LEGACY_HISTORY_MEMORY_VERIFY", command, StringComparison.Ordinal);
        Assert.True(command.IndexOf("PSREADLINE_INITIALIZED", StringComparison.Ordinal) > command.IndexOf("GetHistoryItems", StringComparison.Ordinal));
        Assert.DoesNotContain("Clear-History", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-History", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AtomicReplaceUsesDistinctTransactionalBackupAndDoesNotPassNullBackupPath()
    {
        var command = LegacyHistoryMigration.BuildStartupCommand();

        Assert.Contains("[IO.File]::Replace($temp, $historyPath, $transactionalBackup, $true)", command, StringComparison.Ordinal);
        Assert.Contains("$transactionalBackup =", command, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $transactionalBackup", command, StringComparison.Ordinal);
        Assert.DoesNotContain("[IO.File]::Replace($temp, $historyPath, $null, $true)", command, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellFileReplaceWithTransactionalBackupPreservesOriginalAndReplacesDestination()
    {
        var root = Path.Combine(Path.GetTempPath(), "ps7sd-atomic-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var temp = Path.Combine(root, "filtered.tmp");
            var destination = Path.Combine(root, "history.txt");
            var transactionalBackup = Path.Combine(root, "transactional.bak");
            File.WriteAllText(temp, "filtered");
            File.WriteAllText(destination, "original");

            var psi = new ProcessStartInfo
            {
                FileName = @"C:\Program Files\PowerShell\7\pwsh.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add($"[IO.File]::Replace('{temp}', '{destination}', '{transactionalBackup}', $true)");

            using var process = Process.Start(psi)!;
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("filtered", File.ReadAllText(destination));
            Assert.Equal("original", File.ReadAllText(transactionalBackup));
            Assert.False(File.Exists(temp));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
