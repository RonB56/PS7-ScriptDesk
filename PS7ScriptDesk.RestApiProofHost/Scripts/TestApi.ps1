function Get-SystemInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ComputerName
    )

    [pscustomobject]@{
        ComputerName = $ComputerName
        Message      = "System information requested for $ComputerName"
    }
}

function Invoke-TestFailure {
    [CmdletBinding()]
    param()

    throw "Intentional test failure."
}

function Get-Numbers {
    [CmdletBinding()]
    param()

    1
    2
    3
}

function Get-ExplicitNull {
    [CmdletBinding()]
    param()

    $null
}

function Invoke-Phase4Delay {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RequestId,

        [Parameter(Mandatory)]
        [int]$Milliseconds
    )

    $started = [DateTimeOffset]::UtcNow
    [System.Threading.Thread]::Sleep($Milliseconds)
    $completed = [DateTimeOffset]::UtcNow

    [pscustomobject]@{
        RequestId    = $RequestId
        Milliseconds = $Milliseconds
        StartedTicks = $started.Ticks
        EndedTicks   = $completed.Ticks
    }
}

function Set-Phase4GlobalState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    $global:Phase4LeakedValue = $Value
    [pscustomobject]@{
        Value = $Value
    }
}

function Get-Phase4GlobalState {
    [CmdletBinding()]
    param()

    if ($null -ne $global:Phase4LeakedValue) {
        [pscustomobject]@{
            Value = $global:Phase4LeakedValue
        }
        return
    }

    $null
}

function Invoke-Phase5FormattingOutput {
    [CmdletBinding()]
    param()

    $output = [pscustomobject]@{
        Name  = 'alpha'
        Count = 1
    }
    $output.PSObject.TypeNames.Insert(0, 'Microsoft.PowerShell.Commands.Internal.Format.FormatEntryData')
    $output
}

function Invoke-Phase5BStreams {
    [CmdletBinding()]
    param()

    foreach ($index in 1..120) {
        $PSCmdlet.WriteWarning("phase5b-warning-$index")
    }

    [pscustomobject]@{
        Value = 'stream-success'
    }
}

function Invoke-Phase5BNonTerminatingError {
    [CmdletBinding()]
    param()

    $exception = [System.InvalidOperationException]::new('phase5b-nonterminating-secret C:\Sensitive\Hidden.ps1')
    $record = [System.Management.Automation.ErrorRecord]::new(
        $exception,
        'Phase5BNonTerminatingError',
        [System.Management.Automation.ErrorCategory]::InvalidOperation,
        $null)
    $PSCmdlet.WriteError($record)

    [pscustomobject]@{
        Value = 'partial-output-must-not-leak'
    }
}

function Invoke-Phase5BValidation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateRange(1, 5)]
        [int]$Value
    )

    [pscustomobject]@{
        Value = $Value
    }
}

function Invoke-Phase5BOversizedOutput {
    [CmdletBinding()]
    param()

    'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx'
}

function Get-Phase6Computer {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ComputerName,

        [ValidateSet('Summary', 'Detail')]
        [string]$View = 'Summary',

        [ValidateRange(1, 100)]
        [int]$Limit = 10
    )

    [pscustomobject]@{
        ComputerName = $ComputerName
        View         = $View
        Limit        = $Limit
    }
}

function Set-Phase6Computer {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ComputerName,

        [Parameter(Mandatory)]
        [ValidateLength(1, 50)]
        [string]$DisplayName,

        [bool]$Enabled = $true
    )

    [pscustomobject]@{
        ComputerName = $ComputerName
        DisplayName  = $DisplayName
        Enabled      = $Enabled
    }
}

function Invoke-Phase6UnconfiguredSecret {
    [CmdletBinding()]
    param()

    'this function must not appear in OpenAPI'
}
