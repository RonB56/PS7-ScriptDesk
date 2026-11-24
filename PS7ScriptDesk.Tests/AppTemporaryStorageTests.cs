using PS7ScriptDesk.Application.Utilities;

namespace PS7ScriptDesk.Tests;

public sealed class AppTemporaryStorageTests
{
    [Fact]
    public void ManagedRootNestedUnderNormalWorkingDirectoryIsAccepted()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var managedRoot = GetManagedRoot(localAppData, "DebugSnapshots");
        var workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        AssertValidRoot(managedRoot, localAppData, workingDirectory);
    }

    [Fact]
    public void WorkingDirectoryEqualToManagedRootIsRejected()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "PS7ScriptDeskTests", Guid.NewGuid().ToString("N"));
        var managedRoot = GetManagedRoot(localAppData, "DebugSnapshots");

        AssertInvalidRoot(managedRoot, localAppData, managedRoot);
    }

    [Fact]
    public void WorkingDirectoryInsideManagedRootIsRejected()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "PS7ScriptDeskTests", Guid.NewGuid().ToString("N"));
        var managedRoot = GetManagedRoot(localAppData, "DebugSnapshots");

        AssertInvalidRoot(managedRoot, localAppData, Path.Combine(managedRoot, "Child"));
    }

    [Fact]
    public void SiblingWorkingDirectoryIsAccepted()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "PS7ScriptDeskTests", Guid.NewGuid().ToString("N"));
        var managedRoot = GetManagedRoot(localAppData, "DebugSnapshots");
        var siblingWorkingDirectory = GetManagedRoot(localAppData, "TerminalSnapshots");

        AssertValidRoot(managedRoot, localAppData, siblingWorkingDirectory);
    }

    [Fact]
    public void ManagedRootOutsideApplicationTempBaseIsRejected()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "PS7ScriptDeskTests", Guid.NewGuid().ToString("N"));
        var outsideRoot = Path.Combine(Path.GetTempPath(), "OutsideManagedTemp", Guid.NewGuid().ToString("N"));

        AssertInvalidRoot(outsideRoot, localAppData, Path.GetTempPath());
    }

    [Fact]
    public void ManagedRootOverlappingRuntimeDirectoryIsRejected()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "PS7ScriptDeskTests", Guid.NewGuid().ToString("N"));
        var managedRoot = GetManagedRoot(localAppData, "DebugSnapshots");

        var valid = AppTemporaryStorage.TryValidateManagedRootDirectory(
            managedRoot,
            localAppData,
            Path.Combine(managedRoot, "Runtime"),
            Path.GetTempPath(),
            out _,
            out _);

        Assert.False(valid);
    }

    [Fact]
    public void SnapshotFileOutsideApprovedRootIsRejected()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "PS7ScriptDeskTests", Guid.NewGuid().ToString("N"));
        var managedRoot = GetManagedRoot(localAppData, "DebugSnapshots");
        var outsideSnapshot = Path.Combine(Path.GetDirectoryName(managedRoot)!, "outside.ps1");

        var valid = AppTemporaryStorage.TryValidateManagedPath(
            managedRoot,
            outsideSnapshot,
            out _,
            out _,
            out _);

        Assert.False(valid);
    }

    [Fact]
    public void ManagedDebugSnapshotCanBeCreatedAndCleanedUp()
    {
        var areaName = "DebugSnapshotsTests_" + Guid.NewGuid().ToString("N");
        Assert.True(AppTemporaryStorage.TryGetManagedRootDirectory(areaName, createIfMissing: true, out var root, out var rootFailure), rootFailure);

        var snapshotPath = Path.Combine(root, $"PS7ScriptDesk_Debug_Test_{Guid.NewGuid():N}.ps1");
        try
        {
            Assert.True(AppTemporaryStorage.TryValidateManagedPath(root, snapshotPath, out _, out var normalizedPath, out var pathFailure), pathFailure);
            File.WriteAllText(normalizedPath, "Write-Output 'snapshot'");
            Assert.True(File.Exists(normalizedPath));

            Assert.True(AppTemporaryStorage.TryValidateManagedPath(root, snapshotPath, out _, out normalizedPath, out pathFailure), pathFailure);
            File.Delete(normalizedPath);
            Assert.False(File.Exists(normalizedPath));
        }
        finally
        {
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
            {
                Directory.Delete(root);
            }
        }
    }

    private static string GetManagedRoot(string localAppData, string areaName)
        => Path.Combine(localAppData, "PS7ScriptDesk", "Temp", areaName);

    private static void AssertValidRoot(string managedRoot, string localAppData, string workingDirectory)
    {
        var valid = AppTemporaryStorage.TryValidateManagedRootDirectory(
            managedRoot,
            localAppData,
            Path.Combine(Path.GetTempPath(), "Runtime"),
            workingDirectory,
            out _,
            out var failureReason);

        Assert.True(valid, failureReason);
    }

    private static void AssertInvalidRoot(string managedRoot, string localAppData, string workingDirectory)
    {
        var valid = AppTemporaryStorage.TryValidateManagedRootDirectory(
            managedRoot,
            localAppData,
            Path.Combine(Path.GetTempPath(), "Runtime"),
            workingDirectory,
            out _,
            out _);

        Assert.False(valid);
    }
}
