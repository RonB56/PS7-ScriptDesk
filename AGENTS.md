# PS7 ScriptDesk — Codex Repository Instructions

## 1. Product and scope

This repository builds **PS7 ScriptDesk** (`PS7ScriptDesk` internally), a Windows 11 WPF application for editing, running, and debugging PowerShell 7.x scripts. It uses .NET 10; inspect the relevant `.csproj` for the exact target framework. Preserve the current layered architecture, packaging model, public contracts, and major dependencies unless the owner explicitly authorizes a change.

**Operating principle:** inspect the current source and relevant call paths, then make the smallest *complete* change that meets the requested outcome. Current source and verified task evidence take precedence over historical reports or assumptions. Missing documentation is not a blocker: do not repeatedly search for it or invent replacements.

## 2. Ownership, safety, and repository boundaries

- **Never run Git commands or use Git tooling.** The owner performs all Git operations in GitHub Desktop.
- Preserve existing and unrelated user changes. Never reset, revert, discard, broadly reformat, rename, or remove behavior without authorization.
- Do not make opportunistic refactors, architectural rewrites, new dependency additions, project/namespace/API renames, or unrelated UI changes.
- Prefer existing abstractions, commands, services, styles, and tests when sound; avoid duplicate execution paths, state owners, and services.
- Do not modify generated outputs, caches, third-party code, or package artifacts unless explicitly required and justified.
- Before editing a file, inspect its current contents and the directly affected entry points. Report material uncertainty instead of guessing.
- Never expose secrets or user script contents in reports, diagnostics, test artifacts, or public exports.
- If an unrelated pre-existing failure is encountered, record it accurately and continue only where safe.

## 3. Task modes and authority

Follow the mode stated in the task. Otherwise infer from the request:

- **Investigate / audit / plan / review-only:** inspect and report; **do not modify files** or repair defects without authorization. Identify evidence, root cause versus hypothesis, affected files, risk, and observable acceptance criteria.
- **Implement / fix:** inspect current behavior, implement the requested end-to-end change across affected layers, and validate it. No placeholders, mock-only fixes, or TODO-only implementations.
- **Review and repair / harden:** review affected paths and acceptance criteria; fix only defects covered by the expressly approved repair scope. A request to review alone is read-only.

Prefer one cohesive implementation, not multiple speculative passes. When broader changes are essential, explain why a narrower fix would be incorrect or unsafe.

## 4. Console/editor/debugger architecture — enduring contract

The goal is a native-feeling, independent PowerShell console, with clear responsibilities:

- **Editor:** owns documents, text, selection, and editor UI. It submits Run File, Run Selection, and Debug requests; it does not synthesize console prompts, cursor placement, or interactive input echo.
- **PowerShell/PSES/ConPTY:** owns the shared interactive session, execution state, working directory, functions/variables, native application input/output, `Read-Host`, interruption, and recovery.
- **Terminal renderer (xterm):** renders the correctly ordered VT stream. Do not strip terminal-control bytes by naive substring filtering or break screen-state semantics.
- **Debugger:** preserves distinct debugging semantics, breakpoint behavior, stepping, and call-stack/variable state. Do not conflate Run and Debug.

**Migration state is not permanent policy.** Before changing a path, identify the *currently active* production routing, retired paths, experimental gates, and remaining acceptance gaps in source and recent applicable evidence. Do not revive a retired legacy path, silently promote an experimental one, or create a separate PowerShell session to bypass an issue.

For execution, input, terminal, or debugging changes, trace request admission, process/session lifecycle, streams, VT output, cancellation, prompt/readiness signaling, teardown, and UI state restoration. Preserve execution paths and temporary-file semantics; prevent duplicate output, hidden command echo, races, and lost input. Check recovery after failure or interruption, repeated invocation, and session continuity.

## 5. WPF, editor, and behavioral correctness

- Trace all relevant entry points: toolbar, menu, keyboard, context menu, programmatic requests, settings, and startup restoration.
- Preserve keyboard/focus behavior, accessibility semantics, binding/command routing, and persisted preferences.
- Reuse existing templates, resource dictionaries, and theme conventions. Consider resize, DPI scaling, and relevant enabled/disabled/error states; avoid unnecessary hard-coded dimensions.
- Never block the UI thread. Respect dispatcher, cancellation, async, and process-lifecycle ownership.
- Consider success, failure, cancellation, timeout, re-entry, shutdown, null state, disposal/unsubscription, and ordering races where applicable.
- Do not hide exceptions silently or change behavior outside the stated acceptance criteria.
- Screenshots and explicit measurements are requirements for the affected UI; avoid unrelated visual changes.

## 6. Developer diagnostics — proportionate and safe

For behavior or state-machine changes, **consider** whether existing diagnostics need to be updated or stale events removed. Add diagnostic events where they materially help explain faults, important transitions, rejected actions, lifecycle events, or race/timing behavior. Use correlation IDs and durations for multi-stage asynchronous operations when useful. Avoid routine per-character, per-frame, and entry/exit spam.

