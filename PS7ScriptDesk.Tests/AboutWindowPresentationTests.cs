using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Shell;
using PS7ScriptDesk.Shell.Help;
using PS7ScriptDesk.Shell.Presentation;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class AboutWindowPresentationTests
{
    [Fact]
    public void CreatePresentation_UsesPublicBrandingAndTheRunningShellVersion()
    {
        var presentation = AboutWindow.CreatePresentation();
        var informationalVersion = typeof(AboutWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        Assert.Equal(ApplicationBranding.PublicName, presentation.ProductName);
        Assert.Equal($"Version {AboutWindow.GetRunningVersionText()}", presentation.VersionText);
        Assert.False(string.IsNullOrWhiteSpace(presentation.Description));
        Assert.DoesNotContain(Environment.UserName, presentation.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, presentation.Description, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            Assert.Contains(informationalVersion.Split('+')[0].TrimStart('v', 'V'), presentation.VersionText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DisabledContextHelpMessage_ExplainsHowToRestoreHelp()
    {
        Assert.Equal(
            "Context Help is currently disabled. Enable ‘Help Enabled’ from the Help menu to use this feature.",
            ContextHelp.DisabledContextHelpMessage);
    }

    [Fact]
    public void HelpAndAboutWindows_InitializeWithTheExpectedPresentation()
    {
        RunOnStaThread(() =>
        {
            _ = EnsureShellApplication();

            var contextHelpWindow = new ContextHelpWindow(HelpTopicCatalog.Get(HelpTopicCatalog.OverviewKey));
            var contextHelpLabel = Assert.IsType<TextBlock>(contextHelpWindow.FindName("ContextHelpLabel"));

            Assert.Equal("Context Help", contextHelpLabel.Text);
            Assert.Null(contextHelpLabel.Background);

            contextHelpWindow.ShowHome();
            Assert.True(contextHelpWindow.IsShowingHome);
            Assert.True(contextHelpWindow.CanNavigateBack);
            Assert.True(contextHelpWindow.NavigateBack());
            Assert.False(contextHelpWindow.IsShowingHome);
            contextHelpWindow.ShowTopic(HelpTopicCatalog.Get("Command.RunScript"));
            Assert.False(contextHelpWindow.IsShowingHome);

            var aboutWindow = new AboutWindow();

            Assert.Equal("About PS7 ScriptDesk", aboutWindow.Title);
            Assert.Equal(WindowStartupLocation.CenterOwner, aboutWindow.WindowStartupLocation);
            Assert.False(aboutWindow.ShowInTaskbar);
            Assert.Equal(ResizeMode.NoResize, aboutWindow.ResizeMode);
        });
    }

    [Fact]
    public void StartupFailureWindow_DistinguishesGeneralStartupFromTerminalFailureWithoutATerminal()
    {
        RunOnStaThread(() =>
        {
            _ = EnsureShellApplication();
            var support = new SupportLogsPresenter(_ => { }, _ => { }, (_, _) => { });
            var general = new StartupFailureWindow(null, "startup fixture", support, terminalFailure: false);
            var terminal = new StartupFailureWindow(null, "PSES fixture", support);

            Assert.Equal("Application Startup Failed", general.Title);
            Assert.Equal("PS7 ScriptDesk could not complete application startup.",
                ((TextBlock)general.FindName("FailureDescriptionText")).Text);
            Assert.Equal("Terminal Startup Failed", terminal.Title);
            Assert.Equal("PSES fixture", ((TextBox)terminal.FindName("FailureDetailsText")).Text);
        });
    }

    private static void RunOnStaThread(Action action)
        => WpfTestDispatcher.Run(action);

    private static App EnsureShellApplication()
    {
        if (System.Windows.Application.Current is App existingApp)
        {
            return existingApp;
        }

        var app = new App();
        app.InitializeComponent();
        return app;
    }
}
