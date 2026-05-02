using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Threading;
using System.Threading.Tasks;
using PowerShellStudio.Application.Diagnostics;
using AutomationPowerShell = System.Management.Automation.PowerShell;

namespace PowerShellStudio.Shell.Editor.Sdk
{
    public sealed class SdkPowerShellRuntimeService : IDisposable, IAsyncDisposable
    {
        private static readonly HashSet<string> MutatingCommandVerbs = new(StringComparer.OrdinalIgnoreCase)
        {
            "Add",
            "Clear",
            "Copy",
            "Disable",
            "Enable",
            "Export",
            "Import",
            "Install",
            "Invoke",
            "Move",
            "New",
            "Publish",
            "Register",
            "Remove",
            "Rename",
            "Reset",
            "Restart",
            "Save",
            "Send",
            "Set",
            "Start",
            "Stop",
            "Submit",
            "Sync",
            "Uninstall",
            "Unpublish",
            "Unregister",
            "Update",
            "Write",
        };

        private readonly SdkRunspacePoolOptions _options;
        private readonly SemaphoreSlim _initializationGate = new(1, 1);
        private readonly object _syncRoot = new();

        private RunspacePool? _runspacePool;
        private bool _disposed;

        public SdkPowerShellRuntimeService(SdkRunspacePoolOptions? options = null)
        {
            _options = options ?? new SdkRunspacePoolOptions();
            _options.Validate();
        }

        public bool IsInitialized
        {
            get
            {
                lock (_syncRoot)
                {
                    return _runspacePool is not null && _runspacePool.RunspacePoolStateInfo.State == RunspacePoolState.Opened;
                }
            }
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();

                if (IsInitialized)
                {
                    return;
                }

                var runspacePool = CreateRunspacePool();
                try
                {
                    await OpenRunspacePoolAsync(runspacePool).WaitAsync(cancellationToken).ConfigureAwait(false);

                    lock (_syncRoot)
                    {
                        _runspacePool = runspacePool;
                    }

                    AppLogger.Info("EditorSdk", $"Initialized SDK runspace pool '{_options.RuntimeName ?? "EditorSdkRuntime"}' with MinRunspaces={_options.MinRunspaces}, MaxRunspaces={_options.MaxRunspaces}.");
                }
                catch
                {
                    runspacePool.Dispose();
                    throw;
                }
            }
            finally
            {
                _initializationGate.Release();
            }
        }

        public async Task<SdkPowerShellTaskResult> InvokeScriptAsync(
            string operationName,
            string script,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(operationName))
            {
                throw new ArgumentException("An operation name is required.", nameof(operationName));
            }

            if (string.IsNullOrWhiteSpace(script))
            {
                throw new ArgumentException("A script is required.", nameof(script));
            }

            ThrowIfDisposed();
            EnsureScriptIsReadOnly(script);
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

            var stopwatch = Stopwatch.StartNew();
            using var powerShell = AutomationPowerShell.Create();
            var output = new List<PSObject>();
            var errors = new List<string>();

            powerShell.RunspacePool = GetRunspacePool();
            powerShell.AddScript(script, useLocalScope: true);
            powerShell.Streams.Error.DataAdded += (_, _) => CaptureErrors(powerShell, errors);

            using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCancellationTokenSource.CancelAfter(timeout ?? _options.DefaultTimeout);
            using var cancellationRegistration = linkedCancellationTokenSource.Token.Register(
                static state => StopPowerShell((AutomationPowerShell)state!),
                powerShell);

            bool timedOut = false;
            bool canceled = false;
            PSDataCollection<PSObject>? invocationOutput = null;
            Exception? invocationException = null;

