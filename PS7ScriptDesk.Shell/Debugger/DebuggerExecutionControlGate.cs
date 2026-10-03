namespace PS7ScriptDesk.Shell.Debug;

/// <summary>
/// Owns the shell-level single-flight lease for debugger execution-control commands.
/// This is intentionally independent from PSES protocol request serialization:
/// protocol requests may be queued internally, but execution-control commands must
/// be rejected while another control transition is still owned by the shell.
/// </summary>
internal sealed class DebuggerExecutionControlGate
{
    private readonly object _syncRoot = new();
    private Lease? _activeLease;

    public bool IsBusy
    {
        get
        {
            lock (_syncRoot)
            {
                return _activeLease is not null;
            }
        }
    }

    public bool TryAcquire(object session, string command, out Lease lease)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        lock (_syncRoot)
        {
            if (_activeLease is not null)
            {
                lease = null!;
                return false;
            }

            lease = new Lease(session, command, Guid.NewGuid());
            _activeLease = lease;
            return true;
        }
    }

    public bool Release(Lease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);

        lock (_syncRoot)
        {
            if (!ReferenceEquals(_activeLease, lease))
            {
                return false;
            }

            _activeLease = null;
            return true;
        }
    }

    public bool ReleaseForSession(object session)
        => TryReleaseForSession(session, out _);

    public bool TryReleaseForSession(object session, out Lease? releasedLease)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_syncRoot)
        {
            if (_activeLease is null || !ReferenceEquals(_activeLease.Session, session))
            {
                releasedLease = null;
                return false;
            }

            releasedLease = _activeLease;
            _activeLease = null;
            return true;
        }
    }

    internal sealed class Lease
    {
        internal Lease(object session, string command, Guid operationId)
        {
            Session = session;
            Command = command;
            OperationId = operationId;
        }

        internal object Session { get; }
        internal string Command { get; }
        internal Guid OperationId { get; }
    }
}
