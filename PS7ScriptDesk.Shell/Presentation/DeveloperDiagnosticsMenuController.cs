using System;
using System.Windows.Controls;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Shell.Presentation
{
    internal sealed class DeveloperDiagnosticsMenuControls
    {
        internal MenuItem EnableDeveloperDiagnostics { get; }
        internal MenuItem VerboseUiLogging { get; }
        internal MenuItem VerboseDebuggerLogging { get; }
        internal MenuItem VerboseTerminalLogging { get; }
        internal MenuItem VerboseEditorLogging { get; }
        internal MenuItem CaptureLayoutForensics { get; }

        internal DeveloperDiagnosticsMenuControls(MenuItem enableDeveloperDiagnostics, MenuItem verboseUiLogging,
            MenuItem verboseDebuggerLogging, MenuItem verboseTerminalLogging, MenuItem verboseEditorLogging,
            MenuItem captureLayoutForensics)
        {
            EnableDeveloperDiagnostics = enableDeveloperDiagnostics;
            VerboseUiLogging = verboseUiLogging;
            VerboseDebuggerLogging = verboseDebuggerLogging;
            VerboseTerminalLogging = verboseTerminalLogging;
            VerboseEditorLogging = verboseEditorLogging;
            CaptureLayoutForensics = captureLayoutForensics;
        }
    }

    internal sealed class DeveloperDiagnosticsMenuController
    {
        private readonly ApplicationSettings _settings;
        private readonly DeveloperDiagnosticsMenuControls _controls;
        private readonly Action _saveSettings;
        private readonly Action<string> _updateStatus;

        internal DeveloperDiagnosticsMenuController(ApplicationSettings settings, DeveloperDiagnosticsMenuControls controls,
            Action saveSettings, Action<string> updateStatus)
        {
            _settings = settings;
            _controls = controls;
            _saveSettings = saveSettings;
            _updateStatus = updateStatus;
        }

        internal void UpdateMenuState()
        {
            _controls.EnableDeveloperDiagnostics.IsChecked = _settings.IsDeveloperDiagnosticsEnabled;
            _controls.VerboseUiLogging.IsChecked = _settings.IsDeveloperDiagnosticsVerboseUiEnabled;
            _controls.VerboseDebuggerLogging.IsChecked = _settings.IsDeveloperDiagnosticsVerboseDebuggerEnabled;
            _controls.VerboseTerminalLogging.IsChecked = _settings.IsDeveloperDiagnosticsVerboseTerminalEnabled;
            _controls.VerboseEditorLogging.IsChecked = _settings.IsDeveloperDiagnosticsVerboseEditorEnabled;

            var enabled = _settings.IsDeveloperDiagnosticsEnabled;
            _controls.VerboseUiLogging.IsEnabled = enabled;
            _controls.VerboseDebuggerLogging.IsEnabled = enabled;
            _controls.VerboseTerminalLogging.IsEnabled = enabled;
            _controls.VerboseEditorLogging.IsEnabled = enabled;
            _controls.CaptureLayoutForensics.IsEnabled = enabled;
        }

        internal void PersistSettings(string statusText)
        {
            _saveSettings();
            DeveloperDiagnostics.ConfigureFromSettings(_settings, "MainWindow updated developer diagnostics settings");
            ExecutionCorrelationTelemetry.Shared.LogActivation();
            UpdateMenuState();
            _updateStatus(statusText);

            DeveloperDiagnostics.RefreshSummaryFile();
        }

        internal void ToggleEnabled()
        {
            _settings.IsDeveloperDiagnosticsEnabled = !_settings.IsDeveloperDiagnosticsEnabled;
            PersistSettings(_settings.IsDeveloperDiagnosticsEnabled
                ? "Developer diagnostics enabled"
                : "Developer diagnostics disabled");
            DeveloperDiagnostics.LogUserAction("Settings", "DeveloperDiagnosticsToggle", _settings.IsDeveloperDiagnosticsEnabled ? "Developer diagnostics enabled in-app." : "Developer diagnostics disabled in-app.");
        }

        internal void ToggleVerboseUi()
        {
            _settings.IsDeveloperDiagnosticsVerboseUiEnabled = !_settings.IsDeveloperDiagnosticsVerboseUiEnabled;
            PersistSettings($"Developer diagnostics UI verbosity: {_settings.IsDeveloperDiagnosticsVerboseUiEnabled}");
        }

        internal void ToggleVerboseDebugger()
        {
            _settings.IsDeveloperDiagnosticsVerboseDebuggerEnabled = !_settings.IsDeveloperDiagnosticsVerboseDebuggerEnabled;
            PersistSettings($"Developer diagnostics debugger verbosity: {_settings.IsDeveloperDiagnosticsVerboseDebuggerEnabled}");
        }

        internal void ToggleVerboseTerminal()
        {
            _settings.IsDeveloperDiagnosticsVerboseTerminalEnabled = !_settings.IsDeveloperDiagnosticsVerboseTerminalEnabled;
            PersistSettings($"Developer diagnostics terminal verbosity: {_settings.IsDeveloperDiagnosticsVerboseTerminalEnabled}");
        }

        internal void ToggleVerboseEditor()
        {
            _settings.IsDeveloperDiagnosticsVerboseEditorEnabled = !_settings.IsDeveloperDiagnosticsVerboseEditorEnabled;
            PersistSettings($"Developer diagnostics editor verbosity: {_settings.IsDeveloperDiagnosticsVerboseEditorEnabled}");
        }

    }
}
