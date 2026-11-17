# History Ownership and Execution Trace

## Run File / Run Selection

1. UI run commands call the view model's structured editor execution backend. The authoritative implementation is `PsesOwnedInteractiveSessionService.ExecuteStructuredScriptAsync`.
2. The service writes a request-owned script snapshot and constructs a global script block named `__ps7sd_editor_<request-id>`. Its inner invocation is `& '<snapshot>'` for Run File or `. '<snapshot>'` for Run Selection.
3. `PsesDapEvaluationClient.EvaluateAsync` stages that script block using the PSES debug pipe. The source explicitly avoids running the script in this stage.
4. After staging acknowledgement, the service constructs `& $global:__ps7sd_editor_<id>` or `. $global:__ps7sd_editor_<id>`, registers the request echo filter, and calls `PsesLanguageServiceClient.EvaluateAsync`.
5. The language client sends LSP method `evaluate` with the expression. It waits for the response and post-evaluate idle signal.
6. PSES's console REPL/PSReadLine owns the foreground prompt and input cycle. The code says the foreground LSP evaluation cancels the read before running, and that PSES writes the invocation to ConPTY.
7. ConPTY output is consumed by `PsesOwnedInteractiveSessionService`; the structured echo filter suppresses matching visible output. This is a later stage than history acceptance.

The new history guard is added to the DAP stage immediately before staging the editor block. It configures `AddToHistoryHandler` in the active PSES session; the following LSP invocation then reaches the handler. The handler skips only the exact reserved operator/name/id form and delegates other commands to the handler active at installation time.

## Debug

`AppBootstrapper` selects `PsesDapDebugSession` when the PSES-owned session feature is enabled. That implementation submits DAP `launch`/breakpoint requests, separate from `ExecuteStructuredScriptAsync` and its `__ps7sd_editor_` command. The fallback `PsesDebugSession` has a different legacy terminal-command path. The history guard does not modify debugger ownership or DAP breakpoint requests. Validate the active debugger backend and history after Debug in UAT.

## History classes

- **PSReadLine interactive history:** controls Up/Down Arrow and persisted command history; the new `AddToHistoryHandler` is scoped here.
- **PowerShell engine history (`Get-History`):** not proven to contain or exclude the LSP foreground invocation. The PSReadLine callback does not establish engine history behavior.
- **Saved PSReadLine history file:** no existing history file is read, rewritten, or cleaned by this fix. New editor launcher lines should be skipped before PSReadLine adds them to its queue/file, subject to live verification.
- **Echo/rendering:** existing structured echo filtering is unchanged. It controls display only and does not own history.

## Evidence references

- `PsesOwnedInteractiveSessionService.cs`: `ExecuteStructuredScriptAsync` around lines 706-950; foreground cleanup around 1140-1200; echo filtering around 2800-2860.
- `PsesLanguageServiceClient.cs`: `EvaluateAsync` around lines 215-280; request method is `evaluate`.
- `PsesOwnedInteractiveSessionService.cs`: `BuildArguments` around line 2550 includes `-NoProfile` and `-EnableConsoleRepl`.
- `PsesInteractiveHistoryExclusion.cs`: new handler builder.
- `PsesDapDebugSession.cs`: DAP start/launch and breakpoint requests; `AppBootstrapper.cs` selects the backend.
- `LiveConsoleService.cs`: legacy `#PS7SDi` filtering and bounded engine-history probe are separate from the PSES editor variable path.

The repository's `docs/DEBUGGER_AND_TERMINAL.md`, `docs/KNOWN_FRAGILE_AREAS.md`, and `docs/CODEBASE_OVERVIEW.md` named in `AGENTS.md` were not present at inspection time. The local-only A02 P2B architecture reports supplied the nearby execution-ownership context.
