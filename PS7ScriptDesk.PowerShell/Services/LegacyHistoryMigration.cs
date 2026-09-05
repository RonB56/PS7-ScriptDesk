using System.Text.RegularExpressions;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>
/// Produces the one-time legacy-history migration that runs in the real hosted
/// PowerShell process before its first interactive read. The script discovers the
/// effective PSReadLine HistorySavePath and never records command content.
/// </summary>
internal static class LegacyHistoryMigration
{
    internal const string LogPath = @"C:\Users\rbarn\source\repos\PowerShellStudio\docs\LocalOnly_NotForGitHub\Codex_Work\PSREADLINE_LEGACY_HISTORY_MIGRATION_FORENSIC.log";
    internal static bool IsLegacyManagedLine(string line, string managedRoot)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        var root = Regex.Escape(managedRoot.TrimEnd('\\', '/'));
        return Regex.IsMatch(line, $@"^\s*(?:&|\.)\s+'{root}\\psd-[0-9a-f]{{32}}\.ps1'(?:\s+#PS7SDi)?\s*$", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(line, $@"^\s*(?:&|\.)\s+'{root}\\psh-[0-9a-f]{{32}}\.ps1'\s+'{root}\\psi-[0-9a-f]{{32}}\.ps1'\s*$", RegexOptions.IgnoreCase);
    }

    internal static string BuildStartupCommand() =>
        "try { " + Script.Replace("__PS7SD_MIGRATION_LOG_PATH__", LogPath.Replace("'", "''", StringComparison.Ordinal)) + " } catch { }";

