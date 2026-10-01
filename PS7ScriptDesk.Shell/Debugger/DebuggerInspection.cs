using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Shell.Debug
{
    public sealed record DebugFrameIdentity(
        Guid SessionId,
        long PauseGeneration,
        int FrameIndex,
        string FunctionName,
        string ScriptPath,
        int LineNumber,
        string InvocationName,
        bool IsCurrentFrame,
        bool IsSelectedInspectionFrame,
        bool IsNavigable)
    {
        public long ThreadId { get; init; }
        public string ProviderFrameId { get; init; } = string.Empty;
        public string FrameId => BuildFrameId(SessionId, PauseGeneration, ThreadId, ProviderFrameId);

        public static string BuildFrameId(Guid sessionId, long pauseGeneration, long threadId, string providerFrameId)
            => $"{sessionId:N}/{pauseGeneration}/thread-{threadId}/frame-{providerFrameId}";

        public static DebugFrameIdentity Create(
            Guid sessionId,
            long pauseGeneration,
            int frameIndex,
            string? functionName,
            string? scriptPath,
            int lineNumber,
            string? invocationName,
            bool isCurrentFrame,
            long threadId = 1,
            string? providerFrameId = null)
        {
            var normalizedPath = scriptPath ?? string.Empty;
            var identity = new DebugFrameIdentity(
                sessionId,
                pauseGeneration,
                frameIndex,
                functionName ?? string.Empty,
                normalizedPath,
                lineNumber,
                invocationName ?? string.Empty,
                isCurrentFrame,
                isCurrentFrame,
                lineNumber > 0 && !string.IsNullOrWhiteSpace(normalizedPath) && System.IO.File.Exists(normalizedPath));
            return identity with { ThreadId = threadId, ProviderFrameId = providerFrameId ?? $"test-frame-{frameIndex}" };
        }
    }

    public static class DebugInspectionResultGuard
    {
        public static bool IsCurrent(
            Guid activeSessionId,
            long activePauseGeneration,
            Guid resultSessionId,
            long resultPauseGeneration,
            string? resultFrameId = null,
            string? activeFrameId = null,
            long? resultThreadId = null,
            long? activeThreadId = null,
            string? resultProviderFrameId = null,
            string? activeProviderFrameId = null)
        {
            return activeSessionId != Guid.Empty &&
                   activeSessionId == resultSessionId &&
                   activePauseGeneration == resultPauseGeneration &&
                   (!activeThreadId.HasValue || activeThreadId == resultThreadId) &&
                   (activeProviderFrameId is null || string.Equals(activeProviderFrameId, resultProviderFrameId, StringComparison.Ordinal)) &&
                   (activeFrameId is null || string.Equals(activeFrameId, resultFrameId, StringComparison.Ordinal));
        }
    }

    public enum DebuggerInspectionLifecycleState
    {
        Empty = 0,
        Starting = 1,
        Paused = 2,
        Executing = 3,
        Stopped = 4
    }

    public sealed record DebuggerInspectionRequestIdentity(
        Guid SessionId,
        long PauseGeneration,
        long RequestGeneration,
        string? FrameId);

    public sealed record DebuggerFrameInspectionIdentity(
        Guid SessionId,
        long PauseGeneration,
        long RequestGeneration,
        string FrameId,
        int FrameIndex)
    {
        public long ThreadId { get; init; }
        public string ProviderFrameId { get; init; } = string.Empty;

        public static DebuggerFrameInspectionIdentity FromFrame(DebugFrameIdentity frame, long requestGeneration)
            => new(frame.SessionId, frame.PauseGeneration, requestGeneration, frame.FrameId, frame.FrameIndex)
            {
                ThreadId = frame.ThreadId,
                ProviderFrameId = frame.ProviderFrameId
            };
    }

    public sealed record DebuggerInspectionRequest(
        DebuggerInspectionRequestIdentity Identity,
        CancellationToken CancellationToken);

    public enum DebuggerVariableAvailability
    {
        Available = 0,
        Unavailable = 1,
        Failed = 2,
        Stale = 3,
        Cancelled = 4
    }

    public enum DebuggerVariableScopeKind
    {
        CurrentFrameLocals = 0,
        SharedScriptOrGlobal = 1,
        InvocationMetadata = 2,
        Unavailable = 3
    }

    /// <summary>
    /// The truthful result of a frame-scoped variables request. An unavailable
    /// result is intentionally distinct from an empty, successfully loaded list.
    /// </summary>
    public sealed record DebuggerVariableInspectionResult(
        DebuggerVariableAvailability Availability,
        DebuggerVariableScopeKind ScopeKind,
        IReadOnlyList<DebugVariableInfo> Variables,
        DebuggerFrameInspectionIdentity FrameIdentity,
        string? Reason = null)
    {
        public bool IsAvailable => Availability == DebuggerVariableAvailability.Available;

        public static DebuggerVariableInspectionResult UnavailableFor(
            DebuggerFrameInspectionIdentity frameIdentity,
            string reason)
            => new(
                DebuggerVariableAvailability.Unavailable,
                DebuggerVariableScopeKind.Unavailable,
                Array.Empty<DebugVariableInfo>(),
                frameIdentity,
                reason);
    }

    /// <summary>
    /// Owns pause-scoped debugger inspection identity. The shell is the single owner;
    /// docked and floating panes project this state and never retain independent selection.
    /// </summary>
    public sealed class DebuggerInspectionContext : IDisposable
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _requestCancellation;
        private HashSet<string> _validFrameIds = new(StringComparer.Ordinal);
        private bool _disposed;
        private long _requestGeneration;

        public Guid SessionId { get; private set; }

        public long PauseGeneration { get; private set; }

        public DebugFrameIdentity? CurrentExecutionFrame { get; private set; }

        public DebugFrameIdentity? SelectedInspectionFrame { get; private set; }

        public DebuggerInspectionLifecycleState LifecycleState { get; private set; } = DebuggerInspectionLifecycleState.Empty;

        public long RequestGeneration => Interlocked.Read(ref _requestGeneration);

        public void BeginSession(Guid sessionId)
        {
            if (sessionId == Guid.Empty)
            {
                throw new ArgumentException("A debugger session identity is required.", nameof(sessionId));
            }

            lock (_gate)
            {
                ThrowIfDisposed();
                var previousSelectedFrame = SelectedInspectionFrame;
                var previousCurrentFrame = CurrentExecutionFrame;
                InvalidateRequestsLocked();
                SessionId = sessionId;
                PauseGeneration = 0;
                CurrentExecutionFrame = null;
                SelectedInspectionFrame = null;
                _validFrameIds.Clear();
                LifecycleState = DebuggerInspectionLifecycleState.Starting;
                LogSelectionTransition(previousSelectedFrame, null, "StopClear", "BeginSession", previousCurrentFrame);
            }
        }

        public void PreparePaused(Guid sessionId, long pauseGeneration)
        {
            if (sessionId == Guid.Empty)
            {
                throw new ArgumentException("A debugger session identity is required.", nameof(sessionId));
            }

            lock (_gate)
            {
                ThrowIfDisposed();
                if (SessionId == sessionId && PauseGeneration == pauseGeneration && LifecycleState == DebuggerInspectionLifecycleState.Paused)
                {
                    return;
                }

                var previousSelectedFrame = SelectedInspectionFrame;
                var previousCurrentFrame = CurrentExecutionFrame;
                InvalidateRequestsLocked();
                SessionId = sessionId;
                PauseGeneration = pauseGeneration;
                CurrentExecutionFrame = null;
                SelectedInspectionFrame = null;
                _validFrameIds.Clear();
                LifecycleState = DebuggerInspectionLifecycleState.Paused;
                LogSelectionTransition(previousSelectedFrame, null, "NewPauseInitialization", "PreparePaused", previousCurrentFrame);
            }
        }

        public bool PublishCallStack(IReadOnlyList<DebugCallStackFrame> callStack)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (LifecycleState != DebuggerInspectionLifecycleState.Paused || callStack.Count == 0)
                {
                    return false;
                }

                var currentFrames = callStack.Where(frame => frame.IsCurrentFrame).ToArray();
                if (currentFrames.Length != 1)
                {
                    return false;
                }

                var current = currentFrames[0];
                if (current.SessionId != SessionId || current.PauseGeneration != PauseGeneration)
                {
                    return false;
                }

                _validFrameIds = callStack
                    .Where(frame => frame.SessionId == SessionId && frame.PauseGeneration == PauseGeneration)
                    .Select(frame => frame.FrameId)
                    .ToHashSet(StringComparer.Ordinal);
                if (!_validFrameIds.Contains(current.FrameId))
                {
                    return false;
                }

                var previousSelectedFrame = SelectedInspectionFrame;
                CurrentExecutionFrame = current.Identity with { IsCurrentFrame = true };
                var preservedSelectedFrame = previousSelectedFrame is not null
                    ? callStack.FirstOrDefault(frame => string.Equals(frame.FrameId, previousSelectedFrame.FrameId, StringComparison.Ordinal))
                    : null;
                if (preservedSelectedFrame is not null)
                {
                    SelectedInspectionFrame = preservedSelectedFrame.Identity with
                    {
                        IsCurrentFrame = string.Equals(preservedSelectedFrame.FrameId, current.FrameId, StringComparison.Ordinal),
                        IsSelectedInspectionFrame = true
                    };
                    LogSelectionTransition(
                        previousSelectedFrame,
                        SelectedInspectionFrame,
                        "CallStackRefreshPreserve",
                        "PublishCallStack",
                        CurrentExecutionFrame);
                }
                else
                {
                    SelectedInspectionFrame = CurrentExecutionFrame with { IsSelectedInspectionFrame = true };
                    LogSelectionTransition(
                        previousSelectedFrame,
                        SelectedInspectionFrame,
                        previousSelectedFrame is null ? "NewPauseInitialization" : "SelectedFrameMissing",
                        "PublishCallStack",
                        CurrentExecutionFrame);
                }

                return true;
            }
        }

        public bool TrySelectFrame(DebugCallStackFrame frame, out string rejectionReason)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (LifecycleState != DebuggerInspectionLifecycleState.Paused)
                {
                    rejectionReason = "Inspection selection requires a paused debugger session.";
                    return false;
                }

                if (frame.SessionId != SessionId || frame.PauseGeneration != PauseGeneration)
                {
                    rejectionReason = "The selected frame belongs to a different session or pause generation.";
                    return false;
                }

                if (!_validFrameIds.Contains(frame.FrameId))
                {
                    rejectionReason = "The selected frame is not part of the active call stack.";
                    return false;
                }

                var previousSelectedFrame = SelectedInspectionFrame;
                InvalidateRequestsLocked();
                SelectedInspectionFrame = frame.Identity with
                {
                    IsCurrentFrame = CurrentExecutionFrame?.FrameId == frame.FrameId,
                    IsSelectedInspectionFrame = true
                };
                LogSelectionTransition(previousSelectedFrame, SelectedInspectionFrame, "UserSelection", "TrySelectFrame", CurrentExecutionFrame);
                rejectionReason = string.Empty;
                return true;
            }
        }

        public DebuggerInspectionRequest BeginRequest(string? frameId = null)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (LifecycleState != DebuggerInspectionLifecycleState.Paused || SessionId == Guid.Empty)
                {
                    throw new InvalidOperationException("Inspection requests require a paused debugger session.");
                }

                InvalidateRequestsLocked();
                _requestCancellation = new CancellationTokenSource();
                var requestGeneration = Interlocked.Increment(ref _requestGeneration);
                return new DebuggerInspectionRequest(
                    new DebuggerInspectionRequestIdentity(SessionId, PauseGeneration, requestGeneration, frameId),
                    _requestCancellation.Token);
            }
        }

        public bool IsCurrent(DebuggerInspectionRequestIdentity identity)
        {
            lock (_gate)
            {
                return !_disposed &&
                       LifecycleState == DebuggerInspectionLifecycleState.Paused &&
                       identity.SessionId != Guid.Empty &&
                       identity.SessionId == SessionId &&
                       identity.PauseGeneration == PauseGeneration &&
                       identity.RequestGeneration == RequestGeneration &&
                       (identity.FrameId is null || _validFrameIds.Contains(identity.FrameId));
            }
        }

        public IReadOnlyList<DebugCallStackFrame> ApplySelection(IReadOnlyList<DebugCallStackFrame> callStack)
        {
            lock (_gate)
            {
                var currentFrameId = CurrentExecutionFrame?.FrameId;
                var selectedFrameId = SelectedInspectionFrame?.FrameId;
                return callStack.Select(frame => frame with
                {
                    IsCurrentFrame = string.Equals(frame.FrameId, currentFrameId, StringComparison.Ordinal),
                    IsSelectedInspectionFrame = string.Equals(frame.FrameId, selectedFrameId, StringComparison.Ordinal)
                }).ToArray();
            }
        }

        public void BeginExecution()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                var previousSelectedFrame = SelectedInspectionFrame;
                var previousCurrentFrame = CurrentExecutionFrame;
                InvalidateRequestsLocked();
                CurrentExecutionFrame = null;
                SelectedInspectionFrame = null;
                _validFrameIds.Clear();
                LifecycleState = DebuggerInspectionLifecycleState.Executing;
                LogSelectionTransition(previousSelectedFrame, null, "ResumeClear", "BeginExecution", previousCurrentFrame);
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                var previousSelectedFrame = SelectedInspectionFrame;
                var previousCurrentFrame = CurrentExecutionFrame;
                InvalidateRequestsLocked();
                SessionId = Guid.Empty;
                PauseGeneration = 0;
                CurrentExecutionFrame = null;
                SelectedInspectionFrame = null;
                _validFrameIds.Clear();
                LifecycleState = DebuggerInspectionLifecycleState.Stopped;
                LogSelectionTransition(previousSelectedFrame, null, "StopClear", "Stop", previousCurrentFrame);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                var previousSelectedFrame = SelectedInspectionFrame;
                var previousCurrentFrame = CurrentExecutionFrame;
                InvalidateRequestsLocked();
                _disposed = true;
                SessionId = Guid.Empty;
                PauseGeneration = 0;
                CurrentExecutionFrame = null;
                SelectedInspectionFrame = null;
                _validFrameIds.Clear();
                LifecycleState = DebuggerInspectionLifecycleState.Empty;
                LogSelectionTransition(previousSelectedFrame, null, "StopClear", "Dispose", previousCurrentFrame);
            }
        }

        private void InvalidateRequestsLocked()
        {
            var requestCancellation = _requestCancellation;
            _requestCancellation = null;
            if (requestCancellation is not null)
            {
                requestCancellation.Cancel();
                requestCancellation.Dispose();
            }

            Interlocked.Increment(ref _requestGeneration);
        }

        private static void LogSelectionTransition(
            DebugFrameIdentity? previousSelectedFrame,
            DebugFrameIdentity? selectedFrame,
            string reason,
            string source,
            DebugFrameIdentity? currentExecutionFrame = null)
        {
            try
            {
                DeveloperDiagnostics.LogInfo(
                    "Debugger",
                    "Selected inspection frame transition applied.",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["source"] = source,
                        ["oldSelectedIdentity"] = previousSelectedFrame?.FrameId,
                        ["newSelectedIdentity"] = selectedFrame?.FrameId,
                        ["sessionId"] = currentExecutionFrame?.SessionId ?? selectedFrame?.SessionId ?? previousSelectedFrame?.SessionId,
                        ["pauseGeneration"] = currentExecutionFrame?.PauseGeneration ?? selectedFrame?.PauseGeneration ?? previousSelectedFrame?.PauseGeneration,
                        ["threadId"] = currentExecutionFrame?.ThreadId ?? selectedFrame?.ThreadId ?? previousSelectedFrame?.ThreadId,
                        ["providerFrameId"] = selectedFrame?.ProviderFrameId,
                        ["currentExecutionFrameId"] = currentExecutionFrame?.FrameId,
                        ["frameIndex"] = selectedFrame?.FrameIndex
                    });
            }
            catch
            {
                // Developer diagnostics must never affect debugger state publication.
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DebuggerInspectionContext));
            }
        }
    }
}
