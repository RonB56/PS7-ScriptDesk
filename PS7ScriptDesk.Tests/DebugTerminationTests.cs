using System.Reflection;
using PS7ScriptDesk.Shell.Debug;

namespace PS7ScriptDesk.Tests;

public sealed class DebugTerminationTests
{
    [Theory]
    [InlineData(true, true, true, true, true, true, true, DebugTerminationReason.UserStop)]
    [InlineData(false, true, true, true, true, true, true, DebugTerminationReason.TerminatingException)]
    [InlineData(false, false, true, true, true, true, true, DebugTerminationReason.StartupFailure)]
    [InlineData(false, false, false, true, true, true, true, DebugTerminationReason.TransportFailure)]
    [InlineData(false, false, false, false, true, true, true, DebugTerminationReason.ProtocolFailure)]
    [InlineData(false, false, false, false, false, true, true, DebugTerminationReason.NormalCompletion)]
    [InlineData(false, false, false, false, false, false, true, DebugTerminationReason.ChildProcessExit)]
    public void TerminationPrecedence_IsStable(
        bool userStop,
        bool exception,
        bool startup,
        bool transport,
        bool protocol,
        bool normal,
        bool childExit,
        DebugTerminationReason expected)
    {
        var policy = typeof(DebugTerminationInfo).Assembly
            .GetType("PS7ScriptDesk.Shell.Debug.DebugTerminationPolicy", throwOnError: true)!;
        var method = policy.GetMethod("Select", BindingFlags.Static | BindingFlags.NonPublic)!;

        var actual = (DebugTerminationReason)method.Invoke(null, new object[]
        {
            userStop, exception, startup, transport, protocol, normal, childExit
        })!;

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ExceptionEnvelope_RoundTripsBoundedStructuredData()
    {
        var exception = new DebugExceptionInfo(
            "System.InvalidOperationException",
            "boom",
            fullyQualifiedErrorId: "TestFailure",
            scriptPath: "C:\\test.ps1",
            lineNumber: 7,
            isTerminating: true,
            isHandled: false);
        var envelopeType = typeof(DebugTerminationInfo).Assembly
            .GetType("PS7ScriptDesk.Shell.Debug.DebugExceptionEnvelope", throwOnError: true)!;
        var encode = envelopeType.GetMethod("Encode", BindingFlags.Static | BindingFlags.NonPublic)!;
        var decode = envelopeType.GetMethod("TryDecode", BindingFlags.Static | BindingFlags.NonPublic)!;
        var encoded = (string)encode.Invoke(null, new object[] { exception })!;
        var args = new object?[] { encoded, null };

        Assert.True((bool)decode.Invoke(null, args)!);
        var decoded = Assert.IsType<DebugExceptionInfo>(args[1]);
        Assert.Equal(exception.ExceptionType, decoded.ExceptionType);
        Assert.Equal(exception.Message, decoded.Message);
        Assert.Equal(exception.ScriptPath, decoded.ScriptPath);
        Assert.False(decoded.IsHandled);
    }

    [Fact]
    public void TerminationInfo_IsBoundedAndImmutable()
    {
        var info = new DebugTerminationInfo(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            DebugTerminationReason.TransportFailure,
            new string('x', DebugTerminationInfo.MaxMessageLength + 100),
            details: new string('d', DebugTerminationInfo.MaxDetailsLength + 100));

        Assert.Equal(DebugTerminationInfo.MaxMessageLength, info.Message.Length);
        Assert.Equal(DebugTerminationInfo.MaxDetailsLength, info.Details!.Length);
        Assert.Null(info.ExitCode);
    }
}
