using System.Reflection;
using PS7ScriptDesk.Shell;
using Xunit;

namespace PS7ScriptDesk.Tests;

[Collection("DiagnosticReliability")]
public sealed class DeferredInitializationReliabilityTests
{
    [Fact]
    public async Task UnexpectedDeferredInitializationFault_IsObservedWithoutRethrow()
    {
        var ownerField = typeof(MainWindow).GetField("_deferredInitializationTask", BindingFlags.NonPublic | BindingFlags.Instance);
        var observer = typeof(MainWindow).GetMethod("ObserveDeferredInitializationAsync", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(ownerField);
        Assert.Equal(typeof(Task), ownerField.FieldType);
        Assert.NotNull(observer);

        var escapedInitializationFault = Task.FromException(new InvalidOperationException("Injected deferred initialization fault."));
        var observationTask = Assert.IsAssignableFrom<Task>(observer.Invoke(null, [escapedInitializationFault]));

        await observationTask;
    }
}
