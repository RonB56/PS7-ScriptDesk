# Root Cause Analysis

## Finding

**Most likely root cause (source-supported, live observation pending):** ScriptDesk's structured editor execution emits a generated foreground command through PSES's interactive console read. That command is eligible for PSReadLine history before the ConPTY output echo reaches ScriptDesk's display filter. The repro's Up Arrow symptom is consistent with PSReadLine retaining the submitted line.

The source does not include a captured `Get-PSReadLineOption`/`GetHistoryItems()` row from a live reproduced run. Therefore the exact in-memory versus persisted history state remains to be confirmed by UAT. `Get-History`/PowerShell engine history ownership is also unmeasured for this path.

## Evidence from the implementation

1. `PsesOwnedInteractiveSessionService.ExecuteStructuredScriptAsync` creates a request-specific name `__ps7sd_editor_<32-hex-id>` and stages a script block through private DAP evaluation.
2. After DAP acknowledgement, it creates the foreground invocation as `& $global:<name>` for Run File or `. $global:<name>` for Run Selection, then calls `PsesLanguageServiceClient.EvaluateAsync`.
3. The source comment says the LSP foreground evaluation cancels the pending PSReadLine read before executing the staged script. `PsesOwnedInteractiveSessionService` also comments that PSES writes this invocation before user output.
4. The active `StructuredEchoFilter` is registered around that request and filters terminal output after bytes arrive. It cannot change PSReadLine history already accepted at command submission.
5. PSES is started with `-NoProfile` and `-EnableConsoleRepl`; the owned service's `WriteRawInputAsync` remains the interactive ConPTY path for user input.

## Competing mechanisms

| Candidate | Assessment |
|---|---|
| PSReadLine in-memory history | Most likely source of Up Arrow recall. The foreground request crosses the PSReadLine console read, and PSReadLine owns interactive history. Live capture is pending. |
| Persisted PSReadLine file | Possible if the same accepted command is persisted under the active save style. No file was read or changed. Restart UAT is required. |
| PowerShell engine `Get-History` | Not determined for the PSES LSP path. `AddToHistoryHandler` is not evidence about the engine history collection. |
| ConPTY replay/display | Existing echo code can affect visible rendering but cannot by itself explain Up Arrow recall. It remains a separate visible-echo defect family. |
| PSES-generated foreground submission | Source confirms this is the construction and transport path for Run File/Selection. |
| Other ScriptDesk path | Legacy `LiveConsoleService` dispatch uses a separate `#PS7SDi` line marker and its own PSReadLine filter/history probe. It does not construct the `__ps7sd_editor_...` variable. |

## Validity and impact

The generated variable is created in the shared global session scope and removed in the staged script's `finally`. A recalled history entry may therefore be stale after cleanup; if a matching global variable still exists during a subsequent request, recall can invoke the staged block again. That replay risk follows from the source and is not a live execution finding.

Run File uses call operator `&` and ignores breakpoints; Run Selection uses dot-source `.` to retain shared scope. The history wrapper excludes both forms without changing either invocation. Debug uses the separate PSES DAP debugger session in the current feature-gated path; breakpoint behavior is not routed through this editor launcher matcher.

## Confidence

- **High:** generated invocation construction and LSP submission path, from repository source.
- **High:** echo filtering is downstream of command submission, from source ordering.
- **Medium:** PSReadLine is the Up Arrow history owner, supported by PSES console REPL configuration and official PSReadLine documentation.
- **Unverified:** exact in-memory and persisted rows, engine `Get-History`, restart effects, and absence of visible echoes on the user's reproduced machine.
