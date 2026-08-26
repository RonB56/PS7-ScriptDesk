using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PS7ScriptDesk.Domain.Models;

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
                () => ClassifyCancellation(cancellationToken, timeoutSource.Token));

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
}
