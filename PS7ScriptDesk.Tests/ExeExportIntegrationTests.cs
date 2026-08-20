using System.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class ExeExportIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk-ExportIntegration-{Guid.NewGuid():N}");

    public ExeExportIntegrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task EmbeddedFrameworkDependentConsoleExport_PublishesAndRunsCurrentScriptContent()
    {
        var sourcePath = Path.Combine(_root, "Current Editor Content.ps1");
        var outputPath = Path.Combine(_root, "Output Folder", "Integration Export.exe");
        const string sentinel = "PS7ScriptDesk embedded host integration success";
        await File.WriteAllTextAsync(sourcePath, $"Write-Output '{sentinel}'");
        var configuration = ExeExportConfiguration.CreatePreset(ExeExportPreset.WindowsConsoleApplication, "Integration Export");
        configuration.DeploymentModel = ExeDeploymentModel.FrameworkDependent;
        configuration.OutputExecutablePath = outputPath;
        var runtime = new PowerShellRuntimeInfo("PowerShell test", "Core", "7.6.3", new Version(7, 6, 3), "x64", Environment.ProcessPath!, "test", true, false, true);
        var result = await new ExeExportService().ExportScriptAsExeAsync(new ExeExportRequest(sourcePath, await File.ReadAllTextAsync(sourcePath), outputPath, runtime, configuration));

        Assert.True(result.Succeeded, result.DetailedLog);
        Assert.True(File.Exists(outputPath));
        var startInfo = new ProcessStartInfo { FileName = outputPath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var outputTask = process!.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var standardOutput = await outputTask;
        var standardError = await errorTask;
        Assert.True(process.ExitCode == 0, standardError);
        Assert.Contains(sentinel, standardOutput, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
