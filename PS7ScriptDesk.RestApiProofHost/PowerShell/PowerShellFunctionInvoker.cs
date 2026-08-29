using System.Management.Automation;
using Microsoft.Extensions.Logging;

namespace PS7ScriptDesk.RestApiProofHost.PowerShell;

public sealed class PowerShellFunctionInvoker : IPowerShellFunctionInvoker
{
    private static readonly TimeSpan StopCleanupTimeout = TimeSpan.FromSeconds(2);
    private readonly ILogger<PowerShellFunctionInvoker>? _logger;

    public PowerShellFunctionInvoker(ILogger<PowerShellFunctionInvoker>? logger = null)
    {
        _logger = logger;
    }

    public async Task<ApiInvocationResult> InvokeAsync(
        ApiInvocationRequest request,
        RunspacePoolLease poolLease,
        int retainedStreamLimit,
        CancellationToken cancellationToken,
        Func<ApiInvocationStatus> cancellationStatusProvider,
        PowerShellInvocationStreamSink? streamSink = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(poolLease);
        ArgumentNullException.ThrowIfNull(cancellationStatusProvider);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var powerShell = System.Management.Automation.PowerShell.Create();
        using var input = new PSDataCollection<PSObject>();
        using var output = new PSDataCollection<PSObject>();
        using var invocationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var liveRecordSubscriptions = streamSink is null
            ? Array.Empty<Action>()
            : AttachLiveRecordHandlers(output, powerShell.Streams, streamSink, invocationCancellation);
        input.Complete();
        powerShell.RunspacePool = poolLease.Pool;
        powerShell.AddCommand(request.FunctionName, useLocalScope: true);
        foreach (var parameter in request.Parameters)
        {
            powerShell.AddParameter(parameter.Key, parameter.Value);
        }

        IAsyncResult? asyncResult;
        try
        {
            asyncResult = powerShell.BeginInvoke<PSObject, PSObject>(input, output);
        }
        catch (Exception ex)
        {
            DetachLiveRecordHandlers(liveRecordSubscriptions);
            stopwatch.Stop();
            _logger?.LogError(ex, "PowerShell invocation could not start for function {FunctionName}.", request.FunctionName);
            var status = PowerShellFailureClassifier.ClassifyTerminatingException(ex);
            return ApiInvocationResult.Failure(
                status,
                SafeMessageForStatus(status),
                elapsed: stopwatch.Elapsed,
                poolGeneration: poolLease.Generation,
                requiresPoolRebuild: true);
        }

        var completion = Task.Run(() =>
        {
            asyncResult.AsyncWaitHandle.WaitOne();
            return asyncResult;
        });

        var cancellationSignal = new TaskCompletionSource<ApiInvocationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = invocationCancellation.Token.Register(() =>
            cancellationSignal.TrySetResult(cancellationStatusProvider()));

        try
        {
            var completedTask = await Task.WhenAny(completion, cancellationSignal.Task);
            if (completedTask == cancellationSignal.Task)
            {
                var status = await cancellationSignal.Task;
                await StopPowerShellAsync(powerShell, request.FunctionName);
                var settled = await Task.WhenAny(completion, Task.Delay(StopCleanupTimeout));
                stopwatch.Stop();
                poolLease.RequestPoolRebuild = true;

                if (settled == completion)
                {
                    TryEndInvoke(powerShell, asyncResult);
                }

                return ApiInvocationResult.Failure(
                    status,
                    status == ApiInvocationStatus.InvocationTimedOut
                        ? "The PowerShell invocation timed out."
                        : "The PowerShell invocation was canceled.",
                    CaptureStreams(powerShell.Streams, retainedStreamLimit),
                    stopwatch.Elapsed,
                    poolLease.Generation,
                    requiresPoolRebuild: true);
            }

            powerShell.EndInvoke(asyncResult);
            stopwatch.Stop();
            var streams = CaptureStreams(powerShell.Streams, retainedStreamLimit);
            if (powerShell.HadErrors || powerShell.Streams.Error.Count > 0)
            {
                var status = PowerShellFailureClassifier.ClassifyNonTerminatingErrors(powerShell.Streams.Error);
                return ApiInvocationResult.Failure(
                    status,
                    SafeMessageForStatus(status),
                    streams,
                    stopwatch.Elapsed,
                    poolLease.Generation);
            }

            return ApiInvocationResult.Success(output.ToList(), streams, stopwatch.Elapsed, poolLease.Generation);
        }
        catch (PipelineStoppedException)
        {
            stopwatch.Stop();
            poolLease.RequestPoolRebuild = true;
            var status = cancellationStatusProvider();
            return ApiInvocationResult.Failure(
                status,
                status == ApiInvocationStatus.InvocationTimedOut
                    ? "The PowerShell invocation timed out."
                    : "The PowerShell invocation was canceled.",
                CaptureStreams(powerShell.Streams, retainedStreamLimit),
                stopwatch.Elapsed,
                poolLease.Generation,
                requiresPoolRebuild: true);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger?.LogError(ex, "PowerShell invocation failed for function {FunctionName}.", request.FunctionName);
            var streams = CaptureStreams(powerShell.Streams, retainedStreamLimit).ToList();
            if (streams.Count < retainedStreamLimit)
            {
                streams.Add(new ApiInvocationStreamRecord("Exception", Cap(SanitizeStreamMessage(ex.GetType().Name))));
            }

            var status = PowerShellFailureClassifier.ClassifyTerminatingException(ex);
            return ApiInvocationResult.Failure(
                status,
                SafeMessageForStatus(status),
                streams,
                stopwatch.Elapsed,
                poolLease.Generation);
        }
        finally
        {
            DetachLiveRecordHandlers(liveRecordSubscriptions);
        }
    }

