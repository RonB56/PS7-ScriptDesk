# Owner UAT: experimental Run File interaction

The accepted legacy bridge remains the default. To exercise this experimental bridge, start ScriptDesk from a process with `PS7SCRIPTDESK_EXPERIMENTAL_DIRECT_RUNFILE=1` set before launch.

1. In the shared console, set `$global:A02BridgeShared = "SHARED_SESSION_OK"`.
2. Save and Run File this script:

   ```powershell
   $plain = Read-Host 'R2 plain input'
   Write-Output "R2 plain received: $plain"
   $secure = Read-Host -Prompt 'R2 secure input' -AsSecureString
   if ($secure.Length -gt 0) { Write-Output 'R2 secure input received' }
   Write-Output "R2 shared value: $global:A02BridgeShared"
   ```

3. Confirm the console shows the two Read-Host prompts, accepts ordinary input and secure input through the native terminal, and does not echo the secure characters.
4. Confirm the visible console never shows the internal `& { ... }` invocation, `__ps7sd_status_...`, or `try {` wrapper and does not enter a `>>` continuation prompt.
5. Confirm the final shared value is `SHARED_SESSION_OK`, the normal PowerShell prompt returns, and the same PSES session remains available for another console command.
6. Repeat Run File once. For cancellation coverage, separately run a temporary script containing `Start-Sleep -Seconds 30`, cancel it using the normal execution control, and confirm the same session returns to its prompt.

After UAT, start ScriptDesk without the experiment environment variable and confirm the accepted legacy bridge remains selected by default. Run Selection and Debug are outside this change.
