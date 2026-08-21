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
        Func<ApiInvocationStatus> cancellationStatusProvider)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(poolLease);
        ArgumentNullException.ThrowIfNull(cancellationStatusProvider);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var powerShell = System.Management.Automation.PowerShell.Create();
        powerShell.RunspacePool = poolLease.Pool;
        powerShell.AddCommand(request.FunctionName, useLocalScope: true);
        foreach (var parameter in request.Parameters)
        {
            powerShell.AddParameter(parameter.Key, parameter.Value);
        }

        IAsyncResult? asyncResult;
        try
        {
            asyncResult = powerShell.BeginInvoke();
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger?.LogError(ex, "PowerShell invocation could not start for function {FunctionName}.", request.FunctionName);
            return ApiInvocationResult.Failure(
                ApiInvocationStatus.InternalFailure,
                "The configured PowerShell operation could not be started.",
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
        using var cancellationRegistration = cancellationToken.Register(() =>
            cancellationSignal.TrySetResult(cancellationStatusProvider()));

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

        try
        {
            var output = powerShell.EndInvoke(asyncResult);
            stopwatch.Stop();
            var streams = CaptureStreams(powerShell.Streams, retainedStreamLimit);
            if (powerShell.HadErrors || powerShell.Streams.Error.Count > 0)
            {
                return ApiInvocationResult.Failure(
                    ApiInvocationStatus.PowerShellFailure,
                    "The configured PowerShell operation could not be completed.",
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
                streams.Add(new ApiInvocationStreamRecord("Exception", Cap($"{ex.GetType().Name}: {ex.Message}")));
            }

            return ApiInvocationResult.Failure(
                ApiInvocationStatus.PowerShellFailure,
                "The configured PowerShell operation could not be completed.",
                streams,
                stopwatch.Elapsed,
                poolLease.Generation);
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
        AddRecords(records, "Error", streams.Error.Select(record => record.ToString()), limit);
        AddRecords(records, "Warning", streams.Warning.Select(record => record.Message), limit);
        AddRecords(records, "Verbose", streams.Verbose.Select(record => record.Message), limit);
        AddRecords(records, "Debug", streams.Debug.Select(record => record.Message), limit);
        AddRecords(records, "Information", streams.Information.Select(record => record.ToString()), limit);
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

            records.Add(new ApiInvocationStreamRecord(streamName, Cap(message)));
        }
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
