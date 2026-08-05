# PS7 ScriptDesk — Codex Repository Instructions

## 1. Product identity and technical baseline

This repository builds **PS7 ScriptDesk**. The internal code/project-safe root name is `PS7ScriptDesk`.

PS7 ScriptDesk is a Windows desktop application for opening, editing, running, and debugging user-selected PowerShell 7.x scripts.

Assume the established product baseline is:

- Windows 11
- .NET 8
- WPF
- PowerShell 7.x
- Existing layered architecture and repository conventions

Do not change the product name, internal root name, target framework, UI framework, packaging model, or major dependencies unless the user explicitly requests it.

## 2. Primary operating rule

**Inspect first, understand the current behavior, then make the smallest complete change that satisfies the request.**

Before editing code, Codex must:

1. Read this file.
2. Check the relevant supporting documents in `docs/`.
3. Inspect the current implementation and all directly related call paths.
4. Check the working tree for existing user changes.
5. Identify fragile areas, dependencies, and likely regression risks.
6. State any material uncertainty instead of guessing.

Do not spend time rediscovering the entire repository when the supporting documentation already answers the question.

## 3. Preserve user work and repository integrity

- Never discard, overwrite, revert, reset, or reformat unrelated user changes.
- Never use destructive Git operations unless the user explicitly requests them.
- Do not modify generated files, build outputs, package outputs, caches, or third-party source unless required and explicitly justified.
- Avoid broad formatting-only changes.
- Keep diffs focused on the requested behavior.
- Do not rename projects, namespaces, folders, assemblies, packages, public APIs, or persisted settings without explicit approval.
- Do not introduce a new dependency when the existing platform or repository already provides a reasonable solution.
- Do not remove existing behavior merely because it appears unused. Verify first.

If unrelated repository damage or a pre-existing build failure is discovered, report it clearly and continue only where safe.

## 4. Architecture and scope discipline

Preserve the current layered architecture unless the user explicitly asks for a redesign.

Prefer:

- Targeted fixes over broad refactors
- Existing services, abstractions, commands, styles, and resource dictionaries over parallel replacements
- Cohesive changes that fully implement one requested outcome
- Reuse of existing patterns when those patterns are sound

Avoid:

- Opportunistic cleanup outside the task
- Large rewrites to solve a local problem
- New architectural layers without a demonstrated need
- Moving logic into code-behind when the current design places it elsewhere
- Duplicating state, services, styles, or command paths
- Silent behavior changes outside the acceptance criteria

When a broader change is genuinely required, explain why the narrow approach is unsafe or incomplete before proceeding.

## 5. Work phases

Use the phase named in the user prompt. When no phase is named, infer the safest applicable phase.

### Phase A — Investigation only

Use when the user asks Codex to inspect, diagnose, map, or plan.

- Do not edit files.
- Trace the complete relevant behavior.
- Identify the files, classes, commands, events, services, settings, and resources involved.
- Explain current behavior based on evidence from the repository.
- Identify likely causes, risks, and unknowns.
- Propose the smallest safe implementation.
- List the files expected to change.
- Define observable acceptance criteria.

### Phase B — Implementation

Use when the user explicitly asks Codex to make the change.

- Re-inspect the relevant implementation before editing.
- Implement the complete requested behavior; do not leave placeholders, TODO-only stubs, mock logic, or half-connected UI.
- Keep the change narrow but include every layer required for correctness.
- Update diagnostics, settings, resources, tests, and documentation when affected.
- Build and test before declaring completion.

### Phase C — Review and hardening

Use when the user asks for review, regression analysis, or final validation.

- Review the full diff and affected call paths.
- Look for regressions, race conditions, UI-thread violations, stale diagnostics, dead code, null-state failures, disposal issues, cancellation defects, and inconsistent state transitions.
- Verify every acceptance criterion.
- Fix defects within the approved scope.
- Do not redesign working code merely to express a preference.

