using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PowerShellStudio.Application.Diagnostics;
using PowerShellStudio.Application.Interfaces;
using PowerShellStudio.Application.Utilities;
using PowerShellStudio.Domain.Models;
using PowerShellStudio.Infrastructure.Services;
using PowerShellStudio.PowerShell.Services;
using PowerShellStudio.Shell.Composition;
using PowerShellStudio.Shell.Services;

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
            var startupArgs = e.Args ?? Array.Empty<string>();

            try
            {
                StartupTimingLogger.StartSession("App.OnStartup");
                LogStartupEnvironment(startupArgs);
            }
            catch (Exception ex)
            {
                AppLogger.Warning("App", $"Startup diagnostics initialization failed: {ex.GetType().Name}: {ex.Message}");
            }

            DeveloperDiagnostics.TryPreconfigureFromPersistedSettings();
            DeveloperDiagnostics.LogMethodEntry(
                "Startup",
                "App.OnStartup entered.",
                new Dictionary<string, object?>
                {
                    ["args"] = Array.ConvertAll(startupArgs, arg => DeveloperDiagnostics.SanitizePreview(arg))
                });
            AppLogger.Info("App", "Startup requested.");
            if (Editor.EditorMetadataBuilderHost.IsMetadataBuilderInvocation(startupArgs))
            {
                AppLogger.Info("App", "Launching metadata builder helper mode.");
                DeveloperDiagnostics.LogDecision("Startup", "MetadataBuilderInvocation", "Launching metadata builder helper mode.", "MetadataBuilderMode");
                var exitCode = Editor.EditorMetadataBuilderHost.RunFromArguments(startupArgs);
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

            if (ShouldLaunchConsolePrototype(startupArgs))
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

            var applicationSettingsService = new ApplicationSettingsService();
            var applicationSettings = applicationSettingsService.LoadSettings();
            DeveloperDiagnostics.LogInfo(
                "Startup",
                "Loaded settings for startup runtime validation.",
                new Dictionary<string, object?>
                {
                    ["settingsPath"] = applicationSettingsService.SettingsFilePath,
                    ["savedRuntimePath"] = applicationSettings.SelectedRuntimeExecutablePath
                });

            var startupRuntime = ResolveStartupRuntime(applicationSettings);
            if (startupRuntime is null)
            {
                AppLogger.Info("App", "Startup canceled because no valid PowerShell 7 runtime was available.");
                DeveloperDiagnostics.LogDecision("Startup", "ResolveStartupRuntime", "Application startup exited because no valid PowerShell 7 runtime was available.", "ExitWithoutMainWindow");
                Shutdown(0);
                return;
            }

            applicationSettings.SelectedRuntimeExecutablePath = startupRuntime.LaunchExecutablePath;
            SafeSaveSettings(applicationSettingsService, applicationSettings, startupRuntime);

            var shellWindow = AppBootstrapper.CreateMainWindow(applicationSettingsService, applicationSettings);
            MainWindow = shellWindow;
            AppLogger.Info("App", "Main window created.");
            DeveloperDiagnostics.LogInfo("Startup", "Main window created by AppBootstrapper.");
            shellWindow.Show();
            AppLogger.Info("App", "Main window shown.");
            _ = CheckForStoreUpdatesAfterStartupAsync(shellWindow);
            DeveloperDiagnostics.LogMethodExit("Startup", "Main window shown; OnStartup completed.");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            DeveloperDiagnostics.LogInfo("Startup", $"App.OnExit invoked with exit code {e.ApplicationExitCode}.");
            DeveloperDiagnostics.Shutdown();
            base.OnExit(e);
        }

        private static void LogStartupEnvironment(string[] startupArgs)
        {
            try
            {
                Directory.CreateDirectory(AppLogger.CurrentLogDirectory);
                StartupTimingLogger.Log("App", $"Startup args count: {startupArgs.Length}.");
                StartupTimingLogger.Log("App", $"App version: {ResolveAppVersion()}.");
                StartupTimingLogger.Log("App", $"Process path: {Environment.ProcessPath ?? string.Empty}.");
                StartupTimingLogger.Log("App", $"Base directory: {AppContext.BaseDirectory}.");
                StartupTimingLogger.Log("App", $"Current directory: {Environment.CurrentDirectory}.");
                StartupTimingLogger.Log("App", $"OS description: {RuntimeInformation.OSDescription}.");
                StartupTimingLogger.Log("App", $"OS architecture: {RuntimeInformation.OSArchitecture}; Process architecture: {RuntimeInformation.ProcessArchitecture}.");
                StartupTimingLogger.Log("App", $".NET runtime: {RuntimeInformation.FrameworkDescription}; Environment.Version={Environment.Version}.");
                StartupTimingLogger.Log("App", $"Packaged process guess: {DetectPackagedProcess()}.");
                StartupTimingLogger.Log("App", $"LocalApplicationData: {Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}.");
                StartupTimingLogger.Log("App", $"App data root: {ApplicationBranding.LocalApplicationDataRoot}.");
                StartupTimingLogger.Log("App", $"App logs folder: {AppLogger.CurrentLogDirectory}.");
                StartupTimingLogger.Log("App", $"App log file: {AppLogger.CurrentLogPath}.");

                AppLogger.Info(
                    "App",
                    $"Startup environment captured. Version={ResolveAppVersion()}, ProcessPath='{Environment.ProcessPath ?? string.Empty}', " +
                    $"PackagedGuess={DetectPackagedProcess()}, AppDataRoot='{ApplicationBranding.LocalApplicationDataRoot}', Logs='{AppLogger.CurrentLogDirectory}'.");
            }
            catch (Exception ex)
            {
                AppLogger.Warning("App", $"Startup environment logging failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string ResolveAppVersion()
        {
            try
            {
                var version = Assembly.GetEntryAssembly()?.GetName().Version
                    ?? Assembly.GetExecutingAssembly().GetName().Version;
                return version?.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        private static string DetectPackagedProcess()
        {
            try
            {
                var processPath = Environment.ProcessPath ?? string.Empty;
                var baseDirectory = AppContext.BaseDirectory ?? string.Empty;
                var packageFamilyName = Environment.GetEnvironmentVariable("APPX_PACKAGE_FAMILY_NAME") ?? string.Empty;
                var hasWindowsAppsPath = processPath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                                         baseDirectory.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase);
                return $"PackageFamilyName='{packageFamilyName}', WindowsAppsPath={hasWindowsAppsPath}";
            }
            catch (Exception ex)
            {
                return $"Unknown ({ex.GetType().Name}: {ex.Message})";
            }
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

        private PowerShellRuntimeInfo? ResolveStartupRuntime(ApplicationSettings applicationSettings)
        {
            var operationId = $"StartupRuntimeGate-{Guid.NewGuid():N}";
            using var startupRuntimeScope = DeveloperDiagnostics.BeginTimedOperation(
                "Startup",
                "ResolveStartupRuntime",
                "Startup runtime gate validation started.",
                operationId: operationId);

            var runtimeService = new RuntimeService(applicationSettings.SelectedRuntimeExecutablePath);
            var discoveryResult = runtimeService.DiscoverRuntimes();
            var preferredRuntime = discoveryResult.PreferredRuntime;
            AppLogger.Info(
                "App",
                $"Startup runtime gate evaluated. ConfiguredRuntimePath='{applicationSettings.SelectedRuntimeExecutablePath ?? string.Empty}', " +
                $"SelectedDisplayPath='{preferredRuntime?.ExecutablePath ?? string.Empty}', SelectedLaunchPath='{preferredRuntime?.LaunchExecutablePath ?? string.Empty}', " +
                $"LaunchPathExists={(preferredRuntime is not null && File.Exists(preferredRuntime.LaunchExecutablePath))}, CandidateCount={discoveryResult.CandidateResults.Count}.");

            if (preferredRuntime is not null)
            {
                AppLogger.Info(
                    "App",
                    $"Startup runtime selected: {preferredRuntime.DisplayName}. DisplayPath='{preferredRuntime.ExecutablePath}', LaunchPath='{preferredRuntime.LaunchExecutablePath}', Version='{preferredRuntime.VersionText}', PSHOME='{preferredRuntime.PsHome}'.");
                DeveloperDiagnostics.LogDecision(
                    "Startup",
                    "ResolveStartupRuntime",
                    "Startup runtime discovery found a valid PowerShell 7 runtime.",
                    "Discovered",
                    new Dictionary<string, object?>
                    {
                        ["runtimePath"] = preferredRuntime.LaunchExecutablePath,
                        ["displayPath"] = preferredRuntime.ExecutablePath,
                        ["runtimeVersion"] = preferredRuntime.VersionText,
                        ["candidateCount"] = discoveryResult.CandidateResults.Count
                    });
                return preferredRuntime;
            }

            AppLogger.Info("App", "Startup runtime discovery did not find a valid PowerShell 7 runtime; showing resolver dialog.");
            DeveloperDiagnostics.LogDecision(
                "Startup",
                "ResolveStartupRuntime",
                "Startup runtime discovery did not find a valid PowerShell 7 runtime. Showing startup resolver dialog.",
                "ShowResolver",
                new Dictionary<string, object?>
                {
                    ["candidateCount"] = discoveryResult.CandidateResults.Count,
                    ["savedRuntimePath"] = applicationSettings.SelectedRuntimeExecutablePath
                });

            var resolverWindow = new RuntimeResolverWindow(runtimeService);
            var dialogResult = resolverWindow.ShowDialog();
            if (dialogResult == true && resolverWindow.SelectedRuntime is not null)
            {
                AppLogger.Info(
                    "App",
                    $"Startup runtime selected from resolver dialog: {resolverWindow.SelectedRuntime.DisplayName}. DisplayPath='{resolverWindow.SelectedRuntime.ExecutablePath}', LaunchPath='{resolverWindow.SelectedRuntime.LaunchExecutablePath}', Version='{resolverWindow.SelectedRuntime.VersionText}'.");
                DeveloperDiagnostics.LogDecision(
                    "Startup",
                    "ResolveStartupRuntime",
                    "Startup resolver dialog returned a valid PowerShell 7 runtime.",
                    "ResolverAccepted",
                    new Dictionary<string, object?>
                    {
                        ["runtimePath"] = resolverWindow.SelectedRuntime.LaunchExecutablePath,
                        ["displayPath"] = resolverWindow.SelectedRuntime.ExecutablePath,
                        ["runtimeVersion"] = resolverWindow.SelectedRuntime.VersionText
                    });
                return resolverWindow.SelectedRuntime;
            }

            DeveloperDiagnostics.LogDecision(
                "Startup",
                "ResolveStartupRuntime",
                "Startup resolver dialog was closed without a valid PowerShell 7 runtime.",
                "ResolverCanceled");
            return null;
        }

        private static void SafeSaveSettings(IApplicationSettingsService applicationSettingsService, ApplicationSettings applicationSettings, PowerShellRuntimeInfo runtimeInfo)
        {
            try
            {
                applicationSettingsService.SaveSettings(applicationSettings);
                AppLogger.Info("App", $"Saved validated startup runtime '{runtimeInfo.LaunchExecutablePath}' ({runtimeInfo.VersionText}).");
                DeveloperDiagnostics.LogInfo(
                    "Startup",
                    "Validated startup runtime saved to settings.",
                    new Dictionary<string, object?>
                    {
                        ["runtimePath"] = runtimeInfo.LaunchExecutablePath,
                        ["displayPath"] = runtimeInfo.ExecutablePath,
                        ["runtimeVersion"] = runtimeInfo.VersionText
                    });
            }
            catch (Exception ex)
            {
                AppLogger.Error("App", "Failed to save validated startup runtime to settings.", ex);
                DeveloperDiagnostics.LogException(
                    "Startup",
                    ex,
                    "Failed to save validated startup runtime to settings.",
                    new Dictionary<string, object?>
                    {
                        ["runtimePath"] = runtimeInfo.LaunchExecutablePath,
                        ["displayPath"] = runtimeInfo.ExecutablePath,
                        ["runtimeVersion"] = runtimeInfo.VersionText
                    });
            }
        }

        private static async Task CheckForStoreUpdatesAfterStartupAsync(Window shellWindow)
        {
            try
            {
                if (shellWindow is null)
                {
                    return;
                }

                var storeUpdateService = new StoreUpdateService();
                AppLogger.Info("StoreUpdate", "Startup Store/MSIX update check requested.");
                var storeUpdateCheckResult = await storeUpdateService.CheckForUpdatesAsync(CancellationToken.None).ConfigureAwait(true);
                ShowNonBlockingStoreUpdateNotificationIfNeeded(shellWindow, storeUpdateService, storeUpdateCheckResult);
            }
            catch (Exception ex)
            {
                AppLogger.Error("StoreUpdate", "Startup Store/MSIX update check failed.", ex);
                DeveloperDiagnostics.LogException("StoreUpdate", ex, "Startup Store/MSIX update check failed.");
            }
        }

        private static void ShowNonBlockingStoreUpdateNotificationIfNeeded(Window shellWindow, StoreUpdateService storeUpdateService, StoreUpdateCheckResult? storeUpdateCheckResult)
        {
            if (shellWindow is null || storeUpdateCheckResult is null || !storeUpdateCheckResult.ShouldShowAutomaticNotification)
            {
                return;
            }

            shellWindow.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var updateWindow = new StoreUpdateWindow(storeUpdateService, storeUpdateCheckResult, isMandatory: false)
                    {
                        Owner = shellWindow
                    };
                    if (storeUpdateCheckResult.HasMandatoryUpdate)
                    {
                        AppLogger.Info("StoreUpdate", "Mandatory Store-managed update detected after startup. Showing modal update dialog.");
                        DeveloperDiagnostics.LogDecision("StoreUpdate", "StoreUpdateStartupGate", "Mandatory Store-managed update detected after startup.", "MandatoryUpdateDialog");
                        updateWindow.ShowDialog();
                        System.Windows.Application.Current?.Shutdown(0);
                        return;
                    }

                    updateWindow.Show();

                    AppLogger.Info(
                        "StoreUpdate",
                        storeUpdateCheckResult.HasConfirmedInstallableUpdate
                            ? "Displayed non-blocking Microsoft Store update notification after main window load."
                            : "Skipped automatic Microsoft Store update dialog because no confirmed installable update was returned.");
                    DeveloperDiagnostics.LogDecision(
                        "StoreUpdate",
                        "ShowNonBlockingUpdateNotification",
                        "Displayed Store update dialog after main window load only for a confirmed installable update.",
                        "Shown",
                        new Dictionary<string, object?>
                        {
                            ["packagingKind"] = storeUpdateCheckResult.PackagingKind.ToString(),
                            ["availabilityState"] = storeUpdateCheckResult.AvailabilityState.ToString(),
                            ["updateCount"] = storeUpdateCheckResult.UpdateCount,
                            ["hasMandatoryUpdate"] = storeUpdateCheckResult.HasMandatoryUpdate,
                            ["shouldShowManualInstructions"] = storeUpdateCheckResult.ShouldShowManualInstructions
                        });
                }
                catch (Exception ex)
                {
                    AppLogger.Error("StoreUpdate", "Failed to show non-blocking Microsoft Store update notification.", ex);
                    DeveloperDiagnostics.LogException("StoreUpdate", ex, "Failed to show non-blocking Microsoft Store update notification.");
                }
            }), DispatcherPriority.Background);
        }
    }
}
