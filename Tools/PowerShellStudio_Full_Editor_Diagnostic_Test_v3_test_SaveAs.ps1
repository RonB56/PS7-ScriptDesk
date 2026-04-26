<#
.SYNOPSIS
    PowerShellStudio full editor, console, GUI, and PowerShell 7 diagnostic test v3.

.DESCRIPTION
    This script is intended as a standard regression test for PowerShellStudio.
    It deliberately uses PowerShell 7 features and is not Windows PowerShell 5.1 compatible.

    Expected behavior in PowerShellStudio:
      - No editor diagnostic service timeout.
      - No false diagnostics on classes or valid paths.
      - Script output remains visible.
      - WinForms and WPF test windows briefly appear and close automatically.
      - Console returns to a normal PS prompt without pressing Enter.
      - Console does not show a continuation prompt (>>).
      - Internal dispatch commands are not visible.
#>

#Requires -Version 7.0

[CmdletBinding()]
param(
    [switch]$SkipProgress,
    [switch]$SkipGui,
    [switch]$KeepTempFiles,
    [int]$ProgressDelayMilliseconds = 75
)

Set-StrictMode -Version Latest

$ErrorActionPreference = 'Continue'
$WarningPreference = 'Continue'
$VerbosePreference = 'Continue'
$DebugPreference = 'Continue'
$InformationPreference = 'Continue'

$script:PassedCount = 0
$script:FailedCount = 0
$script:WarningCount = 0
$script:SkippedCount = 0

function Write-Section {
    param([Parameter(Mandatory)][string]$Title)
    Write-Host ''
    Write-Host '============================================================' -ForegroundColor Cyan
    Write-Host $Title -ForegroundColor Cyan
    Write-Host '============================================================' -ForegroundColor Cyan
}

function Write-SubSection {
    param([Parameter(Mandatory)][string]$Title)
    Write-Host ''
    Write-Host '------------------------------------------------------------' -ForegroundColor DarkCyan
    Write-Host $Title -ForegroundColor Yellow
    Write-Host '------------------------------------------------------------' -ForegroundColor DarkCyan
}

function Add-TestPass { param([Parameter(Mandatory)][string]$Message) $script:PassedCount++; Write-Host "[PASS] $Message" -ForegroundColor Green }
function Add-TestFail { param([Parameter(Mandatory)][string]$Message) $script:FailedCount++; Write-Host "[FAIL] $Message" -ForegroundColor Red }
function Add-TestWarning { param([Parameter(Mandatory)][string]$Message) $script:WarningCount++; Write-Host "[WARN] $Message" -ForegroundColor Yellow }
function Add-TestSkipped { param([Parameter(Mandatory)][string]$Message) $script:SkippedCount++; Write-Host "[SKIP] $Message" -ForegroundColor DarkYellow }

function Test-Condition {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$PassMessage,
        [Parameter(Mandatory)][string]$FailMessage
    )

    if ($Condition) { Add-TestPass $PassMessage } else { Add-TestFail $FailMessage }
}

function Invoke-StaPowerShellScript {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][scriptblock]$ScriptBlock,
        [int]$TimeoutMilliseconds = 12000
    )

    $runspace = $null
    $powerShell = $null
    $asyncResult = $null

    try {
        $initialSessionState = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
        $runspace = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace($initialSessionState)
        $runspace.ApartmentState = [System.Threading.ApartmentState]::STA
        $runspace.ThreadOptions = [System.Management.Automation.Runspaces.PSThreadOptions]::ReuseThread
        $runspace.Open()

        $powerShell = [System.Management.Automation.PowerShell]::Create()
        $powerShell.Runspace = $runspace
        [void]$powerShell.AddScript($ScriptBlock)
        $asyncResult = $powerShell.BeginInvoke()

        if (-not $asyncResult.AsyncWaitHandle.WaitOne($TimeoutMilliseconds)) {
            try { $powerShell.Stop() } catch { }
            throw "STA GUI runspace did not finish within $TimeoutMilliseconds ms."
        }

        $output = $powerShell.EndInvoke($asyncResult)
        if ($powerShell.Streams.Error.Count -gt 0) {
            $message = ($powerShell.Streams.Error | ForEach-Object { $_.ToString() }) -join '; '
            throw $message
        }

        return $output
    }
    finally {
        if ($asyncResult -and $asyncResult.AsyncWaitHandle) {
            $asyncResult.AsyncWaitHandle.Dispose()
        }
        if ($powerShell) { $powerShell.Dispose() }
        if ($runspace) { $runspace.Dispose() }
    }
}

