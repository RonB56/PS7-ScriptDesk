using PS7ScriptDesk.PowerShell.Services;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Tests;

public sealed class RecoverableArtifactTransactionTests
{
    [Fact]
    public void R4R1_T05_MissingAuthorizationCapturesOnlyAbsenceAndRejectsLaterOccupancy()
    {
        using var workspace = new TemporaryWorkspace();
        var absentDestination = Path.Combine(workspace.Root, "new-output.exe");
        Assert.True(ExeExportReplacementAuthorization.TryCaptureAbsentDestination(absentDestination, out var authorization));
        Assert.NotNull(authorization);
        File.WriteAllText(absentDestination, "unexpected occupant");
        Assert.False(authorization!.Matches(absentDestination));

        var occupiedDestination = workspace.File("occupied-output.exe", "existing output");
        Assert.False(ExeExportReplacementAuthorization.TryCaptureAbsentDestination(occupiedDestination, out var rejectedAuthorization));
        Assert.Null(rejectedAuthorization);
        Assert.Equal("existing output", File.ReadAllText(occupiedDestination));
    }

    [Fact]
    public async Task R4R1_T05_LegacyAndConfiguredExePathsRefuseExistingDestinationWithoutAuthorization()
    {
        using var workspace = new TemporaryWorkspace();
        var source = workspace.File("source.ps1", "Write-Output 'safe test'");
        var runtime = new PowerShellRuntimeInfo(
            "PowerShell 7", "Core", "7.6", new Version(7, 6), "x64", "missing-pwsh.exe", "test",
            isPowerShell7OrLater: true, isWindowsPowerShell: false, isPreferred: true);
        var legacyDestination = workspace.File("legacy.exe", "preserve legacy");
        var legacy = await new ExeExportService().ExportScriptAsExeAsync(
            new ExeExportRequest(source, "Write-Output 'safe test'", legacyDestination, runtime));
        Assert.False(legacy.Succeeded);
        Assert.Equal("preserve legacy", File.ReadAllText(legacyDestination));

        var configuredDestination = workspace.File("configured.exe", "preserve configured");
        var configuration = ExeExportConfiguration.CreatePreset(ExeExportPreset.PortableWindowsExe, "R4R1");
        configuration.OutputExecutablePath = configuredDestination;
        var configured = await new ExeExportService().ExportScriptAsExeAsync(
            new ExeExportRequest(source, "Write-Output 'safe test'", configuredDestination, runtime, configuration));
        Assert.False(configured.Succeeded);
        Assert.Equal("preserve configured", File.ReadAllText(configuredDestination));
    }

