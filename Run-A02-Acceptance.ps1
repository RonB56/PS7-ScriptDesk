Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Owner configuration: the unattended default runs the supported core suite.
$RunCore = $true
$EnableStress = $false
$OverallBudgetSeconds = 300
$BuildBudgetSeconds = 120

$repoRoot = $PSScriptRoot
$project = Join-Path $repoRoot 'PS7ScriptDesk.AcceptanceRunner/PS7ScriptDesk.AcceptanceRunner.csproj'
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$evidenceParent = Join-Path $repoRoot 'docs/LocalOnly_NotForGitHub/Codex_Work'
$evidenceRoot = Join-Path $evidenceParent "A02-TEST-AUTO-02_$stamp"
if (Test-Path -LiteralPath $evidenceRoot) { $evidenceRoot += '_' + [guid]::NewGuid().ToString('N').Substring(0, 8) }
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
$buildLog = Join-Path $evidenceRoot 'build.log'
$runnerLog = Join-Path $evidenceRoot 'runner.log'
$previousEvidence = $env:PS7SD_ACCEPTANCE_EVIDENCE

function Invoke-BoundedProcess([string] $FilePath, [string[]] $Arguments, [int] $TimeoutSeconds, [string] $LogPath) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Could not start process: $FilePath" }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill($true); $process.WaitForExit(5000) } catch { $_ | Out-String | Set-Content -LiteralPath ($LogPath + '.kill-error') }
        'Process timed out after {0} seconds: {1}' -f $TimeoutSeconds, $FilePath | Set-Content -LiteralPath ($LogPath + '.timeout')
        try { $stdoutTask.GetAwaiter().GetResult() | Set-Content -LiteralPath $LogPath }
        catch { $_ | Out-String | Set-Content -LiteralPath ($LogPath + '.stdout-capture-error') }
        try { $stderrTask.GetAwaiter().GetResult() | Set-Content -LiteralPath ($LogPath + '.stderr') }
        catch { $_ | Out-String | Set-Content -LiteralPath ($LogPath + '.stderr-capture-error') }
        throw "Process exceeded its $TimeoutSeconds second budget: $FilePath"
    }
    $process.WaitForExit()
    $stdoutTask.Result | Set-Content -LiteralPath $LogPath
    $stderrTask.Result | Set-Content -LiteralPath ($LogPath + '.stderr')
    return $process.ExitCode
}

try {
    if (-not $RunCore) { throw 'RunCore must remain enabled for the default acceptance invocation.' }
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $buildArgs = @('build', $project, '--configuration', 'Release', '--runtime', 'win-x64', '-m:1', '-v:minimal')
    # Restore is allowed if the runner has not been built before; it is bounded by the same build budget.
    $buildCode = Invoke-BoundedProcess $dotnet $buildArgs $BuildBudgetSeconds $buildLog
    if ($buildCode -ne 0) { throw "Acceptance runner build failed (exit $buildCode). See $buildLog" }

    $env:PS7SD_ACCEPTANCE_EVIDENCE = $evidenceRoot
    $runnerDll = Join-Path $repoRoot 'PS7ScriptDesk.AcceptanceRunner/bin/Release/net10.0-windows/win-x64/PS7ScriptDesk.AcceptanceRunner.dll'
    if (-not (Test-Path -LiteralPath $runnerDll)) { throw "Built runner was not found: $runnerDll" }
    $runnerCode = Invoke-BoundedProcess $dotnet @($runnerDll) $OverallBudgetSeconds $runnerLog
    Get-Content -LiteralPath $runnerLog
    if (Test-Path -LiteralPath ($runnerLog + '.stderr')) { Get-Content -LiteralPath ($runnerLog + '.stderr') }
    if ($runnerCode -ne 0) { exit $runnerCode }
    Write-Output "Evidence directory: $evidenceRoot"
    exit 0
}
catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $evidenceRoot 'launcher-error.txt')
    Write-Error "A02 acceptance runner failed: $($_.Exception.Message). Evidence: $evidenceRoot"
    exit 2
}
finally {
    if ($null -eq $previousEvidence) { Remove-Item Env:\PS7SD_ACCEPTANCE_EVIDENCE -ErrorAction SilentlyContinue }
    else { $env:PS7SD_ACCEPTANCE_EVIDENCE = $previousEvidence }
}
