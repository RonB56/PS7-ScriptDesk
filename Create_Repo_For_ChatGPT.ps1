# Package-VSProjectForChatGPT.ps1
# Creates a clean ZIP of a Visual Studio project/solution for ChatGPT analysis.
# This version is VARIABLE-DRIVEN for ISE-style use.
# Edit the variables in the USER SETTINGS section below, then run the script.

#requires -Version 5.1

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

# ============================================================
# USER SETTINGS - EDIT THESE VALUES
# ============================================================

# Folder containing your Visual Studio solution or project.
# Example:
# $SourceRoot = "C:\Users\rbarn\source\repos\PowerShellStudio"
$SourceRoot = "C:\Users\rbarn\source\repos\PowerShellStudio"

# Leave blank to create the ZIP on your Desktop automatically.
# Or set a specific full ZIP path, for example:
# $OutputZipPath = "C:\Users\rbarn\Desktop\PowerShellStudio-For-ChatGPT.zip"
$OutputZipPath = ""

# Files larger than this size will be skipped.
$MaxFileSizeMB = 50

# Open File Explorer and select the ZIP when finished.
$OpenOutputFolder = $true

# Include folders normally excluded from analysis.
$IncludeBinObj = $false
$IncludeGit = $false
$IncludeVsFolder = $false
$IncludePackages = $false
$IncludeLogs = $false
$IncludeLargeMedia = $false

# ============================================================
# ADVANCED EXCLUSION SETTINGS
# Usually leave these alone.
# ============================================================

$DefaultExcludedDirectoryNames = @(
    ".vs",
    ".git",
    ".idea",
    ".vscode",
    ".cache",
    ".gradle",
    ".nuget",
    "bin",
    "obj",
    "Debug",
    "Release",
    "x64",
    "x86",
    "arm64",
    "AnyCPU",
    "packages",
    "node_modules",
    "TestResults",
    "AppPackages",
    "BundleArtifacts",
    "PackageLayout",
    "publish",
    "published",
    "artifacts",
    "coverage",
    "CoverageReport",
    ".pytest_cache",
    ".mypy_cache"
)

$DefaultExcludedFileExtensions = @(
    ".exe",
    ".dll",
    ".pdb",
    ".cache",
    ".ilk",
    ".obj",
    ".iobj",
    ".ipdb",
    ".lib",
    ".exp",
    ".appx",
    ".msix",
    ".msixbundle",
    ".appxbundle",
    ".nupkg",
    ".snupkg",
    ".vsix",
    ".zip",
    ".7z",
    ".rar",
    ".tar",
    ".gz",
    ".iso",
    ".bin",
    ".tmp",
    ".temp",
    ".bak",
    ".log",
    ".mp4",
    ".mkv",
    ".mov",
    ".avi",
    ".wmv",
    ".mp3",
    ".wav",
    ".flac",
    ".aac"
)

$ExcludedFilePatterns = @(
    "*.user",
    "*.suo",
    "*.rsuser",
    "*.ncb",
    "*.opendb",
    "*.sdf",
    "*.db",
    "*.db-shm",
    "*.db-wal",
    "*.sqlite",
    "*.sqlite3",
    "*.pfx",
    "*.p12",
    "*.snk",
    "*.key",
    "*.pem",
    "*.cer",
    "*.crt",
    "*.publishsettings",
    "*.pubxml.user",
    "*.binlog",
    "*.trx",
    "*.coverage",
    "*.coveragexml",
    ".env",
    ".env*",
    "secrets.json",
    "appsettings.Development.json",
    "appsettings.*.Development.json",
    "~$*",
    "Thumbs.db",
    "desktop.ini",
    ".DS_Store",
    "project.assets.json",
    "project.nuget.cache",
    "*.nuget.dgspec.json"
)

# ============================================================
# SCRIPT LOGIC - DO NOT EDIT BELOW THIS LINE UNLESS NEEDED
# ============================================================

function Write-Info {
    param([string]$Message)

    Write-Host $Message -ForegroundColor Cyan
}

function Write-Good {
    param([string]$Message)

    Write-Host $Message -ForegroundColor Green
}

function Write-Warn {
    param([string]$Message)

    Write-Host $Message -ForegroundColor Yellow
}

function New-CaseInsensitiveHashSet {
    param([string[]]$Values)

    $set = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($value in $Values) {
        [void]$set.Add($value)
    }

    return $set
}

