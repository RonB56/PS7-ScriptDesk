# Focused validation

## Completed

- Focused test-project build succeeded with Visual Studio MSBuild 18.8.2, configuration `CodexValidation`. The changed service and new integration test compiled. One unrelated existing warning was reported in `UiScaleWpfBehaviorTests.cs` (CS0414).
- Ran `verify-one-line-invocation.ps1` under PowerShell 7.6.10 against the built production invocation builder. Both generated invocations parsed with zero errors and no line breaks. The success case wrote `OK`, preserved a global shared-scope value, and the terminating-error case rethrew `R2_EXPECTED_TERMINATING_ERROR` while writing `ERROR`. The script uses paths containing apostrophes to exercise PowerShell single-quote escaping.
- No full-suite run was performed.

## Blocked

`ExperimentalRunFileInteractiveInputTests.DirectRunFile_InteractiveAndSecureReadHost_UsesSharedConsoleWithoutShowingWrapper` compiled but did not complete in this environment. VSTest failed to connect to its testhost after 90 seconds; a single retry with a 180-second connection window failed the same way. A subsequent runner attempt was stopped per the owner's instruction before producing a result. No assertion result was produced. The owner will execute the focused live test externally.

Run from the PowerShellStudio repository in a Windows/.NET test environment with PowerShell 7 and PSES installed:

```powershell
$env:PS7SCRIPT_DESK_TEST_PWSH = 'C:\Program Files\PowerShell\7\pwsh.exe'
$env:PS7SCRIPTDESK_PSES_ROOT = Join-Path $env:USERPROFILE '.vscode\extensions\ms-vscode.powershell-2025.4.0\modules'
dotnet test .\PS7ScriptDesk.Tests\PS7ScriptDesk.Tests.csproj --configuration CodexValidation --filter 'FullyQualifiedName~PS7ScriptDesk.Tests.ExperimentalRunFileInteractiveInputTests.DirectRunFile_InteractiveAndSecureReadHost_UsesSharedConsoleWithoutShowingWrapper'
```

If PSES is installed under a different extension version, set `PS7SCRIPTDESK_PSES_ROOT` to that installation's `modules` directory. The test intentionally fails fast with a skip if PowerShell 7 or PSES is missing.
