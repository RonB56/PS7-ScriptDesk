using System.Windows;

namespace PS7ScriptDesk.Shell.Services;

internal sealed record AdministratorModeBannerState(
    Visibility Visibility,
    string Heading,
    string Detail)
{
    internal const string AdministratorModeHeading = "ADMINISTRATOR MODE";
    internal const string AdministratorModeDetail = "Scripts launched from this session may run with elevated privileges. Windows security policy may block some features, including drag-and-drop from non-elevated File Explorer.";

    internal static AdministratorModeBannerState Create(bool isElevated)
    {
        return new AdministratorModeBannerState(
            isElevated ? Visibility.Visible : Visibility.Collapsed,
            AdministratorModeHeading,
            AdministratorModeDetail);
    }

    internal string WarningText => $"{Heading} — {Detail}";
}
