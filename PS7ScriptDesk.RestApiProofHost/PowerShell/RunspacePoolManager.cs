using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using Microsoft.Extensions.Logging;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.RestApiProofHost.PowerShell;

public sealed class RunspacePoolManager : IAsyncDisposable
{
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly ILogger<RunspacePoolManager>? _logger;
    private readonly HashSet<string> _allowedFunctionNames = new(StringComparer.OrdinalIgnoreCase);
    private PoolState? _state;
    private string _scriptPath = string.Empty;
    private int _minimumRunspaces = 1;
    private int _maximumRunspaces = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
    private bool _disposed;

    public RunspacePoolManager(ILogger<RunspacePoolManager>? logger = null)
    {
        _logger = logger;
    }

    public bool RequiredFunctionsVerified { get; private set; }
    public bool IsDisposed => _disposed;
    public int CurrentGeneration => _state?.Generation ?? 0;
    public int RebuildCount { get; private set; }

    public async Task InitializeAsync(
        string scriptPath,
        IEnumerable<string> requiredFunctionNames,
        ApiRuntimeOptions runtimeOptions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        ArgumentNullException.ThrowIfNull(requiredFunctionNames);
        ArgumentNullException.ThrowIfNull(runtimeOptions);

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("The proof host sample script was not found.", scriptPath);
        }

        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _scriptPath = Path.GetFullPath(scriptPath);
            _minimumRunspaces = Math.Max(1, runtimeOptions.RunspacePoolMinimum);
            var defaultMaximum = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
            _maximumRunspaces = runtimeOptions.RunspacePoolMaximum > 0
                ? runtimeOptions.RunspacePoolMaximum
                : defaultMaximum;
            _maximumRunspaces = Math.Max(_minimumRunspaces, _maximumRunspaces);

            _allowedFunctionNames.Clear();
            foreach (var functionName in requiredFunctionNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _allowedFunctionNames.Add(functionName.Trim());
            }

            _state?.Pool.Dispose();
            _state = CreatePoolState(generation: 1);
            RequiredFunctionsVerified = true;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public bool IsFunctionAllowed(string functionName)
        => !string.IsNullOrWhiteSpace(functionName) && _allowedFunctionNames.Contains(functionName);

