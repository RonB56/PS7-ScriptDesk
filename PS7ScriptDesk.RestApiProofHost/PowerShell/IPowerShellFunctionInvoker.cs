namespace PS7ScriptDesk.RestApiProofHost.PowerShell;

public interface IPowerShellFunctionInvoker
{
    Task<ApiInvocationResult> InvokeAsync(
        ApiInvocationRequest request,
        RunspacePoolLease poolLease,
        int retainedStreamLimit,
        CancellationToken cancellationToken,
        Func<ApiInvocationStatus> cancellationStatusProvider,
        PowerShellInvocationStreamSink? streamSink = null);
}
