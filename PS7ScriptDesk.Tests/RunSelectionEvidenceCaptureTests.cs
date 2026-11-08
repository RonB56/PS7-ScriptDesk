using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

[Collection("DiagnosticReliability")]
public sealed class RunSelectionEvidenceCaptureTests
{
    [Theory]
    [InlineData(false, "1", false)]
    [InlineData(true, null, false)]
    [InlineData(true, "0", false)]
    [InlineData(true, "true", false)]
    [InlineData(true, "1", true)]
    public void CaptureRequiresDeveloperDiagnosticsAndExplicitEnvironmentOptIn(
        bool developerDiagnosticsEnabled,
        string? settingValue,
        bool expected)
    {
        Assert.Equal(expected, RunSelectionEvidenceCapture.IsCaptureEnabled(developerDiagnosticsEnabled, settingValue));
    }

    [Fact]
    public void CaptureLimitsAreBoundedAndRejectOversizedOrNegativeLengths()
    {
        Assert.True(RunSelectionEvidenceCapture.IsWithinCaptureLimits(0, 0, 0));
        Assert.True(RunSelectionEvidenceCapture.IsWithinCaptureLimits(
            RunSelectionEvidenceCapture.MaximumScriptBytes,
            RunSelectionEvidenceCapture.MaximumScriptBytes,
            RunSelectionEvidenceCapture.MaximumWrapperBytes));
        Assert.False(RunSelectionEvidenceCapture.IsWithinCaptureLimits(
            RunSelectionEvidenceCapture.MaximumScriptBytes + 1, 0, 0));
        Assert.False(RunSelectionEvidenceCapture.IsWithinCaptureLimits(0, 0,
            RunSelectionEvidenceCapture.MaximumWrapperBytes + 1));
        Assert.False(RunSelectionEvidenceCapture.IsWithinCaptureLimits(-1, 0, 0));
    }
}
