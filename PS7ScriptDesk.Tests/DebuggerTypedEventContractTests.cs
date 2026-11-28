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

}
