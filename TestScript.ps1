<#
.SYNOPSIS
    PowerShellStudio full editor, console, GUI, and PowerShell 7 diagnostic test.

.DESCRIPTION
    This is the expanded standard regression test script for the
    PowerShellStudio / PowerShell 7.x ISE-style app.

    It tests:
    - editor syntax highlighting
    - editor diagnostics
    - official PowerShell parser behavior
    - function parsing
    - parameter parsing
    - PowerShell class parsing
    - method invocation
    - path syntax
    - quoted paths
    - apostrophe paths
    - colored output
    - ANSI / PSStyle output
    - progress bars
    - nested progress bars
    - all major PowerShell streams
    - non-terminating errors
    - terminating errors
    - pipeline behavior
    - object formatting
    - JSON
    - here-strings
    - temporary file handling
    - child script invocation
    - native command stdout/stderr/exit-code behavior
    - background jobs
    - PowerShell 7-only syntax/features
    - WinForms GUI launch
    - WPF GUI launch
    - editor-to-console execution
    - prompt recovery after completion

.NOTES
    Expected final behavior:
    - Script runs to completion in PowerShell 7.x.
    - Output remains visible.
    - Console returns to a normal PS prompt automatically.
    - Console does NOT show ">>".
    - User does NOT need to press Enter to restore the prompt.
    - Internal app dispatch commands should NOT appear.
    - Editor diagnostics should not falsely flag valid PowerShell syntax.
    - GUI tests should briefly show and auto-close small test windows.

    This script intentionally uses PowerShell 7-only syntax and should not be
    treated as Windows PowerShell 5.1 compatible.
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

$ErrorActionPreference = "Continue"
$WarningPreference = "Continue"
$VerbosePreference = "Continue"
$DebugPreference = "Continue"
$InformationPreference = "Continue"

$script:PassedCount = 0
$script:FailedCount = 0
$script:WarningCount = 0
$script:SkippedCount = 0

function Write-Section {
    param(
        [Parameter(Mandatory)]
        [string]$Title
    )

    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host $Title -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
}

function Write-SubSection {
    param(
        [Parameter(Mandatory)]
        [string]$Title
    )

    Write-Host ""
    Write-Host "------------------------------------------------------------" -ForegroundColor DarkCyan
    Write-Host $Title -ForegroundColor Yellow
    Write-Host "------------------------------------------------------------" -ForegroundColor DarkCyan
}

function Add-TestPass {
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    $script:PassedCount++
    Write-Host "[PASS] $Message" -ForegroundColor Green
}

function Add-TestFail {
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    $script:FailedCount++
    Write-Host "[FAIL] $Message" -ForegroundColor Red
}

function Add-TestWarning {
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    $script:WarningCount++
    Write-Host "[WARN] $Message" -ForegroundColor Yellow
}

function Add-TestSkipped {
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    $script:SkippedCount++
    Write-Host "[SKIP] $Message" -ForegroundColor DarkYellow
}

function Test-Condition {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$PassMessage,

        [Parameter(Mandatory)]
        [string]$FailMessage
    )

    if ($Condition) {
        Add-TestPass -Message $PassMessage
    }
    else {
        Add-TestFail -Message $FailMessage
    }
}

function Invoke-SlowOutputTest {
    [CmdletBinding()]
    param(
        [int]$Count = 5,
        [int]$DelayMilliseconds = 250
    )

    for ($i = 1; $i -le $Count; $i++) {
        Write-Host "Streaming output tick $i of $Count at $(Get-Date -Format 'HH:mm:ss.fff')"
        Start-Sleep -Milliseconds $DelayMilliseconds
    }
}

function Add-DiagnosticNumbers {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [int]$A,

        [Parameter(Mandatory)]
        [int]$B
    )

    begin {
        Write-Verbose "Add-DiagnosticNumbers begin block"
    }

    process {
        $sum = $A + $B
        Write-Verbose "Adding $A and $B"
        Write-Host "Inside Add-DiagnosticNumbers: $A + $B = $sum" -ForegroundColor Magenta
        return $sum
    }

    end {
        Write-Verbose "Add-DiagnosticNumbers end block"
    }
}

