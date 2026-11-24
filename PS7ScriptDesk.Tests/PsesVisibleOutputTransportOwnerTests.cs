using System.Reflection;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class PsesVisibleOutputTransportOwnerTests
{
    private static readonly MethodInfo PublishMethod = typeof(PsesOwnedInteractiveSessionService)
        .GetMethod("PublishVisibleOutputAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void VisibleOutputTransport_RejectsSecondOwner()
    {
        using var service = new PsesOwnedInteractiveSessionService();
        Func<int, ExecutionOutputStreamKind, string, ValueTask> first = (_, _, _) => ValueTask.CompletedTask;
        Func<int, ExecutionOutputStreamKind, string, ValueTask> second = (_, _, _) => ValueTask.CompletedTask;

        service.VisibleOutputPublishingAsync += first;

        Assert.Throws<InvalidOperationException>(() => service.VisibleOutputPublishingAsync += second);

        service.VisibleOutputPublishingAsync -= first;
    }

    [Fact]
    public async Task VisibleOutputTransport_DeliversOneOrderedCallbackAndSuppressesLegacyRawEvent()
    {
        using var service = new PsesOwnedInteractiveSessionService();
        var chunks = new List<string>();
        var rawCount = 0;
        service.VisibleOutputPublishingAsync += (generation, kind, text) =>
        {
            Assert.Equal(0, generation);
            Assert.Equal(ExecutionOutputStreamKind.StandardOutput, kind);
            chunks.Add(text);
            return ValueTask.CompletedTask;
        };
        service.RawOutputReceived += (_, _) => rawCount++;

        await PublishAsync(service, 0, "first");
        await PublishAsync(service, 0, "second");

        Assert.Equal(["first", "second"], chunks);
        Assert.Equal(0, rawCount);
    }

    [Fact]
    public async Task VisibleOutputTransport_PreservesExecutionRecordWithAwaitablePresentationOwner()
    {
        using var service = new PsesOwnedInteractiveSessionService();
        var records = new List<ExecutionOutputRecord>();
        var rawCount = 0;
        typeof(PsesOwnedInteractiveSessionService)
            .GetField("_onOutput", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, (Action<ExecutionOutputRecord>)(record => records.Add(record)));
        service.VisibleOutputPublishingAsync += (_, _, _) => ValueTask.CompletedTask;
        service.RawOutputReceived += (_, _) => rawCount++;

        await PublishAsync(service, 0, "execution-output");

        var record = Assert.Single(records);
        Assert.Equal("execution-output", record.Text);
        Assert.Equal(ExecutionOutputStreamKind.StandardOutput, record.StreamKind);
        Assert.Equal(0, rawCount);
    }

    [Fact]
    public async Task VisibleOutputTransport_FaultIsReportedAndNeverFallsBackAfterPossiblePartialAdmission()
    {
        using var service = new PsesOwnedInteractiveSessionService();
        var rawChunks = new List<string>();
        var executionRecords = new List<ExecutionOutputRecord>();
        var failures = new List<(int Generation, Exception Error)>();
        var callbackCount = 0;
        typeof(PsesOwnedInteractiveSessionService)
            .GetField("_onOutput", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, (Action<ExecutionOutputRecord>)(record => executionRecords.Add(record)));
        service.VisibleOutputPublishingAsync += (_, _, _) =>
        {
            callbackCount++;
            throw new InvalidOperationException("presentation admission failed");
        };
        service.RawOutputReceived += (_, text) => rawChunks.Add(text);
        service.VisibleOutputPublishingFailed += (generation, error) => failures.Add((generation, error));

        await PublishAsync(service, 0, "may-have-partially-rendered");
        await PublishAsync(service, 0, "later-output-quarantined");

        Assert.Equal(1, callbackCount);
        Assert.Empty(rawChunks);
        Assert.Equal(["may-have-partially-rendered", "later-output-quarantined"], executionRecords.Select(record => record.Text));
        Assert.Single(failures);
        Assert.Equal(0, failures[0].Generation);
        Assert.IsType<InvalidOperationException>(failures[0].Error);
    }

    [Fact]
    public async Task VisibleOutputTransport_RejectsRetiredSessionGeneration()
    {
        using var service = new PsesOwnedInteractiveSessionService();
        var callbackCount = 0;
        service.VisibleOutputPublishingAsync += (_, _, _) =>
        {
            callbackCount++;
            return ValueTask.CompletedTask;
        };
        typeof(PsesOwnedInteractiveSessionService)
            .GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, 7);

        await PublishAsync(service, 6, "stale");
        await PublishAsync(service, 7, "current");

        Assert.Equal(1, callbackCount);
    }

    private static ValueTask PublishAsync(PsesOwnedInteractiveSessionService service, int generation, string text)
        => (ValueTask)PublishMethod.Invoke(service,
            [ExecutionOutputStreamKind.StandardOutput, generation, text])!;
}
