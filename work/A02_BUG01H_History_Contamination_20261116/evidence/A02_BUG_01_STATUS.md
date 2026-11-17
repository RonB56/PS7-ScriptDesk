# A02-BUG-01 Status

## Decision

**FIX IMPLEMENTED — VALIDATION PENDING.** Do not close A02-BUG-01 yet.

## This sub-issue

The reproduced Up Arrow contamination is most consistent with the generated Run File/Run Selection launcher being accepted through PSES's interactive PSReadLine read. A narrowly scoped history callback now skips only the generated launcher form and delegates other history decisions to the current callback. Source and build are verified; focused automated tests and live history persistence/UAT are pending.

## Broader A02-BUG-01 family

This change does not establish that intermittent visible `__ps7sd_editor_...` echoes are fixed. The output echo filter remains unchanged. A02-P2A.3a accepted CRLF/structured echo repair and A02-P2B-1 diagnostic-only shadow telemetry are preserved; prior live validation limitations remain.

## Closure criteria

- Focused handler tests and the combined regression suite pass in the ordinary PowerShell 7.6.10 environment.
- Live Run File, Run Selection, and Debug UAT confirms user Up/Down history remains usable and no new internal transport commands appear.
- A restart check confirms no newly generated invocations from the test session were persisted; existing pre-fix user history is left intact.
- Prompt recovery, shared state, breakpoints, Read-Host/secure input, Ctrl+C, and echo behavior have been checked.
- Engine `Get-History` behavior is recorded separately from PSReadLine's interactive history.

Until these conditions are met, retain the broader bug as open and keep P2B-1 diagnostic-only.
