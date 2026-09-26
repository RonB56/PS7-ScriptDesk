using System.Reflection;
using PS7ScriptDesk.Shell.Debug;

namespace PS7ScriptDesk.Tests;

public sealed class DebuggerTypedEventContractTests
{
    [Fact]
    public void EventModel_IsImmutableAndBoundsPublishedText()
    {
        var sessionId = Guid.NewGuid();
        var value = new DebuggerEvent(
            sessionId,
            DateTimeOffset.UtcNow,
            1,
            DebuggerEventCategory.NativeStdout,
            DebuggerEventSeverity.Information,
            "NativeStdout",
            new string('x', DebuggerEvent.MaxDisplayTextLength + 100),
            details: new string('d', DebuggerEvent.MaxDetailsLength + 100));

        Assert.Equal(sessionId, value.SessionId);
        Assert.Equal(DebuggerEvent.MaxDisplayTextLength, value.DisplayText.Length);
        Assert.Equal(DebuggerEvent.MaxDetailsLength, value.Details!.Length);
        Assert.All(typeof(DebuggerEvent).GetProperties(), property => Assert.False(property.CanWrite));
    }

    [Fact]
    public void EventModel_NavigationRequiresUsableLocation()
    {
        var value = new DebuggerEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow, 1,
            DebuggerEventCategory.Breakpoint, DebuggerEventSeverity.Information,
            "Breakpoint", "hit", isNavigable: true);

        Assert.False(value.IsNavigable);
    }

    [Fact]
    public void MarkerLikeUserOutput_IsNotConsumedAsProtocol()
    {
        var session = new PsesDebugSession();
        var output = new List<string>();
        session.OutputReceived += output.Add;

        var method = typeof(PsesDebugSession).GetMethod("ProcessIncomingLine", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(session, new object?[] { "user __PSS_DEBUG_SESSION_ENDED__ text", false });

        Assert.Single(output);
        Assert.Contains("__PSS_DEBUG_SESSION_ENDED__", output[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TypedEventSubscribersAreIndependentAndSequencesAdvance()
    {
        var session = new PsesDebugSession();
        var received = new List<DebuggerEvent>();
        session.TypedEventReceived += _ => throw new InvalidOperationException("typed subscriber failure");
        session.TypedEventReceived += received.Add;

        var method = typeof(PsesDebugSession).GetMethod("ProcessIncomingLine", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(session, new object?[] { "first", false });
        method.Invoke(session, new object?[] { "second", true });

        Assert.Equal(2, received.Count);
        Assert.True(received[0].Sequence < received[1].Sequence);
        Assert.All(received, value => Assert.Equal(session.SessionId, value.SessionId));
    }
}
