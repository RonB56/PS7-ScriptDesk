namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>
/// Builds the request-stage script that excludes only ScriptDesk's foreground
/// editor launcher from PSReadLine history while delegating all other decisions
/// to the handler that was active before the wrapper was installed.
/// </summary>
internal static class PsesInteractiveHistoryExclusion
{
    private const string HandlerMarker = "PS7SD-INTERNAL-HISTORY-GUARD";
    private static readonly string StateVariableName = "__pssdHistoryExclusion_" + Guid.NewGuid().ToString("N");

    internal static string BuildRegistrationScript() => "try {\n" +
        $"$__pssdHistoryStateVariable = Get-Variable -Name '{StateVariableName}' -Scope Global -ErrorAction SilentlyContinue\n" +
        $"if ($null -eq $__pssdHistoryStateVariable) {{ $global:{StateVariableName} = [pscustomobject]@{{ Handler = $null; PreviousHandler = $null }} }}\n" +
        $"$__pssdHistoryState = $global:{StateVariableName}\n" +
        "$__pssdHistoryOptions = Get-PSReadLineOption -ErrorAction Stop\n" +
        "$__pssdCurrentHistoryHandler = $__pssdHistoryOptions.AddToHistoryHandler\n" +
        // A null current handler and a null stored handler means this state has
        // not been initialized yet; only a non-null stored handler can prove the
        // wrapper is already installed.
        "if (($null -eq $__pssdHistoryState.Handler) -or -not [object]::ReferenceEquals($__pssdCurrentHistoryHandler, $__pssdHistoryState.Handler)) {\n" +
        "$__pssdHistoryState.PreviousHandler = $__pssdCurrentHistoryHandler\n" +
        "$__pssdHistoryHandler = {\n" +
        "param($line)\n" +
        $"$null = '{HandlerMarker}'\n" +
        "if ($line -match '^\\s*(?:\\.|&)\\s+\\$global:__ps7sd_editor_[0-9a-f]{32}\\s*$') { return 'SkipAdding' }\n" +
        "$__pssdPreviousHistoryHandler = $__pssdHistoryState.PreviousHandler\n" +
        "if ($__pssdPreviousHistoryHandler -is [scriptblock]) { return (& $__pssdPreviousHistoryHandler $line) }\n" +
        "if ($__pssdPreviousHistoryHandler -is [System.Delegate]) { return $__pssdPreviousHistoryHandler.DynamicInvoke([object[]]@($line)) }\n" +
        "if ($null -ne $__pssdPreviousHistoryHandler) { throw 'The existing PSReadLine history handler has an unsupported type.' }\n" +
        "return 'MemoryAndFile'\n" +
        "}.GetNewClosure()\n" +
        "Set-PSReadLineOption -AddToHistoryHandler $__pssdHistoryHandler -ErrorAction Stop\n" +
        "$__pssdHistoryState.Handler = (Get-PSReadLineOption).AddToHistoryHandler\n" +
        "}\n" +
        "} catch { } finally {\n" +
        "Remove-Variable -Name __pssdHistoryStateVariable,__pssdHistoryState,__pssdHistoryOptions,__pssdCurrentHistoryHandler,__pssdPreviousHistoryHandler,__pssdHistoryHandler -ErrorAction SilentlyContinue\n" +
        "}\n";
}
