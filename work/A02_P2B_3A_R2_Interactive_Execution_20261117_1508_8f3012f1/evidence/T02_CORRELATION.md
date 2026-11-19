# T02 evidence correlation

Source: Developer Diagnostics session `2026-11-17_144244_pid100672` (PS7 ScriptDesk 10.2.6.0), plus the owner's visible-console observation. Times below are local, America/New_York, 2026-11-17.

## Identity and admission

- Execution operation: `Execution-cbf2ea467fe642988073855397d48caf`.
- Editor request ID: `8f3012f1-f7b3-42cf-a7f8-050a2d41eb92`.
- Runtime session `4251ab42-6fdb-4828-af27-92b30901c608`, runtime generation 1; PSES terminal generation 1, process 386384.
- Run File entered the normal shared-session route. The diagnostic classifier marked the file `KnownInteractive` and selected `legacy-live-console` because interactive input was required.
- The owner had already set `$A02BridgeShared = "SHARED_SESSION_OK"` directly in the console.
- At 14:45:13.264 the service created snapshot `...\editor-8f3012f1f7b342cfa7f8050a2d41eb92.ps1` (662 characters). At 14:45:13.272 the experimental direct Run File bridge was selected.

## Invocation and native terminal

- At 14:45:13.272 the service requested a generated invocation of length 940. The production source built it as a multi-line `& { ... try { ... } finally { ... } }` scriptblock containing the request-unique `__ps7sd_status_8f3012f1f7b342cfa7f8050a2d41eb92` variable and a quoted snapshot path.
- At 14:45:13.283 the request-specific prompt waiter was registered and LSP `evaluate` was submitted. Diagnostics record ConPTY output dispatch to the terminal WebView from 14:45:13.386 onward. The output payload is intentionally omitted from Developer Diagnostics; the owner reports seeing the wrapper text (`& {`, the status variable, `try {`) and a PowerShell `>>` continuation prompt instead of either Read-Host prompt.
- The PSES busy notification was observed while this request was pending. No request-specific post-script prompt was accepted.

## Protocol and completion evidence

- LSP `evaluate` protocol request id: `4`; its request ID field matches `8f3012f1-f7b3-42cf-a7f8-050a2d41eb92`.
- The response wait ended at 14:50:13.272 after 300,019 ms. Diagnostics record `busy=true`, `responseCompleted=true`, `cancellationRequested=false`; prompt handoff reports `handoffStatus=Unknown`, `handoffResponseReceived=true`, and a protocol error (45-character raw error and 58-character user-facing error). The response was not a successful execution completion.
- `statusFileExistsAtEntry=false`; it was still false when the response returned and when the service sampled the status boundary. No `StatusMarkerObserved`, `StatusValidated`, or accepted request prompt was recorded for T02.
- The request-specific foreground evaluation had therefore not completed successfully. The submitted multi-line wrapper was visible while the PSES foreground request remained busy; this was not just expected command echo.

## Conclusion

The experimental bridge submitted a multi-line wrapper to the shared interactive PSES console. Its block form exposed internal source and left the REPL at a continuation prompt, before the script reached its native Read-Host interactions. PSES kept the correlated foreground `evaluate` pending, eventually returning a protocol error without the wrapper's status marker or a verified post-script prompt. The appropriate boundary is the experimental invocation text: make it a single parse-complete PowerShell command while preserving its call scope, `try/catch/finally`, request-specific status file, and the same LSP request/cancellation path.

The owner confirmed T01 passed twice consecutively, including toolbar recovery. Those runs precede T02 in the same diagnostics session. The experimental gate remains off by default; Run Selection continues to bypass it.
