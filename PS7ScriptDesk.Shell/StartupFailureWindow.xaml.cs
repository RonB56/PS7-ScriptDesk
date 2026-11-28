using System.Windows;
using PS7ScriptDesk.Shell.Presentation;

namespace PS7ScriptDesk.Shell;

internal partial class StartupFailureWindow : Window
{
    private readonly SupportLogsPresenter _supportLogsPresenter;

    internal StartupFailureWindow(Window? owner, string details, SupportLogsPresenter supportLogsPresenter, bool terminalFailure = true)
    {
        InitializeComponent();
        if (owner is not null) Owner = owner;
        _supportLogsPresenter = supportLogsPresenter;
        FailureDetailsText.Text = details;
        if (!terminalFailure)
        {
            Title = "Application Startup Failed";
            FailureDescriptionText.Text = "PS7 ScriptDesk could not complete application startup.";
            System.Windows.Automation.AutomationProperties.SetName(this, "Application startup failure");
            System.Windows.Automation.AutomationProperties.SetName(FailureDetailsText, "Application startup failure details");
        }
    }

    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(FailureDetailsText.Text);
            CopyStatusText.Text = "Details copied.";
        }
        catch
        {
            CopyStatusText.Text = "Copy failed. The details remain available above; try again.";
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => _supportLogsPresenter.OpenLogsFolder();

    private void Dismiss_Click(object sender, RoutedEventArgs e) => Close();
}
