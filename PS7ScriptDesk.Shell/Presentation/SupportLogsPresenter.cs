using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Utilities;

namespace PS7ScriptDesk.Shell.Presentation
{
    internal sealed class SupportLogsPresenter
    {
        private readonly Action<string> _updateStatus;
        private readonly Action<string> _updateRequiredStatus;
        private readonly Action<string, string> _showIdeMessage;

        internal SupportLogsPresenter(Action<string> updateStatus, Action<string> updateRequiredStatus,
            Action<string, string> showIdeMessage)
        {
            _updateStatus = updateStatus;
            _updateRequiredStatus = updateRequiredStatus;
            _showIdeMessage = showIdeMessage;
        }

        internal void OpenDeveloperDebuggingFolder()
        {
            OpenFolderInExplorer(DeveloperDiagnostics.DeveloperDebuggingRootDirectory);
        }

        internal void OpenLogsFolder()
        {
            var target = ResolveDiagnosticLogsFolder();
            if (OpenFolderInExplorer(target, copyPathToClipboard: true))
            {
                _updateStatus($"Requested diagnostic logs folder in Explorer and copied path: {target}");
            }
        }

        internal void CopyLogsFolderPath()
        {
            var target = ResolveDiagnosticLogsFolder();
            Directory.CreateDirectory(target);
            TrySetClipboardText(target);
            DeveloperDiagnostics.LogUserAction(
                "UI",
                "CopyLogsFolderPath",
                "Copied diagnostic logs folder path to clipboard.",
                new Dictionary<string, object?> { ["path"] = target });

            _updateStatus($"Diagnostic logs folder path copied: {target}");
        }

        internal void CreateSupportLogsZip()
        {
            CreateSupportLogsPackage(openContainingFolder: true, showConfirmation: true);
        }

        internal void OpenLatestDiagnosticSessionFolder()
        {
            var target = DeveloperDiagnostics.CurrentSessionDirectory;
            if (string.IsNullOrWhiteSpace(target))
            {
                try
                {
                    target = File.Exists(DeveloperDiagnostics.LatestSessionPointerFilePath)
                        ? File.ReadAllText(DeveloperDiagnostics.LatestSessionPointerFilePath).Trim()
                        : null;
                }
                catch
                {
                    target = null;
                }
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                _updateRequiredStatus("No developer diagnostics session folder is available");
                return;
            }

            OpenFolderInExplorer(target);
        }

        internal void CopyDiagnosticsSummaryToClipboard()
        {
            var summary = DeveloperDiagnostics.BuildSummaryText();
            System.Windows.Clipboard.SetText(summary);
            DeveloperDiagnostics.RefreshSummaryFile();
            _updateRequiredStatus("Developer diagnostics summary copied to clipboard");
        }

        internal void PackageDeveloperDiagnosticsForSupport()
        {
            CreateSupportLogsPackage(openContainingFolder: true, showConfirmation: false);
        }

        private static string ResolveDiagnosticLogsFolder()
        {
            var logicalPath = string.IsNullOrWhiteSpace(AppLogger.CurrentLogDirectory)
                ? Path.Combine(ApplicationBranding.LocalApplicationDataRoot, "Logs")
                : AppLogger.CurrentLogDirectory;
            return ExternalProcessPathContract.ResolveApplicationOwnedPath(logicalPath);
        }

        private void CreateSupportLogsPackage(bool openContainingFolder, bool showConfirmation)
        {
            try
            {
                var packagePath = ExternalProcessPathContract.ResolveApplicationOwnedPath(DeveloperDiagnostics.CreateSupportPackage());
                if (!File.Exists(packagePath))
                    throw new FileNotFoundException("The support logs ZIP was not found at its physical path.", packagePath);

                var containingFolder = GetPhysicalContainingFolder(packagePath);
                TrySetClipboardText(packagePath);

                _updateStatus($"Support logs ZIP created and copied to clipboard: {packagePath}");

                if (showConfirmation)
                {
                    _showIdeMessage("Support Logs ZIP Created",
                        $"A support logs ZIP was created and its full path was copied to the clipboard. Send this ZIP for support.\n\n{packagePath}");
                }

                if (openContainingFolder)
                {
                    OpenFolderInExplorer(containingFolder);
                }
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogException("UI", ex, "Failed to create support logs package.");
                _updateStatus($"Create support logs ZIP failed: {ex.Message}");

                _showIdeMessage("Support Logs ZIP Failed", $"PS7 ScriptDesk could not create the support logs ZIP.\n\n{ex.Message}");
            }
        }

        internal static string GetPhysicalContainingFolder(string physicalPackagePath)
            => Path.GetDirectoryName(physicalPackagePath)
                ?? throw new InvalidDataException("The support logs ZIP has no containing folder.");

        private bool OpenFolderInExplorer(string path, bool copyPathToClipboard = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new ArgumentException("The folder path was empty.", nameof(path));
                }

                var normalizedPath = Path.GetFullPath(path.Trim());
                var logicalRoot = Path.GetFullPath(ApplicationBranding.LocalApplicationDataRoot);
                if (normalizedPath.Equals(logicalRoot, StringComparison.OrdinalIgnoreCase) ||
                    normalizedPath.StartsWith(logicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    normalizedPath = ExternalProcessPathContract.ResolveApplicationOwnedPath(normalizedPath);
                Directory.CreateDirectory(normalizedPath);

                if (copyPathToClipboard)
                {
                    TrySetClipboardText(normalizedPath);
                }

                SubmitExplorerShellRequest(normalizedPath, Process.Start);

                DeveloperDiagnostics.LogUserAction(
                    "UI",
                    "OpenFolder",
                    "Submitted folder shell request to Explorer.",
                    new Dictionary<string, object?> { ["path"] = normalizedPath, ["copiedToClipboard"] = copyPathToClipboard });
                return true;
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogException("UI", ex, "Failed to open folder in Explorer.", new Dictionary<string, object?> { ["path"] = path });
                _updateStatus($"Open folder failed: {ex.Message}");

                return false;
            }
        }

        internal static void SubmitExplorerShellRequest(string path, Func<ProcessStartInfo, Process?> startProcess)
        {
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"The folder could not be created or found: {path}");

            using var explorer = startProcess(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }

        private static bool TrySetClipboardText(string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }

                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch
            {
                return false;
            }
        }

    }
}
