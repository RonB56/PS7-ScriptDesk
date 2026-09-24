using System;
using System.Collections.Generic;

namespace PS7ScriptDesk.Domain.Models;

public enum EditorExecutionMode
{
    ScriptCall,
    CurrentScope,
    RunSelection
}

public enum EditorExecutionStatus
{
    Completed,
    Cancelled,
    Failed,
    Rejected
}

public enum EditorExecutionEventKind
{
    Accepted,
    Started,
    Output,
    InputRequested,
    Progress,
    WorkingDirectoryChanged,
    Completed,
    Cancelled,
    Failed,
    Rejected,
    Restarted,
    Disposed
}

public enum ExecutionBackendAvailability
{
    Available,
    Unavailable,
    InitializationFailed,
    RuntimeIncompatible,
    Disposed,
    ShuttingDown
}

public enum ExecutionCompatibilityState
{
    NotRequested,
    Compatible,
    RuntimeIncompatible,
    InitializationFailed,
    Unknown
}

public enum ExecutionRequestClassification
{
    KnownInteractive,
    KnownNonInteractive,
    Unknown
}

public sealed record ExecutionCapabilityRequirements(
    bool RequiresInteractiveInput = false,
    bool RequiresSecureInput = false,
    bool RequiresCurrentScope = false,
    bool RequiresTerminalInterrupt = false,
    bool RequiresScriptCallIsolation = false,
    bool RequiresWorkingDirectoryContinuity = false,
    bool RequiresStructuredStreams = false);

public sealed record ExecutionRequestClassificationResult(
    ExecutionRequestClassification Classification,
    ExecutionCapabilityRequirements RequiredCapabilities,
    string Reason);

public sealed record ExecutionBackendCapabilities(
    bool SupportsInteractiveInput,
    bool SupportsSecureInput,
    bool SupportsCurrentScope,
    bool SupportsScriptCallIsolation,
    bool SupportsTerminalInterrupt,
    bool SupportsStructuredStreams,
    bool SupportsWorkingDirectoryTracking,
    bool SupportsRestart,
    bool SupportsShutdown,
    bool SupportsNativeOutput,
    bool SupportsCancellation = false);

public sealed record ActiveEditorExecutionSnapshot(
    Guid RequestId,
    int SessionGeneration,
    string BackendId,
    ExecutionBackendCapabilities Capabilities,
    DateTimeOffset StartedAt);

public enum EditorOutputStreamKind
{
    Success,
    Error,
    Warning,
    Verbose,
    Debug,
    Information,
    Host,
    Native,
    VirtualTerminal
}

public enum PersistentSessionLifecycle
{
    Created,
    Ready,
    Executing,
    Restarting,
    ShuttingDown,
    Disposed,
    Faulted
}

public enum InteractiveTerminalState
{
    Unavailable,
    Starting,
    InteractiveIdleAtPrompt,
    InteractiveInputEditing,
    InteractiveCommandRunning,
    Stopping
}

public enum TerminalOutputSource
{
    InteractiveTerminal,
    StructuredEditor
}

public enum TerminalRendererLifecycle
{
    Unavailable,
    Starting,
    Ready,
    Failed,
    Retired
}

public sealed record InteractiveTerminalSnapshot(
    int Generation,
    InteractiveTerminalState State,
    string? Reason,
    DateTimeOffset Timestamp);

public sealed record EditorExecutionArtifact(
    Guid OwnerRequestId,
    int OwnerSessionGeneration,
    string ExecutionPath,
    string? OriginalSourcePath,
    bool IsSnapshot,
    bool DeleteAfterRun);

public sealed record EditorExecutionRequest(
    Guid RequestId,
    int SessionGeneration,
    EditorExecutionMode Mode,
    string DocumentDisplayName,
    string ScriptText,
    string? SavedScriptPath = null,
    bool IsSavedClean = false,
    string? WorkingDirectory = null,
    bool ExecuteInCurrentScope = false,
    ExecutionCapabilityRequirements? CapabilityRequirements = null)
{
    public bool IsRunSelection => Mode == EditorExecutionMode.RunSelection;
}

public sealed record EditorOutputRecord(
    Guid RequestId,
    int SessionGeneration,
    long Sequence,
    EditorOutputStreamKind StreamKind,
    string Payload,
    DateTimeOffset Timestamp);

public sealed record EditorExecutionEvent(
    EditorExecutionEventKind Kind,
    Guid RequestId,
    int SessionGeneration,
    long Sequence,
    EditorOutputRecord? Output = null,
    string? WorkingDirectory = null,
    string? ErrorMessage = null,
    DateTimeOffset? Timestamp = null,
    string? BackendId = null,
    string? Detail = null);

public sealed record PersistentSessionSnapshot(
    int SessionGeneration,
    PersistentSessionLifecycle Lifecycle,
    Guid? ActiveRequestId,
    string? CurrentWorkingDirectory,
    bool IsExecutionRunning,
    string? RuntimeIdentity);

public sealed record EditorExecutionResult(
    Guid RequestId,
    int SessionGeneration,
    EditorExecutionStatus Status,
    IReadOnlyList<EditorOutputRecord> Outputs,
    string? CurrentWorkingDirectory,
    EditorExecutionArtifact? Artifact,
    string? ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string BackendId = "unknown",
    string? RejectionReason = null)
{
    public bool Succeeded => Status == EditorExecutionStatus.Completed;

    public TimeSpan Duration => EndedAt - StartedAt;
}

public sealed record TerminalOutputEnvelope(
    long Sequence,
    TerminalOutputSource Source,
    Guid? RequestId,
    int BrokerSessionGeneration,
    int InteractiveTerminalSessionGeneration,
    int RendererGeneration,
    long SourceSequence,
    EditorOutputStreamKind StreamKind,
    string Payload,
    DateTimeOffset Timestamp)
{
    public bool IsEditorOutput => Source == TerminalOutputSource.StructuredEditor;
}
