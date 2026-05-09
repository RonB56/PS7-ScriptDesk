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
4. Build/test result
5. Remaining risks or manual tests needed

Do not paste full files in final summaries unless the user explicitly requests that.