function Invoke-WinFormsGuiTest {
    [CmdletBinding()]
    param()

    Write-SubSection 'WinForms GUI test'

    if (-not $IsWindows) {
        Add-TestSkipped 'WinForms GUI test skipped because this is not Windows.'
        return
    }

    try {
        $output = Invoke-StaPowerShellScript -TimeoutMilliseconds 12000 -ScriptBlock {
            Add-Type -AssemblyName System.Windows.Forms
            Add-Type -AssemblyName System.Drawing

            [System.Windows.Forms.Application]::EnableVisualStyles()

            $form = [System.Windows.Forms.Form]::new()
            $form.Text = 'PowerShellStudio WinForms GUI Test'
            $form.Width = 520
            $form.Height = 220
            $form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
            $form.TopMost = $true

            $label = [System.Windows.Forms.Label]::new()
            $label.AutoSize = $true
            $label.Left = 20
            $label.Top = 20
            $label.Font = [System.Drawing.Font]::new('Segoe UI', 11)
            $label.Text = 'WinForms GUI test window opened successfully. It will auto-close.'

            $progressBar = [System.Windows.Forms.ProgressBar]::new()
            $progressBar.Left = 20
            $progressBar.Top = 70
            $progressBar.Width = 460
            $progressBar.Height = 24
            $progressBar.Minimum = 0
            $progressBar.Maximum = 100
            $progressBar.Value = 0

            $button = [System.Windows.Forms.Button]::new()
            $button.Left = 20
            $button.Top = 115
            $button.Width = 140
            $button.Height = 32
            $button.Text = 'Close Now'
            $button.Add_Click({ $form.Close() })

            $timer = [System.Windows.Forms.Timer]::new()
            $timer.Interval = 100
            $timer.Add_Tick({
                if ($progressBar.Value -lt 100) {
                    $progressBar.Value = [Math]::Min(100, $progressBar.Value + 10)
                }
                else {
                    $timer.Stop()
                    $form.Close()
                }
            })

            [void]$form.Controls.Add($label)
            [void]$form.Controls.Add($progressBar)
            [void]$form.Controls.Add($button)
            $form.Add_Shown({ $timer.Start() })

            [System.Windows.Forms.Application]::Run($form)
            $timer.Dispose()
            $form.Dispose()
            'PASS'
        }

        Test-Condition -Condition ($output -contains 'PASS') -PassMessage 'WinForms GUI opened and auto-closed successfully.' -FailMessage 'WinForms GUI did not return PASS.'
    }
    catch {
        Add-TestFail "WinForms GUI test failed: $($_.Exception.Message)"
    }
}

