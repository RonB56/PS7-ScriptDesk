from pathlib import Path
import difflib, zipfile, shutil

root = Path(r'C:\Users\rbarn\source\repos\PowerShellStudio')
e = Path((root/'work/simp03b_dir.txt').read_text().strip())
def write(name, content):
    (e/name).write_text(content.strip()+'\n', encoding='utf-8')

main_before = (e/'task_start/MainWindow.xaml.cs').read_text(encoding='utf-8-sig').splitlines(keepends=True)
main_after = (root/'PS7ScriptDesk.Shell/MainWindow.xaml.cs').read_text(encoding='utf-8-sig').splitlines(keepends=True)
diff = ''.join(difflib.unified_diff(main_before, main_after, fromfile='task_start/MainWindow.xaml.cs', tofile='PS7ScriptDesk.Shell/MainWindow.xaml.cs', n=3))
(e/'SOURCE_COMPARISON.diff').write_text(diff, encoding='utf-8')
headers = [line.strip() for line in diff.splitlines() if line.startswith('@@')]

write('FINAL_REPORT.md', f'''# IFAA-SIMP-03B final implementation report

Implemented the three authorized presentation extractions from the accepted SIMP-03A MainWindow baseline. MainWindow composes the controllers and presenter and retains the required XAML handlers, diagnostics snapshot/layout forensics, application settings persistence, and destructive log-clear operation. No other production file was edited. No Git operation or GUI automation was performed.

The targeted Release x64 test-project build passed with zero errors. The first build reported seven existing warning occurrences; incremental builds after adding characterization tests reported one existing warning. The bounded six-class test group had 48 PASS, 1 FAIL, 0 skipped. The failed `UiScaleStructuralTests.ViewMenu_ExposesUiScaleAndKeepsEditorZoomShortcutsSeparate` expects `Text="{{Binding UiScaleText}}"` in unchanged MainWindow XAML. The two new characterization tests each passed after correcting one test's initial settings setup. No full regression or owner GUI UAT was performed.

MainWindow changed from 17,120 to 16,878 physical lines and from 647 to 637 direct methods. Combined affected production source changed from 846,300 to 834,657 bytes. See `COMPLEXITY_DELTA.md` for all scoped measurements.

Source comparison is in `SOURCE_COMPARISON.diff` ({len(headers)} bounded diff hunks); all hunks are the three component fields/composition, the approved relocated methods, thin XAML handlers, the log-clear persistence call, and scale-controller disposal. Other MainWindow declarations are unchanged. Task-start MainWindow source is preserved. Directly affected tests and XAML bindings were inspected; XAML was not changed.

Remaining limits: the existing XAML structural assertion fails; support-folder/clipboard/ZIP actions and shutdown need owner GUI UAT. Independent source review and owner acceptance remain pending. The exact restart is independent review of this ZIP and test limitation, then the owner UAT checklist. SIMP-02, SIMP-03 and REM-06 remain open.
''')
write('CHANGED_FILES.md', '''# Changed files

- `PS7ScriptDesk.Shell/MainWindow.xaml.cs` — composition, thin XAML handlers, scale disposal and log-clear persistence delegation.
- `PS7ScriptDesk.Shell/Presentation/UiScaleMenuController.cs` — scale menu construction, checked state and service subscription lifetime.
- `PS7ScriptDesk.Shell/Presentation/DeveloperDiagnosticsMenuController.cs` — diagnostics menu state, settings toggles and persistence sequence; includes cohesive menu-control holder.
- `PS7ScriptDesk.Shell/Presentation/SupportLogsPresenter.cs` — logs folder, clipboard, diagnostics session and support ZIP presentation.
- `PS7ScriptDesk.Tests/UiScaleWpfBehaviorTests.cs` — two targeted controller characterization tests.
- `docs/LocalOnly_NotForGitHub/PS7ScriptDesk_IFAA_SOW.md` — acceptance, T07 status, SIMP-02B boundaries, SIMP-03B results and restart point.

Task-start source: `task_start/MainWindow.xaml.cs`. MainWindow XAML and all other production sources are unchanged.
''')
write('TEST_RESULTS.md', '''# Verification results

All commands used `C:\\Program Files\\PowerShell\\7\\pwsh.exe -NoProfile -File` with the installed `DevelopmentTools` runner. No elevation, environment switch, full regression or automatic retry.

| Run | Result | Evidence directory |
|---|---|---|
| `Invoke-ScriptDeskBuild.ps1 -Target Tests` (initial Release x64 build) | exit 0; 0 errors; 7 existing warning occurrences | `DEV-BUILD_20261127_092903_045_a33ddab8` |
| `Invoke-ScriptDeskTests.ps1 -Filter 'FullyQualifiedName~UiScaleStructuralTests|FullyQualifiedName~UiScaleServiceTests|FullyQualifiedName~UiScaleWpfBehaviorTests|FullyQualifiedName~DeveloperDiagnosticsReliabilityTests|FullyQualifiedName~WholeApplicationVisualConsistencyTests|FullyQualifiedName~Phase11FinalVisualConsistencyContractTests'` | exit 1; 48 passed, 1 failed, 0 skipped | `DEV-TEST_20261127_093049_270_15a486a8` |
| Build after adding two characterization tests | exit 0; 0 errors; 1 existing test warning | `DEV-BUILD_20261127_093324_109_606e8e18` |
| Two new controller tests | exit 1; 1 passed, 1 failed due to incorrect test expectation for default verbosity settings | `DEV-TEST_20261127_093354_300_83b9dc92` |
| Build after correcting the test setup | exit 0; 0 errors; 1 existing test warning | `DEV-BUILD_20261127_093425_051_f3532fb4` |
| Corrected diagnostics controller test | exit 0; 1 passed, 0 failed | `DEV-TEST_20261127_093456_824_13f30c14` |

The broad-group failure is `UiScaleStructuralTests.ViewMenu_ExposesUiScaleAndKeepsEditorZoomShortcutsSeparate`: it expects `Text="{Binding UiScaleText}"` in MainWindow XAML. That XAML was not edited in this task. The first new scale-controller test passed in its single run. The corrected diagnostics-controller test passed in its targeted rerun. No real owner diagnostic logs were deleted.

Baseline and after metrics: installed `Invoke-ScriptDeskMetrics.ps1` with explicit relative C# paths, PowerShell 7.6.10 and bundled Roslyn 5.0.0.0. The first multi-path CLI attempt had an argument-binding error and produced no measurement; corrected single-path invocations all passed. Raw inventories are included in `runner_evidence`.
''')
write('COMPLEXITY_DELTA.md', '''# Scoped complexity delta

Identical installed syntax-based metric runners measured the preserved SIMP-03A MainWindow snapshot and the four affected production files. New files have no before value.

| File | Physical before → after | Nonblank before → after | Bytes before → after | Direct methods after | Fields after | Constructor parameters |
|---|---:|---:|---:|---:|---:|---:|
| MainWindow.xaml.cs | 17,120 → 16,878 | 15,241 → 15,027 | 846,300 → 819,900 | 647 → 637 | 231 → 234 | 5 → 5 |
| UiScaleMenuController.cs | 0 → 79 | 0 → 70 | 0 → 2,480 | 6 | 2 | 2 |
| DeveloperDiagnosticsMenuController.cs | 0 → 107 | 0 → 93 | 0 → 5,107 | controller 8; holder 1 | controller 4; holder 0 | controller 4; holder 6 |
| SupportLogsPresenter.cs | 0 → 196 | 0 → 168 | 0 → 7,170 | 12 | 3 | 3 |
| Combined affected source | 17,120 → 17,260 | 15,241 → 15,358 | 846,300 → 834,657 | — | — | — |

Direct method counts exclude constructors; each new type has one constructor. MainWindow retains 21 methods over 100 lines, 12 methods with cyclomatic complexity over 20, one method with over eight parameters, and its type exceeds 750 lines. No new component violates these mandatory thresholds. No formatting or comment removal was used to improve metrics. The combined physical line increase reflects separate cohesive classes; bytes decreased by 11,643.

Internal dependencies: MainWindow depends on the three presentation components; the diagnostics controller uses the existing settings instance and typed menu holder; the logs presenter uses three narrow callbacks; the scale controller uses the existing service and menu dispatcher. External dependencies are existing WPF, Application diagnostics/services, Domain settings, and system IO/process/clipboard APIs. No new project, package or service framework dependency. Syntax-based counts do not evaluate binding or conditional compilation.
''')
write('EXTRACTION_MAP.md', '''# Extraction map and source review

| Baseline MainWindow responsibility | New owner | MainWindow role |
|---|---|---|
| Lines 11168–11214, UI scale menu build/click/event/checks | `UiScaleMenuController` | Constructor creates and initializes at former point; `Window_Closed` disposes at former unsubscribe point |
| Lines 12788–12849, diagnostics menu state/persistence/toggles | `DeveloperDiagnosticsMenuController` | Constructor initializes state; thin XAML handlers; log-clear calls controller persistence |
| Line 12851 and lines 12963–13173 except log-clear, support/logs operations | `SupportLogsPresenter` | Thin XAML handlers and status/message callbacks |

The scale controller keeps percentage ordering, labels, Tag/tooltip/accessibility, source `Menu`, DataBind dispatcher marshalling and service subscription lifetime. Diagnostics keeps the same settings object and the save/configure/telemetry/menu/status/summary sequence, messages and events. Support logs keeps existing folder resolution, exception boundaries, clipboard direct-summary behavior, confirmation and Explorer/package actions. Guarded and unguarded ViewModel status accesses are separate callbacks. MainWindow retains `SaveApplicationSettings`, diagnostics snapshot/copy/build helpers, layout forensics and log-clear confirmation/deletion.

`SOURCE_COMPARISON.diff` compares the task-start MainWindow copy with final source. The edits are confined to the approved regions and composition. XAML event names remain present and compile. No other MainWindow declaration changed.
''')
write('OWNER_UAT.md', '''# Owner GUI UAT — pending

Run in the normal installed or development WPF application. Record PASS/FAIL and observations for each item. Do not clear real logs merely to validate this checklist; use cancellation for item 9.

1. Open UI Scale menu: verify supported values/order, percentage labels, tooltip, accessibility name, and single checked preset.
2. Change UI scale, verify live application scaling and menu checked-state synchronization, then restart to verify settings persistence.
3. Enable and disable developer diagnostics; verify checked state, status and summary behavior.
4. Toggle UI, debugger, terminal and editor verbosity; verify checked state and enabled state when diagnostics is off/on.
5. Open the normal logs folder and copy its path; verify Explorer, clipboard path and status.
6. Open Developer Debugging folder and latest diagnostics session folder; verify current/latest pointer behavior and no-session status.
7. Copy diagnostics summary; verify clipboard content and status.
8. Create support ZIP from both entry points; verify clipboard path, optional confirmation and containing folder.
9. Choose Keep on Clear Developer Diagnostics Logs confirmation; verify existing diagnostic evidence remains.
10. Verify normal startup and shutdown without scale menu errors or retained service reactions.

Owner results and acceptance: **PENDING**.
''')
write('PROGRESS.md', '''# Progress

- Inspected repository instructions, installed tools, SOW, directly affected source and tests. Verified T07 AGENTS.md/SOW integration already existed.
- Updated SOW with SIMP-03A independent review PASS, owner GUI UAT 7/7 PASS, confirmed Git baseline and SIMP-02B boundaries.
- Saved task-start MainWindow/SOW copies; baseline syntax metrics passed.
- Implemented UiScaleMenuController, DeveloperDiagnosticsMenuController, SupportLogsPresenter and narrow MainWindow composition/handlers.
- Initial targeted Release x64 test-project build passed. Bounded six-class test group: 48 PASS, 1 unrelated XAML structural assertion FAIL.
- Added two targeted WPF characterization tests; scale test passed. Corrected diagnostics test setup and its targeted rerun passed. Builds after test changes passed.
- After metrics passed for all four affected production files; source comparison and review reports packaged.
- Exact restart: independent source review of `IFAA-SIMP-03B_SOURCE_REVIEW.zip` and test limitation, then owner GUI UAT. No owner acceptance or Git baseline approval for SIMP-03B.
''')

