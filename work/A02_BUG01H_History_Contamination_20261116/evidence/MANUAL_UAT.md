# Manual UAT — PSReadLine History Exclusion

Run this with the freshly built ScriptDesk and the user's normal PSReadLine settings. Do not clear, truncate, or edit the user's history file. Record the starting history behavior before testing.

1. Start the built ScriptDesk and open a harmless test script.
2. In the integrated console, enter a harmless identifiable command such as `$A02Bug01hUserCommand = 'history-check'`; confirm it executes.
3. Run File, then Run Selection. Repeat both several times to exercise changing request IDs.
4. Focus the console. Press Up and Down Arrow and use Ctrl+R if configured. Confirm the identifiable user command and older user history remain available; confirm no `__ps7sd_editor_` invocation appears.
5. Confirm completion/editing still work with Tab, left/right arrows, and ordinary text entry.
6. Query shared state with `$A02Bug01hUserCommand`; confirm it remains set. Confirm Run File still ignores breakpoints and Run Selection retains its existing dot-source semantics.
7. Repeat with a harmless script that calls `Read-Host`, then with secure input only if the script already has a safe test value. Confirm prompt recovery afterward. Test Ctrl+C during a safe long-running script and verify the next prompt and ordinary console command work.
8. Start Debug on a test script with one breakpoint. Confirm the breakpoint is hit and Debugger ownership/continue/stop behavior is unchanged. Inspect Up/Down history after Debug.
9. Record the active PSReadLine `HistorySaveStyle` and `HistorySavePath` before and after; confirm they are unchanged. Do not alter the user's existing history handler for this test.
10. Close/restart the console or application. Verify that the new editor-generated invocations from this run are absent from recalled history after restart, while user commands remain available. Pre-existing pre-fix rows may still exist; do not remove them.
11. Inspect the relevant PSReadLine history file read-only if permitted by the user. Do not paste its contents into evidence. Record only whether newly generated current-run invocation entries were found.
12. Where possible, use `Get-History` after Run File and Run Selection to determine whether the PowerShell engine history separately records the LSP invocation. Record that result separately from PSReadLine Up/Down behavior.

Capture only the test outcome, PowerShell/PSReadLine versions, and whether each check passed. Keep script content, typed user history, credentials, and secure values out of the evidence package.

## Result

Pending user-operated live UAT. No GUI automation was used.
