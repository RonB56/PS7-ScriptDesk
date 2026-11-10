[CmdletBinding(SupportsShouldProcess = $true)]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# USER SETTINGS
# Keep the private workspace beside the PowerShellStudio repository. Update the
# package identity and hash together only when staging a separately accepted version.
$expectedPackageId = 'PS7ScriptDesk.PrivateAvalonEdit'
$expectedVersion = '6.3.2-g3.4b2.20261109'
$expectedSha256 = 'e99fdcbce4bf942d4d499059178987e744e2fa10712c324292b123063cc8f299'
$expectedFileName = "$expectedPackageId.$expectedVersion.nupkg"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$privateWorkspaceRoot = Join-Path (Split-Path $repositoryRoot -Parent) 'PrivateAvalonEdit'
$packagePath = Join-Path $privateWorkspaceRoot "packaging\artifacts\$expectedFileName"
$feedPath = Join-Path $repositoryRoot '.private-nuget-feed'
$sourcePath = [IO.Path]::GetFullPath($packagePath)

if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Private package was not found at '$sourcePath'. Build it from the sibling PrivateAvalonEdit workspace first."
}

$sourceFile = Get-Item -LiteralPath $sourcePath
if ($sourceFile.Name -cne $expectedFileName) {
    throw "Expected package file '$expectedFileName'; received '$($sourceFile.Name)'."
}

$actualSha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha256 -cne $expectedSha256) {
    throw "Private package SHA-256 mismatch. Expected $expectedSha256; received $actualSha256."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($sourcePath)
try {
    $nuspecEntries = @($archive.Entries | Where-Object { $_.FullName -match '^[^/]+\.nuspec$' })
    if ($nuspecEntries.Count -ne 1) {
        throw "Expected exactly one root nuspec; found $($nuspecEntries.Count)."
    }

    $stream = $nuspecEntries[0].Open()
    try {
        $settings = [System.Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $reader = [System.Xml.XmlReader]::Create($stream, $settings)
        try {
            $nuspec = [System.Xml.Linq.XDocument]::Load($reader)
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}
finally {
    $archive.Dispose()
}

$metadata = $nuspec.Root.Elements() | Where-Object { $_.Name.LocalName -ceq 'metadata' } | Select-Object -First 1
$idElement = $metadata.Elements() | Where-Object { $_.Name.LocalName -ceq 'id' } | Select-Object -First 1
$versionElement = $metadata.Elements() | Where-Object { $_.Name.LocalName -ceq 'version' } | Select-Object -First 1
$packageId = [string] $idElement.Value
$packageVersion = [string] $versionElement.Value
if ($packageId -cne $expectedPackageId -or $packageVersion -cne $expectedVersion) {
    throw "Package identity mismatch. Expected $expectedPackageId $expectedVersion; received '$packageId' '$packageVersion'."
}

if (-not (Test-Path -LiteralPath $feedPath -PathType Container)) {
    if ($PSCmdlet.ShouldProcess($feedPath, 'Create private local NuGet feed')) {
        $null = New-Item -ItemType Directory -Path $feedPath
    }
}

$destinationPath = Join-Path $feedPath $expectedFileName
if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
    $existingSha256 = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($existingSha256 -cne $expectedSha256) {
        throw "A different file already exists at '$destinationPath'; it was left untouched."
    }
    Write-Output "Private package already initialized: $destinationPath"
    return
}

if ($PSCmdlet.ShouldProcess($destinationPath, 'Copy verified private package into local feed')) {
    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath
    Write-Output "Initialized private package feed with $packageId $packageVersion (SHA-256 $actualSha256)."
}
