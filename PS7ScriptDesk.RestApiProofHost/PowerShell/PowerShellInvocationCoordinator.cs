using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.RestApiProofHost.Api;

namespace PS7ScriptDesk.RestApiProofHost.PowerShell;

public sealed class PowerShellInvocationCoordinator : IAsyncDisposable
{
    private readonly RunspacePoolManager _poolManager;
    private readonly IPowerShellFunctionInvoker _functionInvoker;
    private readonly ILogger<PowerShellInvocationCoordinator>? _logger;
    private readonly PowerShellInvocationMetrics _metrics = new();
    private CancellationTokenSource _shutdown = new();
    private SemaphoreSlim? _admissionSlots;
    private SemaphoreSlim? _executionSlots;
    private ApiRuntimeOptions _runtimeOptions = ApiRuntimeOptions.CreateDefault();
    private bool _initialized;
    private bool _disposed;
    private int _maxConcurrency;
    private int _queueCapacity;

    public PowerShellInvocationCoordinator(
        RunspacePoolManager poolManager,
        IPowerShellFunctionInvoker functionInvoker,
        ILogger<PowerShellInvocationCoordinator>? logger = null)
    {
        _poolManager = poolManager;
        _functionInvoker = functionInvoker;
        _logger = logger;
    }

    public bool RequiredFunctionsVerified => _poolManager.RequiredFunctionsVerified;
    public bool IsDisposed => _disposed;
    public PowerShellInvocationMetrics Metrics => _metrics;

    public async Task InitializeAsync(
        string scriptPath,
        IEnumerable<string> allowedFunctionNames,
        ApiRuntimeOptions runtimeOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeOptions);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _runtimeOptions = runtimeOptions;
        var poolMaximum = runtimeOptions.RunspacePoolMaximum > 0
            ? runtimeOptions.RunspacePoolMaximum
            : Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
        _maxConcurrency = runtimeOptions.MaximumConcurrentExecutions > 0
            ? runtimeOptions.MaximumConcurrentExecutions
            : poolMaximum;
        _maxConcurrency = Math.Max(1, Math.Min(_maxConcurrency, Math.Max(1, poolMaximum)));
        _queueCapacity = Math.Max(0, runtimeOptions.QueueLimit);