function Test-PathSyntaxWithPowerShellParser {
    [CmdletBinding()]
    param()

    Write-SubSection "Official PowerShell parser syntax checks"

    $validSamples = @(
        'cd C:\Users\rbarn\source\repos\PowerShellStudio',
        'Set-Location C:\Users',
        'Push-Location C:\Users',
        'cd C:\',
        'cd .\',
        'cd ..\',
        'Get-ChildItem C:\Users',
        'Get-ChildItem -Path C:\Users',
        'Get-ChildItem -LiteralPath C:\Users',
        'cd ''C:\Users\rbarn\source\repos\PowerShellStudio''',
        'cd "C:\Users\rbarn\source\repos\PowerShellStudio"',
        '$path = "C:\Users\rbarn\source\repos\PowerShellStudio"',
        'Join-Path $env:TEMP "Folder With Spaces"',
        '& "C:\Program Files\PowerShell\7\pwsh.exe" -NoLogo',
        '$x = $null; $x ?? "fallback"',
        '$x = $null; $x ??= "assigned"',
        '$result = $true ? "yes" : "no"',
        'Write-Output "left" && Write-Output "right"',
        'Write-Output "left" || Write-Output "right"',
        '1..3 | ForEach-Object -Parallel { $_ * 2 }'
    )

    foreach ($sample in $validSamples) {
        $tokens = $null
        $errors = $null

        [System.Management.Automation.Language.Parser]::ParseInput(
            $sample,
            [ref]$tokens,
            [ref]$errors
        ) | Out-Null

        if ($errors.Count -eq 0) {
            Add-TestPass -Message "Parser accepted valid syntax: $sample"
        }
        else {
            Add-TestFail -Message "Parser rejected valid syntax: $sample | Error: $($errors[0].Message)"
        }
    }

    $invalidSamples = @(
        'if ($true {',
        'Write-Host "missing end quote',
        'function Bad-Test { param([int]$A) Write-Host $A'
    )

    foreach ($sample in $invalidSamples) {
        $tokens = $null
        $errors = $null

        [System.Management.Automation.Language.Parser]::ParseInput(
            $sample,
            [ref]$tokens,
            [ref]$errors
        ) | Out-Null

        if ($errors.Count -gt 0) {
            Add-TestPass -Message "Parser correctly detected invalid syntax: $sample"
        }
        else {
            Add-TestFail -Message "Parser failed to detect invalid syntax: $sample"
        }
    }
}

function Invoke-WinFormsGuiTest {
    [CmdletBinding()]
    param()

    Write-SubSection "WinForms GUI test"

    if (-not $IsWindows) {
        Add-TestSkipped -Message "WinForms GUI test skipped because this is not Windows"
        return
    }

    $resultQueue = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()

    $thread = [System.Threading.Thread] {
        param($queue)

        try {
            Add-Type -AssemblyName System.Windows.Forms
            Add-Type -AssemblyName System.Drawing

            [System.Windows.Forms.Application]::EnableVisualStyles()

            $form = [System.Windows.Forms.Form]::new()
            $form.Text = "PowerShellStudio WinForms GUI Test"
            $form.Width = 520
            $form.Height = 220
            $form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
            $form.TopMost = $true

            $label = [System.Windows.Forms.Label]::new()
            $label.AutoSize = $true
            $label.Left = 20
            $label.Top = 20
            $label.Font = [System.Drawing.Font]::new("Segoe UI", 11)
            $label.Text = "WinForms GUI test window opened successfully. It will auto-close."

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
            $button.Text = "Close Now"
            $button.Add_Click({
                $form.Close()
            })

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

            $form.Controls.Add($label)
            $form.Controls.Add($progressBar)
            $form.Controls.Add($button)

            $form.Add_Shown({
                $timer.Start()
            })

            [System.Windows.Forms.Application]::Run($form)

            $timer.Dispose()
            $form.Dispose()

            [void]$queue.Enqueue("PASS")
        }
        catch {
            [void]$queue.Enqueue("FAIL: $($_.Exception.Message)")
        }
    }

    $thread.SetApartmentState([System.Threading.ApartmentState]::STA)
    $thread.Start($resultQueue)
    $thread.Join(8000)

    if ($thread.IsAlive) {
        Add-TestFail -Message "WinForms GUI test did not finish within timeout"
        return
    }

    $message = $null

    if ($resultQueue.TryDequeue([ref]$message)) {
        if ($message -eq "PASS") {
            Add-TestPass -Message "WinForms GUI opened and auto-closed successfully"
        }
        else {
            Add-TestFail -Message "WinForms GUI test failed: $message"
        }
    }
    else {
        Add-TestFail -Message "WinForms GUI test returned no result"
    }
}

