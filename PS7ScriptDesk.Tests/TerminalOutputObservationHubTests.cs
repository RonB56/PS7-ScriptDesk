using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;

namespace PS7ScriptDesk.Tests;

public sealed class TerminalOutputObservationHubTests
{
    [Fact]
    public async Task ObservationIsCopiedAsynchronouslyAndDoesNotOwnDelivery()
    {
        using var hub = new TestDisposableHub();
        var observer = new RecordingObserver();
        using var subscription = hub.Hub.Subscribe(observer);
        Assert.True(hub.Hub.Publish(new TerminalOutputObservation(3, "prompt\r\n")));
        await SpinWaitAsync(() => observer.Observations.Count == 1);
        Assert.Equal("prompt\r\n", observer.Observations[0].Data);
        Assert.Equal(3, observer.Observations[0].SessionGeneration);
    }

    private static async Task SpinWaitAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 100 && !predicate(); attempt++) await Task.Delay(5);
        Assert.True(predicate());
    }

    private sealed class RecordingObserver : ITerminalObserver
    {
        public List<TerminalOutputObservation> Observations { get; } = new();
        public void Observe(TerminalOutputObservation observation) => Observations.Add(observation);
    }

    private sealed class TestDisposableHub : IDisposable
    {
        public TerminalOutputObservationHub Hub { get; } = new();
        public void Dispose() { }
    }
}
