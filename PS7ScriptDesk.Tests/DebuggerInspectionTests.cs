using System;
using System.Linq;
using PS7ScriptDesk.Shell.Debug;
using Xunit;

namespace PS7ScriptDesk.Tests
{
    public sealed class DebuggerInspectionTests
    {
        [Fact]
        public void FrameIdentityIsStableWithinPauseAndChangesAcrossGeneration()
        {
            var sessionId = Guid.NewGuid();
            var first = DebugFrameIdentity.Create(sessionId, 4, 0, "Outer", "C:\\scripts\\demo.ps1", 12, "Outer", true);
            var same = DebugFrameIdentity.Create(sessionId, 4, 0, "Outer", "C:\\scripts\\demo.ps1", 12, "Outer", true);
            var nextPause = DebugFrameIdentity.Create(sessionId, 5, 0, "Outer", "C:\\scripts\\demo.ps1", 12, "Outer", true);

            Assert.Equal(first.FrameId, same.FrameId);
            Assert.NotEqual(first.FrameId, nextPause.FrameId);
            Assert.True(first.IsCurrentFrame);
            Assert.True(first.IsSelectedInspectionFrame);
        }

        [Fact]
        public void StaleInspectionResultsAreRejectedBySessionOrPauseGeneration()
        {
            var sessionId = Guid.NewGuid();

            Assert.True(DebugInspectionResultGuard.IsCurrent(sessionId, 2, sessionId, 2));
            Assert.False(DebugInspectionResultGuard.IsCurrent(sessionId, 2, sessionId, 1));
            Assert.False(DebugInspectionResultGuard.IsCurrent(sessionId, 2, Guid.NewGuid(), 2));
            Assert.False(DebugInspectionResultGuard.IsCurrent(sessionId, 2, sessionId, 2, "old", "new"));
        }

        [Fact]
        public void VariablesAreExplicitlyReadOnlyCurrentScopeMetadata()
        {
            var variable = new DebugVariableInfo("items", "Object[]", "Collection Count=3 Type=Object[]")
            {
                SessionId = Guid.NewGuid(),
                PauseGeneration = 7,
                Scope = "Current",
                FrameId = "session/7/0",
                HasChildren = true,
                IsExpandable = false,
                IsTruncated = false,
                LoadState = "Loaded"
            };

            Assert.Equal("Current", variable.Scope);
            Assert.True(variable.HasChildren);
            Assert.False(variable.IsExpandable);
            Assert.Equal(variable.Value, variable.DisplayValue);
        }

        [Fact]
        public void ContextSelectsCurrentFrameByDefaultAndKeepsExecutionFrameWhenSelectionChanges()
        {
            var sessionId = Guid.NewGuid();
            using var context = new DebuggerInspectionContext();
            context.BeginSession(sessionId);
            context.PreparePaused(sessionId, 10);
            var frames = CreateFrames(sessionId, 10);

            Assert.True(context.PublishCallStack(frames));
            Assert.Equal(frames[0].FrameId, context.CurrentExecutionFrame?.FrameId);
            Assert.Equal(frames[0].FrameId, context.SelectedInspectionFrame?.FrameId);

            Assert.True(context.TrySelectFrame(frames[2], out var rejectionReason), rejectionReason);
            Assert.Equal(frames[0].FrameId, context.CurrentExecutionFrame?.FrameId);
            Assert.Equal(frames[2].FrameId, context.SelectedInspectionFrame?.FrameId);

            var projected = context.ApplySelection(frames);
            Assert.True(projected[0].IsCurrentFrame);
            Assert.False(projected[0].IsSelectedInspectionFrame);
            Assert.False(projected[2].IsCurrentFrame);
            Assert.True(projected[2].IsSelectedInspectionFrame);
        }

        [Fact]
        public void ContextRejectsFramesFromAnotherSessionOrPause()
        {
            var sessionId = Guid.NewGuid();
            using var context = new DebuggerInspectionContext();
            context.BeginSession(sessionId);
            context.PreparePaused(sessionId, 1);
            var frames = CreateFrames(sessionId, 1);
            Assert.True(context.PublishCallStack(frames));

            var wrongSession = frames[1] with { SessionId = Guid.NewGuid() };
            var wrongPause = frames[1] with { PauseGeneration = 2 };
            Assert.False(context.TrySelectFrame(wrongSession, out _));
            Assert.False(context.TrySelectFrame(wrongPause, out _));
        }