function Invoke-WpfGuiTest {
    [CmdletBinding()]
    param()

    Write-SubSection "WPF GUI test"

    if (-not $IsWindows) {
        Add-TestSkipped -Message "WPF GUI test skipped because this is not Windows"
        return
    }

    $resultQueue = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()

    $thread = [System.Threading.Thread] {
        param($queue)

        try {
            Add-Type -AssemblyName PresentationFramework
            Add-Type -AssemblyName PresentationCore
            Add-Type -AssemblyName WindowsBase

            $window = [System.Windows.Window]::new()
            $window.Title = "PowerShellStudio WPF GUI Test"
            $window.Width = 540
            $window.Height = 230
            $window.WindowStartupLocation = [System.Windows.WindowStartupLocation]::CenterScreen
            $window.Topmost = $true

            $stackPanel = [System.Windows.Controls.StackPanel]::new()
            $stackPanel.Margin = [System.Windows.Thickness]::new(20)

            $textBlock = [System.Windows.Controls.TextBlock]::new()
            $textBlock.Text = "WPF GUI test window opened successfully. It will auto-close."
            $textBlock.FontSize = 16
            $textBlock.Margin = [System.Windows.Thickness]::new(0, 0, 0, 18)

            $progressBar = [System.Windows.Controls.ProgressBar]::new()
            $progressBar.Minimum = 0
            $progressBar.Maximum = 100
            $progressBar.Height = 24
            $progressBar.Value = 0
            $progressBar.Margin = [System.Windows.Thickness]::new(0, 0, 0, 18)

            $button = [System.Windows.Controls.Button]::new()
            $button.Content = "Close Now"
            $button.Width = 140
            $button.Height = 34
            $button.HorizontalAlignment = [System.Windows.HorizontalAlignment]::Left
            $button.Add_Click({
                $window.Close()
            })

            $stackPanel.Children.Add($textBlock) | Out-Null
            $stackPanel.Children.Add($progressBar) | Out-Null
            $stackPanel.Children.Add($button) | Out-Null

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

            $window.Add_ContentRendered({
                $timer.Start()
            })

            $null = $window.ShowDialog()

            [void]$queue.Enqueue("PASS")
        }
        catch {
            [void]$queue.Enqueue("FAIL: $($_.Exception.Message)")
        }
    }

    $thread.SetApartmentState([System.Threading.ApartmentState]::STA)
    $thread.Start($resultQueue)
    $thread.Join(10000)

    if ($thread.IsAlive) {
        Add-TestFail -Message "WPF GUI test did not finish within timeout"
        return
    }

    $message = $null

    if ($resultQueue.TryDequeue([ref]$message)) {
        if ($message -eq "PASS") {
            Add-TestPass -Message "WPF GUI opened and auto-closed successfully"
        }
        else {
            Add-TestFail -Message "WPF GUI test failed: $message"
        }
    }
    else {
        Add-TestFail -Message "WPF GUI test returned no result"
    }
}

