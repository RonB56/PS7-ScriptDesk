# Manual interrupt/recovery exercises for PS7 ScriptDesk.
# Run each block from the editor or terminal and verify the app remains usable.

function Test-EndlessLoop {
    while ($true) {
        Start-Sleep -Milliseconds 250
        Write-Host "running"
    }
}

function Test-LongSleep {
    Start-Sleep -Seconds 300
}

function Test-StrictModeUnsetVariable {
    Set-StrictMode -Version Latest
    $job.Name
}

function Test-WinFormsCallbackFailure {
    Add-Type -AssemblyName System.Windows.Forms

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "PS7 ScriptDesk WinForms callback failure"

    $button = New-Object System.Windows.Forms.Button
    $button.Text = "Trigger"
    $button.Dock = [System.Windows.Forms.DockStyle]::Fill
    $button.Add_Click({
        Set-StrictMode -Version Latest
        $job.Name
    })

    $form.Controls.Add($button)
    [void]$form.ShowDialog()
}

function Test-WinFormsMessageLoop {
    Add-Type -AssemblyName System.Windows.Forms

    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 250
    $timer.Add_Tick({
        [System.Windows.Forms.Application]::DoEvents()
    })

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "PS7 ScriptDesk message loop test"
    $form.Add_Shown({ $timer.Start() })
    $form.Add_FormClosed({ $timer.Stop(); $timer.Dispose() })

    [System.Windows.Forms.Application]::Run($form)
}

function Test-NativeChildProcess {
    Start-Process -FilePath notepad.exe
    while ($true) {
        Start-Sleep -Seconds 1
    }
}

"Loaded interrupt reliability manual tests:"
"  Test-EndlessLoop"
"  Test-LongSleep"
"  Test-StrictModeUnsetVariable"
"  Test-WinFormsCallbackFailure"
"  Test-WinFormsMessageLoop"
"  Test-NativeChildProcess"
