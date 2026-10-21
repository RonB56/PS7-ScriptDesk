namespace PS7ScriptDesk.Application.Interfaces;

/// <summary>Describes the debug endpoint published by the authoritative PSES session.</summary>
public interface IAuthoritativePsesDebugEndpoint
{
    bool IsSessionRunning { get; }
    int SessionGeneration { get; }
    int? ProcessId { get; }
    DateTimeOffset? ProcessStartTimeUtc { get; }
    string? DebugServicePipeName { get; }
}