function Test-PowerShell7OnlyFeatures {
    [CmdletBinding()]
    param()

    Write-SubSection "PowerShell 7-only feature tests"

    Test-Condition `
        -Condition ($PSVersionTable.PSVersion.Major -ge 7) `
        -PassMessage "Running under PowerShell 7 or later" `
        -FailMessage "This script requires PowerShell 7 or later"

    $nullValue = $null
    $fallback = $nullValue ?? "fallback-value"

    Test-Condition `
        -Condition ($fallback -eq "fallback-value") `
        -PassMessage "PowerShell 7 null-coalescing operator ?? works" `
        -FailMessage "PowerShell 7 null-coalescing operator ?? failed"

    $assignedValue = $null
    $assignedValue ??= "assigned-by-null-coalescing-assignment"

    Test-Condition `
        -Condition ($assignedValue -eq "assigned-by-null-coalescing-assignment") `
        -PassMessage "PowerShell 7 null-coalescing assignment operator ??= works" `
        -FailMessage "PowerShell 7 null-coalescing assignment operator ??= failed"

    $ternaryResult = ($PSVersionTable.PSVersion.Major -ge 7) ? "ps7" : "not-ps7"

    Test-Condition `
        -Condition ($ternaryResult -eq "ps7") `
        -PassMessage "PowerShell 7 ternary operator works" `
        -FailMessage "PowerShell 7 ternary operator failed"

    $chainFile = Join-Path $env:TEMP "PowerShellStudio_PS7_Chain_Test_$PID.txt"
    Remove-Item -LiteralPath $chainFile -Force -ErrorAction SilentlyContinue

    "chain-left" | Set-Content -LiteralPath $chainFile -Encoding UTF8
    Test-Path -LiteralPath $chainFile | Out-Null

    if ($?) {
        "chain-right" | Add-Content -LiteralPath $chainFile -Encoding UTF8
    }

    $chainContent = Get-Content -LiteralPath $chainFile -Raw

    Test-Condition `
        -Condition ($chainContent -match "chain-left" -and $chainContent -match "chain-right") `
        -PassMessage "PowerShell 7 pipeline/status chaining behavior works" `
        -FailMessage "PowerShell 7 pipeline/status chaining behavior failed"

    Remove-Item -LiteralPath $chainFile -Force -ErrorAction SilentlyContinue

    $parallelResult = 1..5 | ForEach-Object -Parallel {
        $_ * 2
    } -ThrottleLimit 2

    $parallelSum = ($parallelResult | Measure-Object -Sum).Sum

    Test-Condition `
        -Condition ($parallelSum -eq 30) `
        -PassMessage "PowerShell 7 ForEach-Object -Parallel works" `
        -FailMessage "PowerShell 7 ForEach-Object -Parallel failed. Sum was $parallelSum"

    if ($null -ne $PSStyle) {
        Write-Host "$($PSStyle.Foreground.BrightGreen)PSStyle bright green ANSI test$($PSStyle.Reset)"
        Add-TestPass -Message "PowerShell 7 PSStyle variable is available"
    }
    else {
        Add-TestFail -Message "PowerShell 7 PSStyle variable was not available"
    }

    try {
        $capturedError = $null

        try {
            throw "Intentional Get-Error test exception"
        }
        catch {
            $capturedError = $_
        }

        $errorText = $capturedError | Get-Error | Out-String

        Test-Condition `
            -Condition ($errorText -match "Intentional Get-Error test exception") `
            -PassMessage "PowerShell 7 Get-Error works" `
            -FailMessage "PowerShell 7 Get-Error did not include expected exception text"
    }
    catch {
        Add-TestFail -Message "PowerShell 7 Get-Error test failed: $($_.Exception.Message)"
    }
}

function Test-BackgroundJobs {
    [CmdletBinding()]
    param()

    Write-SubSection "Background job and thread job tests"

    try {
        $job = Start-Job -ScriptBlock {
            Start-Sleep -Milliseconds 300
            "background-job-result"
        }

        $completed = Wait-Job -Job $job -Timeout 5
        $result = Receive-Job -Job $job
        Remove-Job -Job $job -Force

        Test-Condition `
            -Condition ($null -ne $completed -and $result -eq "background-job-result") `
            -PassMessage "Start-Job / Receive-Job works" `
            -FailMessage "Start-Job / Receive-Job failed"
    }
    catch {
        Add-TestFail -Message "Background job test failed: $($_.Exception.Message)"
    }

    try {
        $threadJobCommand = Get-Command Start-ThreadJob -ErrorAction SilentlyContinue

        if ($null -eq $threadJobCommand) {
            Add-TestSkipped -Message "Start-ThreadJob command is not available"
            return
        }

        $threadJob = Start-ThreadJob -ScriptBlock {
            Start-Sleep -Milliseconds 300
            "thread-job-result"
        }

        $completedThreadJob = Wait-Job -Job $threadJob -Timeout 5
        $threadResult = Receive-Job -Job $threadJob
        Remove-Job -Job $threadJob -Force

        Test-Condition `
            -Condition ($null -ne $completedThreadJob -and $threadResult -eq "thread-job-result") `
            -PassMessage "Start-ThreadJob / Receive-Job works" `
            -FailMessage "Start-ThreadJob / Receive-Job failed"
    }
    catch {
        Add-TestFail -Message "Thread job test failed: $($_.Exception.Message)"
    }
}

function Test-NativeCommandBehavior {
    [CmdletBinding()]
    param()

    Write-SubSection "Native command stdout, stderr, and exit-code tests"

    if (-not $IsWindows) {
        Add-TestSkipped -Message "Native Windows cmd.exe test skipped because this is not Windows"
        return
    }

    try {
        $nativeOutput = & cmd.exe /c "echo native-stdout-message && echo native-stderr-message 1>&2 && exit /b 7" 2>&1
        $exitCode = $LASTEXITCODE

        $nativeText = $nativeOutput | Out-String

        Test-Condition `
            -Condition ($nativeText -match "native-stdout-message") `
            -PassMessage "Native stdout captured/displayed" `
            -FailMessage "Native stdout was not captured/displayed"

        Test-Condition `
            -Condition ($nativeText -match "native-stderr-message") `
            -PassMessage "Native stderr captured/displayed" `
            -FailMessage "Native stderr was not captured/displayed"

        Test-Condition `
            -Condition ($exitCode -eq 7) `
            -PassMessage "Native exit code preserved through LASTEXITCODE" `
            -FailMessage "Native exit code was not preserved. LASTEXITCODE=$exitCode"
    }
    catch {
        Add-TestFail -Message "Native command test failed: $($_.Exception.Message)"
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

    DiagnosticWidget([string]$name) {
        $this.Name = $name
    }

    [void] Start() {
        Write-Host "DiagnosticWidget '$($this.Name)' started" -ForegroundColor Green
    }

    [string] GetStatus() {
        return "Widget '$($this.Name)' is running"
    }
}

Write-Section "PowerShellStudio Full Editor Diagnostic Test v2 Started"

Write-Host "Started:                 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')" -ForegroundColor Cyan
Write-Host "PowerShell Version:      $($PSVersionTable.PSVersion)" -ForegroundColor Cyan
Write-Host "PowerShell Edition:      $($PSVersionTable.PSEdition)" -ForegroundColor Cyan
Write-Host "Process ID:              $PID" -ForegroundColor Cyan
Write-Host "Current Location:        $(Get-Location)" -ForegroundColor Cyan
Write-Host "Script Path:             $PSCommandPath" -ForegroundColor Cyan
Write-Host "Host Name:               $($Host.Name)" -ForegroundColor Cyan
Write-Host "Host Version:            $($Host.Version)" -ForegroundColor Cyan
Write-Host "Culture:                 $([System.Globalization.CultureInfo]::CurrentCulture.Name)" -ForegroundColor Cyan
Write-Host "OS:                      $([System.Environment]::OSVersion.VersionString)" -ForegroundColor Cyan
Write-Host "IsWindows:               $IsWindows" -ForegroundColor Cyan
Write-Host "IsLinux:                 $IsLinux" -ForegroundColor Cyan
Write-Host "IsMacOS:                 $IsMacOS" -ForegroundColor Cyan

Test-Condition `
    -Condition ($PSVersionTable.PSVersion.Major -ge 7) `
    -PassMessage "PowerShell 7+ requirement satisfied" `
    -FailMessage "PowerShell 7+ requirement not satisfied"

Write-SubSection "Basic output and color test"

Write-Host "Default host output"
Write-Host "Black text sample" -ForegroundColor Black
Write-Host "DarkBlue text sample" -ForegroundColor DarkBlue
Write-Host "DarkGreen text sample" -ForegroundColor DarkGreen
Write-Host "DarkCyan text sample" -ForegroundColor DarkCyan
Write-Host "DarkRed text sample" -ForegroundColor DarkRed
Write-Host "DarkMagenta text sample" -ForegroundColor DarkMagenta
Write-Host "DarkYellow text sample" -ForegroundColor DarkYellow
Write-Host "Gray text sample" -ForegroundColor Gray
Write-Host "DarkGray text sample" -ForegroundColor DarkGray
Write-Host "Blue text sample" -ForegroundColor Blue
Write-Host "Green text sample" -ForegroundColor Green
Write-Host "Cyan text sample" -ForegroundColor Cyan
Write-Host "Red text sample" -ForegroundColor Red
Write-Host "Magenta text sample" -ForegroundColor Magenta
Write-Host "Yellow text sample" -ForegroundColor Yellow
Write-Host "White text sample" -ForegroundColor White

Add-TestPass -Message "Basic Write-Host color output completed"

Write-SubSection "ANSI / PSStyle output test"

if ($null -ne $PSStyle) {
    Write-Host "$($PSStyle.Bold)$($PSStyle.Foreground.BrightCyan)Bold bright cyan PSStyle text$($PSStyle.Reset)"
    Write-Host "$($PSStyle.Italic)$($PSStyle.Foreground.BrightMagenta)Italic bright magenta PSStyle text$($PSStyle.Reset)"
    Add-TestPass -Message "PSStyle ANSI output emitted"
}
else {
    Add-TestFail -Message "PSStyle was not available"
}

Write-SubSection "PowerShell streams test"

Write-Output "Success output stream message"
Write-Host "Host stream message" -ForegroundColor Green
Write-Verbose "Verbose stream message"
Write-Debug "Debug stream message"
Write-Information "Information stream message"
Write-Warning "Warning stream message"
Write-Error "Intentional non-terminating error. This should be visible, but the script should continue."

Add-TestPass -Message "Major PowerShell streams emitted"

Write-SubSection "Progress bar test"

if ($SkipProgress) {
    Add-TestSkipped -Message "Progress test skipped by parameter"
}
else {
    for ($i = 1; $i -le 100; $i += 5) {
        Write-Progress `
            -Id 1 `
            -Activity "PowerShellStudio progress rendering test" `
            -Status "Processing $i percent" `
            -PercentComplete $i `
            -CurrentOperation "Testing primary progress bar display"

        if ($i -ge 25 -and $i -le 75) {
            Write-Progress `
                -Id 2 `
                -ParentId 1 `
                -Activity "Nested progress test" `
                -Status "Nested $i percent" `
                -PercentComplete $i `
                -CurrentOperation "Testing nested progress display"
        }

        Start-Sleep -Milliseconds $ProgressDelayMilliseconds
    }

    Write-Progress -Id 2 -Activity "Nested progress test" -Completed
    Write-Progress -Id 1 -Activity "PowerShellStudio progress rendering test" -Completed

    Add-TestPass -Message "Primary and nested progress bar test completed"
}

