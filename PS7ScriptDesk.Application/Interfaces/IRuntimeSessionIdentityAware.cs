namespace PS7ScriptDesk.Application.Interfaces;

/// <summary>Optional terminal capability for joining observations to the runtime coordinator's session identity.</summary>
public interface IRuntimeSessionIdentityAware
{
    void SetRuntimeSessionIdentity(Guid sessionId, long generation);
}