    public async Task<RunspacePoolLease> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _stateGate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_state is null)
                {
                    throw new InvalidOperationException("The proof host runspace pool has not been initialized.");
                }

                if (!_state.RetireWhenIdle)
                {
                    _state.ActiveLeases++;
                    return new RunspacePoolLease(this, _state);
                }

                if (_state.ActiveLeases == 0)
                {
                    RebuildCurrentPoolUnderGate();
                    continue;
                }
            }
            finally
            {
                _stateGate.Release();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    internal async ValueTask ReleaseLeaseAsync(PoolState state, bool requestPoolRebuild)
    {
        await _stateGate.WaitAsync();
        try
        {
            state.ActiveLeases = Math.Max(0, state.ActiveLeases - 1);
            if (requestPoolRebuild && ReferenceEquals(state, _state))
            {
                state.RetireWhenIdle = true;
            }

            if (!_disposed && ReferenceEquals(state, _state) && state.RetireWhenIdle && state.ActiveLeases == 0)
            {
                RebuildCurrentPoolUnderGate();
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task RequestPoolRebuildAsync(CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is null)
            {
                throw new InvalidOperationException("The proof host runspace pool has not been initialized.");
            }

            _state.RetireWhenIdle = true;
            if (_state.ActiveLeases == 0)
            {
                RebuildCurrentPoolUnderGate();
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        PoolState? stateToDispose = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            await _stateGate.WaitAsync();
            try
            {
                if (_disposed && _state is null)
                {
                    return;
                }

                _disposed = true;
                if (_state is null)
                {
                    return;
                }

                _state.RetireWhenIdle = true;
                if (_state.ActiveLeases == 0 || DateTimeOffset.UtcNow >= deadline)
                {
                    stateToDispose = _state;
                    _state = null;
                    break;
                }
            }
            finally
            {
                _stateGate.Release();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        DisposePool(stateToDispose);
    }

    private void RebuildCurrentPoolUnderGate()
    {
        if (_state is null)
        {
            return;
        }

        var oldState = _state;
        var newGeneration = oldState.Generation + 1;
        _logger?.LogWarning("Rebuilding proof host runspace pool generation {OldGeneration}.", oldState.Generation);
        _state = CreatePoolState(newGeneration);
        RebuildCount++;
        DisposePool(oldState);
    }

    private PoolState CreatePoolState(int generation)
    {
        var initialSessionState = InitialSessionState.CreateDefault2();
        AddScriptFunctions(initialSessionState);
        var pool = RunspaceFactory.CreateRunspacePool(initialSessionState);
        try
        {
            pool.SetMinRunspaces(_minimumRunspaces);
            pool.SetMaxRunspaces(_maximumRunspaces);
            pool.Open();
            VerifyRequiredFunctions(pool);
            _logger?.LogInformation(
                "Opened proof host runspace pool generation {Generation} with min {MinimumRunspaces} max {MaximumRunspaces}.",
                generation,
                _minimumRunspaces,
                _maximumRunspaces);
            return new PoolState(pool, generation);
        }
        catch
        {
            pool.Dispose();
            throw;
        }
    }

    private void AddScriptFunctions(InitialSessionState initialSessionState)
    {
        var ast = Parser.ParseFile(_scriptPath, out _, out var errors);
        if (errors.Length > 0)
        {
            throw new InvalidOperationException("The proof host sample script contains parse errors.");
        }

        var functions = ast.FindAll(
                node => node is FunctionDefinitionAst function &&
                        function.Parent is not FunctionDefinitionAst &&
                        function.Parent is not TypeDefinitionAst,
                searchNestedScriptBlocks: true)
            .Cast<FunctionDefinitionAst>();

        foreach (var function in functions)
        {
            initialSessionState.Commands.Add(new SessionStateFunctionEntry(
                function.Name,
                NormalizeFunctionBody(function.Body.Extent.Text)));
        }
    }

    private static string NormalizeFunctionBody(string bodyText)
    {
        var trimmed = bodyText.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '{' && trimmed[^1] == '}')
        {
            return trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    private void VerifyRequiredFunctions(RunspacePool pool)
    {
        foreach (var functionName in _allowedFunctionNames)
        {
            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.RunspacePool = pool;
            powerShell
                .AddCommand("Get-Command")
                .AddParameter("Name", functionName)
                .AddParameter("CommandType", CommandTypes.Function);

            var output = powerShell.Invoke();
            if (powerShell.HadErrors || powerShell.Streams.Error.Count > 0 || output.Count == 0)
            {
                throw new InvalidOperationException($"Required function '{functionName}' was not loaded from the proof host sample script.");
            }

            _logger?.LogInformation("Verified configured PowerShell function {FunctionName}.", functionName);
        }
    }

    private static void DisposePool(PoolState? state)
    {
        if (state is null)
        {
            return;
        }

        state.Pool.Dispose();
    }

    internal sealed class PoolState
    {
        public PoolState(RunspacePool pool, int generation)
        {
            Pool = pool;
            Generation = generation;
        }

        public RunspacePool Pool { get; }
        public int Generation { get; }
        public int ActiveLeases { get; set; }
        public bool RetireWhenIdle { get; set; }
    }
}

public sealed class RunspacePoolLease : IAsyncDisposable
{
    private readonly RunspacePoolManager _owner;
    private readonly RunspacePoolManager.PoolState _state;
    private bool _disposed;

    internal RunspacePoolLease(RunspacePoolManager owner, RunspacePoolManager.PoolState state)
    {
        _owner = owner;
        _state = state;
    }

    public RunspacePool Pool => _state.Pool;
    public int Generation => _state.Generation;
    public bool RequestPoolRebuild { get; set; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _owner.ReleaseLeaseAsync(_state, RequestPoolRebuild);
    }
}