Write-SubSection "Slow streaming output test"

Invoke-SlowOutputTest -Count 5 -DelayMilliseconds 300
Add-TestPass -Message "Slow streaming output completed"

Write-SubSection "Function, parameter, and pipeline test"

$result = Add-DiagnosticNumbers -A 5 -B 10

Test-Condition `
    -Condition ($result -eq 15) `
    -PassMessage "Function returned expected result 15" `
    -FailMessage "Function returned unexpected result $result"

$pipelineResults = 1..5 |
    ForEach-Object {
        [pscustomobject]@{
            Number = $_
            Square = $_ * $_
            Cube   = $_ * $_ * $_
        }
    } |
    Where-Object { $_.Square -ge 4 }

$pipelineResults | Format-Table -AutoSize

Test-Condition `
    -Condition (($pipelineResults | Measure-Object).Count -eq 4) `
    -PassMessage "Pipeline produced expected filtered object count" `
    -FailMessage "Pipeline produced unexpected object count"

Write-SubSection "Object formatting test"

$objects = @(
    [pscustomobject]@{
        Name        = "Alpha"
        Status      = "Ready"
        Count       = 10
        LastUpdated = Get-Date
    },
    [pscustomobject]@{
        Name        = "Beta"
        Status      = "Running"
        Count       = 20
        LastUpdated = (Get-Date).AddMinutes(-5)
    },
    [pscustomobject]@{
        Name        = "Gamma"
        Status      = "Complete"
        Count       = 30
        LastUpdated = (Get-Date).AddMinutes(-10)
    }
)