function Invoke-WpfGuiTest {
    [CmdletBinding()]
    param()

    Write-SubSection 'WPF GUI test'

    if (-not $IsWindows) {
        Add-TestSkipped 'WPF GUI test skipped because this is not Windows.'
        return
    }

    try {
        $output = Invoke-StaPowerShellScript -TimeoutMilliseconds 12000 -ScriptBlock {
            Add-Type -AssemblyName PresentationFramework
            Add-Type -AssemblyName PresentationCore
            Add-Type -AssemblyName WindowsBase

            $window = [System.Windows.Window]::new()
            $window.Title = 'PowerShellStudio WPF GUI Test'
            $window.Width = 540
            $window.Height = 230
            $window.WindowStartupLocation = [System.Windows.WindowStartupLocation]::CenterScreen
            $window.Topmost = $true

            $stackPanel = [System.Windows.Controls.StackPanel]::new()
            $stackPanel.Margin = [System.Windows.Thickness]::new(20)

            $textBlock = [System.Windows.Controls.TextBlock]::new()
            $textBlock.Text = 'WPF GUI test window opened successfully. It will auto-close.'
            $textBlock.FontSize = 16
            $textBlock.Margin = [System.Windows.Thickness]::new(0, 0, 0, 18)

            $progressBar = [System.Windows.Controls.ProgressBar]::new()
            $progressBar.Minimum = 0
            $progressBar.Maximum = 100
            $progressBar.Height = 24
            $progressBar.Value = 0
            $progressBar.Margin = [System.Windows.Thickness]::new(0, 0, 0, 18)

            $button = [System.Windows.Controls.Button]::new()
            $button.Content = 'Close Now'
            $button.Width = 140
            $button.Height = 34
            $button.HorizontalAlignment = [System.Windows.HorizontalAlignment]::Left
            $button.Add_Click({ $window.Close() })

            [void]$stackPanel.Children.Add($textBlock)
            [void]$stackPanel.Children.Add($progressBar)
            [void]$stackPanel.Children.Add($button)
            $window.Content = $stackPanel

            $timer = [System.Windows.Threading.DispatcherTimer]::new()
            $timer.Interval = [System.TimeSpan]::FromMilliseconds(100)
            $timer.Add_Tick({
                if ($progressBar.Value -lt 100) {
                    $progressBar.Value = [Math]::Min(100, $progressBar.Value + 10)
                }
                else {
                    $timer.Stop()
                    $window.Close()
                }
            })
            $window.Add_ContentRendered({ $timer.Start() })

            $null = $window.ShowDialog()
            'PASS'
        }

        Test-Condition -Condition ($output -contains 'PASS') -PassMessage 'WPF GUI opened and auto-closed successfully.' -FailMessage 'WPF GUI did not return PASS.'
    }
    catch {
        Add-TestFail "WPF GUI test failed: $($_.Exception.Message)"
    }
}

class DiagnosticPerson {
    [string]$Name
    [int]$Age

    DiagnosticPerson([string]$name, [int]$age) {
        $this.Name = $name
        $this.Age = $age
    }

    [string] ToDisplayString() {
        return "$($this.Name) is $($this.Age) years old"
    }
}

class DiagnosticWidget {
    [string]$Name

    DiagnosticWidget([string]$name) { $this.Name = $name }
    [void] Start() { Write-Host "DiagnosticWidget '$($this.Name)' started" -ForegroundColor Green }
    [string] GetStatus() { return "Widget '$($this.Name)' is running" }
}

Write-Section 'PowerShellStudio Full Editor Diagnostic Test v3 Started'
Write-Host "Started:                 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')" -ForegroundColor Cyan
Write-Host "PowerShell Version:      $($PSVersionTable.PSVersion)" -ForegroundColor Cyan
Write-Host "PowerShell Edition:      $($PSVersionTable.PSEdition)" -ForegroundColor Cyan
Write-Host "Process ID:              $PID" -ForegroundColor Cyan
Write-Host "Current Location:        $(Get-Location)" -ForegroundColor Cyan
Write-Host "Script Path:             $PSCommandPath" -ForegroundColor Cyan
Write-Host "Host Name:               $($Host.Name)" -ForegroundColor Cyan
Write-Host "IsWindows:               $IsWindows" -ForegroundColor Cyan

Test-Condition -Condition ($PSVersionTable.PSVersion.Major -ge 7) -PassMessage 'PowerShell 7+ requirement satisfied.' -FailMessage 'PowerShell 7+ requirement not satisfied.'