# Preserve the test's original content as a clearly labeled reconstruction; it was only changed by added methods/imports.
test_current = (root/'PS7ScriptDesk.Tests/UiScaleWpfBehaviorTests.cs').read_text(encoding='utf-8')
test_before = test_current.replace('using System.Windows.Automation;\n','').replace('using PS7ScriptDesk.Shell.Presentation;\n','')
start = test_before.index('    [Fact]\n    public void DeveloperDiagnosticsMenuController_ReflectsSettingsAndEnabledState()')
end = test_before.index('    [Fact]\n    public void AttachedWindowRoot_UpdatesVisualBoundsWhenScaleServiceChanges()')
test_before = test_before[:start] + test_before[end:]
(e/'task_start/UiScaleWpfBehaviorTests.reconstructed.cs').write_text(test_before, encoding='utf-8')

raw = e/'runner_evidence'
raw.mkdir(exist_ok=True)
metrics = [('baseline', next((e/'baseline/docs/LocalOnly_NotForGitHub/Codex_Work').glob('DEV-METRICS_*'))),
('MainWindow', root/'docs/LocalOnly_NotForGitHub/Codex_Work/DEV-METRICS_20261127_093144_382_420379a9'),
('UiScaleMenuController', root/'docs/LocalOnly_NotForGitHub/Codex_Work/DEV-METRICS_20261127_093157_555_ff7b0e13'),
('DeveloperDiagnosticsMenuController', root/'docs/LocalOnly_NotForGitHub/Codex_Work/DEV-METRICS_20261127_093159_544_61b0abca'),
('SupportLogsPresenter', root/'docs/LocalOnly_NotForGitHub/Codex_Work/DEV-METRICS_20261127_093201_455_b494f136')]
for label, source in metrics:
    dest = raw/label
    dest.mkdir(exist_ok=True)
    for name in ('SOURCE_FILE_INVENTORY.csv','TYPE_INVENTORY.csv','METHOD_INVENTORY.csv','result.json'):
        shutil.copy2(source/name, dest/name)