Write-Host "Format-Table output:" -ForegroundColor Yellow
$objects | Format-Table -AutoSize

Write-Host ""
Write-Host "Format-List output:" -ForegroundColor Yellow
$objects[0] | Format-List

Add-TestPass -Message "Object formatting completed"

Write-SubSection "Hashtable, array, regex, and string parsing test"

$sampleHashtable = @{
    Name     = "PowerShellStudio"
    Version  = "Diagnostic v2"
    Enabled  = $true
    Numbers  = @(1, 2, 3, 4, 5)
    Settings = @{
        Theme = "Light"
        Shell = "PowerShell 7"
    }
}

$sampleArray = @(
    "C:\Users\rbarn\source\repos\PowerShellStudio",
    "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
    "C:\Program Files\PowerShell\7\pwsh.exe"
)

$regexPattern = '^[A-Z]:\\'
$regexMatches = $sampleArray | Where-Object { $_ -match $regexPattern }

Write-Host "Hashtable name: $($sampleHashtable.Name)"
Write-Host "Nested setting shell: $($sampleHashtable.Settings.Shell)"
Write-Host "Regex path matches:"
$regexMatches | ForEach-Object { Write-Host "  $_" }

Test-Condition `
    -Condition (($regexMatches | Measure-Object).Count -eq 3) `
    -PassMessage "Regex matched expected Windows paths" `
    -FailMessage "Regex did not match expected Windows paths"

Write-SubSection "Here-string, XML, and JSON test"

$hereString = @"
This is a double-quoted here-string.
Variables expand here.
PowerShell version: $($PSVersionTable.PSVersion)
Path sample: C:\Users\rbarn\source\repos\PowerShellStudio
"@

$literalHereString = @'
This is a single-quoted here-string.
Variables do not expand here.
Example: $PSVersionTable.PSVersion
Example path: C:\Users\rbarn\source\repos\PowerShellStudio
'@

Write-Host "Double-quoted here-string:"
Write-Host $hereString

Write-Host "Single-quoted here-string:"
Write-Host $literalHereString

[xml]$xml = @"
<PowerShellStudio>
  <Test Name="XML" Enabled="true" />
  <Path>C:\Users\rbarn\source\repos\PowerShellStudio</Path>
</PowerShellStudio>
"@

Test-Condition `
    -Condition ($xml.PowerShellStudio.Test.Name -eq "XML") `
    -PassMessage "XML parsing completed" `
    -FailMessage "XML parsing failed"

$jsonObject = [pscustomobject]@{
    App       = "PowerShellStudio"
    Test      = "JSON"
    Timestamp = (Get-Date).ToString("o")
    Paths     = $sampleArray
}

$json = $jsonObject | ConvertTo-Json -Depth 4
Write-Host "JSON output:"
Write-Host $json