function Get-SafeFileName {
    param([string]$Name)

    $invalidChars = [System.IO.Path]::GetInvalidFileNameChars()
    $safeName = $Name

    foreach ($char in $invalidChars) {
        $safeName = $safeName.Replace($char, "_")
    }

    if ([string]::IsNullOrWhiteSpace($safeName)) {
        $safeName = "VisualStudioProject"
    }

    return $safeName
}

function Get-RelativePathSafe {
    param(
        [string]$BasePath,
        [string]$FullPath
    )

    try {
        $baseFull = [System.IO.Path]::GetFullPath($BasePath)
        $targetFull = [System.IO.Path]::GetFullPath($FullPath)

        if (-not $baseFull.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
            $baseFull += [System.IO.Path]::DirectorySeparatorChar
        }

        $getRelativePathMethod = [System.IO.Path].GetMethod(
            "GetRelativePath",
            [type[]]@([string], [string])
        )

        if ($null -ne $getRelativePathMethod) {
            return [System.IO.Path]::GetRelativePath($baseFull, $targetFull)
        }
    }
    catch {
        # Fall back below.
    }

    $baseUri = New-Object System.Uri(($BasePath.TrimEnd("\", "/") + [System.IO.Path]::DirectorySeparatorChar))
    $targetUri = New-Object System.Uri($FullPath)
    $relativeUri = $baseUri.MakeRelativeUri($targetUri)

    return [System.Uri]::UnescapeDataString($relativeUri.ToString()).Replace("/", [System.IO.Path]::DirectorySeparatorChar)
}

function Test-IsReparsePoint {
    param([System.IO.FileSystemInfo]$Item)

    return (($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
}

# ------------------------------------------------------------
# Validate source folder
# ------------------------------------------------------------

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    throw "SourceRoot is blank. Edit the SourceRoot variable at the top of the script."
}

$sourceItem = Get-Item -LiteralPath $SourceRoot -ErrorAction Stop

if (-not $sourceItem.PSIsContainer) {
    $sourceItem = $sourceItem.Directory
}

$sourceRootFull = $sourceItem.FullName
$projectName = Get-SafeFileName -Name $sourceItem.Name
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"

# ------------------------------------------------------------
# Build output ZIP path
# ------------------------------------------------------------

if ([string]::IsNullOrWhiteSpace($OutputZipPath)) {
    $desktopPath = [Environment]::GetFolderPath("Desktop")

    if ([string]::IsNullOrWhiteSpace($desktopPath) -or -not (Test-Path -LiteralPath $desktopPath)) {
        $desktopPath = $sourceRootFull
    }

    $OutputZipPath = Join-Path $desktopPath "$projectName-For-ChatGPT-Analysis-$timestamp.zip"
}
else {
    $extension = [System.IO.Path]::GetExtension($OutputZipPath)

    if ((Test-Path -LiteralPath $OutputZipPath -PathType Container) -or $extension -ne ".zip") {
        $OutputZipPath = Join-Path $OutputZipPath "$projectName-For-ChatGPT-Analysis-$timestamp.zip"
    }
}

$outputParent = Split-Path -Parent $OutputZipPath

if (-not (Test-Path -LiteralPath $outputParent)) {
    New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
}

# ------------------------------------------------------------
# Build exclusion sets
# ------------------------------------------------------------

$excludedDirectoryNames = New-CaseInsensitiveHashSet -Values $DefaultExcludedDirectoryNames
$excludedFileExtensions = New-CaseInsensitiveHashSet -Values $DefaultExcludedFileExtensions

if ($IncludeGit) {
    [void]$excludedDirectoryNames.Remove(".git")
}

if ($IncludeVsFolder) {
    [void]$excludedDirectoryNames.Remove(".vs")
}

if ($IncludePackages) {
    [void]$excludedDirectoryNames.Remove("packages")
    [void]$excludedDirectoryNames.Remove("node_modules")
}

if ($IncludeBinObj) {
    foreach ($name in @("bin", "obj", "Debug", "Release", "x64", "x86", "arm64", "AnyCPU")) {
        [void]$excludedDirectoryNames.Remove($name)
    }
}

if ($IncludeLogs) {
    [void]$excludedFileExtensions.Remove(".log")
}

if ($IncludeLargeMedia) {
    foreach ($extension in @(".mp4", ".mkv", ".mov", ".avi", ".wmv", ".mp3", ".wav", ".flac", ".aac")) {
        [void]$excludedFileExtensions.Remove($extension)
    }
}

$selectedFiles = New-Object "System.Collections.Generic.List[System.IO.FileInfo]"
$skippedDirectories = New-Object "System.Collections.Generic.List[string]"
$skippedFiles = New-Object "System.Collections.Generic.List[string]"
$readErrors = New-Object "System.Collections.Generic.List[string]"

function Should-SkipDirectory {
    param([System.IO.DirectoryInfo]$Directory)

    $relativePath = Get-RelativePathSafe -BasePath $sourceRootFull -FullPath $Directory.FullName

    if (Test-IsReparsePoint -Item $Directory) {
        $script:skippedDirectories.Add("$relativePath  [reparse point / link]") | Out-Null
        return $true
    }

    if ($script:excludedDirectoryNames.Contains($Directory.Name)) {
        $script:skippedDirectories.Add("$relativePath  [excluded directory name]") | Out-Null
        return $true
    }

    return $false
}

function Should-SkipFile {
    param([System.IO.FileInfo]$File)

    $relativePath = Get-RelativePathSafe -BasePath $sourceRootFull -FullPath $File.FullName

    if (Test-IsReparsePoint -Item $File) {
        $script:skippedFiles.Add("$relativePath  [reparse point / link]") | Out-Null
        return $true
    }

    if ($script:excludedFileExtensions.Contains($File.Extension)) {
        $script:skippedFiles.Add("$relativePath  [excluded extension: $($File.Extension)]") | Out-Null
        return $true
    }

    foreach ($pattern in $script:ExcludedFilePatterns) {
        if ($File.Name -like $pattern) {
            $script:skippedFiles.Add("$relativePath  [excluded pattern: $pattern]") | Out-Null
            return $true
        }
    }

    $maxBytes = [int64]$MaxFileSizeMB * 1MB

    if ($File.Length -gt $maxBytes) {
        $sizeMB = [math]::Round($File.Length / 1MB, 2)
        $script:skippedFiles.Add("$relativePath  [larger than $MaxFileSizeMB MB: $sizeMB MB]") | Out-Null
        return $true
    }

    return $false
}

function Scan-Directory {
    param([System.IO.DirectoryInfo]$Directory)

    try {
        $children = Get-ChildItem -LiteralPath $Directory.FullName -Force -ErrorAction Stop
    }
    catch {
        $relativePath = Get-RelativePathSafe -BasePath $sourceRootFull -FullPath $Directory.FullName
        $script:readErrors.Add("$relativePath  [could not read: $($_.Exception.Message)]") | Out-Null
        return
    }

    foreach ($child in $children) {
        if ($child.PSIsContainer) {
            if (-not (Should-SkipDirectory -Directory $child)) {
                Scan-Directory -Directory $child
            }
        }
        else {
            if (-not (Should-SkipFile -File $child)) {
                $script:selectedFiles.Add($child) | Out-Null
            }
        }
    }
}

# ------------------------------------------------------------
# Scan project
# ------------------------------------------------------------

Write-Info "Source folder:"
Write-Host "  $sourceRootFull"
Write-Host ""

Write-Info "Scanning project files..."

Scan-Directory -Directory $sourceItem

if ($selectedFiles.Count -eq 0) {
    throw "No files were selected. Check SourceRoot or the exclusion settings at the top of the script."
}

# ------------------------------------------------------------
# Stage clean copy
# ------------------------------------------------------------

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) "VSProjectPackage_$timestamp"
$stagingProjectRoot = Join-Path $stagingRoot $projectName

try {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $stagingProjectRoot -Force | Out-Null

    Write-Info "Copying selected files into clean staging folder..."

    foreach ($file in $selectedFiles) {
        $relativePath = Get-RelativePathSafe -BasePath $sourceRootFull -FullPath $file.FullName
        $destinationPath = Join-Path $stagingProjectRoot $relativePath
        $destinationDirectory = Split-Path -Parent $destinationPath

        if (-not (Test-Path -LiteralPath $destinationDirectory)) {
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        }

        Copy-Item -LiteralPath $file.FullName -Destination $destinationPath -Force
    }

    # --------------------------------------------------------
    # Create package manifest
    # --------------------------------------------------------

    $manifestPath = Join-Path $stagingProjectRoot "_PACKAGE_MANIFEST_FOR_CHATGPT.txt"

    $manifestLines = New-Object "System.Collections.Generic.List[string]"

    $manifestLines.Add("Visual Studio Project Package Manifest") | Out-Null
    $manifestLines.Add("Created: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')") | Out-Null
    $manifestLines.Add("SourceRoot: $sourceRootFull") | Out-Null
    $manifestLines.Add("") | Out-Null
    $manifestLines.Add("Included files: $($selectedFiles.Count)") | Out-Null
    $manifestLines.Add("Skipped directories: $($skippedDirectories.Count)") | Out-Null
    $manifestLines.Add("Skipped files: $($skippedFiles.Count)") | Out-Null
    $manifestLines.Add("Read errors: $($readErrors.Count)") | Out-Null
    $manifestLines.Add("Max file size: $MaxFileSizeMB MB") | Out-Null
    $manifestLines.Add("") | Out-Null

    $manifestLines.Add("User settings used:") | Out-Null
    $manifestLines.Add("IncludeBinObj: $IncludeBinObj") | Out-Null
    $manifestLines.Add("IncludeGit: $IncludeGit") | Out-Null
    $manifestLines.Add("IncludeVsFolder: $IncludeVsFolder") | Out-Null
    $manifestLines.Add("IncludePackages: $IncludePackages") | Out-Null
    $manifestLines.Add("IncludeLogs: $IncludeLogs") | Out-Null
    $manifestLines.Add("IncludeLargeMedia: $IncludeLargeMedia") | Out-Null
    $manifestLines.Add("") | Out-Null

    $manifestLines.Add("Important exclusions used by default:") | Out-Null
    $manifestLines.Add("- .vs, .git, bin, obj, Debug, Release, packages, node_modules") | Out-Null
    $manifestLines.Add("- AppPackages, BundleArtifacts, PackageLayout, TestResults, publish folders") | Out-Null
    $manifestLines.Add("- Compiled binaries such as .exe, .dll, .pdb, .msix, .appx") | Out-Null
    $manifestLines.Add("- Private/local files such as .user, .suo, .pfx, .key, .pem, .env") | Out-Null
    $manifestLines.Add("") | Out-Null

    $manifestLines.Add("Skipped directories:") | Out-Null

    foreach ($entry in $skippedDirectories) {
        $manifestLines.Add("  $entry") | Out-Null
    }

    $manifestLines.Add("") | Out-Null
    $manifestLines.Add("Skipped files:") | Out-Null

    foreach ($entry in $skippedFiles) {
        $manifestLines.Add("  $entry") | Out-Null
    }

    if ($readErrors.Count -gt 0) {
        $manifestLines.Add("") | Out-Null
        $manifestLines.Add("Read errors:") | Out-Null

        foreach ($entry in $readErrors) {
            $manifestLines.Add("  $entry") | Out-Null
        }
    }

    Set-Content -LiteralPath $manifestPath -Value $manifestLines -Encoding UTF8

    # --------------------------------------------------------
    # Create ZIP
    # --------------------------------------------------------

    if (Test-Path -LiteralPath $OutputZipPath) {
        Remove-Item -LiteralPath $OutputZipPath -Force
    }

    Write-Info "Creating ZIP..."

    Compress-Archive `
        -LiteralPath $stagingProjectRoot `
        -DestinationPath $OutputZipPath `
        -CompressionLevel Optimal `
        -Force

    $zipItem = Get-Item -LiteralPath $OutputZipPath
    $zipSizeMB = [math]::Round($zipItem.Length / 1MB, 2)

    Write-Host ""
    Write-Good "Package created successfully."
    Write-Host ""
    Write-Host "ZIP path: $OutputZipPath"
    Write-Host "ZIP size: $zipSizeMB MB"
    Write-Host "Included files: $($selectedFiles.Count)"
    Write-Host "Skipped directories: $($skippedDirectories.Count)"
    Write-Host "Skipped files: $($skippedFiles.Count)"

    if ($readErrors.Count -gt 0) {
        Write-Warn "Some folders could not be read. See _PACKAGE_MANIFEST_FOR_CHATGPT.txt inside the ZIP."
    }

    if ($OpenOutputFolder) {
        Start-Process explorer.exe "/select,`"$OutputZipPath`""
    }
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}