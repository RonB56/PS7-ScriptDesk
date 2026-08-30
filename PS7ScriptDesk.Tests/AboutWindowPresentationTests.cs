using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Shell;
using PS7ScriptDesk.Shell.Help;

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

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

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
