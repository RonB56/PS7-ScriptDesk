# Repair Design

## Implemented

`PsesInteractiveHistoryExclusion.BuildRegistrationScript()` creates a PSReadLine `AddToHistoryHandler` wrapper. `ExecuteStructuredScriptAsync` prepends that registration to the existing private DAP staging expression, before it submits the LSP foreground command.

The matcher recognizes only:

```text
<optional whitespace>(& | .)<whitespace>$global:__ps7sd_editor_<exactly 32 hex digits><optional whitespace>
```

For a match it returns `SkipAdding`. Otherwise it calls the previously installed handler and returns that handler's result unchanged. Both scriptblock handlers and .NET delegate handlers are supported. When there was no previous handler, it returns the normal `MemoryAndFile` outcome. A process-unique internal session-state variable holds the active wrapper/delegate reference and the prior handler so repeated requests are idempotent; if a user replaces the handler during the session, the next editor request wraps the new handler. Temporary registration variables are removed after the closure captures its state. The state object is not written to terminal output or command history; it is an internal session variable visible through session introspection.

The wrapper changes only the AddToHistoryHandler option. It does not change HistorySaveStyle, HistorySavePath, maximum history, key bindings, PSReadLine editing/completion, prompt settings, or unrelated rows. This uses a documented PSReadLine control. Microsoft documents that the callback receives the command line and supports `SkipAdding`, `MemoryOnly`, `MemoryAndFile`, and boolean equivalents: [Set-PSReadLineOption](https://learn.microsoft.com/en-us/powershell/module/psreadline/set-psreadlineoption).

## Why not use the legacy marker filter

The existing `LiveConsoleService` filter matches a typed dispatch command ending in `#PS7SDi` and a TerminalSnapshots path. The PSES structured path submits a variable reference instead, so the legacy marker predicate cannot match this reproduction.

## Scope and safety boundary

This is a history-control patch, not a replacement for PSES execution. It keeps the DAP stage, LSP foreground execution, shared session, File/Selection scope operators, echo filter, status protocol, request serialization, and diagnostic-only P2B-1 telemetry intact. It adds one process-unique internal global state object to the PSES runspace to prevent wrapper stacking; its randomized name minimizes the chance of colliding with a pre-existing variable. No user history files are edited or deleted.

The registration is best effort: if PSReadLine's option command is unavailable or rejects the handler, the catch keeps the editor execution path running, but the guard will not be active. Live UAT must verify successful registration indirectly by checking actual Up/Down recall and persisted history. A future supported native structured foreground execution API remains the architectural alternative; PSES documents only LSP, DAP, and startup as stable integration APIs.

## Tests

Three behavioral tests execute the generated registration script in a PowerShell SDK runspace with mocked PSReadLine option commands. They cover Run File/Selection forms and varying IDs, preservation of a prior `MemoryOnly` handler decision, idempotent consecutive registration, and the no-handler default. These tests simulate the PSReadLine callback; they do not claim to exercise an interactive PSReadLine reader.