for label, dirname in [('initial_build','DEV-BUILD_20261127_092903_045_a33ddab8'),('group_tests','DEV-TEST_20261127_093049_270_15a486a8'),('test_build','DEV-BUILD_20261127_093324_109_606e8e18'),('new_tests','DEV-TEST_20261127_093354_300_83b9dc92'),('corrected_build','DEV-BUILD_20261127_093425_051_f3532fb4'),('corrected_test','DEV-TEST_20261127_093456_824_13f30c14')]:
    source = root/'docs/LocalOnly_NotForGitHub/Codex_Work'/dirname
    dest = raw/label
    dest.mkdir(exist_ok=True)
    for name in ('result.json','focused.trx'):
        if (source/name).exists(): shutil.copy2(source/name, dest/name)

zpath = e/'IFAA-SIMP-03B_SOURCE_REVIEW.zip'
production = ['PS7ScriptDesk.Shell/MainWindow.xaml.cs','PS7ScriptDesk.Shell/Presentation/UiScaleMenuController.cs','PS7ScriptDesk.Shell/Presentation/DeveloperDiagnosticsMenuController.cs','PS7ScriptDesk.Shell/Presentation/SupportLogsPresenter.cs','PS7ScriptDesk.Tests/UiScaleWpfBehaviorTests.cs','docs/LocalOnly_NotForGitHub/PS7ScriptDesk_IFAA_SOW.md']
with zipfile.ZipFile(zpath,'w',zipfile.ZIP_DEFLATED) as z:
    for rel in production: z.write(root/rel, rel)
    for p in e.rglob('*'):
        if p.is_file() and p != zpath and 'baseline' not in p.relative_to(e).parts:
            z.write(p, 'evidence/'+str(p.relative_to(e)).replace('\\','/'))
print(zpath)

