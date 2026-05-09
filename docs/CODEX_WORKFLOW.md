# CODEX_WORKFLOW

## Expected Codex approach in this repo

1. Inspect first.
2. Change narrowly.
3. Preserve current architecture unless the user explicitly asks otherwise.
4. Test only when the environment supports it.
5. Report clearly and conservatively.

## Repo-specific guidance

- Public app name: `PS7 ScriptDesk`
- Historical/internal names may still be `PowerShellStudio`
- Prefer targeted fixes over broad refactors
- Avoid broad namespace/project/folder renames unless explicitly requested
- Avoid broad UI redesign unless explicitly requested
- Preserve debugger, terminal, metadata, and packaging seams unless the task specifically targets them

Read these docs before making behavior changes:

- `docs/CODEBASE_OVERVIEW.md`
- `docs/BUILD_AND_PACKAGING.md`
- `docs/DEBUGGER_AND_TERMINAL.md`
- `docs/KNOWN_FRAGILE_AREAS.md`

## How to work a task

### Inspection

- Identify the active project and layer first.
- For UI behavior, expect the change to span both `PowerShellStudio.Shell` and `PowerShellStudio.UI`.
- For run/debug/terminal work, inspect `PowerShellStudio.PowerShell` and shell wiring together.
- For settings/logging/branding, inspect `PowerShellStudio.Application`, `PowerShellStudio.Infrastructure`, and `PowerShellStudio.Domain`.

### Editing

- Change the smallest practical surface area.
- Preserve existing behavior unless the task explicitly asks for behavior change.
- If something cannot be determined from the repo, mark it as uncertain instead of guessing.
- When code files are changed, the user prefers exact project-relative paths and generally prefers drop-in replacement files over broad scattered edits.

### Verification

- Separate code inspection from executed verification in the final report.
- Do not claim build verification unless a real build was actually run.
- Do not claim test success unless tests were actually run.
- Remember that full WPF/.NET 10/MSIX verification may require native Windows.

## Final response format

Every substantive Codex completion in this repo should include:

1. Phase attempted
2. Exact files changed
3. Short action summary
4. Build/test result
5. Remaining risks or manual tests needed

Also:

- Report exact project-relative paths
- Keep the final summary concise
- Do not paste full file contents unless the user explicitly asks for them