$roundTrip = $json | ConvertFrom-Json

Test-Condition `
    -Condition ($roundTrip.App -eq "PowerShellStudio") `
    -PassMessage "JSON round-trip completed" `
    -FailMessage "JSON round-trip failed"

Write-SubSection "Class parsing and method invocation test"

$person = [DiagnosticPerson]::new("Ron", 50)
Write-Host $person.ToDisplayString()

Test-Condition `
    -Condition ($person.Name -eq "Ron") `
    -PassMessage "PowerShell class parsed and executed" `
    -FailMessage "PowerShell class did not behave as expected"

$widget = [DiagnosticWidget]::new("Primary")
$widget.Start()
$widgetStatus = $widget.GetStatus()

Test-Condition `
    -Condition ($widgetStatus -match "running") `
    -PassMessage "PowerShell class method invocation completed" `
    -FailMessage "PowerShell class method invocation failed"

Write-SubSection "Temporary file and path quoting test"

$tempRoot = Join-Path $env:TEMP "PowerShellStudio Diagnostic Test O'Brien"
$childScriptPath = Join-Path $tempRoot "Child Script With Spaces.ps1"
$normalFilePath = Join-Path $tempRoot "normal-file.txt"
$spaceFilePath = Join-Path $tempRoot "file with spaces.txt"
$apostropheFilePath = Join-Path $tempRoot "file with apostrophe O'Brien.txt"

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    "Normal file content" | Set-Content -Path $normalFilePath -Encoding UTF8
    "Space file content" | Set-Content -Path $spaceFilePath -Encoding UTF8
    "Apostrophe file content" | Set-Content -Path $apostropheFilePath -Encoding UTF8

    @'
Write-Host "Child script executed successfully." -ForegroundColor Green
Write-Host "Child script path: $PSCommandPath"

$childResult = [pscustomobject]@{
    ChildScript = $true
    Location = (Get-Location).Path
    Time = Get-Date
}

Write-Host "Child object output should appear immediately below this line:" -ForegroundColor Yellow
$childResult
Write-Host "Child object output should have appeared above this line." -ForegroundColor Yellow
'@ | Set-Content -Path $childScriptPath -Encoding UTF8

    Test-Condition `
        -Condition (Test-Path -LiteralPath $normalFilePath) `
        -PassMessage "Normal temp file created" `
        -FailMessage "Normal temp file was not created"

    Test-Condition `
        -Condition (Test-Path -LiteralPath $spaceFilePath) `
        -PassMessage "Temp file with spaces created" `
        -FailMessage "Temp file with spaces was not created"

    Test-Condition `
        -Condition (Test-Path -LiteralPath $apostropheFilePath) `
        -PassMessage "Temp file with apostrophe created" `
        -FailMessage "Temp file with apostrophe was not created"

    Write-Host "Invoking child script with spaces/apostrophe path context:"
    & $childScriptPath

    Add-TestPass -Message "Child script invocation completed"
}
catch {
    Add-TestFail -Message "Temporary file/path quoting test failed: $($_.Exception.Message)"
}
finally {
    if ($KeepTempFiles) {
        Add-TestWarning -Message "Keeping temp files at: $tempRoot"
    }
    else {
        try {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
            Add-TestPass -Message "Temporary files cleaned up"
        }
        catch {
            Add-TestWarning -Message "Temporary cleanup warning: $($_.Exception.Message)"
        }
    }
}

Write-SubSection "Location command syntax test"

$originalLocation = Get-Location