    private IReadOnlyList<Action> AttachLiveRecordHandlers(
        PSDataCollection<PSObject> output,
        PSDataStreams streams,
        PowerShellInvocationStreamSink streamSink,
        CancellationTokenSource invocationCancellation)
    {
        var subscriptions = new List<Action>(6);

        EventHandler<DataAddedEventArgs> outputHandler = (_, args) =>
            PublishLiveRecord(
                PowerShellInvocationStreamRecord.ForOutput(output[args.Index]),
                streamSink,
                invocationCancellation);
        output.DataAdded += outputHandler;
        subscriptions.Add(() => output.DataAdded -= outputHandler);

        AttachStreamHandler(
            streams.Warning,
            PowerShellInvocationStreamKind.Warning,
            record => record.Message,
            streamSink,
            invocationCancellation,
            subscriptions);
        AttachStreamHandler(
            streams.Verbose,
            PowerShellInvocationStreamKind.Verbose,
            record => record.Message,
            streamSink,
            invocationCancellation,
            subscriptions);
        AttachStreamHandler(
            streams.Debug,
            PowerShellInvocationStreamKind.Debug,
            record => record.Message,
            streamSink,
            invocationCancellation,
            subscriptions);
        AttachStreamHandler(
            streams.Information,
            PowerShellInvocationStreamKind.Information,
            record => record.MessageData?.ToString() ?? string.Empty,
            streamSink,
            invocationCancellation,
            subscriptions);
        AttachStreamHandler(
            streams.Error,
            PowerShellInvocationStreamKind.Error,
            FormatSafeErrorRecord,
            streamSink,
            invocationCancellation,
            subscriptions);

        _logger?.LogInformation("PowerShell live stream handlers attached.");
        return subscriptions;
    }

    private void AttachStreamHandler<TRecord>(
        PSDataCollection<TRecord> stream,
        PowerShellInvocationStreamKind kind,
        Func<TRecord, string?> messageFactory,
        PowerShellInvocationStreamSink streamSink,
        CancellationTokenSource invocationCancellation,
        List<Action> subscriptions)
    {
        EventHandler<DataAddedEventArgs> handler = (_, args) =>
            PublishLiveRecord(
                PowerShellInvocationStreamRecord.ForStream(kind, Cap(SanitizeStreamMessage(messageFactory(stream[args.Index])))),
                streamSink,
                invocationCancellation);
        stream.DataAdded += handler;
        subscriptions.Add(() => stream.DataAdded -= handler);
    }

