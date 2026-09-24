using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Shell.Composition;

namespace PS7ScriptDesk.Tests;

public sealed class StructuredStartupResilienceTests
{
    [Fact]
    public void FailedStructuredInitializationProducesUnavailableResultWithoutInvokingBackend()
    {
        var invocationCount = 0;

        var result = AppBootstrapper.TryInitializeStructuredExecution(() =>
        {
            invocationCount++;
            throw new TypeLoadException("Could not load type 'System.Management.Automation.PSSnapIn'.");
        });

        Assert.False(result.IsAvailable);
        Assert.Null(result.Adapter);
        Assert.IsType<TypeLoadException>(result.Failure);
        Assert.Equal(1, invocationCount);
    }
}
