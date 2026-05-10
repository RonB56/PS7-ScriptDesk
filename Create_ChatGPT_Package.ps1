<#
.SYNOPSIS
    Creates a clean source-code ZIP package from any repository folder.

.DESCRIPTION
    This script is variable-driven, not parameter-driven.

    Edit the USER VARIABLES section to choose:
    - the repository/source folder to package
    - the output folder
    - the package name prefix
    - excluded folders
    - excluded file patterns

    It excludes common build/package folders such as:
    bin, obj, .vs, AppPackages, packages, TestResults

    It also excludes package/archive files such as:
    *.zip, *.msix, *.msixbundle, *.appx, *.appxbundle

.NOTES
    Safe behavior:
    - Does not delete source files
    - Does not modify source files
    - Does not require admin rights
    - Creates a ZIP file and a manifest file
#>

# ============================================================
# USER VARIABLES - EDIT THESE
# ============================================================

# Point this to the repo/project folder you want to package.
$RepositoryRoot = "C:\Users\rbarn\source\repos\PowerShellStudio"

# Point this to wherever you want the ZIP files created.
# This can be outside the repo.
$OutputRoot = "C:\Users\rbarn\source\repos\PowerShellStudio"

# ZIP file name prefix.
# The script will add a timestamp automatically.
$PackageNamePrefix = "SourcePackage"

# Exclude these directory names anywhere under the repo.
$ExcludeDirectoryNames = @(
    "bin",
    "obj",
    ".vs",
    "AppPackages",
    "packages",
    "TestResults"
)

# Exclude these file name patterns anywhere under the repo.
$ExcludeFileNamePatterns = @(
    "*.zip",
    "*.msix",
    "*.msixbundle",
    "*.appx",
    "*.appxbundle"
)

# Usually keep this false when sending source code for review.
# Set to true only if you specifically want to include the .git folder.
$IncludeGitFolder = $false

# Include hidden files such as .gitignore, .editorconfig, etc.
# Usually keep this true.
$IncludeHiddenFiles = $true

# Create a manifest text file next to the ZIP.
$CreateManifest = $true

# ============================================================
# SCRIPT LOGIC - DO NOT EDIT BELOW UNLESS NEEDED
# ============================================================

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Section {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    Write-Host ""
    Write-Host "============================================================"
    Write-Host " $Text"
    Write-Host "============================================================"
}

function Get-FullNormalizedPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return [System.IO.Path]::GetFullPath($Path)
}

function Get-RelativePathSafe {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BasePath,

        [Parameter(Mandatory = $true)]
        [string]$TargetPath
    )

    $baseFull = [System.IO.Path]::GetFullPath($BasePath)
    $targetFull = [System.IO.Path]::GetFullPath($TargetPath)

    if (-not $baseFull.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $baseFull += [System.IO.Path]::DirectorySeparatorChar
    }

    # PowerShell 7 / modern .NET path.
    try {
        return [System.IO.Path]::GetRelativePath($baseFull, $targetFull)
    }
    catch {
        # Windows PowerShell 5.1 fallback.
        $baseUri = New-Object System.Uri($baseFull)
        $targetUri = New-Object System.Uri($targetFull)
        $relativeUri = $baseUri.MakeRelativeUri($targetUri)
        $relativePath = [System.Uri]::UnescapeDataString($relativeUri.ToString())
        return ($relativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
    }
}

function Test-PathContainsExcludedDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FullPath,

        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [Parameter(Mandatory = $true)]
        [string[]]$ExcludedDirectoryNames
    )

    $relativePath = Get-RelativePathSafe -BasePath $RootPath -TargetPath $FullPath

    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        return $false
    }

    $parts = $relativePath -split '[\\/]+'

    foreach ($part in $parts) {
        foreach ($excluded in $ExcludedDirectoryNames) {
            if ($part.Equals($excluded, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
    }

    return $false
}

function Test-FileNameMatchesExcludedPattern {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FileName,

        [Parameter(Mandatory = $true)]
        [string[]]$Patterns
    )

    foreach ($pattern in $Patterns) {
        if ($FileName -like $pattern) {
            return $true
        }
    }

    return $false
}

function Get-ZipEntryName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootPath,

        [Parameter(Mandatory = $true)]
        [string]$FullPath
    )

    $relativePath = Get-RelativePathSafe -BasePath $RootPath -TargetPath $FullPath

    # ZIP entries should use forward slashes.
    return ($relativePath -replace '\\', '/')
}

