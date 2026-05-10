using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PowerShellStudio.Application.Diagnostics;
using PowerShellStudio.Application.Utilities;
using PowerShellStudio.PowerShell.Services;
using PowerShellStudio.Shell.Composition;

namespace PowerShellStudio.Shell
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
            DeveloperDiagnostics.TryPreconfigureFromPersistedSettings();
            DeveloperDiagnostics.LogMethodEntry(
                "Startup",
                "App.OnStartup entered.",
                new Dictionary<string, object?>
                {
                    ["args"] = e.Args is null ? Array.Empty<string>() : Array.ConvertAll(e.Args, arg => DeveloperDiagnostics.SanitizePreview(arg))
                });
            AppLogger.Info("App", "Startup requested.");
            if (Editor.EditorMetadataBuilderHost.IsMetadataBuilderInvocation(e.Args))
            {
                AppLogger.Info("App", "Launching metadata builder helper mode.");
                DeveloperDiagnostics.LogDecision("Startup", "MetadataBuilderInvocation", "Launching metadata builder helper mode.", "MetadataBuilderMode");
                var exitCode = Editor.EditorMetadataBuilderHost.RunFromArguments(e.Args);
                AppLogger.Info("App", $"Metadata builder helper mode finished with exit code {exitCode}.");
                DeveloperDiagnostics.LogMethodExit("Startup", $"Metadata builder helper mode finished with exit code {exitCode}.");
                Shutdown(exitCode);
                return;
            }

            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            if (SynchronizationContext.Current is null)
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher));
                DeveloperDiagnostics.LogInfo("Startup", "Dispatcher synchronization context was initialized.");
            }

            AppLogger.Info("App", "Cleaning stale execution snapshots from previous sessions.");
            DeveloperDiagnostics.LogInfo("Execution", "Cleaning stale execution snapshots from previous sessions.");
            LiveConsoleService.CleanupStaleExecutionSnapshots();
            ScriptExecutionService.CleanupStaleExecutionSnapshots();

            base.OnStartup(e);
            AppLogger.Info("App", "Base startup completed.");
            DeveloperDiagnostics.LogInfo("Startup", "Base application startup completed.");

            if (ShouldLaunchConsolePrototype(e.Args))
            {
                var prototypeWindow = new ConsolePrototypeWindow();
                MainWindow = prototypeWindow;
                AppLogger.Info("App", "Console prototype window created from command-line switch.");
                DeveloperDiagnostics.LogInfo("Startup", "Console prototype window created from command-line switch.");
                prototypeWindow.Show();
                AppLogger.Info("App", "Console prototype window shown.");
                DeveloperDiagnostics.LogMethodExit("Startup", "Console prototype window shown; OnStartup completed.");
                return;
            }

            var shellWindow = AppBootstrapper.CreateMainWindow();
            MainWindow = shellWindow;
            AppLogger.Info("App", "Main window created.");
            DeveloperDiagnostics.LogInfo("Startup", "Main window created by AppBootstrapper.");
            shellWindow.Show();
            AppLogger.Info("App", "Main window shown.");
            DeveloperDiagnostics.LogMethodExit("Startup", "Main window shown; OnStartup completed.");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            DeveloperDiagnostics.LogInfo("Startup", $"App.OnExit invoked with exit code {e.ApplicationExitCode}.");
            DeveloperDiagnostics.Shutdown();
            base.OnExit(e);
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
            DeveloperDiagnostics.LogException("Startup", exception, source);
            try
            {
                var folderPath = ApplicationBranding.LocalApplicationDataRoot;
                Directory.CreateDirectory(folderPath);
                var logPath = Path.Combine(folderPath, "startup-error.log");
                File.AppendAllText(
                    logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}");

                System.Windows.MessageBox.Show(
                    $"PS7 ScriptDesk hit a startup/runtime exception.\n\nSource: {source}\n\nDetails: {exception.Message}\n\nA log was written to:\n{logPath}",
                    $"{ApplicationBranding.PublicName} Error",
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