            try
            {
                invocationOutput = await BeginInvokeAsync(powerShell, linkedCancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                invocationException = ex;
                canceled = cancellationToken.IsCancellationRequested;
                timedOut = !canceled;
            }
            catch (RuntimeException ex)
            {
                invocationException = ex;
            }
            catch (PSInvalidOperationException ex)
            {
                invocationException = ex;
            }
            stopwatch.Stop();

            if (invocationOutput is not null)
            {
                output.AddRange(invocationOutput);
            }

            CaptureErrors(powerShell, errors);

            if (timedOut)
            {
                AppLogger.Warning("EditorSdk", $"SDK operation '{operationName}' timed out after {stopwatch.ElapsedMilliseconds:N0} ms.");
                return SdkPowerShellTaskResult.Timeout(operationName, stopwatch.Elapsed, output, errors, invocationException);
            }

            if (canceled)
            {
                AppLogger.Debug("EditorSdk", $"SDK operation '{operationName}' canceled after {stopwatch.ElapsedMilliseconds:N0} ms.");
                return SdkPowerShellTaskResult.FromCanceled(operationName, stopwatch.Elapsed, output, errors, invocationException);
            }

            if (invocationException is not null)
            {
                IReadOnlyList<string> failureErrors = errors.Count == 0
                    ? new[] { invocationException.Message }
                    : errors;
                AppLogger.Warning("EditorSdk", $"SDK operation '{operationName}' failed after {stopwatch.ElapsedMilliseconds:N0} ms. {invocationException.Message}");
                return SdkPowerShellTaskResult.Failure(operationName, stopwatch.Elapsed, output, failureErrors, invocationException);
            }

            if (errors.Count > 0)
            {
                AppLogger.Warning("EditorSdk", $"SDK operation '{operationName}' completed with {errors.Count:N0} PowerShell error record(s) after {stopwatch.ElapsedMilliseconds:N0} ms.");
                return SdkPowerShellTaskResult.Failure(operationName, stopwatch.Elapsed, output, errors);
            }

            AppLogger.Debug("EditorSdk", $"SDK operation '{operationName}' completed successfully in {stopwatch.ElapsedMilliseconds:N0} ms with {output.Count:N0} output item(s).");
            return SdkPowerShellTaskResult.Success(operationName, stopwatch.Elapsed, output);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _initializationGate.Dispose();

            RunspacePool? runspacePool;
            lock (_syncRoot)
            {
                runspacePool = _runspacePool;
                _runspacePool = null;
            }

            runspacePool?.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private static Task<PSDataCollection<PSObject>> BeginInvokeAsync(AutomationPowerShell powerShell, CancellationToken cancellationToken)
        {
            return Task.Factory.FromAsync(
                (callback, state) => powerShell.BeginInvoke<PSObject, PSObject>(null, null, settings: null, callback, state),
                powerShell.EndInvoke,
                state: null).WaitAsync(cancellationToken);
        }

        private static Task OpenRunspacePoolAsync(RunspacePool runspacePool)
        {
            return Task.Factory.FromAsync(
                runspacePool.BeginOpen,
                runspacePool.EndOpen,
                state: null);
        }

        private static void StopPowerShell(AutomationPowerShell powerShell)
        {
            try
            {
                powerShell.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (PSInvalidOperationException)
            {
            }
        }

        private static void CaptureErrors(AutomationPowerShell powerShell, List<string> errors)
        {
            if (errors is null)
            {
                throw new ArgumentNullException(nameof(errors));
            }

            for (var index = errors.Count; index < powerShell.Streams.Error.Count; index++)
            {
                var errorRecord = powerShell.Streams.Error[index];
                if (errorRecord is null)
                {
                    continue;
                }

                errors.Add(errorRecord.ToString());
            }
        }

        private void EnsureScriptIsReadOnly(string script)
        {
            Token[] tokens;
            ParseError[] parseErrors;
            var ast = Parser.ParseInput(script, out tokens, out parseErrors);

            if (parseErrors.Length > 0)
            {
                return;
            }

            if (tokens.Any(static token => token.Kind.ToString().Contains("Redir", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("The SDK runtime service only allows read-only scripts without output redirection.");
            }

            foreach (var commandAst in ast.FindAll(static node => node is CommandAst, searchNestedScriptBlocks: true).OfType<CommandAst>())
            {
                var commandName = commandAst.GetCommandName();
                if (string.IsNullOrWhiteSpace(commandName))
                {
                    continue;
                }

                var dashIndex = commandName.IndexOf('-');
                if (dashIndex <= 0)
                {
                    continue;
                }

                var verb = commandName.Substring(0, dashIndex);
                if (MutatingCommandVerbs.Contains(verb))
                {
                    throw new InvalidOperationException($"The SDK runtime service rejected potentially mutating command '{commandName}'.");
                }
            }
        }

        private RunspacePool GetRunspacePool()
        {
            lock (_syncRoot)
            {
                return _runspacePool
                    ?? throw new InvalidOperationException("The SDK runspace pool has not been initialized.");
            }
        }

        private RunspacePool CreateRunspacePool()
        {
            var initialSessionState = InitialSessionState.CreateDefault2();
            initialSessionState.ThrowOnRunspaceOpenError = true;
            initialSessionState.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

            var sessionStateVariableEntry = new SessionStateVariableEntry(
                "PSStudioSdkNoProfile",
                _options.UseNoProfileBehavior,
                "Internal flag for isolated editor SDK PowerShell operations.");
            initialSessionState.Variables.Add(sessionStateVariableEntry);

            if (!_options.LoadDefaultProfile)
            {
                // Profiles are intentionally not loaded for the isolated SDK layer.
                initialSessionState.Variables.Add(new SessionStateVariableEntry(
                    "PROFILE",
                    string.Empty,
                    "Profiles are disabled for the isolated editor SDK PowerShell runtime."));
            }

            var runspacePool = RunspaceFactory.CreateRunspacePool(
                minRunspaces: _options.MinRunspaces,
                maxRunspaces: _options.MaxRunspaces,
                initialSessionState,
                host: null);

            if (!string.IsNullOrWhiteSpace(_options.RuntimeName))
            {
                runspacePool.ThreadOptions = PSThreadOptions.ReuseThread;
                runspacePool.ApartmentState = ApartmentState.MTA;
            }

            return runspacePool;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SdkPowerShellRuntimeService));
            }
        }
    }
}
