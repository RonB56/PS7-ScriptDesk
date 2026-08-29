using System.Management.Automation;

namespace PS7ScriptDesk.RestApiProofHost.PowerShell;

public sealed class PowerShellInvocationStreamSink
{
    private readonly Func<PowerShellInvocationStreamRecord, CancellationToken, ValueTask<bool>> _publish;

    public PowerShellInvocationStreamSink(Func<PowerShellInvocationStreamRecord, CancellationToken, ValueTask<bool>> publish)
        => _publish = publish ?? throw new ArgumentNullException(nameof(publish));

    public ValueTask<bool> PublishAsync(PowerShellInvocationStreamRecord record, CancellationToken cancellationToken)
        => _publish(record, cancellationToken);
}

public sealed record PowerShellInvocationStreamRecord(
    PowerShellInvocationStreamKind Kind,
    PSObject? Output = null,
    string? Message = null)
{
    public static PowerShellInvocationStreamRecord ForOutput(PSObject output)
        => new(PowerShellInvocationStreamKind.Output, output ?? throw new ArgumentNullException(nameof(output)));

    public static PowerShellInvocationStreamRecord ForStream(PowerShellInvocationStreamKind kind, string? message)
        => kind == PowerShellInvocationStreamKind.Output
            ? throw new ArgumentException("Use ForOutput for PowerShell pipeline output records.", nameof(kind))
            : new PowerShellInvocationStreamRecord(kind, Message: message);
}

public enum PowerShellInvocationStreamKind
{
    Output,
    Warning,
    Verbose,
    Debug,
    Information,
    Error
}