        _admissionSlots?.Dispose();
        _executionSlots?.Dispose();
        _admissionSlots = new SemaphoreSlim(_maxConcurrency + _queueCapacity, _maxConcurrency + _queueCapacity);
        _executionSlots = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);

        await _poolManager.InitializeAsync(scriptPath, allowedFunctionNames, runtimeOptions, cancellationToken);
        _initialized = true;
        _logger?.LogInformation(
            "Initialized PowerShell invocation coordinator with max concurrency {MaxConcurrency} and queue capacity {QueueCapacity}.",
            _maxConcurrency,
            _queueCapacity);
    }

    public async Task<ApiInvocationResult> InvokeAsync(ApiInvocationRequest request, CancellationToken cancellationToken)
        => await InvokeCoreAsync(request, cancellationToken, streamSink: null).ConfigureAwait(false);

    private async Task<ApiInvocationResult> InvokeCoreAsync(
        ApiInvocationRequest request,
        CancellationToken cancellationToken,
        PowerShellInvocationStreamSink? streamSink)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_disposed || _shutdown.IsCancellationRequested)
        {
            return ApiInvocationResult.Failure(ApiInvocationStatus.HostUnavailable, "The PowerShell host is shutting down.");
        }

        if (!_initialized || _admissionSlots is null || _executionSlots is null)
        {
            return ApiInvocationResult.Failure(ApiInvocationStatus.HostUnavailable, "The PowerShell host is not initialized.");
        }

        if (!_poolManager.IsFunctionAllowed(request.FunctionName))
        {
            return ApiInvocationResult.Failure(ApiInvocationStatus.InvalidFunction, "The requested PowerShell function is not configured.");
        }

        using var callerAndShutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        if (!_admissionSlots.Wait(0))
        {
            _metrics.IncrementRejectedQueueFull();
            _logger?.LogWarning("Rejected PowerShell invocation for function {FunctionName}: queue full.", request.FunctionName);
            return ApiInvocationResult.Failure(ApiInvocationStatus.QueueFull, "The PowerShell invocation queue is full.");
        }

        _metrics.IncrementAccepted();
        var stopwatch = Stopwatch.StartNew();
        var queued = false;
        var executionSlotHeld = false;
        try
        {
            if (_executionSlots.CurrentCount == 0)
            {
                queued = true;
                _metrics.IncrementQueued();
                _logger?.LogInformation("Queued PowerShell invocation for function {FunctionName}.", request.FunctionName);
            }

            var queueWait = _runtimeOptions.QueueWaitTimeout <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(10)
                : _runtimeOptions.QueueWaitTimeout;
            var executionAdmitted = await _executionSlots.WaitAsync(queueWait, callerAndShutdown.Token);
            if (!executionAdmitted)
            {
                _metrics.IncrementQueueTimeout();
                return ApiInvocationResult.Failure(ApiInvocationStatus.QueueWaitTimedOut, "The PowerShell invocation queue wait timed out.");
            }

            executionSlotHeld = true;
        }
        catch (OperationCanceledException)
        {
            _metrics.IncrementCallerCanceled();
            return ApiInvocationResult.Failure(
                cancellationToken.IsCancellationRequested ? ApiInvocationStatus.CallerCanceled : ApiInvocationStatus.HostUnavailable,
                cancellationToken.IsCancellationRequested
                    ? "The PowerShell invocation was canceled before execution."
                    : "The PowerShell host is shutting down.");
        }
        finally
        {
            if (queued)
            {
                _metrics.DecrementQueued();
            }
        }

        try
        {
            _metrics.IncrementActive();
            _logger?.LogInformation("Started PowerShell invocation for function {FunctionName}.", request.FunctionName);

            var timeout = request.Timeout ?? _runtimeOptions.DefaultInvocationTimeout;
            if (timeout <= TimeSpan.Zero)
            {
                timeout = TimeSpan.FromSeconds(30);
            }

            using var timeoutSource = new CancellationTokenSource(timeout);
            using var invocationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                callerAndShutdown.Token,
                timeoutSource.Token);

            await using var lease = await _poolManager.AcquireLeaseAsync(invocationCancellation.Token);
            var result = await _functionInvoker.InvokeAsync(
                request,
                lease,
                _runtimeOptions.MaximumRetainedStreamEntries,
                invocationCancellation.Token,
                () => ClassifyCancellation(cancellationToken, timeoutSource.Token),
                streamSink);

            TrackResult(result);
            stopwatch.Stop();
            _logger?.LogInformation(
                "Completed PowerShell invocation for function {FunctionName} with status {Status} in {ElapsedMilliseconds} ms.",
                request.FunctionName,
                result.Status,
                stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException)
        {
            var status = cancellationToken.IsCancellationRequested
                ? ApiInvocationStatus.CallerCanceled
                : ApiInvocationStatus.HostUnavailable;
            var result = ApiInvocationResult.Failure(
                status,
                status == ApiInvocationStatus.CallerCanceled
                    ? "The PowerShell invocation was canceled."
                    : "The PowerShell host is shutting down.");
            TrackResult(result);
            return result;
        }
        catch (Exception ex)
        {
            _metrics.IncrementInternalFailure();
            _logger?.LogError(ex, "Internal PowerShell invocation failure for function {FunctionName}.", request.FunctionName);
            return ApiInvocationResult.Failure(ApiInvocationStatus.InternalFailure, "The PowerShell invocation failed internally.");
        }
        finally
        {
            _metrics.DecrementActive();
            if (executionSlotHeld)
            {
                _executionSlots.Release();
            }

            _admissionSlots.Release();
        }
    }

    public Task<ApiStreamingInvocationSession> StartStreamingInvocationAsync(
        ApiStreamingInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.InvocationId))
        {
            throw new ArgumentException("InvocationId is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.EndpointId))
        {
            throw new ArgumentException("EndpointId is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.FunctionName))
        {
            throw new ArgumentException("FunctionName is required.", nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.EventCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Event capacity must be positive.");
        }

        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var events = new ApiStreamingInvocationEventChannel(request.EventCapacity);
        var session = new ApiStreamingInvocationSession(request, events, linkedCancellation);
        _logger?.LogInformation(
            "Started streaming PowerShell invocation {InvocationId} for endpoint {EndpointId} with event capacity {EventCapacity}.",
            request.InvocationId,
            request.EndpointId,
            request.EventCapacity);

        var producer = ProduceStreamingInvocationAsync(session);
        session.AttachCompletion(producer);
        return Task.FromResult(session);
    }

    public PowerShellInvocationMetricsSnapshot CreateMetricsSnapshot()
        => _metrics.CreateSnapshot(_maxConcurrency, _queueCapacity, _poolManager.CurrentGeneration, _poolManager.RebuildCount);

    public Task RequestPoolRebuildAsync(CancellationToken cancellationToken = default)
        => _poolManager.RequestPoolRebuildAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (_metrics.ActiveInvocationCount > 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        await _poolManager.DisposeAsync();
        _admissionSlots?.Dispose();
        _executionSlots?.Dispose();
        _shutdown.Dispose();
    }

    private static ApiInvocationStatus ClassifyCancellation(CancellationToken callerToken, CancellationToken timeoutToken)
    {
        if (callerToken.IsCancellationRequested)
        {
            return ApiInvocationStatus.CallerCanceled;
        }

        if (timeoutToken.IsCancellationRequested)
        {
            return ApiInvocationStatus.InvocationTimedOut;
        }

        return ApiInvocationStatus.HostUnavailable;
    }

    private void TrackResult(ApiInvocationResult result)
    {
        switch (result.Status)
        {
            case ApiInvocationStatus.Success:
                _metrics.IncrementCompleted();
                break;
            case ApiInvocationStatus.QueueFull:
                _metrics.IncrementRejectedQueueFull();
                break;
            case ApiInvocationStatus.QueueWaitTimedOut:
                _metrics.IncrementQueueTimeout();
                break;
            case ApiInvocationStatus.CallerCanceled:
                _metrics.IncrementCallerCanceled();
                break;
            case ApiInvocationStatus.InvocationTimedOut:
                _metrics.IncrementInvocationTimeout();
                break;
            case ApiInvocationStatus.PowerShellFailure:
            case ApiInvocationStatus.PowerShellTerminatingFailure:
            case ApiInvocationStatus.PowerShellNonTerminatingError:
            case ApiInvocationStatus.PowerShellParameterBindingFailure:
            case ApiInvocationStatus.PowerShellValidationFailure:
                _metrics.IncrementPowerShellFailure();
                break;
            case ApiInvocationStatus.InternalFailure:
            case ApiInvocationStatus.InvalidFunction:
            case ApiInvocationStatus.RequestBindingFailure:
            case ApiInvocationStatus.NormalizationFailure:
            case ApiInvocationStatus.SerializationOutputLimitFailure:
            case ApiInvocationStatus.HostUnavailable:
                _metrics.IncrementInternalFailure();
                break;
        }
    }

    private async Task ProduceStreamingInvocationAsync(ApiStreamingInvocationSession session)
    {
        var request = session.Request;
        using var eventGate = new SemaphoreSlim(1, 1);
        long sequence = 0;
        var terminalWritten = false;
        LiveStreamingFailure? liveStreamingFailure = null;
        var liveStreamingFailureGate = new object();

        ApiStreamingInvocationEvent CreateEvent(
            ApiStreamingInvocationEventKind kind,
            object? payload = null,
            string? message = null,
            string? statusCode = null,
            long? elapsedMilliseconds = null)
            => new(
                request.InvocationId,
                request.EndpointId,
                request.ConnectionId,
                request.SessionId,
                Interlocked.Increment(ref sequence),
                kind,
                DateTimeOffset.UtcNow,
                payload,
                message,
                statusCode,
                elapsedMilliseconds);

        async ValueTask<bool> PublishDataAsync(
            ApiStreamingInvocationEventKind kind,
            object? payload = null,
            string? message = null,
            string? statusCode = null,
            long? elapsedMilliseconds = null)
        {
            await eventGate.WaitAsync(session.CancellationToken).ConfigureAwait(false);
            try
            {
                if (terminalWritten)
                {
                    return false;
                }

                return await session.WriteDataAsync(
                    CreateEvent(kind, payload, message, statusCode, elapsedMilliseconds),
                    session.CancellationToken).ConfigureAwait(false);
            }
            finally
            {
                eventGate.Release();
            }
        }

        async ValueTask PublishTerminalAsync(
            ApiStreamingInvocationEventKind kind,
            string? message = null,
            string? statusCode = null,
            long? elapsedMilliseconds = null)
        {
            await eventGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (terminalWritten)
                {
                    return;
                }

                terminalWritten = true;
                await session.WriteTerminalAsync(
                    CreateEvent(kind, message: message, statusCode: statusCode, elapsedMilliseconds: elapsedMilliseconds)).ConfigureAwait(false);
            }
            finally
            {
                eventGate.Release();
            }
        }

        bool TrySetLiveStreamingFailure(string safeMessage, string statusCode)
        {
            lock (liveStreamingFailureGate)
            {
                if (liveStreamingFailure is not null)
                {
                    return false;
                }

                liveStreamingFailure = new LiveStreamingFailure(safeMessage, statusCode);
                _logger?.LogWarning(
                    "Live streaming PowerShell invocation {InvocationId} stopped before completion with status {StatusCode}.",
                    request.InvocationId,
                    statusCode);
                return true;
            }
        }

        LiveStreamingFailure? GetLiveStreamingFailure()
        {
            lock (liveStreamingFailureGate)
            {
                return liveStreamingFailure;
            }
        }

        try
        {
            await PublishDataAsync(ApiStreamingInvocationEventKind.InvocationStarted);

            var liveSink = new PowerShellInvocationStreamSink(async (record, _) =>
            {
                var failure = GetLiveStreamingFailure();
                if (failure is not null)
                {
                    return false;
                }

                switch (record.Kind)
                {
                    case PowerShellInvocationStreamKind.Output:
                    {
                        if (record.Output is null)
                        {
                            return true;
                        }

                        var normalized = PowerShellResultNormalizer.Shared.Normalize(
                            [record.Output],
                            _runtimeOptions,
                            ApiJsonOptions.Shared);
                        if (!normalized.IsSuccess)
                        {
                            TrySetLiveStreamingFailure(
                                normalized.SafeMessage,
                                GetStatusCode(StatusForNormalizationFailure(normalized.FailureKind)));
                            session.Cancel();
                            return false;
                        }

                        return await PublishDataAsync(ApiStreamingInvocationEventKind.Output, normalized.Value).ConfigureAwait(false);
                    }
                    case PowerShellInvocationStreamKind.Warning:
                        return await PublishDataAsync(ApiStreamingInvocationEventKind.Warning, record.Message, record.Message).ConfigureAwait(false);
                    case PowerShellInvocationStreamKind.Verbose:
                        return await PublishDataAsync(ApiStreamingInvocationEventKind.Verbose, record.Message, record.Message).ConfigureAwait(false);
                    case PowerShellInvocationStreamKind.Debug:
                        return await PublishDataAsync(ApiStreamingInvocationEventKind.Debug, record.Message, record.Message).ConfigureAwait(false);
                    case PowerShellInvocationStreamKind.Information:
                        return await PublishDataAsync(ApiStreamingInvocationEventKind.Information, record.Message, record.Message).ConfigureAwait(false);
                    case PowerShellInvocationStreamKind.Error:
                        return await PublishDataAsync(ApiStreamingInvocationEventKind.Error, record.Message, record.Message).ConfigureAwait(false);
                    default:
                        return await PublishDataAsync(ApiStreamingInvocationEventKind.Error, record.Message, record.Message).ConfigureAwait(false);
                }
            });

            var result = await InvokeCoreAsync(
                new ApiInvocationRequest
                {
                    FunctionName = request.FunctionName,
                    Parameters = request.Parameters,
                    Timeout = request.Timeout
                },
                session.CancellationToken,
                liveSink).ConfigureAwait(false);

            var liveFailure = GetLiveStreamingFailure();
            if (liveFailure is not null)
            {
                await PublishTerminalAsync(
                    ApiStreamingInvocationEventKind.InvocationFailed,
                    liveFailure.SafeMessage,
                    liveFailure.StatusCode,
                    ToElapsedMilliseconds(result.Elapsed));
                return;
            }

            if (result.IsSuccess)
            {
                await PublishTerminalAsync(
                    ApiStreamingInvocationEventKind.InvocationCompleted,
                    statusCode: "success",
                    elapsedMilliseconds: ToElapsedMilliseconds(result.Elapsed));
                return;
            }

            var terminalKind = result.Status == ApiInvocationStatus.CallerCanceled
                ? ApiStreamingInvocationEventKind.InvocationCanceled
                : ApiStreamingInvocationEventKind.InvocationFailed;
            await PublishTerminalAsync(
                terminalKind,
                GetSafeFailureMessage(result),
                GetStatusCode(result.Status),
                ToElapsedMilliseconds(result.Elapsed));
        }
        catch (OperationCanceledException)
        {
            await PublishTerminalAsync(
                ApiStreamingInvocationEventKind.InvocationCanceled,
                "The streaming PowerShell invocation was canceled.",
                GetStreamingCancellationStatusCode());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Internal streaming PowerShell invocation failure {InvocationId}.", request.InvocationId);
            await PublishTerminalAsync(
                ApiStreamingInvocationEventKind.InvocationFailed,
                "The streaming PowerShell invocation failed internally.",
                "internal-failure");
        }
        finally
        {
            _logger?.LogInformation(
                "Completed streaming PowerShell invocation {InvocationId} with {EventCount} events.",
                request.InvocationId,
                sequence);
        }

        string GetStreamingCancellationStatusCode()
            => _shutdown.IsCancellationRequested ? "host-shutdown" : "caller-canceled";
    }

    private static long ToElapsedMilliseconds(TimeSpan elapsed)
        => Math.Max(0, (long)Math.Round(elapsed.TotalMilliseconds));

    private static ApiInvocationStatus StatusForNormalizationFailure(NormalizationFailureKind failureKind)
        => failureKind is NormalizationFailureKind.ItemLimitExceeded or NormalizationFailureKind.ByteLimitExceeded
            ? ApiInvocationStatus.SerializationOutputLimitFailure
            : ApiInvocationStatus.NormalizationFailure;

    private static string GetSafeFailureMessage(ApiInvocationResult result)
        => result.Status == ApiInvocationStatus.InternalFailure
            ? "The streaming PowerShell invocation failed internally."
            : string.IsNullOrWhiteSpace(result.SafeMessage)
            ? "The streaming PowerShell invocation failed."
            : result.SafeMessage;

    private static string GetStatusCode(ApiInvocationStatus status)
        => status switch
        {
            ApiInvocationStatus.RequestBindingFailure => "request-binding-failure",
            ApiInvocationStatus.InvalidFunction => "invalid-function",
            ApiInvocationStatus.QueueFull => "queue-full",
            ApiInvocationStatus.QueueWaitTimedOut => "queue-wait-timeout",
            ApiInvocationStatus.CallerCanceled => "caller-canceled",
            ApiInvocationStatus.InvocationTimedOut => "invocation-timeout",
            ApiInvocationStatus.PowerShellTerminatingFailure => "powershell-terminating-failure",
            ApiInvocationStatus.PowerShellNonTerminatingError => "powershell-nonterminating-error",
            ApiInvocationStatus.PowerShellParameterBindingFailure => "powershell-parameter-binding-failure",
            ApiInvocationStatus.PowerShellValidationFailure => "powershell-validation-failure",
            ApiInvocationStatus.PowerShellFailure => "powershell-failure",
            ApiInvocationStatus.NormalizationFailure => "normalization-failure",
            ApiInvocationStatus.SerializationOutputLimitFailure => "serialization-output-limit-failure",
            ApiInvocationStatus.HostUnavailable => "host-unavailable",
            ApiInvocationStatus.InternalFailure => "internal-failure",
            _ => "invocation-failure"
        };

    private sealed record LiveStreamingFailure(string SafeMessage, string StatusCode);
}
