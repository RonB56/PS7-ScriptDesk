using System.Reflection;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class EditorMetadataCacheVersionNormalizationTests
{
    [Fact]
    public void RuntimeIdentity_NormalizesEquivalentNumericVersions_AndPreservesMeaningfulDifferences()
    {
        var identity = BuildIdentity("7.7");

        Assert.Equal(identity, BuildIdentity("7.7.0"));
        Assert.Equal(identity, BuildIdentity("7.7.0.0"));
        Assert.NotEqual(identity, BuildIdentity("7.7.1"));
        Assert.NotEqual(identity, BuildIdentity("7.7.0.1"));
        Assert.NotEqual(BuildIdentity("7.7-preview.1"), identity);
        Assert.Equal("7.7-preview.1", EditorMetadataCacheStore.NormalizeRuntimeVersion("7.7-preview.1"));
        Assert.Equal("not-a-version", EditorMetadataCacheStore.NormalizeRuntimeVersion("not-a-version"));
    }

    [Fact]
    public void Startup_LoadsHealthyStoreVersionSnapshotWithoutSchedulingBuilder()
    {
        using var cache = CreateTemporaryCacheRoot();
        const string runtimePath = @"C:\synthetic\pwsh.exe";
        const string psHome = @"C:\synthetic";
        EditorMetadataCacheStore.SaveSnapshot(
            runtimePath,
            CreateHealthySnapshot(),
            "7.7.0",
            "Core",
            "x64",
            psHome,
            "synthetic-fingerprint");

        // The legacy Store build created the 7.7.0 key. Normalization keeps that exact key.
        Assert.Equal(
            EditorMetadataCacheStore.GetCacheDirectory(runtimePath, "7.7.0", "Core", "x64", psHome),
            EditorMetadataCacheStore.GetCacheDirectory(runtimePath, "7.7", "Core", "x64", psHome));

        var runtime = CreateRuntime(runtimePath, "7.7", psHome);
        using var service = new PowerShellCompletionService();
        EditorMetadataWarmupStatus? finalStatus = null;
        service.MetadataWarmupStatusChanged += (_, args) =>
        {
            if (args.Status.Phase == EditorMetadataWarmupPhase.Completed)
            {
                finalStatus = args.Status;
            }
        };

        service.StartMetadataWarmup(runtime);

        Assert.NotNull(finalStatus);
        Assert.True(finalStatus!.IsLoadedFromCache);
        Assert.Equal(EditorMetadataWarmupReason.CachedLoad, finalStatus.Reason);
        Assert.Null(typeof(PowerShellCompletionService)
            .GetField("_metadataBuilderProcess", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service));
    }

    [Fact]
    public void CacheLookup_RejectsDifferentVersionsAndKeepsRuntimeIdentitiesDistinct()
    {
        using var cache = CreateTemporaryCacheRoot();
        const string runtimePath = @"C:\synthetic\pwsh.exe";
        const string psHome = @"C:\synthetic";
        EditorMetadataCacheStore.SaveSnapshot(
            runtimePath,
            CreateHealthySnapshot(),
            "7.7.0",
            "Core",
            "x64",
            psHome,
            "synthetic-fingerprint");

        Assert.False(EditorMetadataCacheStore.TryLoadSnapshot(
            runtimePath,
            "7.7.1",
            "Core",
            "x64",
            psHome,
            out _, out _, out _, out _));

        var baseIdentity = EditorMetadataCacheStore.GetCacheDirectory(runtimePath, "7.7", "Core", "x64", psHome);
        Assert.NotEqual(baseIdentity, EditorMetadataCacheStore.GetCacheDirectory(runtimePath, "7.7", "Core", "x86", psHome));
        Assert.NotEqual(baseIdentity, EditorMetadataCacheStore.GetCacheDirectory(runtimePath, "7.7", "Core", "x64", @"C:\other"));
        Assert.NotEqual(baseIdentity, EditorMetadataCacheStore.GetCacheDirectory(@"D:\other\pwsh.exe", "7.7", "Core", "x64", psHome));
    }

    [Fact]
    public void IncompleteSnapshot_FollowsExistingQuarantineRecoveryPath()
    {
        using var cache = CreateTemporaryCacheRoot();
        const string runtimePath = @"C:\synthetic\pwsh.exe";
        const string psHome = @"C:\synthetic";
        EditorMetadataCacheStore.SaveSnapshot(
            runtimePath,
            new EditorMetadataCacheSnapshot(
                new PowerShellCommandCatalog(new[] { new PowerShellCommandReference("Get-Item", "Cmdlet", "Microsoft.PowerShell.Management", false, "Get-Item") }),
                new Dictionary<string, PowerShellQuickInfo>()),
            "7.7.0",
            "Core",
            "x64",
            psHome,
            "synthetic-fingerprint");

        using var service = new PowerShellCompletionService();
        var method = typeof(PowerShellCompletionService).GetMethod("TryLoadPersistedMetadataSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments = { CreateRuntime(runtimePath, "7.7", psHome), null, null, null, null, null };

        var loaded = (bool)method.Invoke(service, arguments)!;

        Assert.False(loaded);
        var quarantineRoot = Path.Combine(EditorMetadataCacheStore.GetCacheRootDirectory(), "Quarantine");
        Assert.True(Directory.Exists(quarantineRoot));
        Assert.NotEmpty(Directory.EnumerateDirectories(quarantineRoot));
    }

    private static string BuildIdentity(string version) => EditorMetadataCacheStore.BuildRuntimeCacheIdentity(
        @"C:\synthetic\pwsh.exe", version, "Core", "x64", @"C:\synthetic");

    private static IDisposable CreateTemporaryCacheRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk-MetadataCacheTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TemporaryCacheRoot(path, EditorMetadataCacheStore.UseCacheRootDirectoryForTesting(path));
    }

    private static EditorMetadataCacheSnapshot CreateHealthySnapshot()
    {
        var names = new[] { "Get-ChildItem", "Set-ExecutionPolicy", "Start-Process", "Get-Process" }
            .Concat(Enumerable.Range(0, 96).Select(index => $"Test-Command-{index}"))
            .ToArray();
        var parameter = new PowerShellParameterQuickInfo("Name", "System.String", false, 0);
        var quickInfos = names.ToDictionary(
            name => name,
            name => new PowerShellQuickInfo(name, "Cmdlet", "Synthetic", "Synthetic metadata", name, new[] { parameter }),
            StringComparer.OrdinalIgnoreCase);
        var commands = names.Select(name => new PowerShellCommandReference(name, "Cmdlet", "Synthetic", false, name)).ToArray();
        return new EditorMetadataCacheSnapshot(new PowerShellCommandCatalog(commands), quickInfos);
    }

    private static PowerShellRuntimeInfo CreateRuntime(string path, string version, string psHome) => new(
        "Synthetic PowerShell",
        "Core",
        version,
        Version.Parse(version),
        "x64",
        path,
        "MetadataCacheTest",
        isPowerShell7OrLater: true,
        isWindowsPowerShell: false,
        isPreferred: true,
        isValidated: true,
        psHome: psHome);

    private sealed class TemporaryCacheRoot(string path, IDisposable scope) : IDisposable
    {
        public void Dispose()
        {
            scope.Dispose();
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Preserve a failed test's cache evidence rather than masking its assertion.
            }
        }
    }
}
