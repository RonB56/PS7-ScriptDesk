using System.Management.Automation;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.RestApiProofHost.PowerShell;

namespace PS7ScriptDesk.Tests;

public sealed class StreamingInvocationContractTests
{
    [Fact]
    public async Task StreamingInvocation_EmitsOrderedOutputStreamsAndOneTerminalEvent()
    {
        var invoker = new StubInvoker((_, _) => Task.FromResult(
            ApiInvocationResult.Success(
                [PSObject.AsPSObject("first"), PSObject.AsPSObject("second")],
                [
                    new ApiInvocationStreamRecord("Warning", "warning text"),
                    new ApiInvocationStreamRecord("Error", "error text")
                ],
                TimeSpan.FromMilliseconds(12),
                1)));
        await using var coordinator = await CreateCoordinatorAsync(invoker);

        await using var session = await coordinator.StartStreamingInvocationAsync(CreateRequest("ordered"));
        var events = await ReadEventsAsync(session);

        Assert.Equal(ApiStreamingInvocationEventKind.InvocationStarted, events[0].Kind);
        Assert.Equal(["first", "second"], events
            .Where(item => item.Kind == ApiStreamingInvocationEventKind.Output)
            .Select(item => Assert.IsType<string>(item.Payload)));
        Assert.Contains(events, item => item.Kind == ApiStreamingInvocationEventKind.Warning && item.Message == "warning text");
        Assert.Contains(events, item => item.Kind == ApiStreamingInvocationEventKind.Error && item.Message == "error text");
        Assert.Equal(ApiStreamingInvocationEventKind.InvocationCompleted, events[^1].Kind);
        Assert.Single(events, item => item.IsTerminal);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value), events.Select(item => item.Sequence));
        Assert.Equal("success", events[^1].StatusCode);
    }

    [Fact]
    public async Task StreamingInvocation_CancelProducesTerminalCanceledEventWithoutRawException()
    {
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoker = new StubInvoker(async (_, cancellationToken) =>
        {
            invoked.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return ApiInvocationResult.Failure(ApiInvocationStatus.CallerCanceled, "The PowerShell invocation was canceled.");
            }

            return ApiInvocationResult.Failure(ApiInvocationStatus.InternalFailure, "Unexpected completion.");
        });
        await using var coordinator = await CreateCoordinatorAsync(invoker);
        await using var session = await coordinator.StartStreamingInvocationAsync(CreateRequest("cancel"));
        var readTask = ReadEventsAsync(session);

        await invoked.Task;
        session.Cancel();
        var events = await readTask;

        Assert.Equal(ApiStreamingInvocationEventKind.InvocationCanceled, events[^1].Kind);
        Assert.Equal("caller-canceled", events[^1].StatusCode);
        Assert.Single(events, item => item.IsTerminal);
    }

    [Fact]
    public async Task StreamingInvocation_MapsTimeoutAndNeverPublishesExceptionText()
    {
        var invoker = new StubInvoker((_, _) => Task.FromResult(
            ApiInvocationResult.Failure(
                ApiInvocationStatus.InvocationTimedOut,
                "The PowerShell invocation timed out.")));
        await using var coordinator = await CreateCoordinatorAsync(invoker);

        await using var session = await coordinator.StartStreamingInvocationAsync(CreateRequest("timeout"));
        var events = await ReadEventsAsync(session);
        var terminal = events[^1];

        Assert.Equal(ApiStreamingInvocationEventKind.InvocationFailed, terminal.Kind);
        Assert.Equal("invocation-timeout", terminal.StatusCode);
        Assert.Equal("The PowerShell invocation timed out.", terminal.Message);
        Assert.DoesNotContain("secret", terminal.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StreamingInvocation_MapsUnexpectedInvokerExceptionToSafeFailure()
    {
        var invoker = new StubInvoker((_, _) => throw new InvalidOperationException("secret implementation detail"));
        await using var coordinator = await CreateCoordinatorAsync(invoker);

        await using var session = await coordinator.StartStreamingInvocationAsync(CreateRequest("internal"));
        var events = await ReadEventsAsync(session);
        var terminal = events[^1];

        Assert.Equal(ApiStreamingInvocationEventKind.InvocationFailed, terminal.Kind);
        Assert.Equal("internal-failure", terminal.StatusCode);
        Assert.Equal("The streaming PowerShell invocation failed internally.", terminal.Message);
        Assert.DoesNotContain("secret", terminal.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StreamingEventChannel_AppliesBackpressureAndHonorsCancellation()
    {
        using var channel = new ApiStreamingInvocationEventChannel(1);
        var first = CreateEvent("channel", 1, ApiStreamingInvocationEventKind.Output, "one");
        var second = CreateEvent("channel", 2, ApiStreamingInvocationEventKind.Output, "two");
        Assert.True(await channel.WriteDataAsync(first, CancellationToken.None));

        var blockedWrite = channel.WriteDataAsync(second, CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.False(blockedWrite.IsCompleted);

        await using var reader = channel.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("one", reader.Current.Payload);
        Assert.True(await blockedWrite);

        using var cancellation = new CancellationTokenSource();
        var third = CreateEvent("channel", 3, ApiStreamingInvocationEventKind.Output, "three");
        var canceledWrite = channel.WriteDataAsync(third, cancellation.Token).AsTask();
        cancellation.Cancel();
        Assert.False(await canceledWrite);
    }

    [Fact]
    public async Task StreamingInvocations_UseIndependentSequencesAndPreserveCoordinatorQueueLimits()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var invoker = new StubInvoker(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            return ApiInvocationResult.Success([], [], TimeSpan.Zero, 1);
        });
        await using var coordinator = await CreateCoordinatorAsync(
            invoker,
            new ApiRuntimeOptions
            {
                RunspacePoolMinimum = 1,
                RunspacePoolMaximum = 1,
                MaximumConcurrentExecutions = 1,
                QueueLimit = 0,
                QueueWaitTimeout = TimeSpan.FromSeconds(1),
                DefaultInvocationTimeout = TimeSpan.FromSeconds(5)
            });

        await using var first = await coordinator.StartStreamingInvocationAsync(CreateRequest("first"));
        var firstEventsTask = ReadEventsAsync(first);
        await entered.Task;
        await using var second = await coordinator.StartStreamingInvocationAsync(CreateRequest("second"));
        var secondEvents = await ReadEventsAsync(second);

        Assert.Equal(ApiStreamingInvocationEventKind.InvocationFailed, secondEvents[^1].Kind);
        Assert.Equal("queue-full", secondEvents[^1].StatusCode);

        release.SetResult();
        var firstEvents = await firstEventsTask;
        Assert.Equal(ApiStreamingInvocationEventKind.InvocationCompleted, firstEvents[^1].Kind);
        Assert.Equal(Enumerable.Range(1, firstEvents.Count).Select(value => (long)value), firstEvents.Select(item => item.Sequence));
        Assert.Equal(Enumerable.Range(1, secondEvents.Count).Select(value => (long)value), secondEvents.Select(item => item.Sequence));
    }

    [Fact]
    public async Task StreamingInvocations_WithDifferentIdsRemainIndependent()
    {
        var invoker = new StubInvoker((request, _) => Task.FromResult(
            ApiInvocationResult.Success([PSObject.AsPSObject(request.FunctionName)], [], TimeSpan.Zero, 1)));
        await using var coordinator = await CreateCoordinatorAsync(invoker);

        await using var first = await coordinator.StartStreamingInvocationAsync(CreateRequest("one"));
        await using var second = await coordinator.StartStreamingInvocationAsync(CreateRequest("two"));
        var results = await Task.WhenAll(ReadEventsAsync(first), ReadEventsAsync(second));

        Assert.Equal("one", results[0][0].InvocationId);
        Assert.Equal("two", results[1][0].InvocationId);
        Assert.Equal(1L, results[0][0].Sequence);
        Assert.Equal(1L, results[1][0].Sequence);
    }

    [Fact]
    public async Task StreamingInvocation_PublishesPowerShellOutputBeforeInvocationCompletes()
    {
        await using var coordinator = await CreateRealCoordinatorAsync("Invoke-LiveStreamingTiming");
        await using var session = await coordinator.StartStreamingInvocationAsync(CreateRequest(
            "live-timing",
            functionName: "Invoke-LiveStreamingTiming"));
        await using var reader = session.ReadAllAsync().GetAsyncEnumerator();

        var started = await ReadNextAsync(reader);
        Assert.Equal(ApiStreamingInvocationEventKind.InvocationStarted, started.Kind);

        var first = await ReadNextMatchingAsync(reader, item => item.Kind == ApiStreamingInvocationEventKind.Output);
        Assert.Equal("first", Assert.IsType<string>(first.Payload));
        Assert.False(session.Completion.IsCompleted);

        var second = await ReadNextMatchingAsync(reader, item => item.Kind == ApiStreamingInvocationEventKind.Output);
        Assert.Equal("second", Assert.IsType<string>(second.Payload));
        Assert.False(session.Completion.IsCompleted);

        var remaining = await ReadRemainingAsync(reader);
        Assert.Contains(remaining, item => item.Kind == ApiStreamingInvocationEventKind.Output && (string?)item.Payload == "third");
        Assert.Equal(ApiStreamingInvocationEventKind.InvocationCompleted, remaining[^1].Kind);
        Assert.Equal(Enumerable.Range(1, 3 + remaining.Count).Select(value => (long)value),
            new[] { started, first, second }.Concat(remaining).Select(item => item.Sequence));
    }

    [Fact]
    public async Task StreamingInvocation_PublishesAllPowerShellRecordStreamsAsLiveEvents()
    {
        await using var coordinator = await CreateRealCoordinatorAsync("Invoke-LiveStreamingStreams");
        await using var session = await coordinator.StartStreamingInvocationAsync(CreateRequest(
            "live-streams",
            functionName: "Invoke-LiveStreamingStreams",
            eventCapacity: 16));

        var events = await ReadEventsAsync(session);

        Assert.Equal(ApiStreamingInvocationEventKind.InvocationStarted, events[0].Kind);
        Assert.Contains(events, item => item.Kind == ApiStreamingInvocationEventKind.Output && (string?)item.Payload == "out-1");
        Assert.Contains(events, item => item.Kind == ApiStreamingInvocationEventKind.Warning && item.Message == "warn-1");
        Assert.Contains(events, item => item.Kind == ApiStreamingInvocationEventKind.Verbose && item.Message == "verbose-1");
        Assert.Contains(events, item => item.Kind == ApiStreamingInvocationEventKind.Debug && item.Message == "debug-1");
        Assert.Contains(events, item => item.Kind == ApiStreamingInvocationEventKind.Information && item.Message == "info-1");
        Assert.Contains(events, item => item.Kind == ApiStreamingInvocationEventKind.Error && item.Message!.Contains("LiveStreamingNonTerminatingError", StringComparison.Ordinal));
        Assert.Equal(ApiStreamingInvocationEventKind.InvocationFailed, events[^1].Kind);
        Assert.Equal("powershell-nonterminating-error", events[^1].StatusCode);
        Assert.Single(events, item => item.IsTerminal);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value), events.Select(item => item.Sequence));
    }

    [Fact]
    public async Task StreamingInvocation_BackpressureKeepsProducerBoundedUntilConsumerDrains()
    {
        await using var coordinator = await CreateRealCoordinatorAsync("Invoke-LiveStreamingPressure");
        await using var session = await coordinator.StartStreamingInvocationAsync(CreateRequest(
            "live-pressure",
            functionName: "Invoke-LiveStreamingPressure",
            eventCapacity: 2));

        await Task.Delay(150);
        Assert.False(session.Completion.IsCompleted);

        var events = new List<ApiStreamingInvocationEvent>();
        await foreach (var item in session.ReadAllAsync())
        {
            events.Add(item);
            await Task.Delay(15);
        }

        Assert.Equal(ApiStreamingInvocationEventKind.InvocationStarted, events[0].Kind);
        Assert.Equal(12, events.Count(item => item.Kind == ApiStreamingInvocationEventKind.Output));
        Assert.Equal(ApiStreamingInvocationEventKind.InvocationCompleted, events[^1].Kind);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(value => (long)value), events.Select(item => item.Sequence));
    }

    [Fact]
    public async Task StreamingInvocation_CancellationDuringLiveOutputStopsPipelineAndEmitsOneTerminal()
    {
        await using var coordinator = await CreateRealCoordinatorAsync("Invoke-LiveStreamingCancellation");
        await using var session = await coordinator.StartStreamingInvocationAsync(CreateRequest(
            "live-cancel",
            functionName: "Invoke-LiveStreamingCancellation"));
        await using var reader = session.ReadAllAsync().GetAsyncEnumerator();

        var started = await ReadNextAsync(reader);
        Assert.Equal(ApiStreamingInvocationEventKind.InvocationStarted, started.Kind);

        var first = await ReadNextMatchingAsync(reader, item => item.Kind == ApiStreamingInvocationEventKind.Output);
        Assert.Equal("before-cancel", Assert.IsType<string>(first.Payload));

        session.Cancel();
        var remaining = await ReadRemainingAsync(reader);

        Assert.DoesNotContain(remaining, item => item.Kind == ApiStreamingInvocationEventKind.Output && (string?)item.Payload == "after-cancel");
        Assert.Equal(ApiStreamingInvocationEventKind.InvocationCanceled, remaining[^1].Kind);
        Assert.Equal("caller-canceled", remaining[^1].StatusCode);
        Assert.Single(new[] { started, first }.Concat(remaining), item => item.IsTerminal);
    }

    private static ApiStreamingInvocationRequest CreateRequest(string invocationId)
        => CreateRequest(invocationId, "Get-SystemInfo");

    private static ApiStreamingInvocationRequest CreateRequest(
        string invocationId,
        string functionName,
        int eventCapacity = 8)
        => new()
        {
            InvocationId = invocationId,
            EndpointId = "test-endpoint",
            FunctionName = functionName,
            EventCapacity = eventCapacity
        };

    private static async Task<List<ApiStreamingInvocationEvent>> ReadEventsAsync(ApiStreamingInvocationSession session)
    {
        var events = new List<ApiStreamingInvocationEvent>();
        await foreach (var item in session.ReadAllAsync())
        {
            events.Add(item);
        }

        return events;
    }

    private static async Task<ApiStreamingInvocationEvent> ReadNextAsync(IAsyncEnumerator<ApiStreamingInvocationEvent> reader)
    {
        var moveNext = reader.MoveNextAsync().AsTask();
        if (await Task.WhenAny(moveNext, Task.Delay(TimeSpan.FromSeconds(15))) != moveNext)
        {
            throw new TimeoutException("Timed out waiting for the next streaming event.");
        }

        Assert.True(await moveNext);
        return reader.Current;
    }

    private static async Task<ApiStreamingInvocationEvent> ReadNextMatchingAsync(
        IAsyncEnumerator<ApiStreamingInvocationEvent> reader,
        Func<ApiStreamingInvocationEvent, bool> predicate)
    {
        while (true)
        {
            var item = await ReadNextAsync(reader);
            if (predicate(item))
            {
                return item;
            }
        }
    }

    private static async Task<List<ApiStreamingInvocationEvent>> ReadRemainingAsync(
        IAsyncEnumerator<ApiStreamingInvocationEvent> reader)
    {
        var events = new List<ApiStreamingInvocationEvent>();
        while (true)
        {
            var item = await ReadNextAsync(reader);
            events.Add(item);
            if (item.IsTerminal)
            {
                return events;
            }
        }
    }

    private static async Task<PowerShellInvocationCoordinator> CreateCoordinatorAsync(
        StubInvoker invoker,
        ApiRuntimeOptions? runtimeOptions = null)
    {
        var poolManager = new RunspacePoolManager();
        var coordinator = new PowerShellInvocationCoordinator(poolManager, invoker);
        await coordinator.InitializeAsync(
            ResolveProofScriptPath(),
            ["Get-SystemInfo"],
            runtimeOptions ?? new ApiRuntimeOptions(),
            CancellationToken.None);
        return coordinator;
    }

    private static async Task<PowerShellInvocationCoordinator> CreateRealCoordinatorAsync(
        params string[] allowedFunctions)
        => await CreateRealCoordinatorAsync(null, allowedFunctions);

    private static async Task<PowerShellInvocationCoordinator> CreateRealCoordinatorAsync(
        ApiRuntimeOptions? runtimeOptions,
        params string[] allowedFunctions)
    {
        var poolManager = new RunspacePoolManager();
        var coordinator = new PowerShellInvocationCoordinator(poolManager, new PowerShellFunctionInvoker());
        await coordinator.InitializeAsync(
            ResolveProofScriptPath(),
            allowedFunctions,
            runtimeOptions ?? new ApiRuntimeOptions(),
            CancellationToken.None);
        return coordinator;
    }

    private static string ResolveProofScriptPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "PS7ScriptDesk.RestApiProofHost", "Scripts", "TestApi.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("Could not locate the proof host test script.");
    }

    private static ApiStreamingInvocationEvent CreateEvent(
        string invocationId,
        long sequence,
        ApiStreamingInvocationEventKind kind,
        object? payload)
        => new(invocationId, "test-endpoint", null, null, sequence, kind, DateTimeOffset.UtcNow, payload);

    private sealed class StubInvoker : IPowerShellFunctionInvoker
    {
        private readonly Func<ApiInvocationRequest, CancellationToken, Task<ApiInvocationResult>> _handler;

        public StubInvoker(Func<ApiInvocationRequest, CancellationToken, Task<ApiInvocationResult>> handler)
            => _handler = handler;

        public Task<ApiInvocationResult> InvokeAsync(
            ApiInvocationRequest request,
            RunspacePoolLease poolLease,
            int retainedStreamLimit,
            CancellationToken cancellationToken,
            Func<ApiInvocationStatus> cancellationStatusProvider,
            PowerShellInvocationStreamSink? streamSink = null)
            => InvokeAndPublishAsync(request, cancellationToken, streamSink);

        private async Task<ApiInvocationResult> InvokeAndPublishAsync(
            ApiInvocationRequest request,
            CancellationToken cancellationToken,
            PowerShellInvocationStreamSink? streamSink)
        {
            var result = await _handler(request, cancellationToken);
            if (streamSink is null)
            {
                return result;
            }

            foreach (var item in result.Output)
            {
                if (!await streamSink.PublishAsync(PowerShellInvocationStreamRecord.ForOutput(item), cancellationToken))
                {
                    return result;
                }
            }

            foreach (var stream in result.Streams)
            {
                var kind = stream.StreamName.ToUpperInvariant() switch
                {
                    "WARNING" => PowerShellInvocationStreamKind.Warning,
                    "VERBOSE" => PowerShellInvocationStreamKind.Verbose,
                    "DEBUG" => PowerShellInvocationStreamKind.Debug,
                    "INFORMATION" => PowerShellInvocationStreamKind.Information,
                    "ERROR" => PowerShellInvocationStreamKind.Error,
                    _ => PowerShellInvocationStreamKind.Error
                };
                if (!await streamSink.PublishAsync(PowerShellInvocationStreamRecord.ForStream(kind, stream.Message), cancellationToken))
                {
                    return result;
                }
            }

            return result;
        }
    }
}
