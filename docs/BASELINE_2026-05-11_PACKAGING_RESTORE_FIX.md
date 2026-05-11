# Baseline Notes - 2026-05-11 Packaging Restore Fix

## Purpose

This baseline fixes the Windows App Packaging Project restore failure where referenced projects produced only plain `net10.0` NuGet assets instead of `net10.0/win-x64` assets.

## Root cause

`PowerShellStudio.Shell.csproj` already declared `win-x64`, but the referenced class-library projects did not. When the packaging project asked MSBuild/NuGet for `net10.0/win-x64`, several referenced projects still had stale or incomplete `obj/project.assets.json` files that lacked that target.

## Files intentionally changed

- `Directory.Build.props`
- `PowerShellStudio.Application/PowerShellStudio.Application.csproj`
- `PowerShellStudio.Domain/PowerShellStudio.Domain.csproj`
- `PowerShellStudio.Editor/PowerShellStudio.Editor.csproj`
- `PowerShellStudio.Infrastructure/PowerShellStudio.Infrastructure.csproj`
- `PowerShellStudio.PowerShell/PowerShellStudio.PowerShell.csproj`
- `PowerShellStudio.UI/PowerShellStudio.UI.csproj`
- `PowerShellStudio.Shell/App.xaml.cs`
- `PowerShellStudio.Shell/Editor/EditorMetadataBuilderHost.cs`
- `PowerShellStudio.Shell/Debugger/PsesDebugSession.cs`
- `PowerShellStudio.Shell/MainWindow.xaml.cs`

## What changed

- Added `RuntimeIdentifiers` set to `win-x64` to the referenced class-library projects.
- Added a central `Directory.Build.props` safety net so future package restores consistently know about `win-x64`.
- Fixed the nullable warnings listed in the packaging output with narrow, behavior-preserving changes.

## Recommended clean rebuild/package procedure

Run from Developer PowerShell for Visual Studio, not WSL:

```powershell
cd C:\Users\rbarn\source\repos\PowerShellStudio
dotnet build-server shutdown
Get-ChildItem -Directory -Recurse -Force -Include bin,obj | Remove-Item -Recurse -Force

dotnet restore .\PowerShellStudio.Shell\PowerShellStudio.Shell.csproj --runtime win-x64 /p:Configuration=Release
msbuild .\PowerShellStudio.Package\PowerShellStudio.Package.wapproj /t:Restore /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64
```

Then reopen Visual Studio, set `Release | x64`, and create the App Packages again.

## Validation note

This baseline was edited in a Linux assistant environment where .NET 10 / Visual Studio Windows Packaging targets are not available, so local compile/package verification must be performed on your Windows machine.