    [Fact]
    public async Task FailureBeforeCommit_PreservesPreviousFileHash()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.File("output.exe", "previous payload");
        var staged = workspace.File("staged.exe", "new payload");
        var before = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(destination)));
        RecoverableArtifactTransaction.FaultInjector = point => point == "before-commit" ? new IOException("injected pre-commit failure") : null;
        try
        {
            var result = await RecoverableArtifactTransaction.CommitFileAsync(staged, destination, File.Exists, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.False(result.RecoveryRequired);
            var after = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(destination)));
            Assert.Equal(before, after);
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task DirectoryCommitFailureAfterBackup_RestoresPreviousArtifact()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.Directory("output");
        var staged = workspace.Directory("stage");
        File.WriteAllText(Path.Combine(destination, "artifact.txt"), "previous");
        File.WriteAllText(Path.Combine(staged, "artifact.txt"), "replacement");
        RecoverableArtifactTransaction.FaultInjector = point => point == "after-backup" ? new IOException("injected copy failure") : null;
        try
        {
            var result = await RecoverableArtifactTransaction.CommitDirectoryAsync(staged, destination, Directory.Exists, _ => true, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.False(result.RecoveryRequired);
            Assert.Equal("previous", File.ReadAllText(Path.Combine(destination, "artifact.txt")));
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task DirectoryCommitFailureAfterReplacementInstall_RestoresAndVerifiesOriginalTree()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.Directory("output");
        var staged = workspace.Directory("stage");
        File.WriteAllText(Path.Combine(destination, "artifact.txt"), "previous");
        Directory.CreateDirectory(Path.Combine(destination, "empty-owner-folder"));
        File.WriteAllText(Path.Combine(staged, "artifact.txt"), "replacement");
        RecoverableArtifactTransaction.FaultInjector = point => point == "after-commit" ? new IOException("injected post-install failure") : null;
        try
        {
            var result = await RecoverableArtifactTransaction.CommitDirectoryAsync(staged, destination, Directory.Exists, _ => true, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.False(result.RecoveryRequired);
            Assert.Equal("previous", File.ReadAllText(Path.Combine(destination, "artifact.txt")));
            Assert.True(Directory.Exists(Path.Combine(destination, "empty-owner-folder")));
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task FileCommitFailureAfterReplacementInstall_RestoresAndVerifiesOriginal()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.File("output.exe", "previous");
        var staged = workspace.File("staged.exe", "replacement");
        RecoverableArtifactTransaction.FaultInjector = point => point == "after-commit" ? new IOException("injected post-install failure") : null;
        try
        {
            var result = await RecoverableArtifactTransaction.CommitFileAsync(staged, destination, File.Exists, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.False(result.RecoveryRequired);
            Assert.Equal("previous", File.ReadAllText(destination));
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task FileCommitFailureAfterBackup_RestoresAndVerifiesOriginal()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.File("output.exe", "previous");
        var staged = workspace.File("staged.exe", "replacement");
        RecoverableArtifactTransaction.FaultInjector = point => point == "after-backup" ? new IOException("injected after backup") : null;
        try
        {
            var result = await RecoverableArtifactTransaction.CommitFileAsync(staged, destination, File.Exists, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.False(result.RecoveryRequired);
            Assert.Equal("previous", File.ReadAllText(destination));
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task FileRollback_PreservesUnexpectedSameContentFile()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = Path.Combine(workspace.Root, "output.exe");
        var staged = workspace.File("staged.exe", "same bytes");
        RecoverableArtifactTransaction.FaultInjector = point =>
        {
            if (point == "after-backup")
            {
                File.WriteAllText(destination, "same bytes");
                return new IOException("injected before replacement install");
            }
            return null;
        };
        try
        {
            var result = await RecoverableArtifactTransaction.CommitFileAsync(staged, destination, File.Exists, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.True(result.RecoveryRequired);
            Assert.Equal("same bytes", File.ReadAllText(destination));
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task FileRollback_MissingExpectedBackupRequiresRecovery()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.File("output.exe", "previous");
        var staged = workspace.File("staged.exe", "replacement");
        RecoverableArtifactTransaction.FaultInjector = point =>
        {
            if (point == "after-backup")
            {
                foreach (var backup in Directory.EnumerateFiles(workspace.Root, "*.ps7-backup-*")) File.Delete(backup);
                return new IOException("injected after deleting expected backup");
            }
            return null;
        };
        try
        {
            var result = await RecoverableArtifactTransaction.CommitFileAsync(staged, destination, File.Exists, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.True(result.RecoveryRequired);
            Assert.False(File.Exists(destination));
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task DirectoryRollback_PreservesUnexpectedSameContentDirectory()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.Directory("output");
        var staged = workspace.Directory("stage");
        File.WriteAllText(Path.Combine(destination, "artifact.txt"), "previous");
        File.WriteAllText(Path.Combine(staged, "artifact.txt"), "same bytes");
        RecoverableArtifactTransaction.FaultInjector = point =>
        {
            if (point == "after-backup")
            {
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "artifact.txt"), "same bytes");
                return new IOException("injected before directory install");
            }
            return null;
        };
        try
        {
            var result = await RecoverableArtifactTransaction.CommitDirectoryAsync(staged, destination, Directory.Exists, _ => true, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.True(result.RecoveryRequired);
            Assert.Equal("same bytes", File.ReadAllText(Path.Combine(destination, "artifact.txt")));
            Assert.NotNull(result.BackupPath);
            Assert.Equal("previous", File.ReadAllText(Path.Combine(result.BackupPath!, "artifact.txt")));
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task OriginallyAbsentFileDestination_PreservesExternalArtifactAndRequiresRecovery()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = Path.Combine(workspace.Root, "new-output.exe");
        var staged = workspace.File("staged.exe", "replacement");
        RecoverableArtifactTransaction.FaultInjector = point =>
        {
            if (point == "after-backup")
            {
                File.WriteAllText(destination, "external");
                return new IOException("injected external creation");
            }
            return null;
        };
        try
        {
            var result = await RecoverableArtifactTransaction.CommitFileAsync(staged, destination, File.Exists, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.True(result.RecoveryRequired);
            Assert.Equal("external", File.ReadAllText(destination));
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task DirectoryCommitRollbackFailure_PreservesBackupLocation()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.Directory("output");
        var staged = workspace.Directory("stage");
        File.WriteAllText(Path.Combine(destination, "artifact.txt"), "previous");
        File.WriteAllText(Path.Combine(staged, "artifact.txt"), "replacement");
        RecoverableArtifactTransaction.FaultInjector = point => point switch
        {
            "after-backup" => new IOException("injected commit failure"),
            "before-rollback" => new IOException("injected rollback failure"),
            _ => null
        };
        try
        {
            var result = await RecoverableArtifactTransaction.CommitDirectoryAsync(staged, destination, Directory.Exists, _ => true, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.True(result.RecoveryRequired);
            Assert.NotNull(result.BackupPath);
            Assert.True(File.Exists(Path.Combine(result.BackupPath!, "artifact.txt")));
            Assert.Contains(result.BackupPath!, result.Message, StringComparison.Ordinal);
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task CancellationBeforeCommit_PreservesPreviousFile()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.File("output.exe", "previous");
        var staged = workspace.File("staged.exe", "new");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecoverableArtifactTransaction.CommitFileAsync(staged, destination, File.Exists, cancellation.Token));
        Assert.Equal("previous", File.ReadAllText(destination));
    }

    [Fact]
    public async Task LateCancellation_AfterCommitBeginsReportsCompletedReplacement()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.File("output.exe", "previous");
        var staged = workspace.File("staged.exe", "new");
        using var cancellation = new CancellationTokenSource();
        RecoverableArtifactTransaction.FaultInjector = point =>
        {
            if (point == "after-backup") cancellation.Cancel();
            return null;
        };
        try
        {
            var result = await RecoverableArtifactTransaction.CommitFileAsync(staged, destination, File.Exists, cancellation.Token);
            Assert.True(result.Succeeded);
            Assert.Equal("new", File.ReadAllText(destination));
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task DestinationChangeBeforeCommit_FailsWithoutOverwritingIt()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.File("output.exe", "previous");
        var staged = workspace.File("staged.exe", "new");
        RecoverableArtifactTransaction.FaultInjector = point =>
        {
            if (point == "before-commit") File.WriteAllText(destination, "user change");
            return null;
        };
        try
        {
            var result = await RecoverableArtifactTransaction.CommitFileAsync(staged, destination, File.Exists, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.Equal("user change", File.ReadAllText(destination));
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task FileSetCommit_RefusesUnownedCollisionAndPreservesUserFile()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.Directory("application");
        var staged = workspace.Directory("stage");
        File.WriteAllText(Path.Combine(destination, "App.exe"), "user executable");
        File.WriteAllText(Path.Combine(staged, "App.exe"), "generated executable");

        var result = await RecoverableArtifactTransaction.CommitFileSetAsync(staged, destination, _ => true, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("user executable", File.ReadAllText(Path.Combine(destination, "App.exe")));
    }

    [Fact]
    public async Task FileSetCommit_ReplacesOwnedFilesAndPreservesUnrelatedFiles()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.Directory("application");
        var firstStage = workspace.Directory("first-stage");
        File.WriteAllText(Path.Combine(firstStage, "App.exe"), "old app");
        Directory.CreateDirectory(Path.Combine(firstStage, "runtimes", "win-x64"));
        File.WriteAllText(Path.Combine(firstStage, "runtimes", "win-x64", "helper.dll"), "old helper");
        var first = await RecoverableArtifactTransaction.CommitFileSetAsync(firstStage, destination, _ => true, CancellationToken.None);
        Assert.True(first.Succeeded, first.Message);
        File.WriteAllText(Path.Combine(destination, "owner-notes.txt"), "keep");

        var secondStage = workspace.Directory("second-stage");
        File.WriteAllText(Path.Combine(secondStage, "App.exe"), "new app");
        File.WriteAllText(Path.Combine(secondStage, "App.dll"), "app companion");
        var second = await RecoverableArtifactTransaction.CommitFileSetAsync(secondStage, destination, _ => true, CancellationToken.None);

        Assert.True(second.Succeeded, second.Message);
        Assert.Equal("new app", File.ReadAllText(Path.Combine(destination, "App.exe")));
        Assert.Equal("app companion", File.ReadAllText(Path.Combine(destination, "App.dll")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(destination, "owner-notes.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "runtimes", "win-x64", "helper.dll")));
    }

    [Fact]
    public async Task FileSetFailureAfterManifestInstall_RestoresPreviousFilesAndManifestByteForByte()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.Directory("application");
        var initial = workspace.Directory("initial");
        File.WriteAllText(Path.Combine(initial, "App.exe"), "old exe");
        File.WriteAllText(Path.Combine(initial, "App.dll"), "old companion");
        Assert.True((await RecoverableArtifactTransaction.CommitFileSetAsync(initial, destination, _ => true, CancellationToken.None)).Succeeded);
        File.WriteAllText(Path.Combine(destination, "owner.txt"), "unrelated");
        var before = Inventory(destination);
        var staged = workspace.Directory("replacement");
        File.WriteAllText(Path.Combine(staged, "App.exe"), "new exe");
        File.WriteAllText(Path.Combine(staged, "App.dll"), "new companion");
        RecoverableArtifactTransaction.FaultInjector = point => point == "after-manifest-install" ? new IOException("injected late validation failure") : null;
        try
        {
            var result = await RecoverableArtifactTransaction.CommitFileSetAsync(staged, destination, _ => true, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.False(result.RecoveryRequired);
            Assert.Equal(before, Inventory(destination));
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task FileSetRollback_VerifiesOriginalStateBeforeFinalBackupCleanup()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.Directory("application");
        var initial = workspace.Directory("initial");
        File.WriteAllText(Path.Combine(initial, "App.exe"), "old exe");
        File.WriteAllText(Path.Combine(initial, "App.dll"), "old companion");
        Assert.True((await RecoverableArtifactTransaction.CommitFileSetAsync(initial, destination, _ => true, CancellationToken.None)).Succeeded);
        var before = Inventory(destination);
        var staged = workspace.Directory("replacement");
        File.WriteAllText(Path.Combine(staged, "App.exe"), "new exe");
        File.WriteAllText(Path.Combine(staged, "App.dll"), "new companion");
        var cleanupAttempted = false;
        RecoverableArtifactTransaction.FaultInjector = point => point switch
        {
            "after-manifest-install" => new IOException("injected commit failure"),
            "before-final-backup-cleanup" => SetFlagAndFail(() => cleanupAttempted = true),
            _ => null
        };
        try
        {
            var result = await RecoverableArtifactTransaction.CommitFileSetAsync(staged, destination, _ => true, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.False(result.RecoveryRequired);
            Assert.True(cleanupAttempted);
            Assert.Equal(before, Inventory(destination));
            Assert.NotNull(result.BackupPath);
            Assert.True(Directory.Exists(result.BackupPath));
            Assert.Contains("restored and verified", result.Message, StringComparison.Ordinal);
            Assert.Contains("cleanup failed", result.Message, StringComparison.Ordinal);
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task FileSetRollback_FinalVerificationFailureRequiresRecoveryWithoutFalseBackupPath()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.Directory("application");
        var initial = workspace.Directory("initial");
        File.WriteAllText(Path.Combine(initial, "App.exe"), "old exe");
        File.WriteAllText(Path.Combine(initial, "App.dll"), "old companion");
        Assert.True((await RecoverableArtifactTransaction.CommitFileSetAsync(initial, destination, _ => true, CancellationToken.None)).Succeeded);
        var staged = workspace.Directory("replacement");
        File.WriteAllText(Path.Combine(staged, "App.exe"), "new exe");
        File.WriteAllText(Path.Combine(staged, "App.dll"), "new companion");
        RecoverableArtifactTransaction.FaultInjector = point =>
        {
            if (point == "after-manifest-install") return new IOException("injected commit failure");
            if (point == "before-final-rollback-verification")
                File.WriteAllText(Path.Combine(destination, "App.exe"), "external modification");
            return null;
        };
        try
        {
            var result = await RecoverableArtifactTransaction.CommitFileSetAsync(staged, destination, _ => true, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.True(result.RecoveryRequired);
            Assert.Null(result.BackupPath);
            Assert.Contains("No usable recovery copy remains", result.Message, StringComparison.Ordinal);
            Assert.Contains("original state was not verified", result.Message, StringComparison.Ordinal);
            Assert.Equal("external modification", File.ReadAllText(Path.Combine(destination, "App.exe")));
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task FileSetRollback_ReportsOnlyRecoveryMaterialThatStillExists()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.Directory("application");
        var initial = workspace.Directory("initial");
        File.WriteAllText(Path.Combine(initial, "App.exe"), "old exe");
        File.WriteAllText(Path.Combine(initial, "App.dll"), "old companion");
        Assert.True((await RecoverableArtifactTransaction.CommitFileSetAsync(initial, destination, _ => true, CancellationToken.None)).Succeeded);
        var staged = workspace.Directory("replacement");
        File.WriteAllText(Path.Combine(staged, "App.exe"), "new exe");
        File.WriteAllText(Path.Combine(staged, "App.dll"), "new companion");
        RecoverableArtifactTransaction.FaultInjector = point => point switch
        {
            "after-backup" => new IOException("injected commit failure"),
            "before-rollback" => new IOException("injected rollback failure"),
            _ => null
        };
        try
        {
            var result = await RecoverableArtifactTransaction.CommitFileSetAsync(staged, destination, _ => true, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.True(result.RecoveryRequired);
            Assert.NotNull(result.BackupPath);
            Assert.True(Directory.EnumerateFiles(result.BackupPath!, "*", SearchOption.AllDirectories).Any());
            Assert.Contains(result.BackupPath!, result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("No usable recovery copy remains", result.Message, StringComparison.Ordinal);
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task R4_T06_RecoveryInspectionFailurePreservesOriginalRollbackContext()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.Directory("application");
        var initial = workspace.Directory("initial");
        File.WriteAllText(Path.Combine(initial, "app.exe"), "original");
        Assert.True((await RecoverableArtifactTransaction.CommitFileSetAsync(initial, destination, _ => true, CancellationToken.None)).Succeeded);
        var staged = workspace.Directory("replacement");
        File.WriteAllText(Path.Combine(staged, "app.exe"), "replacement");
        RecoverableArtifactTransaction.FaultInjector = point => point switch
        {
            "after-backup" => new IOException("injected commit failure"),
            "before-rollback" => new IOException("injected original rollback failure"),
            "before-recovery-material-inspection" => new IOException("injected enumeration failure"),
            _ => null
        };
        try
        {
            var result = await RecoverableArtifactTransaction.CommitFileSetAsync(staged, destination, _ => true, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.True(result.RecoveryRequired);
            Assert.Null(result.BackupPath);
            Assert.Contains("injected commit failure", result.Message, StringComparison.Ordinal);
            Assert.Contains("injected original rollback failure", result.Message, StringComparison.Ordinal);
            Assert.Contains("could not be inspected", result.Message, StringComparison.Ordinal);
            Assert.Contains("injected enumeration failure", result.Message, StringComparison.Ordinal);
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task R4_T07_SingleFileAuthorizationAllowsUnchangedDestinationAndRejectsChangedOrOccupiedState()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.File("authorized.exe", "old executable");
        var authorization = ExeExportReplacementAuthorization.Capture(destination);
        var staged = workspace.File("staged.exe", "new executable");
        var committed = await RecoverableArtifactTransaction.CommitFileAsync(
            staged, destination, File.Exists, CancellationToken.None, authorization);
        Assert.True(committed.Succeeded, committed.Message);
        Assert.Equal("new executable", File.ReadAllText(destination));

        var changedAuthorization = ExeExportReplacementAuthorization.Capture(destination);
        File.WriteAllText(destination, "changed after prompt");
        var changedStage = workspace.File("changed-stage.exe", "should not replace");
        var refusedChanged = await RecoverableArtifactTransaction.CommitFileAsync(
            changedStage, destination, File.Exists, CancellationToken.None, changedAuthorization);
        Assert.False(refusedChanged.Succeeded);
        Assert.Equal("changed after prompt", File.ReadAllText(destination));

        var absentDestination = Path.Combine(workspace.Root, "initially-absent.exe");
        var absentAuthorization = ExeExportReplacementAuthorization.Capture(absentDestination);
        File.WriteAllText(absentDestination, "appeared after prompt");
        var absentStage = workspace.File("absent-stage.exe", "should not overwrite");
        var refusedOccupied = await RecoverableArtifactTransaction.CommitFileAsync(
            absentStage, absentDestination, File.Exists, CancellationToken.None, absentAuthorization);
        Assert.False(refusedOccupied.Succeeded);
        Assert.Equal("appeared after prompt", File.ReadAllText(absentDestination));
    }

    [Fact]
    public async Task FileSetFailureAfterFirstBackup_DoesNotDeleteUnmovedSameHashOriginal()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.Directory("application");
        var initial = workspace.Directory("initial");
        File.WriteAllText(Path.Combine(initial, "a.exe"), "old a");
        File.WriteAllText(Path.Combine(initial, "b.dll"), "same bytes");
        Assert.True((await RecoverableArtifactTransaction.CommitFileSetAsync(initial, destination, _ => true, CancellationToken.None)).Succeeded);
        var before = Inventory(destination);
        var staged = workspace.Directory("replacement");
        File.WriteAllText(Path.Combine(staged, "a.exe"), "new a");
        File.WriteAllText(Path.Combine(staged, "b.dll"), "same bytes");
        RecoverableArtifactTransaction.FaultInjector = point => point.StartsWith("after-backup-file:", StringComparison.OrdinalIgnoreCase) ? new IOException("injected backup interruption") : null;
        try
        {
            var result = await RecoverableArtifactTransaction.CommitFileSetAsync(staged, destination, _ => true, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.False(result.RecoveryRequired);
            Assert.Equal(before, Inventory(destination));
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task FileSetFailureDuringInstall_CleansPartialOperationOwnedTemporaryFile()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.Directory("application");
        var initial = workspace.Directory("initial");
        File.WriteAllText(Path.Combine(initial, "App.exe"), "old");
        Assert.True((await RecoverableArtifactTransaction.CommitFileSetAsync(initial, destination, _ => true, CancellationToken.None)).Succeeded);
        var before = Inventory(destination);
        var staged = workspace.Directory("replacement");
        File.WriteAllText(Path.Combine(staged, "App.exe"), "new");
        RecoverableArtifactTransaction.FaultInjector = point => point.StartsWith("after-install-copy:", StringComparison.OrdinalIgnoreCase) ? new IOException("injected mid-install failure") : null;
        try
        {
            var result = await RecoverableArtifactTransaction.CommitFileSetAsync(staged, destination, _ => true, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.False(result.RecoveryRequired);
            Assert.Equal(before, Inventory(destination));
            Assert.Empty(Directory.EnumerateFiles(destination, "*.ps7-install-*", SearchOption.AllDirectories));
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task FileSetFailureAfterSeveralInstalls_RestoresExactOriginalInventory()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.Directory("application");
        var initial = workspace.Directory("initial");
        foreach (var name in new[] { "a.exe", "b.dll", "c.json" }) File.WriteAllText(Path.Combine(initial, name), "old " + name);
        Assert.True((await RecoverableArtifactTransaction.CommitFileSetAsync(initial, destination, _ => true, CancellationToken.None)).Succeeded);
        var before = Inventory(destination);
        var staged = workspace.Directory("replacement");
        foreach (var name in new[] { "a.exe", "b.dll", "c.json" }) File.WriteAllText(Path.Combine(staged, name), "new " + name);
        var installedCount = 0;
        RecoverableArtifactTransaction.FaultInjector = point =>
            point.StartsWith("after-install-file:", StringComparison.OrdinalIgnoreCase) && ++installedCount == 2
                ? new IOException("injected after multiple installs")
                : null;
        try
        {
            var result = await RecoverableArtifactTransaction.CommitFileSetAsync(staged, destination, _ => true, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.False(result.RecoveryRequired);
            Assert.Equal(before, Inventory(destination));
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task FileSetInvalidStaging_DoesNotPerformCommitOperations()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.Directory("application");
        var existing = Path.Combine(destination, "App.exe");
        File.WriteAllText(existing, "valid existing application");
        var staged = workspace.Directory("replacement");
        File.WriteAllText(Path.Combine(staged, "App.exe"), "invalid staged application");
        var before = Inventory(destination);
        var commitOperations = 0;
        RecoverableArtifactTransaction.FaultInjector = point =>
        {
            if (point == "before-commit") commitOperations++;
            return null;
        };
        try
        {
            var result = await RecoverableArtifactTransaction.CommitFileSetAsync(staged, destination, _ => false, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.Equal(0, commitOperations);
            Assert.Equal(before, Inventory(destination));
        }
        finally { RecoverableArtifactTransaction.FaultInjector = null; }
    }

    [Fact]
    public async Task FileSetRefusesReplacementWhenUnmanifestedEmptyDirectoryAppears()
    {
        using var workspace = new TemporaryWorkspace();
        var destination = workspace.Directory("application");
        var initial = workspace.Directory("initial");
        File.WriteAllText(Path.Combine(initial, "App.exe"), "old");
        Assert.True((await RecoverableArtifactTransaction.CommitFileSetAsync(initial, destination, _ => true, CancellationToken.None)).Succeeded);
        Directory.CreateDirectory(Path.Combine(destination, "developer-empty"));
        var before = Inventory(destination);
        var staged = workspace.Directory("replacement");
        File.WriteAllText(Path.Combine(staged, "App.exe"), "new");
        var result = await RecoverableArtifactTransaction.CommitFileSetAsync(staged, destination, _ => true, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(before, Inventory(destination));
    }

    private static SortedDictionary<string, string> Inventory(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            result["D:" + Path.GetRelativePath(root, directory)] = "";
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            result["F:" + Path.GetRelativePath(root, file)] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
        return result;
    }

    private static Exception SetFlagAndFail(Action setFlag)
    {
        setFlag();
        return new IOException("injected backup cleanup failure");
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        internal readonly string Root = Path.Combine(Environment.GetEnvironmentVariable("PS7SCRIPTDESK_EXPORT_R4_EVIDENCE") ?? Path.GetTempPath(), "TestSessions", "ArtifactTransactions", Guid.NewGuid().ToString("N"));

        public TemporaryWorkspace() => System.IO.Directory.CreateDirectory(Root);

        public string Directory(string name)
        {
            var path = Path.Combine(Root, name);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public string File(string name, string contents)
        {
            var path = Path.Combine(Root, name);
            System.IO.File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Root, recursive: true); }
            catch { }
        }
    }
}
