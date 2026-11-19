$ErrorActionPreference = 'Stop'
$serviceAssemblyPath = 'C:\Users\rbarn\source\repos\PowerShellStudio\PS7ScriptDesk.PowerShell\bin\CodexValidation\net10.0\PS7ScriptDesk.PowerShell.dll'
$assembly = [System.Reflection.Assembly]::LoadFrom($serviceAssemblyPath)
$serviceType = $assembly.GetType('PS7ScriptDesk.PowerShell.Services.PsesOwnedInteractiveSessionService', $true)
$builder = $serviceType.GetMethod('BuildExperimentalDirectRunFileInvocation', [System.Reflection.BindingFlags]'Static, NonPublic')
if ($null -eq $builder) { throw 'The built experimental invocation builder was not found.' }

function Quote-PowerShellString([string] $Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("PS7SD_R2_'quote_" + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $okScript = Join-Path $root 'scope-ok.ps1'
    $okStatus = Join-Path $root 'scope-ok.status'
    [System.IO.File]::WriteAllText($okScript, '$global:A02R2InvocationScope = ''SHARED_SCOPE_OK''; Write-Output ''R2_PARSE_OK''')
    $invocation = [string]$builder.Invoke($null, @((Quote-PowerShellString $okScript), (Quote-PowerShellString $okStatus), '__ps7sd_status_r2_ok'))
    $tokens = $null
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseInput($invocation, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -ne 0) { throw "Success wrapper parse failed: $($parseErrors[0].Message)" }
    if ($invocation.Contains("`n") -or $invocation.Contains("`r")) { throw 'The generated wrapper contains a line break.' }
    Invoke-Expression $invocation | Out-Null
    if ([System.IO.File]::ReadAllText($okStatus) -ne 'OK') { throw 'Success status file did not contain OK.' }
    if ($global:A02R2InvocationScope -ne 'SHARED_SCOPE_OK') { throw 'The invoked file did not run with the intended shared global scope.' }

    $errorScript = Join-Path $root 'error.ps1'
    $errorStatus = Join-Path $root 'error.status'
    [System.IO.File]::WriteAllText($errorScript, "throw 'R2_EXPECTED_TERMINATING_ERROR'")
    $errorInvocation = [string]$builder.Invoke($null, @((Quote-PowerShellString $errorScript), (Quote-PowerShellString $errorStatus), '__ps7sd_status_r2_error'))
    $errorTokens = $null
    $errorParseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseInput($errorInvocation, [ref]$errorTokens, [ref]$errorParseErrors) | Out-Null
    if ($errorParseErrors.Count -ne 0) { throw "Error wrapper parse failed: $($errorParseErrors[0].Message)" }
    $reportedError = $null
    try { Invoke-Expression $errorInvocation | Out-Null } catch { $reportedError = $_.Exception.Message }
    if ($reportedError -notlike '*R2_EXPECTED_TERMINATING_ERROR*') { throw 'The script error was not rethrown to the caller.' }
    if ([System.IO.File]::ReadAllText($errorStatus) -ne 'ERROR') { throw 'Error status file did not contain ERROR.' }

    [pscustomobject]@{
        Runtime = $PSVersionTable.PSVersion.ToString()
        SuccessWrapperOneLine = -not ($invocation.Contains("`n") -or $invocation.Contains("`r"))
        SuccessWrapperParseErrors = $parseErrors.Count
        SuccessStatus = [System.IO.File]::ReadAllText($okStatus)
        SharedGlobalScope = $global:A02R2InvocationScope
        ErrorWrapperParseErrors = $errorParseErrors.Count
        ErrorRethrown = $reportedError
        ErrorStatus = [System.IO.File]::ReadAllText($errorStatus)
    } | Format-List
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Variable -Name A02R2InvocationScope -Scope Global -ErrorAction SilentlyContinue
}
