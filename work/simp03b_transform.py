from pathlib import Path

root = Path(r'C:\Users\rbarn\source\repos\PowerShellStudio')
main_path = root / 'PS7ScriptDesk.Shell/MainWindow.xaml.cs'
presentation = root / 'PS7ScriptDesk.Shell/Presentation'
text = main_path.read_text(encoding='utf-8-sig')
newline = '\r\n' if '\r\n' in text else '\n'

def block(start, end):
    return text[text.index(start):text.index(end)]

def replace_once(old, new):
    global text
    assert text.count(old) == 1, (old[:80], text.count(old))
    text = text.replace(old, new, 1)

def save(name, body):
    (presentation / name).write_text(body.replace('\n', newline), encoding='utf-8', newline='')

ui = block('        private void InitializeUiScaleMenu()', '        private void Window_Closed(')
replace_once(ui, '')
ui = ui.replace('private void InitializeUiScaleMenu()', 'internal void Initialize()')
ui = ui.replace('private void UiScalePresetMenuItem_Click', 'private void UiScalePresetMenuItem_Click')
ui = ui.replace('UiScaleMenuItem', '_menu')
ui = ui.replace('_uiScaleService', '_service')
ui = ui.replace('Dispatcher.CheckAccess()', '_menu.Dispatcher.CheckAccess()')
ui = ui.replace('Dispatcher.BeginInvoke', '_menu.Dispatcher.BeginInvoke')
ui = ui.replace('            RefreshUiScaleMenuChecks();\n        }\n\n        private void UiScalePresetMenuItem_Click', '            RefreshUiScaleMenuChecks();\n            _service.ScaleChanged += UiScaleService_ScaleChanged;\n        }\n\n        private void UiScalePresetMenuItem_Click', 1)
ui += '''        public void Dispose()
        {
            _service.ScaleChanged -= UiScaleService_ScaleChanged;
            foreach (var item in _menu.Items.OfType<MenuItem>())
            {
                item.Click -= UiScalePresetMenuItem_Click;
            }
        }
'''
ui = ui.replace('WpfMenuItem', 'MenuItem')
save('UiScaleMenuController.cs', '''using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using PS7ScriptDesk.Application.Interfaces;

namespace PS7ScriptDesk.Shell.Presentation
{
    internal sealed class UiScaleMenuController : IDisposable
    {
        private readonly IUiScaleService _service;
        private readonly MenuItem _menu;

        internal UiScaleMenuController(IUiScaleService service, MenuItem menu)
        {
            _service = service;
            _menu = menu;
        }

''' + ui + '''    }
}
''')
replace_once('        private readonly IUiScaleService _uiScaleService;', '        private readonly IUiScaleService _uiScaleService;\n        private readonly UiScaleMenuController _uiScaleMenuController;\n        private readonly DeveloperDiagnosticsMenuController _developerDiagnosticsMenuController;\n        private readonly SupportLogsPresenter _supportLogsPresenter;')
replace_once('            InitializeUiScaleMenu();\n            _uiScaleService.ScaleChanged += UiScaleService_ScaleChanged;', '''            _uiScaleMenuController = new UiScaleMenuController(_uiScaleService, UiScaleMenuItem);
            _uiScaleMenuController.Initialize();
            _supportLogsPresenter = new SupportLogsPresenter(
                status => { if (ViewModel is not null) ViewModel.StatusText = status; },
                status => ViewModel!.StatusText = status,
                ShowIdeMessage);''')
replace_once('            UpdateDeveloperDiagnosticsMenuState();\n            DeveloperDiagnostics.LogMethodExit(', '''            _developerDiagnosticsMenuController = new DeveloperDiagnosticsMenuController(
                _loadedSettings,
                new DeveloperDiagnosticsMenuControls(
                    EnableDeveloperDiagnosticsMenuItem,
                    VerboseUiLoggingMenuItem,
                    VerboseDebuggerLoggingMenuItem,
                    VerboseTerminalLoggingMenuItem,
                    VerboseEditorLoggingMenuItem,
                    CaptureLayoutForensicsMenuItem),
                SaveApplicationSettings,
                status => { if (ViewModel is not null) ViewModel.StatusText = status; });
            _developerDiagnosticsMenuController.UpdateMenuState();
            DeveloperDiagnostics.LogMethodExit(''')
replace_once('            _uiScaleService.ScaleChanged -= UiScaleService_ScaleChanged;', '            _uiScaleMenuController.Dispose();')

diag = block('        private void UpdateDeveloperDiagnosticsMenuState()', '        private void OpenDeveloperDebuggingFolder_Click(')
replace_once(diag, '''        private void EnableDeveloperDiagnostics_Click(object sender, RoutedEventArgs e)
            => _developerDiagnosticsMenuController.ToggleEnabled();

        private void VerboseUiLogging_Click(object sender, RoutedEventArgs e)
            => _developerDiagnosticsMenuController.ToggleVerboseUi();

        private void VerboseDebuggerLogging_Click(object sender, RoutedEventArgs e)
            => _developerDiagnosticsMenuController.ToggleVerboseDebugger();

        private void VerboseTerminalLogging_Click(object sender, RoutedEventArgs e)
            => _developerDiagnosticsMenuController.ToggleVerboseTerminal();

        private void VerboseEditorLogging_Click(object sender, RoutedEventArgs e)
            => _developerDiagnosticsMenuController.ToggleVerboseEditor();

''')
diag = diag.replace('private void UpdateDeveloperDiagnosticsMenuState()', 'internal void UpdateMenuState()')
diag = diag.replace('UpdateDeveloperDiagnosticsMenuState();', 'UpdateMenuState();')
diag = diag.replace('private void PersistDeveloperDiagnosticsSettings', 'internal void PersistSettings')
diag = diag.replace('PersistDeveloperDiagnosticsSettings(', 'PersistSettings(')
diag = diag.replace('private void EnableDeveloperDiagnostics_Click(object sender, RoutedEventArgs e)', 'internal void ToggleEnabled()')
for item, method in [('Ui', 'ToggleVerboseUi'), ('Debugger', 'ToggleVerboseDebugger'), ('Terminal', 'ToggleVerboseTerminal'), ('Editor', 'ToggleVerboseEditor')]:
    diag = diag.replace(f'private void Verbose{item}Logging_Click(object sender, RoutedEventArgs e)', f'internal void {method}()')
