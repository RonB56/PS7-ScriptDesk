using System.Reflection;
using System.Text.RegularExpressions;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class PsesPromptReadinessContractTests
{
    [Fact]
    public void ConcatenatedPsesBannerAndPrompt_IsRecognizedAsReadyBoundary()
    {
        var pattern = typeof(PsesOwnedInteractiveSessionService)
            .GetField("PromptBoundaryRegex", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null) as Regex;

        Assert.NotNull(pattern);
        Assert.Matches(pattern!, "=====> PowerShell Editor Services <=====PS C:\\Program Files\\PowerShell>");
    }

    [Fact]
    public void OrdinaryOutputContainingPsText_DoesNotBecomeReadyBoundary()
    {
        var pattern = typeof(PsesOwnedInteractiveSessionService)
            .GetField("PromptBoundaryRegex", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null) as Regex;

        Assert.NotNull(pattern);
        Assert.DoesNotMatch(pattern!, "status=PS not a prompt>");
    }

    [Theory]
    [InlineData("PS C:\\>")]
    [InlineData("4\r\nPS C:\\>")]
    [InlineData("\u001b[32mPS C:\\>\u001b[0m")]
    public void DriveRootPrompt_IsRecognizedAsReadyBoundary(string output)
    {
        var pattern = typeof(PsesOwnedInteractiveSessionService)
            .GetField("PromptBoundaryRegex", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null) as Regex;

        Assert.NotNull(pattern);
        var normalized = Regex.Replace(output, "\\x1B\\[[0-?]*[ -/]*[@-~]", string.Empty);
        Assert.Matches(pattern!, normalized);
    }
}
