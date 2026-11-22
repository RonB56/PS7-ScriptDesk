using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class PsesExecutionPolicyDiagnosticsTests
{
    [Fact]
    public void LaunchArgumentDescription_RetainsPolicyAndRedactsPaths()
    {
        var arguments = PsesOwnedInteractiveSessionService.BuildArguments(
            @"C:\PSES", @"C:\PSES\Start-EditorServices.ps1", @"C:\Session\session.json",
            @"C:\Session\Logs", "2025.4.0");

        var description = PsesExecutionPolicyDiagnostics.DescribeArguments(arguments);

        Assert.Equal("Bypass", description[Array.IndexOf(description, "-ExecutionPolicy") + 1]);
        Assert.DoesNotContain(description, item => item.Contains(@"C:\", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("-EnableConsoleRepl", description);
    }

    [Fact]
    public async Task ZoneObservation_ReportsMarkingWithoutReadingScriptContentIntoDiagnostics()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ps7sd-policy-diagnostic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "harmless.ps1");
        try
        {
            await File.WriteAllTextAsync(path, "Write-Output 'CONTROLLED_OK'");
            var clean = await PsesExecutionPolicyDiagnostics.ObserveZoneAsync(path);
            Assert.True(clean.PathExists);
            Assert.False(clean.HasZoneIdentifier);

            await File.WriteAllTextAsync(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            var marked = await PsesExecutionPolicyDiagnostics.ObserveZoneAsync(path);
            Assert.True(marked.PathExists);
            Assert.True(marked.HasZoneIdentifier);
            Assert.Equal(3, marked.ZoneId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("File is not digitally signed", "ExecutionPolicyCandidate")]
    [InlineData("ordinary failure", "Other")]
    public void ErrorClassification_DoesNotReturnErrorText(string error, string expected)
        => Assert.Equal(expected, PsesExecutionPolicyDiagnostics.ClassifyError(error));
}
