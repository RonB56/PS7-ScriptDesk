using System.Windows;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Shell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class AdministratorModeBannerStateTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-1, true)]
    public void TokenElevationFlag_MapsToTheNativeElevationMeaning(int tokenIsElevated, bool expected)
    {
        Assert.Equal(expected, CurrentProcessElevation.IsElevatedTokenValue(tokenIsElevated));
    }

    [Fact]
    public void NonElevatedMode_CollapsesTheBannerWithoutLeavingLayoutSpace()
    {
        var state = AdministratorModeBannerState.Create(isElevated: false);

        Assert.Equal(Visibility.Collapsed, state.Visibility);
    }

    [Fact]
    public void ElevatedMode_ShowsTheExpectedAdministratorWarning()
    {
        var state = AdministratorModeBannerState.Create(isElevated: true);

        Assert.Equal(Visibility.Visible, state.Visibility);
        Assert.Equal("ADMINISTRATOR MODE", state.Heading);
        Assert.Contains("elevated privileges", state.WarningText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows security policy", state.WarningText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("drag-and-drop from non-elevated File Explorer", state.WarningText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BannerPresentation_IsIndependentOfTerminalAndDebuggerOutput()
    {
        var state = AdministratorModeBannerState.Create(isElevated: true);

        Assert.DoesNotContain("terminal", state.WarningText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("debugger", state.WarningText, StringComparison.OrdinalIgnoreCase);
    }
}
