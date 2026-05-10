# Codex Primer Rules

This repo builds the Windows desktop app **PS7 ScriptDesk**. Historical/internal names still use `PowerShellStudio`. The app is a WPF/.NET desktop tool for opening, editing, running, and debugging user-selected PowerShell 7.x scripts.

Future Codex sessions must:

- Inspect first, then change narrowly.
- Preserve the current layered architecture unless the user explicitly asks for a redesign.
- Avoid broad refactors, broad namespace renames, project renames, folder renames, assembly renames, or package renames unless explicitly requested.
- Avoid broad UI redesign unless explicitly requested.
- Prefer targeted fixes that keep existing behavior stable.
- Mark uncertain findings as uncertain instead of guessing.
- Use the supporting docs in `docs/` instead of re-discovering the whole repo every turn.
- Keep the developer diagnostics/debugging system current with every code addition, behavior change, workflow change, or bug fix.

## Developer diagnostics/debugging maintenance rule

The developer diagnostics system is part of the app's maintainability contract. Every Codex change must consider whether developer diagnostics need to be added, updated, corrected, or removed.

For every new or changed feature, Codex must update diagnostics when the change touches any of these areas:

- User actions, menu handlers, toolbar handlers, keyboard shortcuts, or command routing.
- Debugging, breakpoints, stepping, continue/stop behavior, debugger state, debugger prompts, debugger process handling, variables, or call stack logic.
- Terminal hosting, live console input/output, prompt detection, command execution, sentinels, run script, run selection, temporary script snapshots, or process lifecycle.
- Editor actions, active document changes, dirty-state changes, save/open/close behavior, selection/caret behavior, diagnostics, hover/help, completion, metadata warmup, or command catalog loading.
- Settings load/save, runtime toggles, app startup, shutdown, background services, file I/O, cleanup, retention, packaging, install helpers, or error handling.
- Any async workflow, background task, state machine, retry path, cancellation path, timeout path, or recovery path.

Diagnostics should be detailed enough to reconstruct what happened in order. When adding or changing logic, Codex should log or update logs for:

- Method/event-handler entry and exit where useful.
- User action accepted/rejected and the reason.
- Important decision branches.
- State before and after the change.
- State transitions.
- Operation/correlation IDs when an action spans multiple classes or async calls.
- Elapsed time for operations that can block, race, fail, or feel slow.
- File paths involved in execution/debugging, when safe.
- Counts, lengths, hashes, and capped previews instead of full user content.
- Exceptions with enough context to diagnose the failure.

When changing existing behavior, Codex must also update or remove stale diagnostic messages so logs do not become misleading.

Diagnostics safety rules:

- Do not log full scripts, full terminal buffers, passwords, tokens, API keys, cookies, certificates, private keys, Authorization headers, or full environment dumps.
- For script text, command text, terminal text, or selected text, log length, line count, hash when useful, and a sanitized capped preview only.
- Logging must be best-effort and must never crash, freeze, or materially alter app behavior.
- When developer diagnostics are disabled, overhead must remain minimal.
- Developer debugging logs must remain distinct from normal app logs, preferably under `%LOCALAPPDATA%\PS7ScriptDesk\DeveloperDebugging\`.

Codex may decide that a small change does not require diagnostics changes, but the final response must explicitly say either:

- `Developer diagnostics updated: <brief reason>`

or

- `Developer diagnostics not changed: <brief reason why not affected>`

If a task modifies debugger, terminal, execution, editor metadata, settings, startup, or async state-machine behavior, Codex should assume diagnostics need to be updated unless it can clearly justify otherwise.

Key supporting docs:

- `docs/CODEBASE_OVERVIEW.md`
- `docs/BUILD_AND_PACKAGING.md`
- `docs/DEBUGGER_AND_TERMINAL.md`
- `docs/RELEASE_READINESS.md`
- `docs/KNOWN_FRAGILE_AREAS.md`
- `docs/CODEX_WORKFLOW.md`

Final response requirements for future Codex tasks:

1. Phase attempted
2. Exact files changed
3. Short action summary
4. Developer diagnostics updated or not changed, with reason
5. Build/test result
6. Remaining risks or manual tests needed

Do not paste full files in final summaries unless the user explicitly requests that.
