$ErrorActionPreference = 'Stop'

param(
    [switch]$Force = $false
)

function Get-FileVersionFromName {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$File
    )

    $match = [regex]::Match($File.BaseName, '\d+(?:\.\d+){1,3}')
    if ($match.Success) {
        try {
            return [version]$match.Value
        }
        catch {
        }
    }

    return [version]'0.0.0.0'
}

function Get-PackageRank {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$File
    )

    switch ($File.Extension.ToLowerInvariant()) {
        '.msixbundle' { return 4 }
        '.appxbundle' { return 3 }
        '.msix' { return 2 }
        '.appx' { return 1 }
        default { return 0 }
    }
}

function Format-CandidateLine {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$File
    )

    $version = Get-FileVersionFromName -File $File
    return '{0} | Version={1} | Modified={2:u}' -f $File.FullName, $version, $File.LastWriteTimeUtc
}

function Select-BestFile {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo[]]$Files
    )

    return $Files |
        Sort-Object `
            @{ Expression = { Get-FileVersionFromName -File $_ }; Descending = $true }, `
            @{ Expression = { $_.LastWriteTimeUtc }; Descending = $true }, `
            @{ Expression = { Get-PackageRank -File $_ }; Descending = $true }, `
            @{ Expression = { $_.FullName }; Descending = $false } |
        Select-Object -First 1
}

function Get-RelativePathOrSelf {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    try {
        return [System.IO.Path]::GetRelativePath($Root, $Path)
    }
    catch {
        return $Path
    }
}

if ([string]::IsNullOrWhiteSpace($PSCommandPath)) {
    throw 'This installer requires $PSCommandPath to resolve its own location.'
}

$scriptRoot = Split-Path -Path $PSCommandPath -Parent
$packageExtensions = @('*.msixbundle', '*.appxbundle', '*.msix', '*.appx')

Write-Host "Installer script: $PSCommandPath"
Write-Host "Search root: $scriptRoot"

$allRelevantFiles = Get-ChildItem -Path $scriptRoot -Recurse -File -Include $packageExtensions, '*.cer' |
    Sort-Object FullName

$mainPackageCandidates = $allRelevantFiles |
    Where-Object {
        $_.Extension -in '.msixbundle', '.appxbundle', '.msix', '.appx' -and
        $_.FullName -notmatch '(?i)[\\/]+Dependencies([\\/]+|$)'
    }

if (-not $mainPackageCandidates) {
    Write-Error "No installable main package was found under '$scriptRoot'."
    Write-Host 'Relevant files discovered during recursive search:'
    if ($allRelevantFiles) {
        $allRelevantFiles | ForEach-Object { Write-Host " - $($_.FullName)" }
    }
    else {
        Write-Host ' - None'
    }
    exit 1
}

Write-Host 'Main package candidates:'
$mainPackageCandidates |
    Sort-Object FullName |
    ForEach-Object { Write-Host " - $(Format-CandidateLine -File $_)" }

$package = Select-BestFile -Files $mainPackageCandidates
Write-Host "Selected package: $($package.FullName)"

$certificateCandidates = $allRelevantFiles | Where-Object { $_.Extension -eq '.cer' }
$certificate = $null

if ($certificateCandidates) {
    if (($certificateCandidates | Measure-Object).Count -gt 1) {
        Write-Host 'Certificate candidates:'
        $certificateCandidates |
            Sort-Object FullName |
            ForEach-Object { Write-Host " - $(Format-CandidateLine -File $_)" }
    }

    $certificate = Select-BestFile -Files $certificateCandidates
    Write-Host "Selected certificate: $($certificate.FullName)"
    Write-Host "Importing certificate to Cert:\CurrentUser\TrustedPeople"
    Import-Certificate -FilePath $certificate.FullName -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null
}
else {
    Write-Host 'No .cer certificate found under the extracted package folder.'
}

$dependencyFolders = Get-ChildItem -Path $scriptRoot -Recurse -Directory |
    Where-Object { $_.Name -ieq 'Dependencies' }

$dependencyPackages = @()
foreach ($dependencyFolder in $dependencyFolders) {
    $dependencyPackages += Get-ChildItem -Path $dependencyFolder.FullName -Recurse -File -Include '*.appx', '*.msix'
}

$dependencyPackages = $dependencyPackages |
    Sort-Object -Property FullName -Unique

if ($dependencyPackages) {
    Write-Host 'Dependency packages:'
    $dependencyPackages | ForEach-Object {
        $relative = Get-RelativePathOrSelf -Root $scriptRoot -Path $_.FullName
        Write-Host " - $relative"
    }
}

$addAppxArgs = @{
    Path = $package.FullName
    ForceApplicationShutdown = $true
}

if ($dependencyPackages) {
    $addAppxArgs.DependencyPath = @($dependencyPackages.FullName)
}

Write-Host "Installing package with Add-AppxPackage -Path '$($package.FullName)'"
Add-AppxPackage @addAppxArgs

Write-Host 'Installation completed successfully.'