diag = diag.replace('_loadedSettings', '_settings').replace('SaveApplicationSettings();', '_saveSettings();')
diag = diag.replace('if (ViewModel is not null)\n            {\n                ViewModel.StatusText = statusText;\n            }', '_updateStatus(statusText);')
for item in ['EnableDeveloperDiagnostics', 'VerboseUiLogging', 'VerboseDebuggerLogging', 'VerboseTerminalLogging', 'VerboseEditorLogging', 'CaptureLayoutForensics']:
    diag = diag.replace(item + 'MenuItem', '_controls.' + item)
save('DeveloperDiagnosticsMenuController.cs', '''using System;
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

''' + diag + '''    }
}
''')
replace_once('PersistDeveloperDiagnosticsSettings("Developer diagnostics disabled before clearing logs");', '_developerDiagnosticsMenuController.PersistSettings("Developer diagnostics disabled before clearing logs");')

replace_once('''        private void OpenDeveloperDebuggingFolder_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderInExplorer(DeveloperDiagnostics.DeveloperDebuggingRootDirectory);
        }''', '''        private void OpenDeveloperDebuggingFolder_Click(object sender, RoutedEventArgs e)
            => _supportLogsPresenter.OpenDeveloperDebuggingFolder();''')

support_a = block('        private void OpenLogsFolder_Click(', '        private void ClearDeveloperDiagnosticsLogs_Click(')
support_b = block('        private static string ResolveDiagnosticLogsFolder()', '        private static T? FindAncestor<T>(')
replace_once(support_a, '''        private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
            => _supportLogsPresenter.OpenLogsFolder();

        private void CopyLogsFolderPath_Click(object sender, RoutedEventArgs e)
            => _supportLogsPresenter.CopyLogsFolderPath();

        private void CreateSupportLogsZip_Click(object sender, RoutedEventArgs e)
            => _supportLogsPresenter.CreateSupportLogsZip();

        private void OpenLatestDiagnosticSessionFolder_Click(object sender, RoutedEventArgs e)
            => _supportLogsPresenter.OpenLatestDiagnosticSessionFolder();

        private void CopyDiagnosticsSummaryToClipboard_Click(object sender, RoutedEventArgs e)
            => _supportLogsPresenter.CopyDiagnosticsSummaryToClipboard();

        private void PackageDeveloperDiagnosticsForSupport_Click(object sender, RoutedEventArgs e)
            => _supportLogsPresenter.PackageDeveloperDiagnosticsForSupport();

''')
replace_once(support_b, '')
support = '''        internal void OpenDeveloperDebuggingFolder()
        {
            OpenFolderInExplorer(DeveloperDiagnostics.DeveloperDebuggingRootDirectory);
        }

''' + support_a + support_b
for name in ['OpenLogsFolder', 'CopyLogsFolderPath', 'CreateSupportLogsZip', 'OpenLatestDiagnosticSessionFolder', 'CopyDiagnosticsSummaryToClipboard', 'PackageDeveloperDiagnosticsForSupport']:
    support = support.replace(f'private void {name}_Click(object sender, RoutedEventArgs e)', f'internal void {name}()')
support = support.replace('if (ViewModel is not null)\n                {\n                    ViewModel.StatusText = ', 'if (true)\n                {\n                    _updateStatus(')
# Restore statement endings for guarded status callbacks.
support = support.replace('_updateStatus($"Opened diagnostic logs folder and copied path: {target}";', '_updateStatus($"Opened diagnostic logs folder and copied path: {target}");')
support = support.replace('_updateStatus($"Diagnostic logs folder path copied: {target}";', '_updateStatus($"Diagnostic logs folder path copied: {target}");')
support = support.replace('_updateStatus($"Support logs ZIP created and copied to clipboard: {packagePath}";', '_updateStatus($"Support logs ZIP created and copied to clipboard: {packagePath}");')
support = support.replace('_updateStatus($"Create support logs ZIP failed: {ex.Message}";', '_updateStatus($"Create support logs ZIP failed: {ex.Message}");')
support = support.replace('_updateStatus($"Open folder failed: {ex.Message}";', '_updateStatus($"Open folder failed: {ex.Message}");')
support = support.replace('ViewModel!.StatusText = "No developer diagnostics session folder is available";', '_updateRequiredStatus("No developer diagnostics session folder is available");')
support = support.replace('ViewModel!.StatusText = "Developer diagnostics summary copied to clipboard";', '_updateRequiredStatus("Developer diagnostics summary copied to clipboard");')
support = support.replace('ShowIdeMessage(', '_showIdeMessage(')
support = support.replace('if (true)\n                {\n                    _updateStatus(', '_updateStatus(')
support = support.replace(');\n                }', ');')
save('SupportLogsPresenter.cs', '''using System;
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

''' + support + '''    }
}
''')
main_path.write_text(text, encoding='utf-8', newline='')
