using System.Diagnostics;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class ExternalProcessPathContractTests
{
    [Fact]
    public void SyntheticPackageRoots_AreDynamicAndPreserveUnpackagedPaths()
    {
        var local = Path.Combine(Path.GetTempPath(), "local space Ω");
        var ordinaryTemp = Path.Combine(Path.GetTempPath(), "ordinary temp Ω");
        Assert.Equal(local, ExternalProcessPathContract.ResolveDataRoot(null, null, local));
        Assert.Equal(ordinaryTemp, ExternalProcessPathContract.ResolveTempRoot(null, null, local, ordinaryTemp));

        var x64 = ExternalProcessPathContract.ResolveDataRoot("App_1.0_x64", "App_alpha", local);
        var arm64 = ExternalProcessPathContract.ResolveDataRoot("App_2.0_arm64", "App_beta", local);
        Assert.Equal(Path.Combine(local, "Packages", "App_alpha", "LocalCache", "Local"), x64);
        Assert.Equal(Path.Combine(local, "Packages", "App_beta", "LocalCache", "Local"), arm64);
        Assert.NotEqual(x64, arm64);
        Assert.Equal(Path.Combine(x64, "Temp"), ExternalProcessPathContract.ResolveTempRoot("App_1.0_x64", "App_alpha", local, ordinaryTemp));
        Assert.Equal(x64, ExternalProcessPathContract.ResolveDataRoot("App_1.0_x64", "App_alpha", local));
        Assert.Throws<InvalidOperationException>(() => ExternalProcessPathContract.ResolveDataRoot("App_1.0_x64", null, local));
        Assert.Throws<InvalidOperationException>(() => ExternalProcessPathContract.ResolveDataRoot("App_1.0_x64", "..\\escape", local));
        Assert.Throws<InvalidOperationException>(() => ExternalProcessPathContract.ResolveDataRoot(null, null, ""));
    }

    [Fact]
    public void MetadataRootsAndBackgroundEnvironment_UseSharedPhysicalContract()
    {
        Assert.Equal(Path.Combine(ExternalProcessPathContract.DataRoot, "PS7ScriptDesk", "MetadataWorker"),
            EditorMetadataBuilderHost.GetMetadataWorkerRootDirectory());
        Assert.Equal(Path.Combine(ExternalProcessPathContract.DataRoot, "PS7ScriptDesk", "Logs"),
            MetadataPerformanceLog.GetLogDirectory());
        Assert.Equal(Path.Combine(ExternalProcessPathContract.DataRoot, "PS7ScriptDesk", "EditorMetadataCache"),
            EditorMetadataCacheStore.GetCacheRootDirectory());

        Assert.True(PowerShellBackgroundProcessEnvironment.TryBuildEnvironmentInfo("MetadataBuilder", null,
            createDirectories: true, out var environment, out var failure), failure);
        Assert.Equal(ExternalProcessPathContract.DataRoot, environment.LocalAppData);
        Assert.True(Directory.Exists(environment.HomePath));
        Assert.True(Directory.Exists(environment.UserProfilePath));
        Assert.True(Directory.Exists(environment.TempPath));
        Assert.True(Directory.Exists(Path.GetDirectoryName(environment.PsModuleAnalysisCachePath)));
        Assert.True(Directory.Exists(environment.ModuleRoot));
        var info = new ProcessStartInfo();
        Assert.True(PowerShellBackgroundProcessEnvironment.Apply(info, "MetadataBuilder"));
        Assert.Equal(ExternalProcessPathContract.DataRoot, info.Environment["LOCALAPPDATA"]);
        Assert.Equal(environment.TempPath, info.Environment["TEMP"]);
    }

    [Fact]
    public async Task ParentAndRealPowerShellChild_ExchangeFilesAndWorkingDirectory()
    {
        var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        Assert.True(File.Exists(pwsh), $"PowerShell 7 is required for the cross-process test: {pwsh}");
        var root = Path.Combine(Path.GetTempPath(), "ScriptDesk path Ω " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "input space Ω.txt");
            var output = Path.Combine(root, "output space Ω.txt");
            var script = Path.Combine(root, "worker space Ω.ps1");
            File.WriteAllText(input, "parent wrote this");
            File.WriteAllText(script, "param($inputFile,$outputFile) if ((Get-Content -LiteralPath $inputFile -Raw) -ne 'parent wrote this') { exit 11 }; if ((Get-Location).Path -ne $env:SCRIPT_DESK_FIXTURE_ROOT) { exit 12 }; Set-Content -LiteralPath $outputFile -Value 'child wrote this' -NoNewline");
            using var process = new Process { StartInfo = new ProcessStartInfo
            {
                FileName = pwsh,
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            } };
            process.StartInfo.Environment["SCRIPT_DESK_FIXTURE_ROOT"] = root;
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(script);
            process.StartInfo.ArgumentList.Add(input);
            process.StartInfo.ArgumentList.Add(output);
            process.Start();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await errorTask);
            Assert.Equal("child wrote this", File.ReadAllText(output));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
