using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Tests;

public sealed class ResizeForensicsTraceTests
{
    [Fact]
    public void FeatureGateDefaultsOffWhenUnset()
    {
        var original = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_RESIZE_FORENSICS");
        try
        {
            Environment.SetEnvironmentVariable("PS7SCRIPTDESK_RESIZE_FORENSICS", null);
            Assert.False(ResizeForensicsTrace.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PS7SCRIPTDESK_RESIZE_FORENSICS", original);
        }
    }

    [Fact]
    public void SanitizedPreviewIsCappedAndDoesNotChangePayload()
    {
        var payload = "abc\r\n" + new string('x', 500);
        var preview = ResizeForensicsTrace.SanitizePreview(payload);

        Assert.Equal(200, preview.Length);
        Assert.Equal(505, payload.Length);
        Assert.StartsWith("abc↵␊", preview);
    }
}