    private static readonly string Script = """
        $__pssdMigrateLegacyHistory = {
            try {
                $__pssdMigrationLogPath = '__PS7SD_MIGRATION_LOG_PATH__'
                function Write-Ps7SdMigrationEvent([string] $event, [hashtable] $fields) {
                    try {
                        $parts = [Collections.Generic.List[string]]::new()
                        $parts.Add("event=$event")
                        foreach ($key in $fields.Keys) { $parts.Add("$key=$($fields[$key])") }
                        $parts.Add("timestamp=$([DateTimeOffset]::UtcNow.ToString('O'))")
                        [IO.File]::AppendAllText($__pssdMigrationLogPath, (($parts -join ' ') + [Environment]::NewLine))
                    } catch { }
                }
                function Write-Ps7SdMigrationStageException([string] $stage, [string] $api, $errorRecord) {
                    try {
                        $ex = $errorRecord.Exception
                        $inner = [Collections.Generic.List[string]]::new()
                        while ($null -ne $ex) {
                            $inner.Add("$($ex.GetType().FullName):$($ex.Message -replace '[\r\n ]+', ' ')")
                            $ex = $ex.InnerException
                        }
                        $target = if ($errorRecord.Exception.TargetSite) { $errorRecord.Exception.TargetSite.ToString() -replace '[\r\n ]+', ' ' } else { '(none)' }
                        $stack = if ($errorRecord.Exception.StackTrace) { $errorRecord.Exception.StackTrace -replace '[\r\n]+', ' ' } else { '(none)' }
                        Write-Ps7SdMigrationEvent 'MIGRATION_STAGE_EXCEPTION' @{ stage = $stage; api = $api; exceptionType = $errorRecord.Exception.GetType().FullName; exceptionMessage = ($errorRecord.Exception.Message -replace '[\r\n ]+', ' '); innerExceptions = ($inner -join '|'); targetSite = $target; stackTrace = $stack; powershellVersion = $PSVersionTable.PSVersion.ToString(); dotnetVersion = [Environment]::Version.ToString() }
                    } catch { }
                }
                function Get-Ps7SdSha256([byte[]] $data) {
                    $algorithm = [Security.Cryptography.SHA256]::Create()
                    try { return [Convert]::ToHexString($algorithm.ComputeHash($data)) }
                    finally { $algorithm.Dispose() }
                }
                $opt = Get-PSReadLineOption
                $historyPath = $opt.HistorySavePath
                if ([string]::IsNullOrWhiteSpace($historyPath) -or -not [IO.File]::Exists($historyPath)) {
                    Write-Ps7SdMigrationEvent 'LEGACY_HISTORY_MIGRATION_BEGIN' @{ effectiveHistorySavePath = $historyPath; sourceLineCount = 0; sourceHash = '(none)'; matchingPersistentCount = 0 }
                    Write-Ps7SdMigrationEvent 'LEGACY_HISTORY_BACKUP_NOT_REQUIRED' @{}
                    Write-Ps7SdMigrationEvent 'LEGACY_HISTORY_MIGRATION_END' @{ reason = 'HistoryPathMissing' }
                    return
                }
                $root = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'PS7ScriptDesk\Temp\TerminalSnapshots')).TrimEnd('\')
                $q = [regex]::Escape($root)
                $psdPattern = "^\s*(?:&|\.)\s+'$q\\psd-[0-9a-f]{32}\.ps1'(?:\s+#PS7SDi)?\s*$"
                $helperPattern = "^\s*(?:&|\.)\s+'$q\\psh-[0-9a-f]{32}\.ps1'\s+'$q\\psi-[0-9a-f]{32}\.ps1'\s*$"
                $bytes = [IO.File]::ReadAllBytes($historyPath)
                $sha = Get-Ps7SdSha256 $bytes
                $encoding = [Text.UTF8Encoding]::new($false)
                $offset = 0
                if ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191) { $encoding = [Text.UTF8Encoding]::new($true); $offset = 3 }
                elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 255 -and $bytes[1] -eq 254) { $encoding = [Text.UnicodeEncoding]::new($false, $true); $offset = 2 }
                elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 254 -and $bytes[1] -eq 255) { $encoding = [Text.UnicodeEncoding]::new($true, $true); $offset = 2 }
                $text = $encoding.GetString($bytes, $offset, $bytes.Length - $offset)
                $rows = [regex]::Matches($text, '.*?(?:\r\n|\n|\r|$)') | ForEach-Object Value | Where-Object { $_ -ne '' }
                $kept = [Collections.Generic.List[string]]::new()
                $removed = 0
                foreach ($row in $rows) {
                    $content = $row.TrimEnd("`r", "`n")
                    if ($content -match $psdPattern -or $content -match $helperPattern) { $removed++; continue }
                    $kept.Add($row)
                }
                Write-Ps7SdMigrationEvent 'LEGACY_HISTORY_MIGRATION_BEGIN' @{ effectiveHistorySavePath = $historyPath; sourceLineCount = $rows.Count; sourceHash = $sha; matchingPersistentCount = $removed }
                if ($removed -eq 0) {
                    Write-Ps7SdMigrationEvent 'LEGACY_HISTORY_BACKUP_NOT_REQUIRED' @{}
                    Write-Ps7SdMigrationEvent 'LEGACY_HISTORY_MIGRATION_END' @{ reason = 'NoMatches' }
                    return
                }
                $expectedFinalLineCount = $rows.Count - $removed
                $backup = "$historyPath.pre-PS7ScriptDesk-cleanup-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')).txt"
                $suffix = 0
                while ($true) {
                    try { $stream = [IO.File]::Open($backup, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None); $stream.Write($bytes, 0, $bytes.Length); $stream.Dispose(); break }
                    catch [IO.IOException] { $suffix++; $backup = "$historyPath.pre-PS7ScriptDesk-cleanup-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))-$suffix.txt" }
                }
                $backupHash = Get-Ps7SdSha256 ([IO.File]::ReadAllBytes($backup))
                if (-not [string]::Equals($sha, $backupHash, [StringComparison]::OrdinalIgnoreCase)) { Write-Ps7SdMigrationEvent 'LEGACY_HISTORY_BACKUP_FAILED' @{ reason = 'HashMismatch' }; return }
                Write-Ps7SdMigrationEvent 'LEGACY_HISTORY_BACKUP_CREATED' @{ backupPath = $backup; hashVerified = $true; backupHash = $backupHash; backupLineCount = $rows.Count }
                $stage = 'SourceRehashBeforeCommit'; $api = '[IO.File]::ReadAllBytes + Get-Ps7SdSha256'
                Write-Ps7SdMigrationEvent 'MIGRATION_STAGE_BEGIN' @{ stage = $stage; api = $api }
                try { $current = [IO.File]::ReadAllBytes($historyPath); $currentHash = Get-Ps7SdSha256 $current; Write-Ps7SdMigrationEvent 'MIGRATION_STAGE_SUCCESS' @{ stage = $stage } }
                catch { Write-Ps7SdMigrationStageException $stage $api $_; throw }
                if (-not [string]::Equals($sha, $currentHash, [StringComparison]::OrdinalIgnoreCase)) { Write-Ps7SdMigrationEvent 'LEGACY_HISTORY_PERSISTENT_COMMIT' @{ removedCount = 0; finalLineCount = $rows.Count; finalHash = $sha; result = 'ABORT_SOURCE_CHANGED' }; return }
                $stage = 'FilterHistory'; $api = '[regex]::Matches / history-row predicate'
                Write-Ps7SdMigrationEvent 'MIGRATION_STAGE_BEGIN' @{ stage = $stage; api = $api }
                Write-Ps7SdMigrationEvent 'MIGRATION_STAGE_SUCCESS' @{ stage = $stage }
                $temp = "$historyPath.ps7sd-migration-$([Guid]::NewGuid().ToString('N')).tmp"
                $transactionalBackup = "$historyPath.ps7sd-atomic-$([Guid]::NewGuid().ToString('N')).bak"
                try {
                    $stage = 'WriteTemporaryFile'; $api = '[IO.File]::WriteAllBytes'
                    Write-Ps7SdMigrationEvent 'MIGRATION_STAGE_BEGIN' @{ stage = $stage; api = $api }
                    $finalText = [string]::Concat($kept)
                    [IO.File]::WriteAllBytes($temp, $encoding.GetBytes($finalText))
                    Write-Ps7SdMigrationEvent 'MIGRATION_STAGE_SUCCESS' @{ stage = $stage }
                    $stage = 'ValidateTemporaryFile'; $api = '[IO.File]::ReadAllBytes + encoding.GetString + regex validation'
                    Write-Ps7SdMigrationEvent 'MIGRATION_STAGE_BEGIN' @{ stage = $stage; api = $api }
                    $verify = $encoding.GetString([IO.File]::ReadAllBytes($temp))
                    $verifyRows = [regex]::Matches($verify, '.*?(?:\r\n|\n|\r|$)') | ForEach-Object Value | Where-Object { $_ -ne '' }
                    if ($verify.Length -ne $finalText.Length -or $verifyRows.Count -ne $expectedFinalLineCount -or @($verifyRows | Where-Object { $_.TrimEnd("`r", "`n") -match $psdPattern -or $_.TrimEnd("`r", "`n") -match $helperPattern }).Count -ne 0) { Write-Ps7SdMigrationEvent 'LEGACY_HISTORY_PERSISTENT_COMMIT' @{ removedCount = 0; finalLineCount = $verifyRows.Count; finalHash = '(none)'; result = 'ABORT_OUTPUT_VALIDATION' }; return }
                    Write-Ps7SdMigrationEvent 'MIGRATION_STAGE_SUCCESS' @{ stage = $stage }
                    $stage = 'AtomicReplace'; $api = '[IO.File]::Replace'
                    Write-Ps7SdMigrationEvent 'MIGRATION_STAGE_BEGIN' @{ stage = $stage; api = $api }
                    [IO.File]::Replace($temp, $historyPath, $transactionalBackup, $true)
                    Write-Ps7SdMigrationEvent 'MIGRATION_STAGE_SUCCESS' @{ stage = $stage; transactionalBackupPath = $transactionalBackup }
                } finally { if ([IO.File]::Exists($temp)) { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue } }
                $stage = 'PostCommitValidation'; $api = '[IO.File]::ReadAllBytes + encoding.GetString + regex validation'
                Write-Ps7SdMigrationEvent 'MIGRATION_STAGE_BEGIN' @{ stage = $stage; api = $api }
                $committedText = $encoding.GetString([IO.File]::ReadAllBytes($historyPath))
                $committedRows = [regex]::Matches($committedText, '.*?(?:\r\n|\n|\r|$)') | ForEach-Object Value | Where-Object { $_ -ne '' }
                $committedHash = Get-Ps7SdSha256 ([IO.File]::ReadAllBytes($historyPath))
                if ($committedRows.Count -ne $expectedFinalLineCount -or @($committedRows | Where-Object { $_.TrimEnd("`r", "`n") -match $psdPattern -or $_.TrimEnd("`r", "`n") -match $helperPattern }).Count -ne 0) { Write-Ps7SdMigrationEvent 'LEGACY_HISTORY_PERSISTENT_COMMIT' @{ removedCount = $removed; finalLineCount = $committedRows.Count; finalHash = $committedHash; result = 'POST_COMMIT_VALIDATION_FAILED' }; return }
                Write-Ps7SdMigrationEvent 'MIGRATION_STAGE_SUCCESS' @{ stage = $stage }
                if ([IO.File]::Exists($transactionalBackup)) { Remove-Item -LiteralPath $transactionalBackup -Force -ErrorAction SilentlyContinue }
                Write-Ps7SdMigrationEvent 'LEGACY_HISTORY_PERSISTENT_COMMIT' @{ removedCount = $removed; finalLineCount = $committedRows.Count; finalHash = $committedHash; result = 'COMMITTED' }
                $global:__pssdMigrationPromptCalls = 0
                $global:__pssdMigrationVerified = $false
                $global:__pssdMigrationOriginalPrompt = (Get-Command prompt -CommandType Function -ErrorAction SilentlyContinue).ScriptBlock
                if ($null -ne $global:__pssdMigrationOriginalPrompt) {
                    function global:prompt {
                        $global:__pssdMigrationPromptCalls++
                        $result = & $global:__pssdMigrationOriginalPrompt
                        if ($global:__pssdMigrationPromptCalls -gt 1 -and -not $global:__pssdMigrationVerified) {
                            try {
                                $items = @([Microsoft.PowerShell.PSConsoleReadLine]::GetHistoryItems())
                                $memoryMatches = @($items | Where-Object { $_.CommandLine -and ($_.CommandLine -match $psdPattern -or $_.CommandLine -match $helperPattern) }).Count
                                Write-Ps7SdMigrationEvent 'PSREADLINE_INITIALIZED' @{ initialization = 'FirstReadLineCompleted' }
                                Write-Ps7SdMigrationEvent 'LEGACY_HISTORY_MEMORY_VERIFY' @{ itemCount = $items.Count; matchingInternalCount = $memoryMatches }
                                Write-Ps7SdMigrationEvent 'LEGACY_HISTORY_MIGRATION_END' @{ reason = 'Completed' }
                                $global:__pssdMigrationVerified = $true
                            } catch { }
                        }
                        $result
                    }
                }
            } catch { Write-Ps7SdMigrationStageException $stage $api $_; Write-Ps7SdMigrationEvent 'LEGACY_HISTORY_MIGRATION_FAILED' @{ stage = $stage; reason = $_.Exception.GetType().Name }; Write-Ps7SdMigrationEvent 'LEGACY_HISTORY_MIGRATION_END' @{ reason = 'Exception' } }
        }
        & $__pssdMigrateLegacyHistory
        Remove-Variable __pssdMigrateLegacyHistory -ErrorAction SilentlyContinue
        """;
}
