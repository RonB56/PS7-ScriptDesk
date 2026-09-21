using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalControllerPhase1Tests
{
    [Fact]
    public void SessionAndRendererGenerationsAreIndependent()
    {
        var (session, host, controller) = Create();
        session.EmitStarted(7);
        host.EmitReady(3);

        Assert.Equal(7, controller.Snapshot.SessionGeneration);
        Assert.Equal(3, controller.Snapshot.RendererGeneration);
        Assert.Equal(TerminalControllerLifecycleState.SessionActiveHostReady, controller.Snapshot.State);
    }

    [Fact]
    public void RendererFailureDoesNotStopHealthySession()
    {
        var (session, host, controller) = Create();
        session.EmitStarted(4);
        host.EmitReady(9);
        host.EmitUnavailable(9, "test");

        Assert.True(session.IsRunning);
        Assert.Equal(4, controller.Snapshot.SessionGeneration);
        Assert.Equal(TerminalControllerLifecycleState.SessionActiveHostUnavailable, controller.Snapshot.State);
    }

    [Fact]
    public void StaleSessionEventsAreRejected()
    {
        var (session, _, controller) = Create();
        session.EmitStarted(2);
        session.EmitStarted(1);
        session.EmitStopping(1);

        Assert.Equal(2, controller.Snapshot.SessionGeneration);
        Assert.NotEqual(TerminalControllerLifecycleState.ShutdownRequested, controller.Snapshot.State);
    }

    [Fact]
    public void StaleRendererEventsAreRejected()
    {
        var (_, host, controller) = Create();
        host.EmitReady(5);
        host.EmitUnavailable(4, "stale");

        Assert.Equal(5, controller.Snapshot.RendererGeneration);
        Assert.Equal(TerminalControllerLifecycleState.Detached, controller.Snapshot.State);
    }

    [Fact]
    public void DisposalRejectsLaterCallbacksAndCommands()
    {
        var (session, host, controller) = Create();
        session.EmitStarted(1);
        controller.Dispose();
        host.EmitReady(2);

        var result = controller.TryResize(80, 24);
        Assert.False(result.Succeeded);
        Assert.Equal(TerminalOperationFailureKind.Disposed, result.Failure);
        Assert.Equal(TerminalControllerLifecycleState.Disposed, controller.Snapshot.State);
    }

    [Fact]
    public async Task ShutdownIsIdempotent()
    {
        var (session, _, controller) = Create();
        session.EmitStarted(1);
        var first = controller.ShutdownAsync();
        var second = controller.ShutdownAsync();

        Assert.Same(first, second);
        Assert.True(await first);
        Assert.Equal(1, session.ShutdownCalls);
    }

    [Fact]
    public void ResizeGenerationIsCorrelatedWithoutCreatingASecondHostAuthority()
    {
        var (session, host, controller) = Create();
        session.EmitStarted(1);
        host.EmitReady(1);
        var result = controller.TryResize(120, 40);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Identity.SessionGeneration);
        Assert.Equal(1, result.Identity.RendererGeneration);
        Assert.Equal(1, result.Identity.ResizeGeneration);
        Assert.Equal(1, session.ResizeCalls);
        Assert.Empty(host.ResizeCalls);
    }

    [Fact]
    public void OutputSequenceRejectsOutOfOrderEvents()
    {
        var (session, _, controller) = Create();
        session.EmitStarted(1);
        session.EmitOutput(1, 2, "two");
        session.EmitOutput(1, 1, "one");

        Assert.Equal(2, controller.Snapshot.LastOutputSequence);
    }

    [Fact]
    public void ObserverLifetimeAttachesOnceAndDisposesWithoutActiveOperations()
    {
        var session = new FakeSession();
        var host = new FakeHost();
        using var lifetime = new TerminalObserverLifetime(session, host);

        var first = lifetime.Attach();
        var second = lifetime.Attach();

        Assert.Same(first, second);
        Assert.True(lifetime.IsAttached);
        Assert.Equal(0, session.ShutdownCalls);
        Assert.Equal(0, session.ResizeCalls);

        lifetime.Dispose();

        Assert.Equal(1, session.DisposeCalls);
        Assert.Equal(1, host.DisposeCalls);
        Assert.Equal(TerminalControllerLifecycleState.Disposed, first.Snapshot.State);
    }

    [Fact]
    public void DiagnosticSinkFailureDoesNotBreakLiveObservation()
    {
        var (session, host, controller) = Create();
        using (controller)
        {
            session.EmitStarted(3);
            host.EmitReady(4);
        }

        var diagnosticFailureController = new TerminalController(session, host, _ => throw new InvalidOperationException("diagnostic test failure"));
        using (diagnosticFailureController)
        {
            session.EmitStarted(5);
            host.EmitReady(6);

            Assert.Equal(5, diagnosticFailureController.Snapshot.SessionGeneration);
            Assert.Equal(6, diagnosticFailureController.Snapshot.RendererGeneration);
        }
    }

    private static (FakeSession Session, FakeHost Host, TerminalController Controller) Create()
    {
        var session = new FakeSession();
        var host = new FakeHost();
        return (session, host, new TerminalController(session, host));
    }

    private sealed class FakeSession : ITerminalSession
    {
        public bool IsRunning { get; private set; }
        public int? CurrentGeneration { get; private set; }
        public int ShutdownCalls { get; private set; }
        public int ResizeCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public event Action<TerminalSessionLifecycleEventArgs>? Started;
        public event Action<TerminalSessionLifecycleEventArgs>? Stopping;
        public event Action<TerminalSessionLifecycleEventArgs>? Terminated;
        public event Action<TerminalOutputEventArgs>? OutputReceived;
        public Task StartAsync(PS7ScriptDesk.Domain.Models.PowerShellRuntimeInfo runtime, Action<PS7ScriptDesk.Domain.Models.ExecutionOutputRecord> onOutput, string? startupWorkingDirectory = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> StopAsync(CancellationToken cancellationToken = default) { IsRunning = false; return Task.FromResult(true); }
        public Task<bool> ShutdownAsync(CancellationToken cancellationToken = default) { ShutdownCalls++; IsRunning = false; return Task.FromResult(true); }
        public Task WriteInputAsync(string data, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Resize(int columns, int rows) => ResizeCalls++;
        public void Dispose() { DisposeCalls++; }
        public void EmitStarted(int generation) { CurrentGeneration = generation; IsRunning = true; Started?.Invoke(new(generation)); }
        public void EmitStopping(int generation) => Stopping?.Invoke(new(generation));
        public void EmitTerminated(int generation) => Terminated?.Invoke(new(generation));
        public void EmitOutput(int generation, long sequence, string data) => OutputReceived?.Invoke(new(generation, data, sequence));
    }

    private sealed class FakeHost : ITerminalHost
    {
        public int? CurrentGeneration { get; private set; }
        public bool IsReady { get; private set; }
        public int DisposeCalls { get; private set; }
        public List<(int Columns, int Rows)> ResizeCalls { get; } = new();
        public event Action<TerminalHostLifecycleEventArgs>? Ready;
        public event Action<TerminalHostLifecycleEventArgs>? Unavailable;
        public event Action<TerminalHostLifecycleEventArgs>? Disposed;
        public event Action<TerminalGeometryProposal>? GeometryProposed;
        public void WriteRaw(int sessionGeneration, string data) { }
        public void Focus() { }
        public void Dispose() { DisposeCalls++; }
        public void EmitReady(int generation) { CurrentGeneration = generation; IsReady = true; Ready?.Invoke(new(generation)); }
        public void EmitUnavailable(int generation, string reason) { CurrentGeneration = generation; IsReady = false; Unavailable?.Invoke(new(generation, reason)); }
        public void EmitDisposed(int generation) => Disposed?.Invoke(new(generation));
        public void EmitGeometry(TerminalGeometryProposal proposal) => GeometryProposed?.Invoke(proposal);
    }
}
