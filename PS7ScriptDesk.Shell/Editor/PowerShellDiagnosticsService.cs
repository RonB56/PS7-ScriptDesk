using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Shell.Editor
{
    public enum PowerShellDiagnosticsMode
    {
        SyntaxOnly,
        FullAuthoring
    }

    /// <summary>
    /// Keeps a lightweight hidden pwsh.exe parser session alive for live editor diagnostics.
    ///
    /// Important behavior:
    /// - a single background PowerShell process is reused across parse requests
    /// - startup readiness is confirmed with an explicit marker, not prompt text guessing
    /// - each parse request is wrapped in unique start/end markers so the payload can be extracted reliably
    /// - the transport payload is base64-encoded JSON so prompt/output noise does not corrupt syntax results
    /// </summary>
    public sealed class PowerShellDiagnosticsService : IDisposable
    {
        private readonly object _syncRoot = new();
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private readonly StringBuilder _sharedOutputTail = new();

        private Process? _process;
        private StreamWriter? _stdin;
        private CancellationTokenSource? _processCancellationTokenSource;
        private Task? _stdoutReaderTask;
        private Task? _stderrReaderTask;
        private TaskCompletionSource<bool>? _readyCompletionSource;
        private ActiveRequest? _activeRequest;
        private string? _readyMarker;
        private string? _activeRuntimePath;
        private bool _disposed;

        private static readonly TimeSpan ProcessReadinessTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan ParseRequestTimeout = TimeSpan.FromSeconds(12);
        private const int FullAuthoringAnalysisMaxCharacters = 45000;
        private const int FullAuthoringAnalysisMaxLines = 800;
        private const int MaximumCommandMetadataLookups = 80;

        public Task<DiagnosticsParseResult> ParseAsync(
            string scriptText,
            string pwshExecutablePath,
            CancellationToken cancellationToken = default)
        {
            return ParseAsync(scriptText, pwshExecutablePath, PowerShellDiagnosticsMode.FullAuthoring, cancellationToken);
        }

        public async Task<DiagnosticsParseResult> ParseAsync(
            string scriptText,
            string pwshExecutablePath,
            PowerShellDiagnosticsMode diagnosticsMode,
            CancellationToken cancellationToken = default)
        {
            // The hidden syntax-checking process is disposable infrastructure.
            // If it loses stdin, exits, or times out, restart it and retry once.
            // If it is still unavailable after the retry, do not paint a scary
            // diagnostics failure into the editor; return an empty successful
            // result so diagnostics gracefully pause until the next edit/refresh.
            var result = await ParseOnceAsync(scriptText, pwshExecutablePath, diagnosticsMode, cancellationToken).ConfigureAwait(false);
            if (!IsRetryableInfrastructureFailure(result.FailureMessage))
            {
                return result;
            }

            TeardownProcess();

            result = await ParseOnceAsync(scriptText, pwshExecutablePath, diagnosticsMode, cancellationToken).ConfigureAwait(false);
            if (IsRetryableInfrastructureFailure(result.FailureMessage))
            {
                TeardownProcess();
                return new DiagnosticsParseResult(Array.Empty<ParseErrorInfo>());
            }

            return result;
        }

        private static bool IsRetryableInfrastructureFailure(string? failureMessage)
        {
            if (string.IsNullOrWhiteSpace(failureMessage))
            {
                return false;
            }

            if (string.Equals(failureMessage, "Syntax checking was canceled.", StringComparison.Ordinal))
            {
                return false;
            }

            if (failureMessage.Contains("No PowerShell runtime is available", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return failureMessage.Contains("syntax-checking PowerShell session is not available", StringComparison.OrdinalIgnoreCase)
                || failureMessage.Contains("diagnostics service timed out", StringComparison.OrdinalIgnoreCase)
                || failureMessage.Contains("syntax-checking PowerShell process exited", StringComparison.OrdinalIgnoreCase)
                || failureMessage.Contains("Unable to send syntax-checking request", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<DiagnosticsParseResult> ParseOnceAsync(
            string scriptText,
            string pwshExecutablePath,
            PowerShellDiagnosticsMode diagnosticsMode,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(scriptText))
            {
                return new DiagnosticsParseResult(Array.Empty<ParseErrorInfo>());
            }

            if (string.IsNullOrWhiteSpace(pwshExecutablePath))
            {
                return new DiagnosticsParseResult(Array.Empty<ParseErrorInfo>(), "No PowerShell runtime is available for syntax checking.");
            }

            var requestGateEntered = false;
            try
            {
                await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                requestGateEntered = true;
            }
            catch (OperationCanceledException)
            {
                return new DiagnosticsParseResult(Array.Empty<ParseErrorInfo>(), "Syntax checking was canceled.");
            }
            catch (ObjectDisposedException)
            {
                return new DiagnosticsParseResult(Array.Empty<ParseErrorInfo>(), "Syntax checking was canceled.");
            }

            try
            {
                await EnsureProcessReadyAsync(pwshExecutablePath, cancellationToken).ConfigureAwait(false);

                var request = new ActiveRequest();
                lock (_syncRoot)
                {
                    _activeRequest = request;
                }

                var diagnosticsOptions = DiagnosticsRequestOptions.FromScript(scriptText, diagnosticsMode);
                var command = BuildParseCommand(scriptText, request.StartMarker, request.EndMarker, diagnosticsOptions);

                try
                {
                    await SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    ClearActiveRequest(request);
                    return new DiagnosticsParseResult(Array.Empty<ParseErrorInfo>(), "Syntax checking was canceled.");
                }
                catch (Exception ex)
                {
                    ClearActiveRequest(request);
                    return new DiagnosticsParseResult(Array.Empty<ParseErrorInfo>(), $"Unable to send syntax-checking request to PowerShell: {ex.Message}");
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(ParseRequestTimeout);

                string payload;
                try
                {
                    payload = await request.CompletionSource.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    var stderr = request.GetErrorCaptureText();
                    ClearActiveRequest(request);
                    if (cancellationToken.IsCancellationRequested || request.CompletionSource.Task.IsCanceled)
                    {
                        return new DiagnosticsParseResult(Array.Empty<ParseErrorInfo>(), "Syntax checking was canceled.");
                    }

                    return new DiagnosticsParseResult(
                        Array.Empty<ParseErrorInfo>(),
                        BuildInfrastructureFailureMessage("The diagnostics service timed out while waiting for PowerShell to respond.", stderr));
                }
                catch (Exception ex)
                {
                    var stderr = request.GetErrorCaptureText();
                    ClearActiveRequest(request);
                    return new DiagnosticsParseResult(
                        Array.Empty<ParseErrorInfo>(),
                        BuildInfrastructureFailureMessage(ex.Message, stderr));
                }

                var requestErrorOutput = request.GetErrorCaptureText();
                ClearActiveRequest(request);
                return ParseTransportPayload(payload, scriptText, requestErrorOutput);
            }
            finally
            {
                if (requestGateEntered)
                {
                    try
                    {
                        _requestGate.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            TeardownProcess();
        }

        private async Task EnsureProcessReadyAsync(string pwshExecutablePath, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            var normalizedRuntimePath = NormalizeRuntimePath(pwshExecutablePath);

            lock (_syncRoot)
            {
                if (_process is not null &&
                    !_process.HasExited &&
                    _stdin is not null &&
                    _processCancellationTokenSource is not null &&
                    !_processCancellationTokenSource.IsCancellationRequested &&
                    string.Equals(_activeRuntimePath, normalizedRuntimePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            TeardownProcess();

            var processCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var readyCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readyMarker = $"##PSSTUDIO_DIAG_READY_{Guid.NewGuid():N}##";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = pwshExecutablePath,
                    Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -NoExit",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardInputEncoding = new UTF8Encoding(false),
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                },
                EnableRaisingEvents = true,
            };

            process.Exited += (_, _) => HandleProcessExited(process);
            PowerShellBackgroundProcessEnvironment.Apply(process.StartInfo, "Diagnostics", pwshExecutablePath);
            AppLogger.Info("EditorDiagnostics", $"Diagnostics helper ProcessStartInfo.FileName='{process.StartInfo.FileName}'.");

            try
            {
                process.Start();
            }
            catch
            {
                processCancellationTokenSource.Dispose();
                process.Dispose();
                throw;
            }

            lock (_syncRoot)
            {
                _process = process;
                _stdin = process.StandardInput;
                _processCancellationTokenSource = processCancellationTokenSource;
                _readyCompletionSource = readyCompletionSource;
                _readyMarker = readyMarker;
                _activeRuntimePath = normalizedRuntimePath;
                _sharedOutputTail.Clear();
                _activeRequest = null;
            }

            _stdoutReaderTask = Task.Run(() => ReadLoopAsync(process, process.StandardOutput, isErrorStream: false, processCancellationTokenSource.Token), processCancellationTokenSource.Token);
            _stderrReaderTask = Task.Run(() => ReadLoopAsync(process, process.StandardError, isErrorStream: true, processCancellationTokenSource.Token), processCancellationTokenSource.Token);

            try
            {
                await SendCommandAsync($"[Console]::Out.Write('{readyMarker}'); [Console]::Out.Flush()", cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TeardownProcess();
                throw;
            }

            using var readinessTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readinessTimeoutCts.CancelAfter(ProcessReadinessTimeout);

            try
            {
                var ready = await readyCompletionSource.Task.WaitAsync(readinessTimeoutCts.Token).ConfigureAwait(false);
                if (!ready)
                {
                    TeardownProcess();
                    throw new IOException("The syntax-checking PowerShell process exited before it was ready.");
                }
            }
            catch
            {
                TeardownProcess();
                throw;
            }
        }

        private async Task SendCommandAsync(string command, CancellationToken cancellationToken)
        {
            StreamWriter? stdin;
            lock (_syncRoot)
            {
                stdin = _stdin;
            }

            if (stdin is null)
            {
                throw new InvalidOperationException("The syntax-checking PowerShell session is not available.");
            }

            await stdin.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync().ConfigureAwait(false);
        }

        private async Task ReadLoopAsync(Process owningProcess, TextReader reader, bool isErrorStream, CancellationToken cancellationToken)
        {
            var buffer = new char[1024];

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    ProcessIncomingChunk(new string(buffer, 0, read), isErrorStream);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                HandleProcessExited(owningProcess);
            }
        }

        private void ProcessIncomingChunk(string chunk, bool isErrorStream)
        {
            TaskCompletionSource<bool>? readyCompletionSource = null;
            ActiveRequest? activeRequest = null;
            string? completedPayload = null;

            lock (_syncRoot)
            {
                if (isErrorStream)
                {
                    _activeRequest?.ErrorCapture.Append(chunk);
                }
                else
                {
                    _sharedOutputTail.Append(chunk);
                    TrimStringBuilderIfNeeded(_sharedOutputTail);

                    if (_readyCompletionSource is not null &&
                        !_readyCompletionSource.Task.IsCompleted &&
                        !string.IsNullOrWhiteSpace(_readyMarker) &&
                        _sharedOutputTail.ToString().Contains(_readyMarker, StringComparison.Ordinal))
                    {
                        readyCompletionSource = _readyCompletionSource;
                        _sharedOutputTail.Clear();
                    }
                }

                activeRequest = _activeRequest;
                if (activeRequest is not null)
                {
                    if (isErrorStream)
                    {
                        TrimStringBuilderIfNeeded(activeRequest.ErrorCapture);
                    }
                    else
                    {
                        activeRequest.Capture.Append(chunk);
                        if (TryExtractPayloadBlock(activeRequest.Capture.ToString(), activeRequest.StartMarker, activeRequest.EndMarker, out var extractedPayload))
                        {
                            completedPayload = extractedPayload;
                        }
                    }
                }
            }

            readyCompletionSource?.TrySetResult(true);

            if (activeRequest is not null && completedPayload is not null)
            {
                activeRequest.CompletionSource.TrySetResult(completedPayload);
            }
        }

        private void HandleProcessExited(Process exitedProcess)
        {
            ActiveRequest? activeRequest;
            TaskCompletionSource<bool>? readyCompletionSource;

            lock (_syncRoot)
            {
                if (!ReferenceEquals(_process, exitedProcess))
                {
                    return;
                }

                activeRequest = _activeRequest;
                readyCompletionSource = _readyCompletionSource;
                _activeRequest = null;
                _readyCompletionSource = null;
                _stdin = null;
                _readyMarker = null;
                _activeRuntimePath = null;
                _sharedOutputTail.Clear();
            }

            // Do not set Task exceptions here. The reader/exit path can run after the
            // active parse request was superseded or after the service is tearing down,
            // and an exception-bearing TaskCompletionSource can surface later as an
            // unobserved task exception. Return a normal diagnostics failure payload for
            // active requests and a false readiness result for startup instead.
            readyCompletionSource?.TrySetResult(false);
            activeRequest?.CompletionSource.TrySetResult(BuildFailurePayload("The syntax-checking PowerShell process exited unexpectedly."));
        }

        private void ClearActiveRequest(ActiveRequest request)
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_activeRequest, request))
                {
                    _activeRequest = null;
                }
            }
        }

        private void TeardownProcess()
        {
            Process? process;
            CancellationTokenSource? processCancellationTokenSource;
            TaskCompletionSource<bool>? readyCompletionSource;
            ActiveRequest? activeRequest;
            Task? stdoutReaderTask;
            Task? stderrReaderTask;

            lock (_syncRoot)
            {
                process = _process;
                processCancellationTokenSource = _processCancellationTokenSource;
                readyCompletionSource = _readyCompletionSource;
                activeRequest = _activeRequest;
                stdoutReaderTask = _stdoutReaderTask;
                stderrReaderTask = _stderrReaderTask;

                _process = null;
                _stdin = null;
                _processCancellationTokenSource = null;
                _readyCompletionSource = null;
                _activeRequest = null;
                _readyMarker = null;
                _activeRuntimePath = null;
                _stdoutReaderTask = null;
                _stderrReaderTask = null;
                _sharedOutputTail.Clear();
            }

            readyCompletionSource?.TrySetCanceled();
            activeRequest?.CompletionSource.TrySetCanceled();

            try
            {
                processCancellationTokenSource?.Cancel();
            }
            catch
            {
            }

            try
            {
                if (process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try
            {
                stdoutReaderTask?.Wait(TimeSpan.FromMilliseconds(250));
                stderrReaderTask?.Wait(TimeSpan.FromMilliseconds(250));
            }
            catch
            {
            }

            processCancellationTokenSource?.Dispose();
            process?.Dispose();
        }

        private static void TrimStringBuilderIfNeeded(StringBuilder builder)
        {
            const int maxBufferLength = 8192;
            const int retainedTailLength = 4096;

            if (builder.Length <= maxBufferLength)
            {
                return;
            }

            var tail = builder.ToString(builder.Length - retainedTailLength, retainedTailLength);
            builder.Clear();
            builder.Append(tail);
        }

        private static string BuildParseCommand(string scriptText, string startMarker, string endMarker, DiagnosticsRequestOptions options)
        {
            var scriptB64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(scriptText));
            var builder = new StringBuilder();

            builder.AppendLine("& {");
            builder.AppendLine("$ErrorActionPreference = 'Stop'");
            builder.AppendLine("$WarningPreference = 'SilentlyContinue'");
            builder.AppendLine("$InformationPreference = 'SilentlyContinue'");
            builder.AppendLine("$ProgressPreference = 'SilentlyContinue'");
            builder.AppendLine("$VerbosePreference = 'SilentlyContinue'");
            builder.AppendLine("$DebugPreference = 'SilentlyContinue'");
            builder.AppendLine("try {");
            builder.Append("$b64 = '").Append(EscapePowerShellSingleQuotedString(scriptB64)).AppendLine("'");
            builder.AppendLine("$script = [System.Text.Encoding]::Unicode.GetString([System.Convert]::FromBase64String($b64))");
            builder.AppendLine("$tokens = $null; $errors = $null");
            builder.AppendLine("$ast = [System.Management.Automation.Language.Parser]::ParseInput($script, [ref]$tokens, [ref]$errors)");
            builder.AppendLine("$result = @()");
            builder.AppendLine("foreach ($e in @($errors)) { $result += [PSCustomObject]@{ message = $e.Message; startLine = $e.Extent.StartLineNumber; startColumn = $e.Extent.StartColumnNumber; endLine = $e.Extent.EndLineNumber; endColumn = $e.Extent.EndColumnNumber } }");
            builder.AppendLine("$tokenResult = @()");
            builder.AppendLine("foreach ($t in @($tokens)) { $tokenResult += [PSCustomObject]@{ kind = [string]$t.Kind; text = [string]$t.Text; startLine = $t.Extent.StartLineNumber; startColumn = $t.Extent.StartColumnNumber; endLine = $t.Extent.EndLineNumber; endColumn = $t.Extent.EndColumnNumber } }");

            if (!options.IncludeAuthoringFacts)
            {
                builder.AppendLine("$facts = [PSCustomObject]@{ functions = @(); commands = @(); commandMetadata = @(); variables = @(); availableCommandNames = @(); approvedVerbs = @() }");
                builder.AppendLine("$response = [PSCustomObject]@{ ok = $true; message = $null; errors = @($result); tokens = @($tokenResult); facts = $facts }");
                builder.AppendLine("} catch {");
                builder.AppendLine("$response = [PSCustomObject]@{ ok = $false; message = $_.Exception.Message; errors = @(); tokens = @(); facts = $null }");
                builder.AppendLine("}");
                AppendTransportEnvelope(builder, startMarker, endMarker);
                return builder.ToString();
            }

            builder.AppendLine("$functionResult = @()");
            builder.AppendLine("foreach ($f in @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true))) {");
            builder.AppendLine("  if ([string]::IsNullOrWhiteSpace([string]$f.Name)) { continue }");
            builder.AppendLine("  $definitionText = [string]$f.Extent.Text");
            builder.AppendLine("  if ([string]::IsNullOrWhiteSpace($definitionText) -or $definitionText -notmatch '^\\s*(function|filter)\\b') { continue }");
            builder.AppendLine("  $functionResult += [PSCustomObject]@{ name = [string]$f.Name; startLine = $f.Extent.StartLineNumber; startColumn = $f.Extent.StartColumnNumber; endLine = $f.Extent.EndLineNumber; endColumn = $f.Extent.EndColumnNumber }");
            builder.AppendLine("}");
            builder.AppendLine("$commandResult = @(); $commandNames = @{}");
            builder.AppendLine("foreach ($c in @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true))) {");
            builder.AppendLine("  $name = $c.GetCommandName()");
            builder.AppendLine("  if ([string]::IsNullOrWhiteSpace($name)) { continue }");
            builder.AppendLine("  $parameters = @()");
            builder.AppendLine("  foreach ($element in @($c.CommandElements)) { if ($element -is [System.Management.Automation.Language.CommandParameterAst]) { $parameters += [PSCustomObject]@{ name = [string]$element.ParameterName; text = [string]$element.Extent.Text; startLine = $element.Extent.StartLineNumber; startColumn = $element.Extent.StartColumnNumber; endLine = $element.Extent.EndLineNumber; endColumn = $element.Extent.EndColumnNumber } } }");
            builder.AppendLine("  $commandResult += [PSCustomObject]@{ name = [string]$name; startLine = $c.Extent.StartLineNumber; startColumn = $c.Extent.StartColumnNumber; endLine = $c.Extent.EndLineNumber; endColumn = $c.Extent.EndColumnNumber; parameters = @($parameters) }");
            builder.AppendLine("  $commandNames[[string]$name] = $true");
            builder.AppendLine("}");
            builder.AppendLine("$metadataResult = @(); $availableCommandNames = @(); $unknownCommandCount = 0");
            builder.AppendLine("if ($null -eq $script:__psstudioCommandMetadataCache) { $script:__psstudioCommandMetadataCache = @{} }");
            builder.AppendLine($"if ($commandNames.Count -le {MaximumCommandMetadataLookups}) {{");
            builder.AppendLine("  foreach ($name in @($commandNames.Keys)) {");
            builder.AppendLine("    $cacheKey = [string]$name");
            builder.AppendLine("    if ($script:__psstudioCommandMetadataCache.ContainsKey($cacheKey)) {");
            builder.AppendLine("      $cachedMetadata = $script:__psstudioCommandMetadataCache[$cacheKey]");
            builder.AppendLine("      $metadataResult += $cachedMetadata");
            builder.AppendLine("      if (-not [bool]$cachedMetadata.exists) { $unknownCommandCount++ }");
            builder.AppendLine("      continue");
            builder.AppendLine("    }");
            builder.AppendLine("    $info = @(Get-Command -Name $name -ErrorAction SilentlyContinue)[0]");
            builder.AppendLine("    if ($null -eq $info) { $metadata = [PSCustomObject]@{ name = [string]$name; exists = $false; resolvedName = $null; commandType = $null; moduleName = $null; definition = $null; parameterNames = @() }; $script:__psstudioCommandMetadataCache[$cacheKey] = $metadata; $metadataResult += $metadata; $unknownCommandCount++; continue }");
            builder.AppendLine("    $parameterNames = @{}");
            builder.AppendLine("    foreach ($p in @($info.Parameters.Values)) { if ($null -ne $p -and -not [string]::IsNullOrWhiteSpace($p.Name)) { $parameterNames[[string]$p.Name] = $true; foreach ($alias in @($p.Aliases)) { if (-not [string]::IsNullOrWhiteSpace($alias)) { $parameterNames[[string]$alias] = $true } } } }");
            builder.AppendLine("    $metadata = [PSCustomObject]@{ name = [string]$name; exists = $true; resolvedName = [string]$info.Name; commandType = [string]$info.CommandType; moduleName = [string]$info.ModuleName; definition = [string]$info.Definition; parameterNames = @($parameterNames.Keys) }");
            builder.AppendLine("    $script:__psstudioCommandMetadataCache[$cacheKey] = $metadata");
            builder.AppendLine("    $metadataResult += $metadata");
            builder.AppendLine("  }");
            builder.AppendLine("  if ($unknownCommandCount -gt 0) { if ($null -eq $script:__psstudioCommandNameCache) { $script:__psstudioCommandNameCache = @(Get-Command -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name | Sort-Object -Unique) }; $availableCommandNames = @($script:__psstudioCommandNameCache) }");
            builder.AppendLine("}");
            builder.AppendLine("$script:__psstudioVariableResult = @(); $script:__psstudioVariableDefinitionKeys = @{}");
            builder.AppendLine("function Add-PSStudioVariableFact($node, [bool]$isDefinition, [bool]$isRead, [string]$kind) {");
            builder.AppendLine("  if ($null -eq $node -or -not ($node -is [System.Management.Automation.Language.VariableExpressionAst])) { return }");
            builder.AppendLine("  if ($node.VariablePath.IsDriveQualified) { return }");
            builder.AppendLine("  $name = [string]$node.VariablePath.UserPath");
            builder.AppendLine("  if ([string]::IsNullOrWhiteSpace($name)) { return }");
            builder.AppendLine("  $script:__psstudioVariableResult += [PSCustomObject]@{ name = $name; startLine = $node.Extent.StartLineNumber; startColumn = $node.Extent.StartColumnNumber; endLine = $node.Extent.EndLineNumber; endColumn = $node.Extent.EndColumnNumber; isDefinition = $isDefinition; isRead = $isRead; definitionKind = $kind }");
            builder.AppendLine("  if ($isDefinition) { $script:__psstudioVariableDefinitionKeys[[string]::Concat($node.Extent.StartLineNumber, ':', $node.Extent.StartColumnNumber)] = $true }");
            builder.AppendLine("}");
            builder.AppendLine("function Test-PSStudioSameExtent($left, $right) { if ($null -eq $left -or $null -eq $right) { return $false }; return $left.Extent.StartLineNumber -eq $right.Extent.StartLineNumber -and $left.Extent.StartColumnNumber -eq $right.Extent.StartColumnNumber -and $left.Extent.EndLineNumber -eq $right.Extent.EndLineNumber -and $left.Extent.EndColumnNumber -eq $right.Extent.EndColumnNumber }");
            builder.AppendLine("function Test-PSStudioAssignmentTarget($varNode) {");
            builder.AppendLine("  $node = $varNode; $parent = $varNode.Parent");
            builder.AppendLine("  while ($null -ne $parent -and ($parent -is [System.Management.Automation.Language.ConvertExpressionAst] -or $parent -is [System.Management.Automation.Language.AttributedExpressionAst])) { $node = $parent; $parent = $parent.Parent }");
            builder.AppendLine("  return $parent -is [System.Management.Automation.Language.AssignmentStatementAst] -and (Test-PSStudioSameExtent $parent.Left $node)");
            builder.AppendLine("}");
            builder.AppendLine("foreach ($p in @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.ParameterAst] }, $true))) { Add-PSStudioVariableFact $p.Name $true $false 'Parameter' }");
            builder.AppendLine("foreach ($assignment in @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true))) { foreach ($v in @($assignment.Left.FindAll({ param($node) $node -is [System.Management.Automation.Language.VariableExpressionAst] }, $true))) { Add-PSStudioVariableFact $v $true $false 'Assignment' } }");
            builder.AppendLine("foreach ($foreachAst in @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.ForEachStatementAst] }, $true))) { Add-PSStudioVariableFact $foreachAst.Variable $true $false 'ForEach' }");
            builder.AppendLine("foreach ($usingAst in @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.UsingExpressionAst] }, $true))) { if ($usingAst.SubExpression -is [System.Management.Automation.Language.VariableExpressionAst]) { Add-PSStudioVariableFact $usingAst.SubExpression $true $true 'Using' } }");
            builder.AppendLine("foreach ($v in @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.VariableExpressionAst] }, $true))) {");
            builder.AppendLine("  $key = [string]::Concat($v.Extent.StartLineNumber, ':', $v.Extent.StartColumnNumber)");
            builder.AppendLine("  if ($script:__psstudioVariableDefinitionKeys.ContainsKey($key)) { continue }");
            builder.AppendLine("  if (Test-PSStudioAssignmentTarget $v) { continue }");
            builder.AppendLine("  Add-PSStudioVariableFact $v $false $true 'Read'");
            builder.AppendLine("}");
            builder.AppendLine("if ($null -eq $script:__psstudioApprovedVerbCache) { $script:__psstudioApprovedVerbCache = @(Get-Verb -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Verb | Sort-Object -Unique) }");
            builder.AppendLine("$approvedVerbs = @($script:__psstudioApprovedVerbCache)");
            builder.AppendLine("$facts = [PSCustomObject]@{ functions = @($functionResult); commands = @($commandResult); commandMetadata = @($metadataResult); variables = @($script:__psstudioVariableResult); availableCommandNames = @($availableCommandNames); approvedVerbs = @($approvedVerbs) }");
            builder.AppendLine("$response = [PSCustomObject]@{ ok = $true; message = $null; errors = @($result); tokens = @($tokenResult); facts = $facts }");
            builder.AppendLine("} catch {");
            builder.AppendLine("$response = [PSCustomObject]@{ ok = $false; message = $_.Exception.Message; errors = @(); tokens = @(); facts = $null }");
            builder.AppendLine("}");
            AppendTransportEnvelope(builder, startMarker, endMarker);
            return builder.ToString();
        }

        private static void AppendTransportEnvelope(StringBuilder builder, string startMarker, string endMarker)
        {
            builder.AppendLine("$json = $response | ConvertTo-Json -Compress -Depth 8");
            builder.AppendLine("$payload = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($json))");
            builder.Append("$transport = [string]::Concat([Environment]::NewLine, '").Append(EscapePowerShellSingleQuotedString(startMarker)).Append("', [Environment]::NewLine, 'PAYLOAD:', $payload, [Environment]::NewLine, '").Append(EscapePowerShellSingleQuotedString(endMarker)).AppendLine("', [Environment]::NewLine)");
            builder.AppendLine("[Console]::Out.Write($transport)");
            builder.AppendLine("[Console]::Out.Flush()");
            builder.AppendLine("}");
        }

        private static string EscapePowerShellSingleQuotedString(string value)
        {
            return value.Replace("'", "''", StringComparison.Ordinal);
        }

        private static bool TryExtractPayloadBlock(string text, string startMarker, string endMarker, out string payload)
        {
            payload = string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var normalizedText = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            var lines = normalizedText.Split('\n');

            var blockStartLine = -1;
            for (var index = 0; index < lines.Length; index++)
            {
                var trimmedLine = lines[index].Trim();
                if (string.Equals(trimmedLine, startMarker, StringComparison.Ordinal))
                {
                    blockStartLine = index;
                    continue;
                }

                if (blockStartLine >= 0 && string.Equals(trimmedLine, endMarker, StringComparison.Ordinal))
                {
                    for (var payloadLineIndex = blockStartLine + 1; payloadLineIndex < index; payloadLineIndex++)
                    {
                        var payloadLine = lines[payloadLineIndex].Trim();
                        if (payloadLine.StartsWith("PAYLOAD:", StringComparison.Ordinal))
                        {
                            payload = payloadLine.Substring("PAYLOAD:".Length).Trim();
                            return true;
                        }
                    }

                    return false;
                }
            }

            return false;
        }

        private static string BuildFailurePayload(string message)
        {
            var response = new
            {
                ok = false,
                message,
                errors = Array.Empty<object>(),
                tokens = Array.Empty<object>(),
                facts = (object?)null
            };

            var json = JsonSerializer.Serialize(response);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        private static DiagnosticsParseResult ParseTransportPayload(string payload, string scriptText, string requestErrorOutput)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return new DiagnosticsParseResult(Array.Empty<ParseErrorInfo>(), BuildInfrastructureFailureMessage("The diagnostics service did not return any output.", requestErrorOutput));
            }

            string json;
            if (payload.Length > 0 && (payload[0] == '{' || payload[0] == '['))
            {
                // Backward-compatible fallback in case an older session returns raw JSON.
                json = payload;
            }
            else
            {
                try
                {
                    var jsonBytes = Convert.FromBase64String(payload);
                    json = Encoding.UTF8.GetString(jsonBytes);
                }
                catch (Exception ex)
                {
                    return new DiagnosticsParseResult(
                        Array.Empty<ParseErrorInfo>(),
                        BuildInfrastructureFailureMessage($"The diagnostics service returned malformed transport data: {ex.Message}", requestErrorOutput));
                }
            }

            var lineStartOffsets = BuildLineStartOffsets(scriptText);

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    return new DiagnosticsParseResult(ParseErrorsArray(root, scriptText, lineStartOffsets));
                }

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return new DiagnosticsParseResult(Array.Empty<ParseErrorInfo>(), BuildInfrastructureFailureMessage("The diagnostics service returned output in an unexpected format.", requestErrorOutput));
                }

                if (!root.TryGetProperty("ok", out var okElement) ||
                    (okElement.ValueKind != JsonValueKind.True && okElement.ValueKind != JsonValueKind.False))
                {
                    return new DiagnosticsParseResult(Array.Empty<ParseErrorInfo>(), BuildInfrastructureFailureMessage("The diagnostics service returned an incomplete response envelope.", requestErrorOutput));
                }

                if (!okElement.GetBoolean())
                {
                    var message = root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                        ? messageElement.GetString()
                        : null;
                    return new DiagnosticsParseResult(Array.Empty<ParseErrorInfo>(), BuildInfrastructureFailureMessage(message ?? "The diagnostics service reported an internal parser failure.", requestErrorOutput));
                }

                if (!root.TryGetProperty("errors", out var errorsElement) || errorsElement.ValueKind != JsonValueKind.Array)
                {
                    return new DiagnosticsParseResult(Array.Empty<ParseErrorInfo>(), BuildInfrastructureFailureMessage("The diagnostics service returned a response without an errors array.", requestErrorOutput));
                }

                var parsedErrors = ParseErrorsArray(errorsElement, scriptText, lineStartOffsets);
                var parsedTokens = root.TryGetProperty("tokens", out var tokensElement) && tokensElement.ValueKind == JsonValueKind.Array
                    ? ParseTokensArray(tokensElement, scriptText, lineStartOffsets)
                    : Array.Empty<SyntaxTokenInfo>();

                var authoringFacts = root.TryGetProperty("facts", out var factsElement) && factsElement.ValueKind == JsonValueKind.Object
                    ? ParseAuthoringFacts(factsElement, scriptText, lineStartOffsets)
                    : ScriptAuthoringFacts.Empty;

                return new DiagnosticsParseResult(parsedErrors, syntaxTokens: parsedTokens, authoringFacts: authoringFacts);
            }
            catch (Exception ex)
            {
                return new DiagnosticsParseResult(
                    Array.Empty<ParseErrorInfo>(),
                    BuildInfrastructureFailureMessage($"The diagnostics service returned malformed JSON output: {ex.Message}", requestErrorOutput));
            }
        }

        private static List<ParseErrorInfo> ParseErrorsArray(JsonElement arrayElement, string scriptText, IReadOnlyList<int> lineStartOffsets)
        {
            var results = new List<ParseErrorInfo>(arrayElement.GetArrayLength());
            foreach (var element in arrayElement.EnumerateArray())
            {
                var message = element.GetProperty("message").GetString() ?? string.Empty;
                var startLine = element.GetProperty("startLine").GetInt32();
                var startColumn = element.GetProperty("startColumn").GetInt32();
                var endLine = element.GetProperty("endLine").GetInt32();
                var endColumn = element.GetProperty("endColumn").GetInt32();

                var startOffset = LineColumnToOffset(lineStartOffsets, scriptText, startLine, startColumn);
                var endOffset = LineColumnToOffset(lineStartOffsets, scriptText, endLine, endColumn);

                if (startOffset >= scriptText.Length && scriptText.Length > 0)
                {
                    startOffset = scriptText.Length - 1;
                }

                if (endOffset <= startOffset)
                {
                    endOffset = scriptText.Length == 0
                        ? 0
                        : Math.Min(startOffset + 1, scriptText.Length);
                }

                results.Add(new ParseErrorInfo(message, startOffset, endOffset));
            }

            return results;
        }

        private static IReadOnlyList<SyntaxTokenInfo> ParseTokensArray(JsonElement arrayElement, string scriptText, IReadOnlyList<int> lineStartOffsets)
        {
            var results = new List<SyntaxTokenInfo>(arrayElement.GetArrayLength());
            foreach (var element in arrayElement.EnumerateArray())
            {
                var kind = element.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String
                    ? kindElement.GetString() ?? string.Empty
                    : string.Empty;
                var text = element.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                    ? textElement.GetString() ?? string.Empty
                    : string.Empty;
                var startLine = element.GetProperty("startLine").GetInt32();
                var startColumn = element.GetProperty("startColumn").GetInt32();
                var endLine = element.GetProperty("endLine").GetInt32();
                var endColumn = element.GetProperty("endColumn").GetInt32();

                var startOffset = LineColumnToOffset(lineStartOffsets, scriptText, startLine, startColumn);
                var endOffset = LineColumnToOffset(lineStartOffsets, scriptText, endLine, endColumn);

                if (startOffset < 0 || startOffset > scriptText.Length || endOffset <= startOffset)
                {
                    continue;
                }

                endOffset = Math.Clamp(endOffset, startOffset + 1, scriptText.Length);
                results.Add(new SyntaxTokenInfo(kind, text, startOffset, endOffset));
            }

            return results;
        }

        private static ScriptAuthoringFacts ParseAuthoringFacts(JsonElement factsElement, string scriptText, IReadOnlyList<int> lineStartOffsets)
        {
            var functions = factsElement.TryGetProperty("functions", out var functionsElement) && functionsElement.ValueKind == JsonValueKind.Array
                ? ParseFunctionsArray(functionsElement, scriptText, lineStartOffsets)
                : Array.Empty<FunctionDefinitionInfo>();

            var commands = factsElement.TryGetProperty("commands", out var commandsElement) && commandsElement.ValueKind == JsonValueKind.Array
                ? ParseCommandsArray(commandsElement, scriptText, lineStartOffsets)
                : Array.Empty<CommandInvocationInfo>();

            var metadata = factsElement.TryGetProperty("commandMetadata", out var metadataElement) && metadataElement.ValueKind == JsonValueKind.Array
                ? ParseCommandMetadataArray(metadataElement)
                : Array.Empty<CommandMetadataInfo>();

            var variables = factsElement.TryGetProperty("variables", out var variablesElement) && variablesElement.ValueKind == JsonValueKind.Array
                ? ParseVariablesArray(variablesElement, scriptText, lineStartOffsets)
                : Array.Empty<VariableUsageInfo>();

            var availableCommands = factsElement.TryGetProperty("availableCommandNames", out var availableElement) && availableElement.ValueKind == JsonValueKind.Array
                ? ParseStringArray(availableElement)
                : Array.Empty<string>();

            var approvedVerbs = factsElement.TryGetProperty("approvedVerbs", out var approvedVerbsElement) && approvedVerbsElement.ValueKind == JsonValueKind.Array
                ? ParseStringArray(approvedVerbsElement)
                : Array.Empty<string>();

            return new ScriptAuthoringFacts(functions, commands, metadata, variables, availableCommands, approvedVerbs);
        }

        private static IReadOnlyList<FunctionDefinitionInfo> ParseFunctionsArray(JsonElement arrayElement, string scriptText, IReadOnlyList<int> lineStartOffsets)
        {
            var results = new List<FunctionDefinitionInfo>(arrayElement.GetArrayLength());
            foreach (var element in arrayElement.EnumerateArray())
            {
                var name = TryGetString(element, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var startOffset = ExtentToStartOffset(element, scriptText, lineStartOffsets);
                var endOffset = ExtentToEndOffset(element, scriptText, lineStartOffsets);
                var nameStartOffset = FindFunctionNameOffset(scriptText, startOffset, endOffset, name);
                var nameEndOffset = Math.Min(nameStartOffset + name.Length, scriptText.Length);

                results.Add(new FunctionDefinitionInfo(name, startOffset, endOffset, nameStartOffset, nameEndOffset));
            }

            return results;
        }

        private static IReadOnlyList<CommandInvocationInfo> ParseCommandsArray(JsonElement arrayElement, string scriptText, IReadOnlyList<int> lineStartOffsets)
        {
            var results = new List<CommandInvocationInfo>(arrayElement.GetArrayLength());
            foreach (var element in arrayElement.EnumerateArray())
            {
                var name = TryGetString(element, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var parameters = element.TryGetProperty("parameters", out var parametersElement) && parametersElement.ValueKind == JsonValueKind.Array
                    ? ParseCommandParametersArray(parametersElement, scriptText, lineStartOffsets)
                    : Array.Empty<CommandParameterUsageInfo>();

                results.Add(new CommandInvocationInfo(
                    name,
                    ExtentToStartOffset(element, scriptText, lineStartOffsets),
                    ExtentToEndOffset(element, scriptText, lineStartOffsets),
                    parameters));
            }

            return results;
        }

        private static IReadOnlyList<CommandParameterUsageInfo> ParseCommandParametersArray(JsonElement arrayElement, string scriptText, IReadOnlyList<int> lineStartOffsets)
        {
            var results = new List<CommandParameterUsageInfo>(arrayElement.GetArrayLength());
            foreach (var element in arrayElement.EnumerateArray())
            {
                var name = TryGetString(element, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                results.Add(new CommandParameterUsageInfo(
                    name,
                    TryGetString(element, "text") ?? "-" + name,
                    ExtentToStartOffset(element, scriptText, lineStartOffsets),
                    ExtentToEndOffset(element, scriptText, lineStartOffsets)));
            }

            return results;
        }

        private static IReadOnlyList<CommandMetadataInfo> ParseCommandMetadataArray(JsonElement arrayElement)
        {
            var results = new List<CommandMetadataInfo>(arrayElement.GetArrayLength());
            foreach (var element in arrayElement.EnumerateArray())
            {
                var name = TryGetString(element, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var exists = element.TryGetProperty("exists", out var existsElement) && existsElement.ValueKind == JsonValueKind.True;
                var parameterNames = element.TryGetProperty("parameterNames", out var parameterNamesElement) && parameterNamesElement.ValueKind == JsonValueKind.Array
                    ? ParseStringArray(parameterNamesElement)
                    : Array.Empty<string>();

                results.Add(new CommandMetadataInfo(
                    name,
                    exists,
                    TryGetString(element, "resolvedName"),
                    TryGetString(element, "commandType"),
                    TryGetString(element, "moduleName"),
                    TryGetString(element, "definition"),
                    parameterNames));
            }

            return results;
        }

        private static IReadOnlyList<VariableUsageInfo> ParseVariablesArray(JsonElement arrayElement, string scriptText, IReadOnlyList<int> lineStartOffsets)
        {
            var results = new List<VariableUsageInfo>(arrayElement.GetArrayLength());
            foreach (var element in arrayElement.EnumerateArray())
            {
                var name = TryGetString(element, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var startOffset = ExtentToStartOffset(element, scriptText, lineStartOffsets);
                var endOffset = ExtentToEndOffset(element, scriptText, lineStartOffsets);
                if (endOffset <= startOffset)
                {
                    endOffset = Math.Min(startOffset + Math.Max(1, name.Length + 1), scriptText.Length);
                }

                results.Add(new VariableUsageInfo(
                    name,
                    startOffset,
                    Math.Clamp(endOffset, Math.Min(startOffset + 1, scriptText.Length), scriptText.Length),
                    TryGetBool(element, "isDefinition"),
                    TryGetBool(element, "isRead"),
                    TryGetString(element, "definitionKind")));
            }

            return results;
        }

        private static IReadOnlyList<string> ParseStringArray(JsonElement arrayElement)
        {
            var results = new List<string>(arrayElement.GetArrayLength());
            foreach (var element in arrayElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    results.Add(value);
                }
            }

            return results;
        }

        private static int FindFunctionNameOffset(string scriptText, int startOffset, int endOffset, string functionName)
        {
            if (string.IsNullOrWhiteSpace(functionName) || string.IsNullOrEmpty(scriptText))
            {
                return Math.Clamp(startOffset, 0, Math.Max(0, scriptText.Length - 1));
            }

            var safeStart = Math.Clamp(startOffset, 0, scriptText.Length);
            var safeEnd = Math.Clamp(endOffset, safeStart, scriptText.Length);
            var searchLength = Math.Max(0, safeEnd - safeStart);
            if (searchLength == 0)
            {
                return safeStart;
            }

            var relativeIndex = scriptText.IndexOf(functionName, safeStart, searchLength, StringComparison.OrdinalIgnoreCase);
            return relativeIndex >= 0 ? relativeIndex : safeStart;
        }

        private static int ExtentToStartOffset(JsonElement element, string scriptText, IReadOnlyList<int> lineStartOffsets)
        {
            return LineColumnToOffset(
                lineStartOffsets,
                scriptText,
                TryGetInt(element, "startLine", 1),
                TryGetInt(element, "startColumn", 1));
        }

        private static int ExtentToEndOffset(JsonElement element, string scriptText, IReadOnlyList<int> lineStartOffsets)
        {
            var offset = LineColumnToOffset(
                lineStartOffsets,
                scriptText,
                TryGetInt(element, "endLine", 1),
                TryGetInt(element, "endColumn", 1));
            return Math.Clamp(offset, 0, scriptText.Length);
        }

        private static string? TryGetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var valueElement) && valueElement.ValueKind == JsonValueKind.String
                ? valueElement.GetString()
                : null;
        }

        private static int TryGetInt(JsonElement element, string propertyName, int defaultValue)
        {
            return element.TryGetProperty(propertyName, out var valueElement) && valueElement.ValueKind == JsonValueKind.Number && valueElement.TryGetInt32(out var value)
                ? value
                : defaultValue;
        }

        private static bool TryGetBool(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var valueElement) && valueElement.ValueKind == JsonValueKind.True;
        }

        private static string BuildInfrastructureFailureMessage(string message, string requestErrorOutput)
        {
            var normalizedMessage = string.IsNullOrWhiteSpace(message)
                ? "Diagnostics service failure."
                : message.Trim();

            if (!normalizedMessage.StartsWith("Diagnostics service failure:", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(normalizedMessage, "Diagnostics service failure.", StringComparison.OrdinalIgnoreCase))
            {
                normalizedMessage = $"Diagnostics service failure: {normalizedMessage}";
            }

            if (string.IsNullOrWhiteSpace(requestErrorOutput))
            {
                return normalizedMessage;
            }

            var normalizedError = string.Join(" ", requestErrorOutput
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Take(24));

            if (string.IsNullOrWhiteSpace(normalizedError))
            {
                return normalizedMessage;
            }

            return $"{normalizedMessage} PowerShell stderr: {normalizedError}";
        }

        private static List<int> BuildLineStartOffsets(string text)
        {
            var offsets = new List<int> { 0 };
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] == '\n')
                {
                    offsets.Add(index + 1);
                }
            }

            return offsets;
        }

        private static int LineColumnToOffset(IReadOnlyList<int> lineStartOffsets, string text, int lineNumber, int columnNumber)
        {
            if (lineStartOffsets.Count == 0)
            {
                return 0;
            }

            var normalizedLine = Math.Max(1, lineNumber);
            if (normalizedLine > lineStartOffsets.Count)
            {
                return text.Length;
            }

            var lineStart = lineStartOffsets[normalizedLine - 1];
            var normalizedColumn = Math.Max(1, columnNumber);
            return Math.Clamp(lineStart + normalizedColumn - 1, 0, text.Length);
        }

        private static string NormalizeRuntimePath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path.Trim();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PowerShellDiagnosticsService));
            }
        }

        private sealed class DiagnosticsRequestOptions
        {
            private DiagnosticsRequestOptions(bool includeAuthoringFacts)
            {
                IncludeAuthoringFacts = includeAuthoringFacts;
            }

            public bool IncludeAuthoringFacts { get; }

            public static DiagnosticsRequestOptions FromScript(string scriptText, PowerShellDiagnosticsMode diagnosticsMode)
            {
                if (diagnosticsMode == PowerShellDiagnosticsMode.SyntaxOnly || string.IsNullOrEmpty(scriptText))
                {
                    return new DiagnosticsRequestOptions(includeAuthoringFacts: false);
                }

                var lineCount = 1;
                foreach (var ch in scriptText)
                {
                    if (ch == '\n')
                    {
                        lineCount++;
                    }
                }

                var includeAuthoringFacts = scriptText.Length <= FullAuthoringAnalysisMaxCharacters &&
                    lineCount <= FullAuthoringAnalysisMaxLines;

                return new DiagnosticsRequestOptions(includeAuthoringFacts);
            }
        }

        private sealed class ActiveRequest
        {
            public ActiveRequest()
            {
                var id = Guid.NewGuid().ToString("N");
                StartMarker = $"##PSSTUDIO_DIAG_START_{id}##";
                EndMarker = $"##PSSTUDIO_DIAG_END_{id}##";
            }

            public string StartMarker { get; }
            public string EndMarker { get; }
            public StringBuilder Capture { get; } = new();
            public StringBuilder ErrorCapture { get; } = new();
            public TaskCompletionSource<string> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public string GetErrorCaptureText()
            {
                return ErrorCapture.ToString();
            }
        }
    }
}