## 6. WPF and visual-change rules

For any UI or appearance change:

- Inspect existing styles, templates, resource dictionaries, layout conventions, and theme handling before adding new ones.
- Preserve keyboard navigation, focus behavior, tab order, access keys, and accessibility semantics.
- Preserve behavior at common DPI scaling levels and when the window is resized.
- Avoid hard-coded dimensions unless the requested design or existing convention requires them.
- Prefer shared resources for genuinely reusable values; do not create a global resource for a one-off value.
- Keep visual changes consistent with the rest of PS7 ScriptDesk unless the user explicitly requests a broader redesign.
- Add or update tooltips where a new or changed control is not self-explanatory.
- Do not solve a visual problem by breaking command routing, bindings, automation IDs, or existing interaction behavior.
- Check both enabled and disabled states, hover/focus states, selected states, empty states, error states, and narrow-window behavior when relevant.

When screenshots or explicit measurements are provided, treat them as requirements. When the request is subjective, translate it into concrete layout and behavior decisions and identify assumptions.

## 7. Behavior-change rules

For any behavioral change:

- Trace all entry points, not only the most obvious button or handler.
- Identify keyboard shortcuts, menus, toolbar commands, context menus, startup restoration, settings, and automated paths that may invoke the same behavior.
- Preserve unrelated behavior and persisted user preferences.
- Define what happens during success, failure, cancellation, timeout, repeated invocation, shutdown, and re-entry when applicable.
- Avoid blocking the UI thread.
- Use existing cancellation, dispatcher, async, command, and process-lifecycle patterns where available.
- Ensure state exposed to the UI remains internally consistent.
- Do not suppress exceptions without appropriate handling and diagnostics.

## 8. PowerShell execution, terminal, and debugger safeguards

These are fragile, stateful subsystems. Changes require end-to-end inspection.

When touching execution, terminal hosting, live input/output, script snapshots, prompt detection, sentinels, breakpoints, stepping, variables, call stack, run/stop/continue, or process management:

- Read `docs/DEBUGGER_AND_TERMINAL.md` and `docs/KNOWN_FRAGILE_AREAS.md` first.
- Trace process creation, stream handling, cancellation, teardown, and UI state transitions.
- Preserve script path and temporary-file semantics.
- Preserve output ordering and avoid duplicate output.
- Avoid races between process exit, stream completion, cancellation, and UI cleanup.
- Verify repeated runs and recovery after a failed or interrupted run.
- Treat diagnostics updates as required unless there is a clear, documented reason otherwise.

## 9. Developer diagnostics/debugging maintenance contract

The developer diagnostics system is part of the application's maintainability contract. Every code addition, behavior change, workflow change, and bug fix must consider whether developer diagnostics need to be added, updated, corrected, or removed.

Diagnostics normally require updates when the task touches:

- User actions, menu handlers, toolbar handlers, keyboard shortcuts, or command routing
- Debugging, breakpoints, stepping, continue/stop behavior, debugger state, prompts, processes, variables, or call stack logic
- Terminal hosting, live console input/output, prompt detection, command execution, sentinels, run script, run selection, temporary snapshots, or process lifecycle
- Editor actions, active-document changes, dirty state, save/open/close behavior, selection/caret behavior, diagnostics, hover/help, completion, metadata warmup, or command catalog loading
- Settings load/save, runtime toggles, startup, shutdown, background services, file I/O, cleanup, retention, packaging, install helpers, or error handling
- Any async workflow, background task, state machine, retry path, cancellation path, timeout path, or recovery path

Diagnostics should be detailed enough to reconstruct what happened in order. Add or update logs for the following where useful:

- Method or event-handler entry and exit
- User action accepted or rejected, including the reason
- Important decision branches
- Relevant state before and after a change
- State transitions
- Operation or correlation IDs for actions spanning classes or async calls
- Elapsed time for operations that can block, race, fail, or feel slow
- File paths involved in execution or debugging, when safe
- Counts, lengths, hashes, and capped previews instead of full user content
- Exceptions with enough surrounding context to diagnose the failure

