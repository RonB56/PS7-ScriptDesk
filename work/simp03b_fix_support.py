from pathlib import Path
import re

root = Path(r'C:\Users\rbarn\source\repos\PowerShellStudio')
baseline = Path((root / 'work/simp03b_dir.txt').read_text().strip()) / 'task_start/MainWindow.xaml.cs'
text = baseline.read_text(encoding='utf-8-sig')
a = text[text.index('        private void OpenLogsFolder_Click('):text.index('        private void ClearDeveloperDiagnosticsLogs_Click(')]
b = text[text.index('        private static string ResolveDiagnosticLogsFolder()'):text.index('        private static T? FindAncestor<T>(')]
support = '''        internal void OpenDeveloperDebuggingFolder()
        {
            OpenFolderInExplorer(DeveloperDiagnostics.DeveloperDebuggingRootDirectory);
        }

''' + a + b
for name in ('OpenLogsFolder', 'CopyLogsFolderPath', 'CreateSupportLogsZip', 'OpenLatestDiagnosticSessionFolder', 'CopyDiagnosticsSummaryToClipboard', 'PackageDeveloperDiagnosticsForSupport'):
    support = support.replace(f'private void {name}_Click(object sender, RoutedEventArgs e)', f'internal void {name}()')
pattern = r'if \(ViewModel is not null\)\n(?P<indent>\s*)\{\n\s*ViewModel\.StatusText = (?P<value>[^;]+);\n(?P=indent)\}'
support, count = re.subn(pattern, lambda m: '_updateStatus(' + m.group('value') + ');', support)
assert count == 5, count
support = support.replace('ViewModel!.StatusText = "No developer diagnostics session folder is available";', '_updateRequiredStatus("No developer diagnostics session folder is available");')
support = support.replace('ViewModel!.StatusText = "Developer diagnostics summary copied to clipboard";', '_updateRequiredStatus("Developer diagnostics summary copied to clipboard");')
support = support.replace('ShowIdeMessage(', '_showIdeMessage(')
assert 'ViewModel' not in support
path = root / 'PS7ScriptDesk.Shell/Presentation/SupportLogsPresenter.cs'
existing = path.read_text()
start = existing.index('        internal void OpenDeveloperDebuggingFolder()')
end = existing.rindex('    }\n}')
path.write_text(existing[:start] + support + existing[end:], encoding='utf-8')