        [Fact]
        public void ContextInvalidatesRequestsOnSelectionExecutionPauseStopAndRestart()
        {
            var sessionId = Guid.NewGuid();
            using var context = new DebuggerInspectionContext();
            context.BeginSession(sessionId);
            context.PreparePaused(sessionId, 1);
            var frames = CreateFrames(sessionId, 1);
            Assert.True(context.PublishCallStack(frames));
            var firstRequest = context.BeginRequest();

            Assert.True(context.TrySelectFrame(frames[1], out _));
            Assert.False(context.IsCurrent(firstRequest.Identity));
            Assert.True(firstRequest.CancellationToken.IsCancellationRequested);

            context.BeginExecution();
            Assert.Null(context.CurrentExecutionFrame);
            Assert.Null(context.SelectedInspectionFrame);
            Assert.Equal(DebuggerInspectionLifecycleState.Executing, context.LifecycleState);

            context.PreparePaused(sessionId, 2);
            Assert.True(context.PublishCallStack(CreateFrames(sessionId, 2)));
            Assert.Equal(2, context.PauseGeneration);
            Assert.Equal(0, context.SelectedInspectionFrame?.FrameIndex);

            context.Stop();
            Assert.Equal(Guid.Empty, context.SessionId);
            Assert.Equal(DebuggerInspectionLifecycleState.Stopped, context.LifecycleState);

            var restartedSession = Guid.NewGuid();
            context.BeginSession(restartedSession);
            Assert.Equal(restartedSession, context.SessionId);
            Assert.Equal(DebuggerInspectionLifecycleState.Starting, context.LifecycleState);
            Assert.Null(context.SelectedInspectionFrame);
        }

        [Fact]
        public void ContextRejectsLateRequestAfterNewPause()
        {
            var sessionId = Guid.NewGuid();
            using var context = new DebuggerInspectionContext();
            context.BeginSession(sessionId);
            context.PreparePaused(sessionId, 1);
            Assert.True(context.PublishCallStack(CreateFrames(sessionId, 1)));
            var oldRequest = context.BeginRequest();

            context.PreparePaused(sessionId, 2);
            Assert.False(context.IsCurrent(oldRequest.Identity));
            Assert.True(oldRequest.CancellationToken.IsCancellationRequested);
        }

        [Fact]
        public void CompletedRequestCanBeInvalidatedBySelectionWithoutDisposedCancellationSource()
        {
            var sessionId = Guid.NewGuid();
            using var context = new DebuggerInspectionContext();
            context.BeginSession(sessionId);
            context.PreparePaused(sessionId, 1);
            var frames = CreateFrames(sessionId, 1);
            Assert.True(context.PublishCallStack(frames));

            var request = context.BeginRequest();
            var initialGeneration = request.Identity.RequestGeneration;

            // Request completion no longer disposes the context-owned CTS. The
            // context remains responsible for invalidation and disposal.
            Assert.True(context.IsCurrent(request.Identity));
            Assert.True(context.TrySelectFrame(frames[1], out var rejectionReason), rejectionReason);
            Assert.False(context.IsCurrent(request.Identity));
            Assert.True(request.CancellationToken.IsCancellationRequested);
            Assert.True(context.RequestGeneration > initialGeneration);
            Assert.Equal(frames[0].FrameId, context.CurrentExecutionFrame?.FrameId);
            Assert.Equal(frames[1].FrameId, context.SelectedInspectionFrame?.FrameId);

            Assert.True(context.TrySelectFrame(frames[2], out rejectionReason), rejectionReason);
            Assert.Equal(frames[2].FrameId, context.SelectedInspectionFrame?.FrameId);
        }

        private static DebugCallStackFrame[] CreateFrames(Guid sessionId, long pauseGeneration)
        {
            return Enumerable.Range(0, 3)
                .Select(index => new DebugCallStackFrame($"Function{index}", $"C:\\scripts\\frame{index}.ps1", index + 1)
                {
                    SessionId = sessionId,
                    PauseGeneration = pauseGeneration,
                    FrameIndex = index,
                    InvocationName = $"Function{index}",
                    IsCurrentFrame = index == 0,
                    IsSelectedInspectionFrame = index == 0
                })
                .ToArray();
        }
    }
}