When behavior changes, update or remove stale messages so logs do not become misleading.

### Diagnostics safety

- Never log full scripts, full terminal buffers, passwords, tokens, API keys, cookies, certificates, private keys, authorization headers, or full environment dumps.
- For script, command, terminal, or selected text, log only length, line count, a hash when useful, and a sanitized capped preview.
- Logging must be best-effort and must never crash, freeze, or materially alter application behavior.
- When developer diagnostics are disabled, overhead must remain minimal.
- Developer debugging logs must remain separate from normal application logs, preferably under `%LOCALAPPDATA%\PS7ScriptDesk\DeveloperDebugging\`.

The final response must contain exactly one of these statements, completed with a specific reason:

- `Developer diagnostics updated: <brief reason>`
- `Developer diagnostics not changed: <brief reason why the affected behavior does not require it>`

If the task modifies debugger, terminal, execution, editor metadata, settings, startup, or an async state machine, assume diagnostics need updating unless clearly proven otherwise.

## 10. Testing and validation requirements

Before reporting an implementation complete:

1. Review the final diff for accidental or unrelated changes.
2. Build the appropriate project or full solution using the documented repository command.
3. Run relevant automated tests when present.
4. Run targeted validation for the changed behavior when the environment allows it.
5. Confirm every acceptance criterion individually.
6. Check for new warnings introduced by the change.
7. Report any validation that could not be performed and why.

Do not claim that a behavior works merely because the code compiles.

For async, debugger, terminal, editor, settings, or lifecycle changes, explicitly consider:

- Repeated invocation
- Cancellation or interruption
- Failure and recovery
- Application shutdown during the operation
- Null or missing state
- Timing and ordering races
- UI-thread safety
- Disposal and event unsubscription

Do not weaken tests or delete failing tests to make the build pass unless the user explicitly requests a test change and the revised expectation is correct.

## 11. Documentation maintenance

Update documentation when the change alters:

- Architecture or major ownership boundaries
- Build, packaging, installation, or release steps
- Debugger or terminal behavior
- Known fragile areas
- User-visible workflows that existing documentation describes
- The Codex workflow itself

Do not rewrite unrelated documentation. Keep changes factual and synchronized with the implementation.

## 12. Supporting documents

Use these documents before re-deriving repository knowledge:

- `docs/CODEBASE_OVERVIEW.md`
- `docs/BUILD_AND_PACKAGING.md`
- `docs/DEBUGGER_AND_TERMINAL.md`
- `docs/RELEASE_READINESS.md`
- `docs/KNOWN_FRAGILE_AREAS.md`
- `docs/CODEX_WORKFLOW.md`

If a listed document is missing or stale, report that fact rather than inventing its contents.

## 13. Efficient Codex behavior

To conserve context and usage while maintaining quality:

- Read only the files needed for the current feature after consulting the repository docs.
- Search for symbols and call sites before opening large files in full.
- Do not repeatedly restate repository history or these instructions.
- Do not paste full source files into the final response.
- Keep progress updates focused on findings, decisions, blockers, and validation.
- Prefer one cohesive implementation over multiple partial passes.
- Ask no question that can be answered by inspecting the repository.
- When a requirement is genuinely ambiguous, make the safest narrow assumption, state it, and keep the implementation reversible.

## 14. Required final response format

Every Codex task must end with a concise report containing:

1. **Phase attempted**
2. **Exact files changed** — or `None` for investigation-only work
3. **Action summary**
4. **Acceptance criteria status**
5. **Developer diagnostics updated or not changed, with reason**
6. **Build/test result**, including command and outcome
7. **Remaining risks or manual tests needed**

Do not paste full files in the final summary unless the user explicitly requests them.
Do not claim completion when required validation failed or was not performed; describe the actual state precisely.
