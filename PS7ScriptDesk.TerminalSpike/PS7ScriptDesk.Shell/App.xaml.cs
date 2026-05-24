using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.PowerShell.Services;
using PS7ScriptDesk.Shell.Composition;

namespace PS7ScriptDesk.Shell
{
    public partial class App : System.Windows.Application
    {
        private static readonly string[] ConsolePrototypeSwitches =
        {
            "--console-prototype",
            "/console-prototype"
        };

        protected override void OnStartup(StartupEventArgs e)
        {
            AppLogger.Info("App", "Startup requested.");
            if (Editor.EditorMetadataBuilderHost.IsMetadataBuilderInvocation(e.Args))
            {
                AppLogger.Info("App", "Launching metadata builder helper mode.");
                var exitCode = Editor.EditorMetadataBuilderHost.RunFromArguments(e.Args);
                AppLogger.Info("App", $"Metadata builder helper mode finished with exit code {exitCode}.");
                Shutdown(exitCode);
                return;
            }

            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            if (SynchronizationContext.Current is null)
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher));
            }

            AppLogger.Info("App", "Cleaning stale execution snapshots from previous sessions.");
            LiveConsoleService.CleanupStaleExecutionSnapshots();
            ScriptExecutionService.CleanupStaleExecutionSnapshots();

            base.OnStartup(e);
            AppLogger.Info("App", "Base startup completed.");

            if (ShouldLaunchConsolePrototype(e.Args))
            {
                var prototypeWindow = new ConsolePrototypeWindow();
                MainWindow = prototypeWindow;
                AppLogger.Info("App", "Console prototype window created from command-line switch.");
                prototypeWindow.Show();
                AppLogger.Info("App", "Console prototype window shown.");
                return;
            }

            var shellWindow = AppBootstrapper.CreateMainWindow();
            MainWindow = shellWindow;
            AppLogger.Info("App", "Main window created.");
            shellWindow.Show();
            AppLogger.Info("App", "Main window shown.");
        }

        private static bool ShouldLaunchConsolePrototype(string[] args)
        {
            if (args is null || args.Length == 0)
            {
                return false;
            }

            foreach (var arg in args)
            {
                foreach (var candidate in ConsolePrototypeSwitches)
                {
                    if (string.Equals(arg, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        AppLogger.Info("App", $"Console prototype launch switch detected: {arg}");
                        return true;
                    }
                }
            }

            return false;
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            ReportStartupException("Dispatcher unhandled exception", e.Exception);
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception exception)
            {
                ReportStartupException("AppDomain unhandled exception", exception);
            }
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            ReportStartupException("Task scheduler unobserved exception", e.Exception);
            e.SetObserved();
        }

        private void ReportStartupException(string source, Exception exception)
        {
            AppLogger.Error("App", source, exception);
            try
            {
                var folderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PS7ScriptDesk");
                Directory.CreateDirectory(folderPath);
                var logPath = Path.Combine(folderPath, "startup-error.log");
                File.AppendAllText(
                    logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}");

                System.Windows.MessageBox.Show(
                    $"PS7ScriptDesk hit a startup/runtime exception.\n\nSource: {source}\n\nDetails: {exception.Message}\n\nA log was written to:\n{logPath}",
                    "PS7ScriptDesk Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // Last-resort handler only.
            }
        }
    }
}
