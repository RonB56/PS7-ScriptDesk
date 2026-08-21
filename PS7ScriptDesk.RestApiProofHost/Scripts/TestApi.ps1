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