Write-Section "Repository Source Package Creator"

$RepositoryRoot = Get-FullNormalizedPath -Path $RepositoryRoot
$OutputRoot = Get-FullNormalizedPath -Path $OutputRoot

Write-Host "Repository root:"
Write-Host "  $RepositoryRoot"

Write-Host ""
Write-Host "Output root:"
Write-Host "  $OutputRoot"

if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
    throw "Repository root does not exist: $RepositoryRoot"
}

if (-not (Test-Path -LiteralPath $OutputRoot -PathType Container)) {
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
}

$effectiveExcludedDirectoryNames = New-Object System.Collections.Generic.List[string]

foreach ($dir in $ExcludeDirectoryNames) {
    $effectiveExcludedDirectoryNames.Add($dir)
}

if (-not $IncludeGitFolder) {
    $effectiveExcludedDirectoryNames.Add(".git")
}

# If the output folder is inside the repo, exclude it too.
try {
    $relativeOutput = Get-RelativePathSafe -BasePath $RepositoryRoot -TargetPath $OutputRoot
    if (-not $relativeOutput.StartsWith("..")) {
        $firstOutputPart = ($relativeOutput -split '[\\/]+')[0]
        if (-not [string]::IsNullOrWhiteSpace($firstOutputPart)) {
            $alreadyExcluded = $false
            foreach ($dir in $effectiveExcludedDirectoryNames) {
                if ($dir.Equals($firstOutputPart, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $alreadyExcluded = $true
                    break
                }
            }

            if (-not $alreadyExcluded) {
                $effectiveExcludedDirectoryNames.Add($firstOutputPart)
            }
        }
    }
}
catch {
    # If relative path detection fails, continue with normal exclusions.
}

Write-Section "Exclusion Rules"

Write-Host "Excluded directory names:"
foreach ($dir in $effectiveExcludedDirectoryNames) {
    Write-Host "  $dir"
}

Write-Host ""
Write-Host "Excluded file patterns:"
foreach ($pattern in $ExcludeFileNamePatterns) {
    Write-Host "  $pattern"
}

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$zipFileName = "$PackageNamePrefix`_$timestamp.zip"
$manifestFileName = "$PackageNamePrefix`_$timestamp`_Manifest.txt"

$zipPath = Join-Path $OutputRoot $zipFileName
$manifestPath = Join-Path $OutputRoot $manifestFileName

Write-Section "Scanning Repository"

$getChildItemOptions = @{
    LiteralPath = $RepositoryRoot
    Recurse     = $true
    File        = $true
    Force       = $IncludeHiddenFiles
}

$allFiles = Get-ChildItem @getChildItemOptions

$includedFiles = New-Object System.Collections.Generic.List[System.IO.FileInfo]
$excludedFiles = New-Object System.Collections.Generic.List[string]

foreach ($file in $allFiles) {
    $fullPath = $file.FullName
    $relativePath = Get-RelativePathSafe -BasePath $RepositoryRoot -TargetPath $fullPath

    $excludedByDirectory = Test-PathContainsExcludedDirectory `
        -FullPath $fullPath `
        -RootPath $RepositoryRoot `
        -ExcludedDirectoryNames $effectiveExcludedDirectoryNames.ToArray()

    if ($excludedByDirectory) {
        $excludedFiles.Add("DIR  | $relativePath")
        continue
    }

    $excludedByFilePattern = Test-FileNameMatchesExcludedPattern `
        -FileName $file.Name `
        -Patterns $ExcludeFileNamePatterns

    if ($excludedByFilePattern) {
        $excludedFiles.Add("FILE | $relativePath")
        continue
    }

    $includedFiles.Add($file)
}

Write-Host "Total files scanned: $($allFiles.Count)"
Write-Host "Files included:      $($includedFiles.Count)"
Write-Host "Files excluded:      $($excludedFiles.Count)"

if ($includedFiles.Count -eq 0) {
    throw "No files were selected for packaging. Check your repository path and exclusion rules."
}

Write-Section "Creating ZIP"

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zipFileStream = [System.IO.File]::Open(
    $zipPath,
    [System.IO.FileMode]::CreateNew,
    [System.IO.FileAccess]::ReadWrite,
    [System.IO.FileShare]::None
)

try {
    $zipArchive = New-Object System.IO.Compression.ZipArchive(
        $zipFileStream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $false
    )

    try {
        $fileNumber = 0

        foreach ($file in $includedFiles) {
            $fileNumber++

            $entryName = Get-ZipEntryName `
                -RootPath $RepositoryRoot `
                -FullPath $file.FullName

            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zipArchive,
                $file.FullName,
                $entryName,
                [System.IO.Compression.CompressionLevel]::Optimal
            ) | Out-Null

            if (($fileNumber % 250) -eq 0) {
                Write-Host "  Added $fileNumber of $($includedFiles.Count) files..."
            }
        }
    }
    finally {
        $zipArchive.Dispose()
    }
}
finally {
    $zipFileStream.Dispose()
}

$zipInfo = Get-Item -LiteralPath $zipPath
$zipHash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256

if ($CreateManifest) {
    Write-Section "Creating Manifest"

    $manifestLines = New-Object System.Collections.Generic.List[string]

    $manifestLines.Add("Repository Source Package Manifest")
    $manifestLines.Add("Created: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $manifestLines.Add("")
    $manifestLines.Add("Repository root:")
    $manifestLines.Add("  $RepositoryRoot")
    $manifestLines.Add("")
    $manifestLines.Add("Output ZIP:")
    $manifestLines.Add("  $zipPath")
    $manifestLines.Add("")
    $manifestLines.Add("ZIP size:")
    $manifestLines.Add("  $([Math]::Round($zipInfo.Length / 1MB, 2)) MB")
    $manifestLines.Add("")
    $manifestLines.Add("SHA256:")
    $manifestLines.Add("  $($zipHash.Hash)")
    $manifestLines.Add("")
    $manifestLines.Add("Include .git folder:")
    $manifestLines.Add("  $IncludeGitFolder")
    $manifestLines.Add("")
    $manifestLines.Add("Include hidden files:")
    $manifestLines.Add("  $IncludeHiddenFiles")
    $manifestLines.Add("")
    $manifestLines.Add("Included file count:")
    $manifestLines.Add("  $($includedFiles.Count)")
    $manifestLines.Add("")
    $manifestLines.Add("Excluded file count:")
    $manifestLines.Add("  $($excludedFiles.Count)")
    $manifestLines.Add("")
    $manifestLines.Add("Excluded directory names:")
    foreach ($dir in $effectiveExcludedDirectoryNames) {
        $manifestLines.Add("  $dir")
    }
    $manifestLines.Add("")
    $manifestLines.Add("Excluded file patterns:")
    foreach ($pattern in $ExcludeFileNamePatterns) {
        $manifestLines.Add("  $pattern")
    }
    $manifestLines.Add("")
    $manifestLines.Add("Included files:")
    foreach ($file in $includedFiles) {
        $manifestLines.Add("  $(Get-RelativePathSafe -BasePath $RepositoryRoot -TargetPath $file.FullName)")
    }
    $manifestLines.Add("")
    $manifestLines.Add("Excluded files:")
    foreach ($excludedFile in $excludedFiles) {
        $manifestLines.Add("  $excludedFile")
    }

    Set-Content -LiteralPath $manifestPath -Value $manifestLines -Encoding UTF8
}

Write-Section "Package Complete"

Write-Host "ZIP created:"
Write-Host "  $zipPath"

Write-Host ""
Write-Host "ZIP size:"
Write-Host "  $([Math]::Round($zipInfo.Length / 1MB, 2)) MB"

Write-Host ""
Write-Host "SHA256:"
Write-Host "  $($zipHash.Hash)"

if ($CreateManifest) {
    Write-Host ""
    Write-Host "Manifest created:"
    Write-Host "  $manifestPath"
}

Write-Host ""
Write-Host "Upload this ZIP file:"
Write-Host "  $zipPath"