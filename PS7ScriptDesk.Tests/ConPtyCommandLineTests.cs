using System.Diagnostics;
using System.Text.Json;
using PS7ScriptDesk.PowerShell.Services;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class ConPtyCommandLineTests
{
    [Fact]
    public async Task Quote_ArgumentsRoundTripThroughWindowsPowerShellProcessParser()
    {
        var pwsh = @"C:\Program Files\PowerShell\7\pwsh.exe";
        Assert.True(File.Exists(pwsh), $"Required test runtime was not found: {pwsh}");

        var root = Path.Combine(Path.GetTempPath(), $"ScriptDesk arguments {Guid.NewGuid():N} Ω's");
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "record arguments.ps1");
        var output = Path.Combine(root, "arguments.json");
        var expected = new[]
        {
            "plain", "two words", "", "embedded\"quote", "trailing slash\\",
            @"C:\path with spaces\", "apostrophe's", "Ω漢字"
        };

        try
        {
            await File.WriteAllTextAsync(script, "ConvertTo-Json -InputObject @($args) -Compress | Set-Content -LiteralPath $env:ARGUMENT_RECORD -Encoding utf8");
            var arguments = new[] { "-NoLogo", "-NoProfile", "-File", script }.Concat(expected);
            var start = new ProcessStartInfo(pwsh)
            {
                Arguments = string.Join(" ", arguments.Select(ConPtyProcess.Quote)),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.Environment["ARGUMENT_RECORD"] = output;

            using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell child process did not start.");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, process.ExitCode);
            var actual = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(output));
            Assert.Equal(expected, actual);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