- Logging is best-effort: it must not block, crash, alter behavior, or materially slow normal operation. Disabled-mode overhead must be negligible.
- Never log full scripts, raw terminal buffers, complete selections/commands, passwords, tokens, private keys, or environment dumps. Prefer lengths, counts, hashes, and carefully sanitized bounded metadata; omit content previews when unsafe.
- Keep developer diagnostics separate from normal application logs, under `%LOCALAPPDATA%\PS7ScriptDesk\DeveloperDebugging\` where applicable.
- In the final response state **either** `Developer diagnostics updated: <reason>` **or** `Developer diagnostics not changed: <reason>`.

## 7. Build, testing, and acceptance — bounded by risk

Solution: `PS7ScriptDesk.slnx` (verify actual projects/configuration). Use the existing Windows/.NET 10/PowerShell 7 toolchain; do not alter machine-wide tooling or security settings without approval.

### Mandatory Codex development verification

When verification is required, first read `docs\LocalOnly_NotForGitHub\DevelopmentTools\README.md`. Use the installed authoritative runners in `C:\Users\rbarn\source\repos\PowerShellStudio\docs\LocalOnly_NotForGitHub\DevelopmentTools`: `Invoke-ScriptDeskBuild.ps1` for routine ScriptDesk compilation, `Invoke-ScriptDeskTests.ps1` for focused automated tests, and `Invoke-ScriptDeskMetrics.ps1` for source complexity measurements. Invoke them with `C:\Program Files\PowerShell\7\pwsh.exe`. Do not construct alternative routine build, test, or metric collectors.

Examples (check `$LASTEXITCODE` after each command; stop on nonzero):

```powershell
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoProfile -File 'C:\Users\rbarn\source\repos\PowerShellStudio\docs\LocalOnly_NotForGitHub\DevelopmentTools\Invoke-ScriptDeskBuild.ps1' -Target Tests
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoProfile -File 'C:\Users\rbarn\source\repos\PowerShellStudio\docs\LocalOnly_NotForGitHub\DevelopmentTools\Invoke-ScriptDeskTests.ps1' -Filter 'FullyQualifiedName~EditorColorUtilitiesTests'
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoProfile -File 'C:\Users\rbarn\source\repos\PowerShellStudio\docs\LocalOnly_NotForGitHub\DevelopmentTools\Invoke-ScriptDeskMetrics.ps1' -RelativePath 'PS7ScriptDesk.Shell\MainWindow.xaml.cs'
```

Keep verification bounded and forward-only: do not automatically retry a failed command, silently switch execution environments, or request elevation merely to obtain a passing result. Record actual exit codes, test counts, and evidence paths. Do not load SDK Roslyn assemblies into PowerShell's default assembly load context. Preserve the installed collectors and their methodology; document any future approved version change. If a runner is missing or fails, report the exact blocker; do not recreate or modify it. Any exceptional verification outside these runners requires specific justification and authorization. Existing bounded-testing and failure-stop policies below still apply. These requirements govern Codex verification only, not the owner's normal Visual Studio workflow.

1. Review changed files for accidental edits; build the smallest appropriate affected target. Use a solution-wide build only when warranted.
2. Run focused tests relevant to the change. Prefer existing **bounded production-service acceptance runners** for console/session behavior; avoid unnecessary new harnesses and duplicate suites.
3. For VSTest/testhost or MSBuild infrastructure stalls, record the exact failure, perform **at most one** targeted diagnostic if justified, then stop that route and report the blocker. Do not repeatedly retry, weaken tests, or present a workaround as a platform repair.
4. Re-run only the failing or directly affected checks after a concrete fix; reserve broad regression for requested or high-risk changes.
5. State precise coverage: source inspection, compilation, synthetic/unit test, live production-service test, and **owner-operated WPF GUI UAT are different levels**. Do not claim GUI acceptance from service-level results or use GUI automation.
6. Check newly introduced warnings and state which acceptance criteria passed, failed, or remain unverified. A successful build alone does not prove runtime behavior.

For interactive terminal changes, check Run/Interrupt restoration **without an extra Enter**, prompt/cursor placement, echoed input and Backspace for `Read-Host`, native input/output, and continuity of the shared session. For async/lifecycle changes, test relevant cancellation, repeat, failure/recovery, shutdown, and UI-thread cases.

## 8. Documentation, evidence, and file placement

- Read only pertinent, existing guidance: this file, directly affected source/tests, applicable current roadmap, and latest relevant task evidence when available. Search symbols/callers before opening large files. Do not repeatedly summarize project history.
- Update documentation only when the change affects documented architecture, build/release, diagnostics, workflows, or developer instructions. Do not create missing historical documents as placeholders.
- **Never place task reports, logs, temporary test sessions, captures, or evidence in the repository root.** Put task-local artifacts in one unique subfolder under:

  `C:\Users\rbarn\source\repos\PowerShellStudio\docs\LocalOnly_NotForGitHub\Codex_Work\<TASK-ID>`

- Keep production changes and checked-in tests within their normal project directories. Keep user data, private logs, secrets, and large evidence out of public source exports.
- Generate only evidence needed to substantiate the task. A brief ordinary fix requires a concise final response, **not** a mandatory report file. For architecture migrations, investigations, regressions, or tasks requesting evidence, prefer one consolidated `FINAL_REPORT.md` in the private task folder, plus only indispensable raw outputs.

## 9. Efficient execution and final response

Keep tool use and context proportional to the question: targeted searches, bounded tests, one coherent implementation, no repeated large suites, no redundant reports. Ask questions only when a consequential decision cannot be resolved from source, evidence, or the task's scope. Do not interrupt work to request information already available.

The final response should briefly identify:

- Mode and what changed (exact file paths, or **none** for read-only tasks).
- Acceptance criteria and **actual** build/test commands with outcomes, including unrun checks.
- Diagnostics updated/not changed, with reason (as specified above).
- Remaining risks and any specific owner GUI UAT needed.

Do not paste full source files or declare completion when material acceptance remains unverified. Report partial completion candidly.
