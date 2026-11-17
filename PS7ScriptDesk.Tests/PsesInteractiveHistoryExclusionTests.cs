using System.Management.Automation;
using PS7ScriptDesk.PowerShell.Services;
using PowerShellEngine = System.Management.Automation.PowerShell;

namespace PS7ScriptDesk.Tests;

public sealed class PsesInteractiveHistoryExclusionTests
{
    [Fact]
    public void RunFileAndRunSelectionLaunchersAreSkippedForDifferentRequestIds()
    {
        using var powerShell = CreateMockHistoryRunspace();
        Run(powerShell, PsesInteractiveHistoryExclusion.BuildRegistrationScript());

        Assert.Equal("SkipAdding", InvokeHandler(powerShell, "& $global:__ps7sd_editor_0123456789abcdef0123456789abcdef"));
        Assert.Equal("SkipAdding", InvokeHandler(powerShell, ". $global:__ps7sd_editor_fedcba9876543210fedcba9876543210"));
    }

    [Fact]
    public void LegitimateCommandsKeepExistingHandlerDecisionAndRepeatedRequestsRemainIdempotent()
    {
        using var powerShell = CreateMockHistoryRunspace(
            "$global:priorHandlerCalls = 0; " +
            "$global:mockHistoryHandler = { param($line) $global:priorHandlerCalls++; return 'MemoryOnly' }");

        Run(powerShell, PsesInteractiveHistoryExclusion.BuildRegistrationScript());
        var firstHandler = GetInstalledHandler(powerShell);
        Run(powerShell, PsesInteractiveHistoryExclusion.BuildRegistrationScript());
        var secondHandler = GetInstalledHandler(powerShell);

        Assert.Same(firstHandler, secondHandler);
        Assert.Equal("MemoryOnly", InvokeHandler(powerShell, "Get-ChildItem -Force"));
        Assert.Equal("1", Invoke(powerShell, "$global:priorHandlerCalls"));
        Assert.Equal("SkipAdding", InvokeHandler(powerShell, "& $global:__ps7sd_editor_00000000000000000000000000000001"));
        Assert.Equal("1", Invoke(powerShell, "$global:priorHandlerCalls"));
    }

    [Fact]
    public void NormalCommandUsesDefaultMemoryAndFileDecisionWhenNoPriorHandlerExists()
    {
        using var powerShell = CreateMockHistoryRunspace();
        Run(powerShell, PsesInteractiveHistoryExclusion.BuildRegistrationScript());

        Assert.Equal("MemoryAndFile", InvokeHandler(powerShell, "Write-Output 'user command'"));
    }

    [Fact]
    public void NoPriorHandlerStillInstallsAndRunsTheProductionGeneratedHandler()
    {
        using var powerShell = CreateMockHistoryRunspace();
        Run(powerShell, PsesInteractiveHistoryExclusion.BuildRegistrationScript());

        Assert.IsType<ScriptBlock>(GetInstalledHandler(powerShell));
        Assert.Equal("SkipAdding", InvokeHandler(powerShell, "& $global:__ps7sd_editor_0123456789abcdef0123456789abcdef"));
        Assert.Equal("MemoryAndFile", InvokeHandler(powerShell, "Get-Date"));
    }

    [Fact]
    public void ExistingDelegateHandlerDecisionIsPreserved()
    {
        using var powerShell = CreateMockHistoryRunspace(
            "$global:priorHandlerCalls = 0; " +
            "$global:mockHistoryHandler = [Func[string,object]] { param($line) $global:priorHandlerCalls++; return 'MemoryOnly' }");
        Run(powerShell, PsesInteractiveHistoryExclusion.BuildRegistrationScript());

        Assert.Equal("MemoryOnly", InvokeHandler(powerShell, "Get-Location"));
        Assert.Equal("1", Invoke(powerShell, "$global:priorHandlerCalls"));
    }

    [Fact]
    public void ExistingHistoryPersistenceSettingsRemainUnchanged()
    {
        using var powerShell = CreateMockHistoryRunspace(
            "$global:mockHistorySaveStyle = 'SaveAtExit'; " +
            "$global:mockHistorySavePath = 'user-history.txt'");
        Run(powerShell, PsesInteractiveHistoryExclusion.BuildRegistrationScript());

        Assert.Equal("SaveAtExit", Invoke(powerShell, "$global:mockHistorySaveStyle"));
        Assert.Equal("user-history.txt", Invoke(powerShell, "$global:mockHistorySavePath"));
    }

    private static PowerShellEngine CreateMockHistoryRunspace(string? setup = null)
    {
        var powerShell = PowerShellEngine.Create();
        Run(powerShell,
            "$global:mockHistoryHandler = $null; $global:mockHistorySaveStyle = 'SaveIncrementally'; $global:mockHistorySavePath = 'default-history.txt'; " +
            "function global:Get-PSReadLineOption { [pscustomobject]@{ AddToHistoryHandler = $global:mockHistoryHandler; HistorySaveStyle = $global:mockHistorySaveStyle; HistorySavePath = $global:mockHistorySavePath } }; " +
            "function global:Set-PSReadLineOption { param([scriptblock]$AddToHistoryHandler) $global:mockHistoryHandler = $AddToHistoryHandler }; " +
            setup);
        return powerShell;
    }

    private static ScriptBlock GetInstalledHandler(PowerShellEngine powerShell)
    {
        var values = powerShell.AddScript("$global:mockHistoryHandler").Invoke();
        powerShell.Commands.Clear();
        return Assert.IsType<ScriptBlock>(Assert.Single(values).BaseObject);
    }

    private static string InvokeHandler(PowerShellEngine powerShell, string commandLine)
    {
        Assert.IsType<ScriptBlock>(GetInstalledHandler(powerShell));
        return Invoke(powerShell, "& $global:mockHistoryHandler " + QuoteSingle(commandLine));
    }

    private static string Invoke(PowerShellEngine powerShell, string script)
    {
        var values = Run(powerShell, script);
        return Assert.Single(values).ToString();
    }

    private static System.Collections.ObjectModel.Collection<PSObject> Run(PowerShellEngine powerShell, string? script)
    {
        powerShell.Commands.Clear();
        if (!string.IsNullOrEmpty(script)) powerShell.AddScript(script);
        var result = powerShell.Invoke();
        Assert.Empty(powerShell.Streams.Error);
        return result;
    }

    private static string QuoteSingle(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