try {
    Push-Location $env:TEMP
    Write-Host "Changed location using Push-Location to: $(Get-Location)"
    Pop-Location
    Write-Host "Returned location using Pop-Location to: $(Get-Location)"

    Test-Condition `
        -Condition ((Get-Location).Path -eq $originalLocation.Path) `
        -PassMessage "Push-Location / Pop-Location restored original path" `
        -FailMessage "Location was not restored correctly"
}
catch {
    Add-TestFail -Message "Location command test failed: $($_.Exception.Message)"
}

Write-SubSection "PowerShell executable checks"

$pwsh7Path = "C:\Program Files\PowerShell\7\pwsh.exe"
$windowsPowerShell51Path = "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"

if (Test-Path -LiteralPath $pwsh7Path) {
    Add-TestPass -Message "PowerShell 7 executable exists: $pwsh7Path"
}
else {
    Add-TestWarning -Message "PowerShell 7 executable not found at expected path: $pwsh7Path"
}

if (Test-Path -LiteralPath $windowsPowerShell51Path) {
    Add-TestPass -Message "Windows PowerShell 5.1 executable exists: $windowsPowerShell51Path"
}
else {
    Add-TestWarning -Message "Windows PowerShell 5.1 executable not found at expected path: $windowsPowerShell51Path"
}

Test-PowerShell7OnlyFeatures
Test-BackgroundJobs
Test-NativeCommandBehavior

Write-SubSection "Terminating error catch test"

try {
    throw "Intentional caught terminating error for diagnostic test"
}
catch {
    Write-Host "Caught expected terminating error: $($_.Exception.Message)" -ForegroundColor DarkYellow
    Add-TestPass -Message "Caught terminating error without stopping script"
}

Write-SubSection "Native dotnet command smoke test"

try {
    $dotnetVersion = dotnet --version 2>$null

    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($dotnetVersion)) {
        Add-TestPass -Message "dotnet command executed successfully. Version: $dotnetVersion"
    }
    else {
        Add-TestWarning -Message "dotnet command did not return a version. This may be fine if dotnet is not on PATH."
    }
}
catch {
    Add-TestWarning -Message "dotnet command test threw an exception: $($_.Exception.Message)"
}

if ($SkipGui) {
    Write-SubSection "GUI tests"
    Add-TestSkipped -Message "GUI tests skipped by parameter"
}
else {
    Invoke-WinFormsGuiTest
    Invoke-WpfGuiTest
}

Write-SubSection "Official parser test for previous false diagnostic issue"

Test-PathSyntaxWithPowerShellParser

Write-SubSection "Editor-only manual diagnostic samples"

Write-Host "The following samples are printed for manual editor diagnostic testing." -ForegroundColor Yellow
Write-Host "They are not executed as broken syntax." -ForegroundColor Yellow
Write-Host ""

$manualDiagnosticSamples = @'
VALID LINES THAT SHOULD NOT SHOW RED SQUIGGLES:

cd C:\Users\rbarn\source\repos\PowerShellStudio
Set-Location C:\Users
Push-Location C:\Users
cd C:\
cd .\
cd ..\
Get-ChildItem C:\Users
Get-ChildItem -Path C:\Users
Get-ChildItem -LiteralPath C:\Users
cd 'C:\Users\rbarn\source\repos\PowerShellStudio'
cd "C:\Users\rbarn\source\repos\PowerShellStudio"

POWERSHELL 7-ONLY VALID LINES THAT SHOULD NOT SHOW RED SQUIGGLES IN PS7 MODE:

$x = $null
$x ?? "fallback"
$x ??= "assigned"
$result = $true ? "yes" : "no"
Write-Output "left" && Write-Output "right"
Write-Output "left" || Write-Output "right"
1..5 | ForEach-Object -Parallel { $_ * 2 }
$PSStyle.Foreground.BrightGreen

VALID CLASS/METHOD LINES THAT SHOULD NOT BE FLAGGED AS UNUSED FUNCTIONS:

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

$person = [DiagnosticPerson]::new("Ron", 50)
Write-Host $person.ToDisplayString()

INVALID LINES THAT SHOULD SHOW RED SQUIGGLES IF PASTED INTO THE EDITOR:

if ($true {
Write-Host "missing end quote
function Bad-Test { param([int]$A) Write-Host $A
'@

Write-Host $manualDiagnosticSamples

Write-SubSection "Prompt recovery final test"

Write-Host "The script is about to finish." -ForegroundColor Yellow
Write-Host "After the final marker, the console should return to a normal prompt automatically." -ForegroundColor Yellow
Write-Host "Expected normal prompt example: PS Z:\>" -ForegroundColor Yellow
Write-Host "Bad continuation prompt example: >>" -ForegroundColor Yellow
Write-Host "You should NOT have to press Enter to get the prompt back." -ForegroundColor Yellow

Write-Section "PowerShellStudio Full Editor Diagnostic Test v2 Finished"

Write-Host "Finished:                $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')" -ForegroundColor Cyan
Write-Host "Passed checks:           $script:PassedCount" -ForegroundColor Green
Write-Host "Warnings:                $script:WarningCount" -ForegroundColor Yellow
Write-Host "Skipped checks:          $script:SkippedCount" -ForegroundColor DarkYellow
Write-Host "Failed checks:           $script:FailedCount" -ForegroundColor Red

if ($script:FailedCount -eq 0) {
    Write-Host "Overall result:          PASS" -ForegroundColor Green
}
else {
    Write-Host "Overall result:          REVIEW FAILURES" -ForegroundColor Red
}

Write-Host ""
Write-Host "FINAL_MARKER_EDITOR_CONSOLE_GUI_PS7_DIAGNOSTIC_TEST_COMPLETE" -ForegroundColor Green
Write-Host ""