Write-SubSection 'Basic output, streams, and color test'
Write-Output 'Success output stream message'
Write-Host 'Host stream message' -ForegroundColor Green
Write-Verbose 'Verbose stream message'
Write-Debug 'Debug stream message'
Write-Information 'Information stream message'
Write-Warning 'Warning stream message'
Write-Error 'Intentional non-terminating error. This should be visible, and the script should continue.'
foreach ($color in [enum]::GetNames([System.ConsoleColor])) {
    Write-Host "Foreground color sample: $color" -ForegroundColor $color
}
Add-TestPass 'Major streams and colors emitted.'

Write-SubSection 'Progress and streaming output test'
if ($SkipProgress) {
    Add-TestSkipped 'Progress test skipped.'
}
else {
    for ($i = 1; $i -le 100; $i += 10) {
        Write-Progress -Id 1 -Activity 'PowerShellStudio progress rendering test' -Status "Processing $i percent" -PercentComplete $i
        if ($i -ge 30 -and $i -le 70) {
            Write-Progress -Id 2 -ParentId 1 -Activity 'Nested progress test' -Status "Nested $i percent" -PercentComplete $i
        }
        Start-Sleep -Milliseconds $ProgressDelayMilliseconds
    }
    Write-Progress -Id 2 -Activity 'Nested progress test' -Completed
    Write-Progress -Id 1 -Activity 'PowerShellStudio progress rendering test' -Completed
    Add-TestPass 'Progress bars completed.'
}

1..5 | ForEach-Object {
    Write-Host "Streaming output tick $_ of 5 at $(Get-Date -Format 'HH:mm:ss.fff')"
    Start-Sleep -Milliseconds 250
}
Add-TestPass 'Streaming output completed.'

Write-SubSection 'PowerShell 7-only feature tests'
$nullValue = $null
$fallback = $nullValue ?? 'fallback-value'
Test-Condition -Condition ($fallback -eq 'fallback-value') -PassMessage 'Null-coalescing operator ?? works.' -FailMessage 'Null-coalescing operator ?? failed.'
$assignedValue = $null
$assignedValue ??= 'assigned-value'
Test-Condition -Condition ($assignedValue -eq 'assigned-value') -PassMessage 'Null-coalescing assignment ??= works.' -FailMessage 'Null-coalescing assignment ??= failed.'
$ternaryResult = $true ? 'yes' : 'no'
Test-Condition -Condition ($ternaryResult -eq 'yes') -PassMessage 'Ternary operator works.' -FailMessage 'Ternary operator failed.'
$parallelResult = 1..5 | ForEach-Object -Parallel { $_ * 2 } -ThrottleLimit 2
Test-Condition -Condition ((($parallelResult | Measure-Object -Sum).Sum) -eq 30) -PassMessage 'ForEach-Object -Parallel works.' -FailMessage 'ForEach-Object -Parallel failed.'
Write-Host "$($PSStyle.Foreground.BrightGreen)PSStyle bright green ANSI test$($PSStyle.Reset)"
Add-TestPass 'PSStyle output emitted.'

Write-SubSection 'Functions, classes, objects, and formatting'
function Add-DiagnosticNumbers {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$A, [Parameter(Mandatory)][int]$B)
    Write-Verbose "Adding $A and $B"
    return ($A + $B)
}

$sum = Add-DiagnosticNumbers -A 5 -B 10
Test-Condition -Condition ($sum -eq 15) -PassMessage 'Function returned expected value.' -FailMessage "Function returned unexpected value $sum."
$person = [DiagnosticPerson]::new('Ron', 50)
Write-Host $person.ToDisplayString()
$widget = [DiagnosticWidget]::new('Primary')
$widget.Start()
Test-Condition -Condition (($widget.GetStatus()) -match 'running') -PassMessage 'Class method invocation worked.' -FailMessage 'Class method invocation failed.'

