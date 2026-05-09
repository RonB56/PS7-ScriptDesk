# BUILD_AND_PACKAGING

## Expected build shape

Observed primary solution file:

- `PowerShellStudio.slnx`

Observed app entry project:

- `PowerShellStudio.Shell/PowerShellStudio.Shell.csproj`

Observed packaging project:

- `PowerShellStudio.Package/PowerShellStudio.Package.wapproj`

The shell project is the WPF desktop application. The packaging project wraps that shell into an MSIX/Desktop Bridge package.

## Platform and tooling cautions

- The shell targets `.NET 10` with `net10.0-windows`.
- The shell enables both `UseWPF` and `UseWindowsForms`.
- The packaging project uses Desktop Bridge/WAP targets and Windows SDK build tools.
- Full build and package verification is expected to require native Windows tooling.
- This repo should not be treated as fully verifiable from a non-Windows environment.

## Build/package workflow visible in the repo

No custom repo-wide build script was found during this pass. The repo appears to rely on standard Visual Studio/MSBuild project and solution builds.

Observed build/package layers:

1. Build the app projects, especially `PowerShellStudio.Shell`.
2. Build/package `PowerShellStudio.Package` when producing an MSIX.
3. Use the generated package output plus the installer helper scripts under `PowerShellStudio.Package/Installer/`.

Supporting packaging-related files seen at the repo root:

- `How to create app packages.txt`
- `PackageRestore_Rebuild_Log.txt`
- `NuGet.config`

Those files suggest packaging and restore have needed manual attention before, but they were not executed or validated in this documentation pass.

## Packaging project details

`PowerShellStudio.Package/PowerShellStudio.Package.wapproj` currently shows:

- Entry point project: `..\PowerShellStudio.Shell\PowerShellStudio.Shell.csproj`
- Target platform version: `10.0.26100.0`
- Minimum platform version: `10.0.19041.0`
- Bundle platform currently set to `x64`
- App bundle generation enabled
- Package signing enabled
- `Microsoft.Windows.SDK.BuildTools` package reference

`PowerShellStudio.Package/Package.appxmanifest` currently shows:

- Display name: `PS7 ScriptDesk`
- Publisher display name: `rbarn`
- `runFullTrust` capability
- `internetClient` capability

## Restore and packaging risks visible in the repo

### WAP/Desktop Bridge dependency risk

The packaging project imports Desktop Bridge props/targets. Packaging will depend on Windows-specific build components being installed correctly.

### Avalonia/AvalonEdit compatibility warning suppression

The packaging project suppresses `NU1701` and `NU1702`, with an inline note that this is tied to the WPF shell's `AvalonEdit` dependency. That is a known packaging compatibility seam and should not be removed casually.

### Certificate/signing risk

The packaging project references:

- `PowerShellStudio.Package_TemporaryKey.pfx`
- a fixed `PackageCertificateThumbprint`

This means packaging on another machine may fail unless the expected certificate/private key setup exists there.

### Installer script behavior

`PowerShellStudio.Package/Installer/Install.ps1`:

- recursively searches for the best package artifact
- can import a `.cer` into `Cert:\CurrentUser\TrustedPeople`
- installs with `Add-AppxPackage`

`Install.cmd` launches that script with PowerShell `-ExecutionPolicy Bypass`.

### Version/branding mismatch risk

The shell project currently reports `1.2.4`, while the package manifest currently reports `1.0.37.0`. That may be intentional, but it is a release-risk area because visible app versioning and package versioning can diverge.

## What not to assume

- Do not claim packaging is healthy unless a real Windows package build succeeds.
- Do not assume the installer scripts are optional; the WAP project explicitly copies custom installer scripts into generated package folders.
- Do not assume `PowerShellStudio.UI/PowerShellStudio.slnx` is sufficient for packaging work; it does not include the packaging project.
