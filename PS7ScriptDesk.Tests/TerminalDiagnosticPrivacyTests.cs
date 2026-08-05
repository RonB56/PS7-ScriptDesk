using System.Reflection;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalDiagnosticPrivacyTests
{
    [Fact]
    public void CreatePrivateTextMetadata_RecordsShapeWithoutContent()
    {
        const string sensitiveText = "password=CorrectHorseBatteryStaple\r\napi-key=secret-value";

        var metadata = DeveloperDiagnostics.CreatePrivateTextMetadata(sensitiveText);

        Assert.Equal(sensitiveText.Length, metadata["length"]);
        Assert.Equal(2, metadata["lineCount"]);
        Assert.Equal(true, metadata["contentOmitted"]);
        Assert.DoesNotContain("preview", metadata.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha256", metadata.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            metadata.Values,
            value => value is string text && text.Contains("CorrectHorseBatteryStaple", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, 0, 0)]
    [InlineData("", 0, 0)]
    [InlineData("single line", 11, 1)]
    [InlineData("one\ntwo\nthree", 13, 3)]
    public void CreatePrivateTextMetadata_ReportsLengthAndLineCount(string? text, int expectedLength, int expectedLineCount)
    {
        var metadata = DeveloperDiagnostics.CreatePrivateTextMetadata(text);

        Assert.Equal(expectedLength, metadata["length"]);
        Assert.Equal(expectedLineCount, metadata["lineCount"]);
        Assert.Equal(true, metadata["contentOmitted"]);
    }

    [Theory]
    [InlineData("TerminalCaptures/capture.log", true)]
    [InlineData("nested/TERMINALCAPTURES/capture.log", true)]
    [InlineData("PS7ScriptDesk.log", false)]
    [InlineData("TerminalCaptureSummary.log", false)]
    public void SupportPackageFilter_ExcludesLegacyTerminalCaptureDirectories(string relativePath, bool expectedExcluded)
    {
        var method = typeof(DeveloperDiagnostics).GetMethod(
            "IsLegacyTerminalCapturePath",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var excluded = Assert.IsType<bool>(method.Invoke(null, [relativePath]));
        Assert.Equal(expectedExcluded, excluded);
    }
}