$objects = 1..5 | ForEach-Object { [pscustomobject]@{ Number = $_; Square = $_ * $_; Cube = $_ * $_ * $_ } }
$objects | Format-Table -AutoSize
Test-Condition -Condition (($objects | Measure-Object).Count -eq 5) -PassMessage 'Object pipeline produced expected count.' -FailMessage 'Object pipeline count was wrong.'

Write-SubSection 'Path, JSON, XML, and child script test'
$validPaths = @(
    'C:\Users\rbarn\source\repos\PowerShellStudio',
    'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe',
    'C:\Program Files\PowerShell\7\pwsh.exe'
)
$validPaths | ForEach-Object { Write-Host "Path sample: $_" }
$json = [pscustomobject]@{ App='PowerShellStudio'; Test='v3'; Paths=$validPaths; Time=(Get-Date).ToString('o') } | ConvertTo-Json -Depth 4
$roundTrip = $json | ConvertFrom-Json
Test-Condition -Condition ($roundTrip.App -eq 'PowerShellStudio') -PassMessage 'JSON round-trip completed.' -FailMessage 'JSON round-trip failed.'
[xml]$xml = '<PowerShellStudio><Test Name="XML" Enabled="true" /></PowerShellStudio>'
Test-Condition -Condition ($xml.PowerShellStudio.Test.Name -eq 'XML') -PassMessage 'XML parsing completed.' -FailMessage 'XML parsing failed.'

