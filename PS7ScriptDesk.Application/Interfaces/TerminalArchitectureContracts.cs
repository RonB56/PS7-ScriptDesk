using System;

namespace PS7ScriptDesk.Application.Interfaces;

public enum TerminalControllerLifecycleState
{
    Detached,
    SessionStarting,
    SessionActiveHostUnavailable,
    SessionActiveHostReady,
    ShutdownRequested,
    Stopped,
    Disposed
}

public enum TerminalOperationFailureKind
{
    None,
    SessionNotRunning,
    SessionStopping,
    HostNotReady,
    HostUnavailable,
    ResizeSuperseded,
    ResizeRejected,
    OutputBackpressure,
    ReplayIncomplete,
    Disposed,
    NativeTransportFailure
}

public readonly record struct TerminalOperationIdentity(
    long OperationId,
    int? SessionGeneration,
    int? RendererGeneration,
    long? ResizeGeneration,
    long? OutputSequence);

public readonly record struct TerminalGeometry(
    double PixelWidth,
    double PixelHeight,
    bool IsVisible,
    string Source,
    int? Columns = null,
    int? Rows = null)
{
    public bool IsValid => PixelWidth > 0 && PixelHeight > 0;
}

public readonly record struct TerminalGeometryProposal(
    TerminalGeometry Geometry,
    int RendererGeneration,
    long Sequence);

public readonly record struct TerminalSessionLifecycleEventArgs(
    int Generation,
    string Reason = "")
{
    public TerminalOperationIdentity Identity => new(0, Generation, null, null, null);
}

public readonly record struct TerminalHostLifecycleEventArgs(
    int Generation,
    string Reason = "")
{
    public TerminalOperationIdentity Identity => new(0, null, Generation, null, null);
}

public readonly record struct TerminalOutputEventArgs(
    int SessionGeneration,
    string Data,
    long Sequence)
{
    public int CharacterCount => Data.Length;
}

public readonly record struct TerminalOperationResult(
    bool Succeeded,
    TerminalOperationFailureKind Failure,
    TerminalOperationIdentity Identity,
    string? Detail = null)
{
    public static TerminalOperationResult Success(TerminalOperationIdentity identity) => new(true, TerminalOperationFailureKind.None, identity);
    public static TerminalOperationResult Failed(TerminalOperationFailureKind failure, TerminalOperationIdentity identity, string? detail = null) => new(false, failure, identity, detail);
}

public readonly record struct TerminalControllerSnapshot(
    TerminalControllerLifecycleState State,
    int? SessionGeneration,
    int? RendererGeneration,
    long ResizeGeneration,
    long LastOutputSequence,
    bool IsDisposed);

public readonly record struct TerminalControllerDiagnostic(
    string Event,
    TerminalOperationIdentity Identity,
    TerminalControllerLifecycleState State,
    string? Detail = null);

