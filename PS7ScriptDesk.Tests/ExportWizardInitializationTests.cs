using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell.Dialogs;

namespace PS7ScriptDesk.Tests;

public sealed class ExportWizardInitializationTests
{
    [Theory]
    [InlineData(ExeExportPreset.PortableWindowsExe)]
    [InlineData(ExeExportPreset.WindowsConsoleApplication)]
    [InlineData(ExeExportPreset.WindowsGuiApplication)]
    [InlineData(ExeExportPreset.Arm64PortableExe)]
    [InlineData(ExeExportPreset.SmallExe)]
    [InlineData(ExeExportPreset.Custom)]
    public void EverySupportedPresetTag_ResolvesSafely(ExeExportPreset expected)
    {
        Assert.True(ExportWizardWindow.TryResolvePreset(expected.ToString(), out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NotAPreset")]
    [InlineData("999")]
    public void MissingOrInvalidPresetTag_IsRejectedWithoutException(string? presetTag)
    {
        Assert.False(ExportWizardWindow.TryResolvePreset(presetTag, out _));
    }

    [Theory]
    [InlineData(-1, 6, 0)]
    [InlineData(0, 6, 0)]
    [InlineData(3, 6, 3)]
    [InlineData(-1, 0, -1)]
    public void InitialPageSelection_NormalizesMissingSelectionToFirstPage(int selectedIndex, int itemCount, int expected)
    {
        Assert.Equal(expected, ExportWizardWindow.ResolveInitialPageIndex(selectedIndex, itemCount));
    }
}