    private void PublishLiveRecord(
        PowerShellInvocationStreamRecord record,
        PowerShellInvocationStreamSink streamSink,
        CancellationTokenSource invocationCancellation)
    {
        if (invocationCancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            if (streamSink.PublishAsync(record, invocationCancellation.Token).AsTask().GetAwaiter().GetResult())
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "PowerShell live stream record publisher failed.");
        }

        invocationCancellation.Cancel();
    }

    private static void DetachLiveRecordHandlers(IReadOnlyList<Action> subscriptions)
    {
        foreach (var unsubscribe in subscriptions)
        {
            try
            {
                unsubscribe();
            }
            catch
            {
            }
        }
    }

    private static void TryEndInvoke(System.Management.Automation.PowerShell powerShell, IAsyncResult? asyncResult)
    {
        if (asyncResult is null)
        {
            return;
        }

        try
        {
            powerShell.EndInvoke(asyncResult);
        }
        catch
        {
        }
    }

    private async Task StopPowerShellAsync(System.Management.Automation.PowerShell powerShell, string functionName)
    {
        try
        {
            await powerShell.StopAsync(callback: null, state: null).WaitAsync(StopCleanupTimeout);
            _logger?.LogWarning("Stopped PowerShell invocation for function {FunctionName}.", functionName);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "PowerShell stop did not settle cleanly for function {FunctionName}.", functionName);
        }
    }

    private static IReadOnlyList<ApiInvocationStreamRecord> CaptureStreams(PSDataStreams streams, int retainedStreamLimit)
    {
        var limit = Math.Max(0, retainedStreamLimit);
        if (limit == 0)
        {
            return Array.Empty<ApiInvocationStreamRecord>();
        }

        var records = new List<ApiInvocationStreamRecord>(limit);
        AddRecords(records, "Error", streams.Error.Select(FormatSafeErrorRecord), limit);
        AddRecords(records, "Warning", streams.Warning.Select(record => record.Message), limit);
        AddRecords(records, "Verbose", streams.Verbose.Select(record => record.Message), limit);
        AddRecords(records, "Debug", streams.Debug.Select(record => record.Message), limit);
        AddRecords(records, "Information", streams.Information.Select(record => record.MessageData?.ToString() ?? string.Empty), limit);
        return records;
    }

    private static void AddRecords(List<ApiInvocationStreamRecord> records, string streamName, IEnumerable<string> messages, int limit)
    {
        foreach (var message in messages)
        {
            if (records.Count >= limit)
            {
                return;
            }

            records.Add(new ApiInvocationStreamRecord(streamName, Cap(SanitizeStreamMessage(message))));
        }
    }

    private static string FormatSafeErrorRecord(ErrorRecord record)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(record.FullyQualifiedErrorId))
        {
            parts.Add(record.FullyQualifiedErrorId);
        }

        parts.Add(record.CategoryInfo.Category.ToString());
        parts.Add(record.Exception.GetType().Name);
        return string.Join("; ", parts);
    }

    private static string SafeMessageForStatus(ApiInvocationStatus status)
        => status switch
        {
            ApiInvocationStatus.PowerShellParameterBindingFailure => "The PowerShell invocation parameters are invalid.",
            ApiInvocationStatus.PowerShellValidationFailure => "The PowerShell invocation parameters failed validation.",
            ApiInvocationStatus.PowerShellNonTerminatingError => "The configured PowerShell operation reported a non-terminating error.",
            _ => "The configured PowerShell operation could not be completed."
        };

    private static string SanitizeStreamMessage(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();
    }

    private static string Cap(string? value)
    {
        const int maximumLength = 512;
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maximumLength ? value : value[..maximumLength];
    }
}

public sealed class ProofPowerShellInvocationException : Exception
{
    public ProofPowerShellInvocationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
