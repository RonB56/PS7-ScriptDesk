using System;
using System.Windows;

namespace PS7ScriptDesk.TerminalSpike
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            TerminalSpikeLogger.Info("App", "Terminal spike startup requested.");
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            TerminalSpikeLogger.Info("App", $"Terminal spike exiting with code {e.ApplicationExitCode}.");
            TerminalSpikeLogger.Dispose();
            base.OnExit(e);
        }

        private static void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            TerminalSpikeLogger.Error("App", "Dispatcher unhandled exception.", e.Exception);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            TerminalSpikeLogger.Debug("App", "Application activated.");
        }
    }
}
