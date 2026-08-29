using System.Net.Http;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class ApiLocalTestConsoleServiceTests
{
    [Fact]
    public void EventBuffer_TrimsOldestRowsAndReportsRetention()
    {
        var buffer = new ApiLocalTestEventBuffer(capacity: 3);

        Assert.False(buffer.Add(CreateRow(1)));
        Assert.False(buffer.Add(CreateRow(2)));
        Assert.False(buffer.Add(CreateRow(3)));
        Assert.True(buffer.Add(CreateRow(4)));

        Assert.Equal([2L, 3L, 4L], buffer.Items.Select(item => item.Sequence).ToArray());
        Assert.Equal(1, buffer.TrimmedCount);
    }

    [Theory]
    [InlineData(ApiStreamingInvocationEventKind.Output, "Output")]
    [InlineData(ApiStreamingInvocationEventKind.Warning, "Warning")]
    [InlineData(ApiStreamingInvocationEventKind.Verbose, "Verbose")]
    [InlineData(ApiStreamingInvocationEventKind.Debug, "Debug")]
    [InlineData(ApiStreamingInvocationEventKind.Information, "Information")]
    [InlineData(ApiStreamingInvocationEventKind.Error, "Error")]
    public void EventRow_PreservesDistinctPowerShellStreamLabels(ApiStreamingInvocationEventKind kind, string expectedStream)
    {
        var row = ApiLocalTestEventRow.FromEvent(new ApiStreamingInvocationEvent(
            "invocation",
            "endpoint",
            null,
            null,
            7,
            kind,
            DateTimeOffset.UtcNow,
            Message: "stream message"));

        Assert.Equal(expectedStream, row.Stream);
        Assert.Equal(kind.ToString(), row.EventKind);
        Assert.Contains("stream message", row.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_SuccessStreamsOrderedEventsAndCanRunAgain()
    {
        var transport = new FakeTransport(async (_, stateChanged, eventReceived, cancellationToken) =>
        {
            stateChanged(ApiLocalTestSessionState.Running);
            eventReceived(CreateEvent(1, ApiStreamingInvocationEventKind.InvocationStarted));
            eventReceived(CreateEvent(2, ApiStreamingInvocationEventKind.Output, "first"));
            eventReceived(CreateEvent(3, ApiStreamingInvocationEventKind.InvocationCompleted));
            await Task.Yield();
            return CreateResponse(true, "completed");
        });
        await using var service = new ApiLocalTestConsoleService(transport);
        var states = new List<ApiLocalTestSessionState>();
        service.StateChanged += (_, args) => states.Add(args.State);

        var first = await service.RunAsync(CreateRequest());
        var second = await service.RunAsync(CreateRequest());

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(ApiLocalTestSessionState.Completed, service.State);
        Assert.Contains(ApiLocalTestSessionState.Connecting, states);
        Assert.Contains(ApiLocalTestSessionState.Running, states);
        Assert.Equal([1L, 2L, 3L], service.EventBuffer.Items.Select(item => item.Sequence).ToArray());
    }

    [Fact]
    public async Task Session_FailureReturnsToReusableTerminalState()
    {
        var calls = 0;
        var transport = new FakeTransport((_, stateChanged, _, _) =>
        {
            stateChanged(ApiLocalTestSessionState.Running);
            calls++;
            return Task.FromResult(CreateResponse(calls > 1, calls > 1 ? "recovered" : "connection refused"));
        });
        await using var service = new ApiLocalTestConsoleService(transport);

        var states = new List<ApiLocalTestSessionState>();
        service.StateChanged += (_, args) => states.Add(args.State);
        var failed = await service.RunAsync(CreateRequest());
        Assert.Equal(ApiLocalTestSessionState.Failed, service.State);
        var recovered = await service.RunAsync(CreateRequest());

        Assert.False(failed.Succeeded);
        Assert.Contains(ApiLocalTestSessionState.Failed, states);
        Assert.True(recovered.Succeeded);
        Assert.Equal(ApiLocalTestSessionState.Completed, service.State);
    }

    [Fact]
    public async Task Session_CancellationTransitionsToCanceledAndAllowsReuse()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSecondRun = false;
        var transport = new FakeTransport(async (_, stateChanged, _, cancellationToken) =>
        {
            stateChanged(ApiLocalTestSessionState.Running);
            if (!allowSecondRun)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return CreateResponse(true, "reused");
        });
        await using var service = new ApiLocalTestConsoleService(transport);

        var firstTask = service.RunAsync(CreateRequest());
        await started.Task;
        service.Cancel();
        var canceled = await firstTask;
        Assert.Equal(ApiLocalTestSessionState.Canceled, service.State);

        allowSecondRun = true;
        var reused = await service.RunAsync(CreateRequest());

        Assert.True(canceled.WasCanceled);
        Assert.True(reused.Succeeded);
        Assert.Equal(ApiLocalTestSessionState.Completed, service.State);
    }

    [Fact]
    public async Task Session_DuplicateRunIsRejectedWhileActive()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport(async (_, stateChanged, _, cancellationToken) =>
        {
            stateChanged(ApiLocalTestSessionState.Running);
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateResponse(true, "unexpected");
        });
        await using var service = new ApiLocalTestConsoleService(transport);

        var firstTask = service.RunAsync(CreateRequest());
        await started.Task;
        var duplicate = await service.RunAsync(CreateRequest());
        service.Cancel();
        await firstTask;

        Assert.False(duplicate.Succeeded);
        Assert.Contains("already running", duplicate.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EventDetails_IncludeNormalizedContractAndTerminalMetadata()
    {
        var row = ApiLocalTestEventRow.FromEvent(new ApiStreamingInvocationEvent(
            "invocation",
            "endpoint",
            "connection",
            "session",
            9,
            ApiStreamingInvocationEventKind.InvocationCompleted,
            DateTimeOffset.Parse("2026-08-29T12:34:56Z"),
            Message: "done",
            StatusCode: "success",
            ElapsedMilliseconds: 123));

        Assert.True(row.IsTerminal);
        Assert.Equal("success", row.TerminalStatus);
        Assert.Contains("InvocationCompleted", row.SerializedJson, StringComparison.Ordinal);
        Assert.Contains("123", row.SerializedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponseFormatting_IndentsJsonAndCapsLargeText()
    {
        var formatted = ApiLocalTestTransportClient.FormatResponseBody("{\"name\":\"widget\",\"count\":2}");
        var large = ApiLocalTestTransportClient.FormatResponseBody(new string('x', 70_000));

        Assert.Contains(Environment.NewLine, formatted, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"widget\"", formatted, StringComparison.Ordinal);
        Assert.Equal(65_536, large.Length);
        Assert.EndsWith("...", large, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorPresentation_UsesUserSafeMessagesForConnectionFailures()
    {
        Assert.Contains("could not be reached", ApiLocalTestTransportClient.DescribeException(new HttpRequestException()), StringComparison.Ordinal);
        Assert.Contains("WebSocket connection failed", ApiLocalTestTransportClient.DescribeException(new System.Net.WebSockets.WebSocketException()), StringComparison.Ordinal);
        Assert.Contains("Developer Diagnostics", ApiLocalTestTransportClient.DescribeException(new InvalidOperationException()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_DuplicateTerminalEventsArePresentedOnce()
    {
        var transport = new FakeTransport((_, stateChanged, eventReceived, _) =>
        {
            stateChanged(ApiLocalTestSessionState.Running);
            eventReceived(CreateEvent(1, ApiStreamingInvocationEventKind.InvocationCompleted));
            eventReceived(CreateEvent(2, ApiStreamingInvocationEventKind.InvocationFailed));
            return Task.FromResult(CreateResponse(true, "completed"));
        });
        await using var service = new ApiLocalTestConsoleService(transport);

        await service.RunAsync(CreateRequest());

        Assert.Single(service.EventBuffer.Items, item => item.IsTerminal);
    }

    private static ApiLocalTestRequest CreateRequest()
        => new(ApiTransport.Rest, new Uri("http://127.0.0.1/api/test"), HttpMethod.Get, null, new Dictionary<string, string>(), TimeSpan.FromSeconds(5));

    private static ApiLocalTestConsoleResponse CreateResponse(bool succeeded, string message)
        => new(succeeded, new Uri("http://127.0.0.1/api/test"), succeeded ? 200 : 503, succeeded ? "OK" : "Service Unavailable", new Dictionary<string, string>(), message, 12, message);

    private static ApiStreamingInvocationEvent CreateEvent(long sequence, ApiStreamingInvocationEventKind kind, string? message = null)
        => new("invocation", "endpoint", null, null, sequence, kind, DateTimeOffset.UtcNow, Message: message, StatusCode: kind is ApiStreamingInvocationEventKind.InvocationCompleted ? "success" : null);

    private static ApiLocalTestEventRow CreateRow(long sequence)
        => ApiLocalTestEventRow.FromEvent(CreateEvent(sequence, ApiStreamingInvocationEventKind.Output, sequence.ToString()));

    private sealed class FakeTransport : IApiLocalTestTransportClient
    {
        private readonly Func<ApiLocalTestRequest, Action<ApiLocalTestSessionState>, Action<ApiStreamingInvocationEvent>, CancellationToken, Task<ApiLocalTestConsoleResponse>> _execute;

        public FakeTransport(Func<ApiLocalTestRequest, Action<ApiLocalTestSessionState>, Action<ApiStreamingInvocationEvent>, CancellationToken, Task<ApiLocalTestConsoleResponse>> execute)
            => _execute = execute;

        public Task<ApiLocalTestConsoleResponse> ExecuteAsync(
            ApiLocalTestRequest request,
            Action<ApiLocalTestSessionState> stateChanged,
            Action<ApiStreamingInvocationEvent> eventReceived,
            CancellationToken cancellationToken)
            => _execute(request, stateChanged, eventReceived, cancellationToken);
    }
}
