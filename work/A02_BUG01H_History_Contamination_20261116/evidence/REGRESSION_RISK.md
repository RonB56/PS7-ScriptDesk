# Regression Risk

| Area | Risk | Current mitigation / verification |
|---|---|---|
| PSReadLine handler behavior | Replacing the callback could bypass user policy. | Wrapper captures and delegates to the active handler; callback simulation tests cover a non-default `MemoryOnly` result. Actual PSReadLine behavior pending. |
| Repeated editor requests | Wrapper could nest repeatedly. | A process-unique state object compares handler references and wraps only when the current handler changes; a behavioral test checks reference stability. |
| Internal session state | A process-unique global object tracks the installed handler and captured prior delegate. | It is not output or history text. Its randomized name minimizes collision risk; it is visible through session introspection and is an additive internal runspace variable. |
| User enters a lookalike command | An explicitly typed command using the reserved variable prefix and 32-hex form would also be skipped. | Matcher is restricted to exact operator, reserved name, hex length and otherwise-whitespace. The prefix is ScriptDesk's private transport namespace. |
| PSReadLine not loaded or option failure | Guard may not install. | Registration is best effort so it cannot prevent execution; live validation must confirm it is effective. No message or command is emitted to the user console. |
| History persistence | Handler change must not alter configured persistence settings. | No save style/path is set or changed; UAT compares them and tests restart behavior without editing the history file. |
| Engine history | `Get-History` may separately retain a submitted expression. | Not controlled or established by this fix; UAT includes a separate `Get-History` observation. Do not claim engine-history exclusion. |
| Echo filtering | Visible command echo may remain intermittently. | Existing structured echo path and its tests are unchanged. History exclusion is not display filtering; live echo UAT remains necessary. |
| Debug and breakpoints | Debug should not run through the Run File wrapper. | Current feature-gated DAP debugger is separate; UAT must verify breakpoints and inspect history. Legacy fallback behavior remains a boundary to verify if enabled. |
| Shared variables, prompts, input and recovery | Changing foreground transport would affect session state/input. | No transport, execution mode, prompt, or authority changes; UAT covers shared state, Read-Host, secure input, Ctrl+C and prompt recovery. |
| P2B-1 telemetry | Diagnostics might gain authority or expose command content. | Only a content-free event records guard staging; no telemetry predicate or authority is changed. Existing regression suite not run here. |

Overall risk remains **medium until focused tests and live UAT pass**. The patch compiles, but the VSTest sandbox hang and unmeasured live PSReadLine state prevent validation status.