$tempRoot = Join-Path $env:TEMP "PowerShellStudio Diagnostic Test O'Brien"
$childScriptPath = Join-Path $tempRoot 'Child Script With Spaces.ps1'
try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    @'
Write-Host "Child script executed successfully." -ForegroundColor Green
Write-Host "Child script path: $PSCommandPath"
$childResult = [pscustomobject]@{
    ChildScript = $true
    Location = (Get-Location).Path
    Time = Get-Date
}
Write-Host "Child object output using Out-String for stable ordering:" -ForegroundColor Yellow
$childResult | Format-List | Out-String | Write-Host
Write-Host "Child object output completed." -ForegroundColor Yellow
'@ | Set-Content -LiteralPath $childScriptPath -Encoding UTF8
    & $childScriptPath
    Add-TestPass 'Child script path with spaces/apostrophe executed.'
}
catch {
    Add-TestFail "Child script test failed: $($_.Exception.Message)"
}
finally {
    if ($KeepTempFiles) { Add-TestWarning "Keeping temp files at: $tempRoot" }
    else { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-SubSection 'Jobs and native command tests'
$job = Start-Job -ScriptBlock { Start-Sleep -Milliseconds 250; 'background-job-result' }
$completed = Wait-Job -Job $job -Timeout 5
$jobResult = Receive-Job -Job $job
Remove-Job -Job $job -Force
Test-Condition -Condition ($null -ne $completed -and $jobResult -eq 'background-job-result') -PassMessage 'Start-Job works.' -FailMessage 'Start-Job failed.'

if (Get-Command Start-ThreadJob -ErrorAction SilentlyContinue) {
    $threadJob = Start-ThreadJob -ScriptBlock { Start-Sleep -Milliseconds 250; 'thread-job-result' }
    $completedThreadJob = Wait-Job -Job $threadJob -Timeout 5
    $threadResult = Receive-Job -Job $threadJob
    Remove-Job -Job $threadJob -Force
    Test-Condition -Condition ($null -ne $completedThreadJob -and $threadResult -eq 'thread-job-result') -PassMessage 'Start-ThreadJob works.' -FailMessage 'Start-ThreadJob failed.'
}
else {
    Add-TestSkipped 'Start-ThreadJob is unavailable.'
}

$nativeOutput = & cmd.exe /c "echo native-stdout-message && echo native-stderr-message 1>&2 && exit /b 7" 2>&1
$nativeText = $nativeOutput | Out-String
Test-Condition -Condition ($nativeText -match 'native-stdout-message') -PassMessage 'Native stdout captured/displayed.' -FailMessage 'Native stdout missing.'
Test-Condition -Condition ($nativeText -match 'native-stderr-message') -PassMessage 'Native stderr captured/displayed.' -FailMessage 'Native stderr missing.'
Test-Condition -Condition ($LASTEXITCODE -eq 7) -PassMessage 'Native exit code preserved.' -FailMessage "Native exit code was $LASTEXITCODE."

Write-SubSection 'Runtime executable checks'
Test-Condition -Condition (Test-Path -LiteralPath 'C:\Program Files\PowerShell\7\pwsh.exe') -PassMessage 'PowerShell 7 executable exists.' -FailMessage 'PowerShell 7 executable missing at expected path.'
Test-Condition -Condition (Test-Path -LiteralPath 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe') -PassMessage 'Windows PowerShell 5.1 executable exists.' -FailMessage 'Windows PowerShell 5.1 executable missing at expected path.'

if ($SkipGui) {
    Write-SubSection 'GUI tests'
    Add-TestSkipped 'GUI tests skipped.'
}
else {
    Invoke-WinFormsGuiTest
    Invoke-WpfGuiTest
}

Write-SubSection 'Official parser syntax tests'
$validSamples = @(
    'cd C:\Users\rbarn\source\repos\PowerShellStudio',
    'Set-Location C:\Users',
    'Push-Location C:\Users',
    'Get-ChildItem -Path C:\Users',
    '$x = $null; $x ?? "fallback"',
    '$x = $null; $x ??= "assigned"',
    '$result = $true ? "yes" : "no"',
    '1..3 | ForEach-Object -Parallel { $_ * 2 }'
)
foreach ($sample in $validSamples) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseInput($sample, [ref]$tokens, [ref]$errors) | Out-Null
    Test-Condition -Condition ($errors.Count -eq 0) -PassMessage "Parser accepted valid syntax: $sample" -FailMessage "Parser rejected valid syntax: $sample"
}
$invalidSamples = @('if ($true {', 'Write-Host "missing end quote')
foreach ($sample in $invalidSamples) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseInput($sample, [ref]$tokens, [ref]$errors) | Out-Null
    Test-Condition -Condition ($errors.Count -gt 0) -PassMessage "Parser detected invalid syntax: $sample" -FailMessage "Parser failed to detect invalid syntax: $sample"
}

Write-SubSection 'Prompt recovery final test'
Write-Host 'The script is about to finish.' -ForegroundColor Yellow
Write-Host 'After the final marker, the console should return to a normal prompt automatically.' -ForegroundColor Yellow
Write-Host 'Expected normal prompt example: PS Z:\>' -ForegroundColor Yellow
Write-Host 'Bad continuation prompt example: >>' -ForegroundColor Yellow
Write-Host 'You should NOT have to press Enter to get the prompt back.' -ForegroundColor Yellow

Write-Section 'PowerShellStudio Full Editor Diagnostic Test v3 Finished'
Write-Host "Finished:                $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')" -ForegroundColor Cyan
Write-Host "Passed checks:           $script:PassedCount" -ForegroundColor Green
Write-Host "Warnings:                $script:WarningCount" -ForegroundColor Yellow
Write-Host "Skipped checks:          $script:SkippedCount" -ForegroundColor DarkYellow
Write-Host "Failed checks:           $script:FailedCount" -ForegroundColor Red
if ($script:FailedCount -eq 0) { Write-Host 'Overall result:          PASS' -ForegroundColor Green } else { Write-Host 'Overall result:          REVIEW FAILURES' -ForegroundColor Red }
Write-Host ''
Write-Host 'FINAL_MARKER_EDITOR_CONSOLE_GUI_PS7_DIAGNOSTIC_TEST_V3_COMPLETE' -ForegroundColor Green
Write-Host ''
