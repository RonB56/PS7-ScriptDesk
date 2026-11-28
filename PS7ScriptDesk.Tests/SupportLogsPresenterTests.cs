using System.Diagnostics;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Shell.Presentation;

namespace PS7ScriptDesk.Tests;

public sealed class SupportLogsPresenterTests
{
    [Fact]
    public void LogicalSupportZipPath_ResolvesToPhysicalApplicationData()
    {
        var logicalZip = Path.Combine(ApplicationBranding.LocalApplicationDataRoot,
            "DeveloperDebugging", "Packages", "support.zip");

        var physicalZip = ExternalProcessPathContract.ResolveApplicationOwnedPath(logicalZip);

        Assert.Equal(Path.Combine(ExternalProcessPathContract.ApplicationDataRoot,
            "DeveloperDebugging", "Packages", "support.zip"), physicalZip);
        Assert.Equal(Path.GetDirectoryName(physicalZip),
            SupportLogsPresenter.GetPhysicalContainingFolder(physicalZip));
    }

    [Fact]
    public void PhysicalContainingFolder_IsDerivedWithoutResolvingItAgain()
    {
        var packagedFolder = Path.Combine(Path.GetTempPath(), "Packages", "fixture",
            "LocalCache", "Local", "PS7ScriptDesk", "DeveloperDebugging", "Packages");
        var physicalZip = Path.Combine(packagedFolder, "support.zip");

        Assert.Equal(packagedFolder, SupportLogsPresenter.GetPhysicalContainingFolder(physicalZip));
        Assert.Throws<ArgumentException>(() => ExternalProcessPathContract.ResolveApplicationOwnedPath(packagedFolder));
    }

    [Fact]
    public void UnpackagedSupportZipPath_RemainsUnderOrdinaryLocalAppData()
    {
        Assert.Null(ExternalProcessPathContract.TryGetCurrentPackageFullName());
        var logicalZip = Path.Combine(ApplicationBranding.LocalApplicationDataRoot,
            "DeveloperDebugging", "Packages", "support.zip");

        Assert.Equal(logicalZip, ExternalProcessPathContract.ResolveApplicationOwnedPath(logicalZip));
        Assert.Equal(Path.GetDirectoryName(logicalZip),
            SupportLogsPresenter.GetPhysicalContainingFolder(logicalZip));
    }

    [Fact]
    public void Resolver_RejectsPathsOutsideTheLogicalApplicationRoot()
    {
        var outsidePath = Path.Combine(Path.GetTempPath(), "support.zip");

        Assert.Throws<ArgumentException>(() => ExternalProcessPathContract.ResolveApplicationOwnedPath(outsidePath));
    }

    [Fact]
    public void ExplorerShellRequest_AcceptsNullProcessResultAndUsesShellExecution()
    {
        var folder = Path.GetTempPath();
        ProcessStartInfo? submitted = null;

        SupportLogsPresenter.SubmitExplorerShellRequest(folder, startInfo =>
        {
            submitted = startInfo;
            return null;
        });

        Assert.NotNull(submitted);
        Assert.Equal(folder, submitted.FileName);
        Assert.True(submitted.UseShellExecute);
    }

    [Fact]
    public void ExplorerShellRequest_RejectsMissingFolderAndPropagatesLaunchFailure()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(() =>
            SupportLogsPresenter.SubmitExplorerShellRequest(missing, _ => throw new Exception("must not launch")));

        var launchError = new InvalidOperationException("shell launch failed");
        Assert.Same(launchError, Assert.Throws<InvalidOperationException>(() =>
            SupportLogsPresenter.SubmitExplorerShellRequest(Path.GetTempPath(), _ => throw launchError)));
    }
}
