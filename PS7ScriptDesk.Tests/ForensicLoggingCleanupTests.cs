using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class ForensicLoggingCleanupTests
{
    [Fact]
    public void RetiredForensicLoggersAndKnownRuntimeFilesAreAbsentFromProductSources()
    {
        var root = FindRepositoryRoot();
        var productSource = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "PS7ScriptDesk.Tests" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .ToArray();
        var retiredMarkers = new[]
        {
            "AdmissionForensicLog",
            "StartupEnablementForensicLog",
            "TerminalRecallEnterForensicLog",
            "DisabledToolbarTooltipForensicLogger",
            "TERMINAL_THREE_BYTE_INPUT_CLASSIFICATION",
            "TERMINAL_RECALL_ENTER_SUBMISSION_FORENSIC",
            "DISABLED_TOOLBAR_TOOLTIP_RUNTIME_FORENSIC",
            "LIVE_RUN_ENABLEMENT_FORENSIC",
            "PSREADLINE_LEGACY_HISTORY_MIGRATION_FORENSIC",
            "PS7SCRIPTDESK_TOOLTIP_FORENSIC_LOG"
        };

        foreach (var marker in retiredMarkers)
        {
            Assert.DoesNotContain(productSource, source => source.Contains(marker, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void DeveloperDiagnosticsEnabledDoesNotEnableRetiredForensicWriters()
    {
        DeveloperDiagnostics.ConfigureFromSettings(
            new ApplicationSettings { IsDeveloperDiagnosticsEnabled = true },
            "forensic logger retirement test");

        try
        {
            Assert.True(DeveloperDiagnostics.IsEnabled);
            var root = FindRepositoryRoot();
            var codexWork = Path.Combine(root, "docs", "LocalOnly_NotForGitHub", "Codex_Work");
            foreach (var fileName in new[]
            {
                "TERMINAL_THREE_BYTE_INPUT_CLASSIFICATION.log",
                "TERMINAL_RECALL_ENTER_SUBMISSION_FORENSIC.log",
                "DISABLED_TOOLBAR_TOOLTIP_RUNTIME_FORENSIC.log",
                "LIVE_RUN_ENABLEMENT_FORENSIC.log"
            })
            {
                Assert.False(File.Exists(Path.Combine(codexWork, fileName)), $"Retired forensic file exists: {fileName}");
            }
        }
        finally
        {
            DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings(), "forensic logger retirement test cleanup");
        }
    }

    [Fact]
    public void DeveloperDiagnosticsDisabledDoesNotCreateRetiredForensicFiles()
    {
        DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings(), "forensic logger retirement disabled test");

        Assert.False(DeveloperDiagnostics.IsEnabled);
        var root = FindRepositoryRoot();
        var codexWork = Path.Combine(root, "docs", "LocalOnly_NotForGitHub", "Codex_Work");
        foreach (var fileName in new[]
        {
            "TERMINAL_THREE_BYTE_INPUT_CLASSIFICATION.log",
            "TERMINAL_RECALL_ENTER_SUBMISSION_FORENSIC.log",
            "DISABLED_TOOLBAR_TOOLTIP_RUNTIME_FORENSIC.log",
            "LIVE_RUN_ENABLEMENT_FORENSIC.log"
        })
        {
            Assert.False(File.Exists(Path.Combine(codexWork, fileName)), $"Retired forensic file exists: {fileName}");
        }
    }

    [Fact]
    public void SupportedDiagnosticsAndTerminalRunPathsRemainPresentAfterForensicRemoval()
    {
        var root = FindRepositoryRoot();
        var developerDiagnostics = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.Application", "Diagnostics", "DeveloperDiagnostics.cs"));
        var terminalControl = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.Shell", "Controls", "TerminalControl.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "PS7ScriptDesk.Shell", "MainWindow.xaml.cs"));

        Assert.Contains("public static class DeveloperDiagnostics", developerDiagnostics, StringComparison.Ordinal);
        Assert.Contains("DeveloperDiagnostics.Log", terminalControl, StringComparison.Ordinal);
        Assert.Contains("WriteRawInputAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("RunSelectionAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("RunSelectionFromEditorAsync", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("ForensicLog", terminalControl + viewModel + mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyMigrationRetainsMigrationButDoesNotWriteForensicEvents()
    {
        var command = LegacyHistoryMigration.BuildStartupCommand();

        Assert.Contains("HistorySavePath", command, StringComparison.Ordinal);
        Assert.Contains("IsLegacyManagedLine", File.ReadAllText(Path.Combine(FindRepositoryRoot(), "PS7ScriptDesk.PowerShell", "Services", "LegacyHistoryMigration.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("[IO.File]::AppendAllText($__pssdMigrationLogPath", command, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Ps7SdMigrationEvent([string] $event, [hashtable] $fields) {\n                    try", command, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
