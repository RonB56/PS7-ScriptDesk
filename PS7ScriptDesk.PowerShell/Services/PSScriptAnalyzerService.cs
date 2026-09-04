using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.PowerShell.Services;

public sealed class PSScriptAnalyzerService : IPSScriptAnalyzerService, IAsyncDisposable, IDisposable
{
    public const string BundledVersion = "1.25.0";
    public const string LiveEditingProfile = "LiveEditingFast";
    /// <summary>CSV contract for rules excluded from live feedback after measured pathological latency.</summary>
    public const string LiveEditingExcludedRulesCsv = "PSAvoidUsingCmdletAliases,PSAvoidUsingPositionalParameters,PSShouldProcess,PSUseCmdletCorrectly";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly string _runtimePath;
    private readonly string _moduleManifestPath;
    private readonly Func<CancellationToken, Task<IPSScriptAnalyzerWorker>>? _workerFactory;
    private readonly TimeSpan _requestTimeout;
    private readonly object _sync = new();
    private Process? _worker;
    private IPSScriptAnalyzerWorker? _injectedWorker;
    private StreamWriter? _input;
    private Task? _stderrTask;
    private bool _disposed;
    private PSScriptAnalyzerWorkerState _workerState = PSScriptAnalyzerWorkerState.NotStarted;
    private int _workerGeneration;
    private int _restartCount;
    private int? _workerProcessId;
    private string? _currentRequestId;
    private string? _lastFailureCategory;
    private long? _lastColdStartMilliseconds;
    private long? _lastAnalysisMilliseconds;
    private long? _lastSuccessfulAnalysisMilliseconds;

    public PSScriptAnalyzerService(string runtimePath, string? moduleManifestPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePath);
        _runtimePath = runtimePath;
        _moduleManifestPath = moduleManifestPath ?? Path.Combine(AppContext.BaseDirectory, "Dependencies", "PSScriptAnalyzer", BundledVersion, "PSScriptAnalyzer.psd1");
        BundledAnalyzerVersion = ResolveBundledAnalyzerVersion(_moduleManifestPath);
        _requestTimeout = RequestTimeout;
    }

    internal PSScriptAnalyzerService(Func<CancellationToken, Task<IPSScriptAnalyzerWorker>> workerFactory, TimeSpan requestTimeout)
    {
        _workerFactory = workerFactory ?? throw new ArgumentNullException(nameof(workerFactory));
        _requestTimeout = requestTimeout > TimeSpan.Zero ? requestTimeout : throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _runtimePath = string.Empty;
        _moduleManifestPath = string.Empty;
        BundledAnalyzerVersion = null;
    }

    public string? BundledAnalyzerVersion { get; }

    public PSScriptAnalyzerWorkerHealthSnapshot Health
    {
        get { lock (_sync) return new(_workerState, _workerGeneration, _restartCount, _workerProcessId, _runtimePath, BundledAnalyzerVersion, _currentRequestId, _lastFailureCategory, _lastColdStartMilliseconds, _lastAnalysisMilliseconds, _lastSuccessfulAnalysisMilliseconds); }
    }

    public Task<PSScriptAnalyzerResult> AnalyzeAsync(PSScriptAnalyzerRequest request, CancellationToken cancellationToken = default)
        => AnalyzeCoreAsync(request, null, cancellationToken);

    public Task<PSScriptAnalyzerResult> AnalyzeWithProgressAsync(PSScriptAnalyzerRequest request, Action<PSScriptAnalyzerProgress> onProgress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onProgress);
        return AnalyzeCoreAsync(request with { EnableProgress = true }, onProgress, cancellationToken);
    }

    private async Task<PSScriptAnalyzerResult> AnalyzeCoreAsync(PSScriptAnalyzerRequest request, Action<PSScriptAnalyzerProgress>? onProgress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        lock (_sync) { _workerState = PSScriptAnalyzerWorkerState.Busy; _currentRequestId = request.RequestId; }
        try
        {
            await EnsureWorkerReadyAsync(cancellationToken).ConfigureAwait(false);
            var progressMode = request.EnableProgress && CanUseRuleLevelProgress(request);
            var effectiveRequest = request with { EnableProgress = progressMode };
            DeveloperDiagnostics.LogInfo("PSScriptAnalyzer", "Selected analyzer execution mode.", new Dictionary<string, object?>
            {
                ["requestId"] = request.RequestId,
                ["profile"] = request.Profile,
                ["canUseRuleLevelProgress"] = progressMode,
                ["executionMode"] = progressMode ? "SequentialRules" : "Monolithic",
                ["fallbackReason"] = request.EnableProgress && !progressMode ? "ProfileNotEquivalenceApproved" : null
            });
            if (request.EnableProgress && !progressMode)
            {
                DeveloperDiagnostics.LogInfo("PSScriptAnalyzer", "Progress mode fell back to monolithic analysis because the request profile is not equivalence-approved.", new Dictionary<string, object?>
                {
                    ["requestId"] = request.RequestId,
                    ["profile"] = request.Profile
                });
            }
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(effectiveRequest)));
            if (_workerFactory is not null)
            {
                IPSScriptAnalyzerWorker injected;
                lock (_sync) injected = _injectedWorker ?? throw new InvalidOperationException("Analyzer test worker is unavailable.");
                await injected.WriteLineAsync(payload, cancellationToken).ConfigureAwait(false);
                using var injectedTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                injectedTimeout.CancelAfter(_requestTimeout);
                return await ReadResultAsync(effectiveRequest, injected, injectedTimeout.Token, stopwatch, onProgress).ConfigureAwait(false);
            }
            StreamWriter input;
            lock (_sync) input = _input ?? throw new InvalidOperationException("Analyzer worker input is unavailable.");
            await input.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!effectiveRequest.EnableProgress || _workerFactory is not null)
            {
                timeout.CancelAfter(_requestTimeout);
            }
            return await ReadResultAsync(effectiveRequest, null, timeout.Token, stopwatch, onProgress).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            onProgress?.Invoke(new PSScriptAnalyzerProgress(request.RequestId, request.DocumentId, request.Revision, PSScriptAnalyzerProgressState.AnalysisCancelled, 0, 0, null, stopwatch.ElapsedMilliseconds, 0, 0, "Canceled"));
            DeveloperDiagnostics.LogInfo("PSScriptAnalyzer", "Analysis request canceled.", new Dictionary<string, object?> { ["requestId"] = request.RequestId, ["progressMode"] = request.EnableProgress });
            DisposeWorker("Cancellation");
            throw;
        }
        catch (Exception ex)
        {
            DeveloperDiagnostics.LogException("PSScriptAnalyzer", ex, "Analyzer worker request failed.", new Dictionary<string, object?> { ["requestId"] = request.RequestId, ["durationMs"] = stopwatch.ElapsedMilliseconds });
            lock (_sync) { _workerState = PSScriptAnalyzerWorkerState.Faulted; _lastFailureCategory = ex.GetType().Name; }
            DisposeWorker("RequestFailure");
            onProgress?.Invoke(new PSScriptAnalyzerProgress(request.RequestId, request.DocumentId, request.Revision, PSScriptAnalyzerProgressState.WorkerHealthFailure, 0, 0, null, stopwatch.ElapsedMilliseconds, 0, 0, ex.GetType().Name));
            return new PSScriptAnalyzerResult(request.RequestId, Array.Empty<PSScriptAnalyzerFinding>(), ex.Message);
        }
        finally
        {
            lock (_sync)
            {
                _lastAnalysisMilliseconds = stopwatch.ElapsedMilliseconds;
                if (!_disposed) _workerState = _worker is not null || _injectedWorker is not null ? PSScriptAnalyzerWorkerState.Ready : PSScriptAnalyzerWorkerState.Unavailable;
                _currentRequestId = null;
            }
            _requestGate.Release();
        }
    }

    public void Dispose() { if (!_disposed) { _disposed = true; lock (_sync) _workerState = PSScriptAnalyzerWorkerState.Disposed; DisposeWorker("Shutdown"); _requestGate.Dispose(); } }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

    private async Task EnsureWorkerReadyAsync(CancellationToken cancellationToken)
    {
        if (_workerFactory is not null)
        {
            lock (_sync) if (_injectedWorker is not null) return;
            var injected = await _workerFactory(cancellationToken).ConfigureAwait(false);
            var injectedReady = await injected.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(injectedReady, "##PSSA_READY##", StringComparison.Ordinal)) { injected.Dispose(); throw new InvalidDataException("PSScriptAnalyzer worker readiness marker was not received."); }
            lock (_sync) _injectedWorker = injected;
            return;
        }
        lock (_sync) if (_worker is { HasExited: false } && _input is not null) return;
        lock (_sync)
        {
            _workerState = PSScriptAnalyzerWorkerState.Starting;
            if (_workerGeneration > 0) _restartCount++;
        }
        DisposeWorker("Recovery");
        if (!File.Exists(_runtimePath)) throw new FileNotFoundException("PowerShell runtime was not found.", _runtimePath);
        if (!File.Exists(_moduleManifestPath)) throw new FileNotFoundException("Bundled PSScriptAnalyzer module was not found.", _moduleManifestPath);
        var script = """
$ErrorActionPreference='Stop'
$moduleImportClock=[Diagnostics.Stopwatch]::StartNew()
Import-Module -Force -Name '__MODULE__'
$moduleImportClock.Stop()
$warmupClock=[Diagnostics.Stopwatch]::StartNew()
Invoke-ScriptAnalyzer -ScriptDefinition 'Write-Host warmup' -IncludeRule 'PSAvoidUsingCmdletAliases' -ErrorAction Stop | Out-Null
$warmupClock.Stop()
[Console]::Out.WriteLine('##PSSA_READY##')
[Console]::Out.Flush()
function Send-Frame($marker,$value){$j=$value|ConvertTo-Json -Compress -Depth 8;$b=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($j));[Console]::Out.WriteLine($marker+$b);[Console]::Out.Flush()}
function Send-Diagnostic($request,$phase,$elapsed,$details){Send-Frame ('##PSSA_DIAGNOSTIC_'+$request.RequestId+'##') ([ordered]@{RequestId=[string]$request.RequestId;DocumentId=[string]$request.DocumentId;DocumentRevision=$request.Revision;Phase=$phase;ElapsedMilliseconds=$elapsed;Details=$details})}
while($line=[Console]::In.ReadLine()){
  try {
    $requestClock=[Diagnostics.Stopwatch]::StartNew()
    $r=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($line)) | ConvertFrom-Json
    $requestClock.Stop(); Send-Diagnostic $r 'RequestDeserialization' $requestClock.ElapsedMilliseconds @{Bytes=$line.Length;ProcessId=$PID}
    $parseClock=[Diagnostics.Stopwatch]::StartNew();$tokens=$null;$parseErrors=$null;[System.Management.Automation.Language.Parser]::ParseInput([string]$r.ScriptText,[ref]$tokens,[ref]$parseErrors)|Out-Null;$parseClock.Stop();Send-Diagnostic $r 'ScriptParsing' $parseClock.ElapsedMilliseconds @{Characters=([string]$r.ScriptText).Length;ParseErrors=@($parseErrors).Count}
    $discoveryClock=[Diagnostics.Stopwatch]::StartNew();$rules=@(Get-ScriptAnalyzerRule|Sort-Object RuleName);$discoveryClock.Stop();
    $liveExcludedRules=@('PSAvoidUsingCmdletAliases','PSAvoidUsingPositionalParameters','PSShouldProcess','PSUseCmdletCorrectly')
    if([string]$r.Profile -eq 'LiveEditingFast'){$rules=@($rules|Where-Object RuleName -notin $liveExcludedRules)}
    Send-Diagnostic $r 'RuleDiscovery' $discoveryClock.ElapsedMilliseconds @{RuleCount=$rules.Count;Profile=[string]$r.Profile;ExcludedRules=if([string]$r.Profile -eq 'LiveEditingFast'){$liveExcludedRules -join ','}else{$null}}
    $allItems=@();$clock=[Diagnostics.Stopwatch]::StartNew()
    if($r.EnableProgress -or [string]$r.Profile -eq 'LiveEditingFast'){
      if($r.EnableProgress){Send-Frame ('##PSSA_PROGRESS_'+$r.RequestId+'##') ([ordered]@{RequestId=$r.RequestId;DocumentId=$r.DocumentId;DocumentRevision=$r.Revision;State='AnalysisStarted';CurrentRuleIndex=0;TotalRules=$rules.Count;RuleName=$null;ElapsedMilliseconds=0;RuleElapsedMilliseconds=0;FindingsSoFar=0});Send-Frame ('##PSSA_PROGRESS_'+$r.RequestId+'##') ([ordered]@{RequestId=$r.RequestId;DocumentId=$r.DocumentId;DocumentRevision=$r.Revision;State='PreparingAnalyzer';CurrentRuleIndex=0;TotalRules=$rules.Count;RuleName=$null;ElapsedMilliseconds=$clock.ElapsedMilliseconds;RuleElapsedMilliseconds=0;FindingsSoFar=0})}
      $i=0;foreach($rule in $rules){$i++;$ruleClock=[Diagnostics.Stopwatch]::StartNew();if($r.EnableProgress){Send-Frame ('##PSSA_PROGRESS_'+$r.RequestId+'##') ([ordered]@{RequestId=$r.RequestId;DocumentId=$r.DocumentId;DocumentRevision=$r.Revision;State='RuleStarted';CurrentRuleIndex=$i;TotalRules=$rules.Count;RuleName=$rule.RuleName;ElapsedMilliseconds=$clock.ElapsedMilliseconds;RuleElapsedMilliseconds=0;FindingsSoFar=$allItems.Count})};$items=@(Invoke-ScriptAnalyzer -ScriptDefinition ([string]$r.ScriptText) -IncludeRule $rule.RuleName -ErrorAction Stop|ForEach-Object{$c=$null;if($_.Correction){$c=$_.Correction.Text};[ordered]@{RuleId=$_.RuleName;Message=$_.Message;Severity=[string]$_.Severity;Line=$_.Extent.StartLineNumber;Column=$_.Extent.StartColumnNumber;EndLine=$_.Extent.EndLineNumber;EndColumn=$_.Extent.EndColumnNumber;Correction=$c}});$allItems+=$items;$ruleClock.Stop();if($r.EnableProgress){Send-Frame ('##PSSA_PROGRESS_'+$r.RequestId+'##') ([ordered]@{RequestId=$r.RequestId;DocumentId=$r.DocumentId;DocumentRevision=$r.Revision;State='RuleCompleted';CurrentRuleIndex=$i;TotalRules=$rules.Count;RuleName=$rule.RuleName;ElapsedMilliseconds=$clock.ElapsedMilliseconds;RuleElapsedMilliseconds=$ruleClock.ElapsedMilliseconds;FindingsSoFar=$allItems.Count})}}
    } else {
      $allItems=@(Invoke-ScriptAnalyzer -ScriptDefinition ([string]$r.ScriptText) -ErrorAction Stop|ForEach-Object{$c=$null;if($_.Correction){$c=$_.Correction.Text};[ordered]@{RuleId=$_.RuleName;Message=$_.Message;Severity=[string]$_.Severity;Line=$_.Extent.StartLineNumber;Column=$_.Extent.StartColumnNumber;EndLine=$_.Extent.EndLineNumber;EndColumn=$_.Extent.EndColumnNumber;Correction=$c}})
    }
    $clock.Stop();Send-Diagnostic $r 'RuleExecution' $clock.ElapsedMilliseconds @{Findings=$allItems.Count;RuleCount=$rules.Count;Mode=if($r.EnableProgress){'SequentialRules'}elseif([string]$r.Profile -eq 'LiveEditingFast'){'LiveSequentialRules'}else{'Monolithic'}}
    $serializeClock=[Diagnostics.Stopwatch]::StartNew();$o=[ordered]@{RequestId=[string]$r.RequestId;Findings=$allItems;Error=$null};$j=$o|ConvertTo-Json -Compress -Depth 8;$serializeClock.Stop();Send-Diagnostic $r 'ResultSerialization' $serializeClock.ElapsedMilliseconds @{JsonCharacters=$j.Length;FindingCount=$allItems.Count}
    $b=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($j));[Console]::Out.WriteLine(('##PSSA_RESULT_'+$r.RequestId+'##')+$b);[Console]::Out.Flush()
  } catch {$o=[ordered]@{RequestId=[string]$r.RequestId;Findings=@();Error=$_.Exception.Message};$j=$o|ConvertTo-Json -Compress -Depth 8;$b=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($j));[Console]::Out.WriteLine(('##PSSA_RESULT_'+$r.RequestId+'##')+$b);[Console]::Out.Flush()}
}
""".Replace("__MODULE__", Escape(_moduleManifestPath), StringComparison.Ordinal);
        var psi = new ProcessStartInfo { FileName = _runtimePath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-NoLogo"); psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-NonInteractive"); psi.ArgumentList.Add("-ExecutionPolicy"); psi.ArgumentList.Add("Bypass"); psi.ArgumentList.Add("-Command"); psi.ArgumentList.Add(script);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var workerStart = Stopwatch.StartNew();
        if (!process.Start()) throw new InvalidOperationException("PSScriptAnalyzer worker could not start.");
        var ready = await process.StandardOutput.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(ready, "##PSSA_READY##", StringComparison.Ordinal)) { process.Dispose(); throw new InvalidDataException("PSScriptAnalyzer worker readiness marker was not received."); }
        lock (_sync) { _worker = process; _input = process.StandardInput; }
        _stderrTask = process.StandardError.ReadToEndAsync();
        workerStart.Stop();
        lock (_sync)
        {
            _workerGeneration++;
            _workerProcessId = process.Id;
            _lastColdStartMilliseconds = workerStart.ElapsedMilliseconds;
            _workerState = PSScriptAnalyzerWorkerState.Ready;
        }
        DeveloperDiagnostics.LogInfo("PSScriptAnalyzer", "Analyzer worker ready.", new Dictionary<string, object?> { ["runtimePath"] = _runtimePath, ["modulePath"] = _moduleManifestPath, ["version"] = BundledVersion, ["processId"] = process.Id, ["workerStartupImportAndWarmupMs"] = workerStart.ElapsedMilliseconds, ["reused"] = false });
    }

    private async Task<string?> ReadWorkerLineAsync(CancellationToken cancellationToken)
    {
        Process? worker; lock (_sync) worker = _worker;
        if (worker is null) return null;
        return await worker.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PSScriptAnalyzerResult> ReadResultAsync(PSScriptAnalyzerRequest request, IPSScriptAnalyzerWorker? injected, CancellationToken cancellationToken, Stopwatch stopwatch, Action<PSScriptAnalyzerProgress>? onProgress)
    {
        var prefix = $"##PSSA_RESULT_{request.RequestId}##";
        var phaseTimings = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = injected is null ? await ReadWorkerLineAsync(cancellationToken).ConfigureAwait(false) : await injected.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) throw new IOException("PSScriptAnalyzer worker exited before returning a result.");
            var progressPrefix = $"##PSSA_PROGRESS_{request.RequestId}##";
            if (line.StartsWith("##PSSA_PROGRESS_", StringComparison.Ordinal))
            {
                if (!line.StartsWith(progressPrefix, StringComparison.Ordinal)) continue;
                try
                {
                    var progressJson = Encoding.UTF8.GetString(Convert.FromBase64String(line[progressPrefix.Length..]));
                    var progress = JsonSerializer.Deserialize<PSScriptAnalyzerProgress>(progressJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        Converters = { new JsonStringEnumConverter() }
                    });
                    if (progress is not null && string.Equals(progress.RequestId, request.RequestId, StringComparison.Ordinal) && string.Equals(progress.DocumentId, request.DocumentId, StringComparison.Ordinal) && progress.DocumentRevision == request.Revision)
                    {
                        DeveloperDiagnostics.LogInfo("PSScriptAnalyzer", "Accepted analyzer progress frame.", new Dictionary<string, object?>
                        {
                            ["requestId"] = progress.RequestId,
                            ["state"] = progress.State.ToString(),
                            ["ruleIndex"] = progress.CurrentRuleIndex,
                            ["totalRules"] = progress.TotalRules,
                            ["ruleName"] = progress.RuleName
                        });
                        onProgress?.Invoke(progress);
                    }
                }
                catch (Exception ex)
                {
                    DeveloperDiagnostics.LogInfo("PSScriptAnalyzer", "Malformed progress frame ignored.", new Dictionary<string, object?> { ["requestId"] = request.RequestId, ["exceptionType"] = ex.GetType().Name });
                }
                continue;
            }
            var diagnosticPrefix = $"##PSSA_DIAGNOSTIC_{request.RequestId}##";
            if (line.StartsWith("##PSSA_DIAGNOSTIC_", StringComparison.Ordinal))
            {
                if (!line.StartsWith(diagnosticPrefix, StringComparison.Ordinal)) continue;
                try
                {
                    using var diagnostic = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(line[diagnosticPrefix.Length..])));
                    var root = diagnostic.RootElement;
                    var phaseName = root.TryGetProperty("Phase", out var phase) ? phase.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(phaseName))
                    {
                        phaseTimings[phaseName] = new Dictionary<string, object?>
                        {
                            ["elapsedMs"] = root.TryGetProperty("ElapsedMilliseconds", out var elapsed) ? elapsed.GetInt64() : 0,
                            ["details"] = root.TryGetProperty("Details", out var details) ? details.ToString() : null
                        };
                    }
                }
                catch (Exception ex) { DeveloperDiagnostics.LogInfo("PSScriptAnalyzer", "Malformed worker diagnostic frame ignored.", new Dictionary<string, object?> { ["requestId"] = request.RequestId, ["exceptionType"] = ex.GetType().Name }); }
                continue;
            }
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var resultJson = Encoding.UTF8.GetString(Convert.FromBase64String(line[prefix.Length..]));
            var result = JsonSerializer.Deserialize<PSScriptAnalyzerResult>(resultJson);
            if (result is null) throw new InvalidDataException("PSScriptAnalyzer worker returned an empty result.");
            DeveloperDiagnostics.LogInfo("PSScriptAnalyzer", "Analysis request completed.", new Dictionary<string, object?>
            {
                ["requestId"] = request.RequestId, ["documentId"] = request.DocumentId, ["revision"] = request.Revision,
                ["durationMs"] = stopwatch.ElapsedMilliseconds, ["diagnosticCount"] = result.Findings.Count, ["phaseTimings"] = phaseTimings
            });
            lock (_sync) _lastSuccessfulAnalysisMilliseconds = stopwatch.ElapsedMilliseconds;
            onProgress?.Invoke(new PSScriptAnalyzerProgress(request.RequestId, request.DocumentId, request.Revision, PSScriptAnalyzerProgressState.AnalysisCompleted, 0, 0, null, stopwatch.ElapsedMilliseconds, 0, result.Findings.Count));
            return result;
        }
    }

    private void DisposeWorker(string reason = "Unknown")
    {
        Process? worker; StreamWriter? input; IPSScriptAnalyzerWorker? injected;
        lock (_sync) { worker = _worker; input = _input; injected = _injectedWorker; _worker = null; _input = null; _injectedWorker = null; }
        try { input?.Dispose(); } catch { }
        try { injected?.Dispose(); } catch { }
        try { if (worker is { HasExited: false }) worker.Kill(entireProcessTree: true); } catch { }
        try { worker?.Dispose(); } catch { }
        lock (_sync)
        {
            if (!_disposed && worker is not null) _lastFailureCategory ??= reason;
            _workerProcessId = null;
        }
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static bool CanUseRuleLevelProgress(PSScriptAnalyzerRequest request)
        => string.Equals(request.Profile, "DefaultBundled", StringComparison.OrdinalIgnoreCase);
    private static string? ResolveBundledAnalyzerVersion(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return null;
            var manifest = File.ReadAllText(manifestPath);
            var match = Regex.Match(manifest, @"(?m)^\s*ModuleVersion\s*=\s*['""](?<version>[^'""]+)['""]", RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["version"].Value : null;
        }
        catch
        {
            return null;
        }
    }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(PSScriptAnalyzerService)); }
}

internal interface IPSScriptAnalyzerWorker : IDisposable
{
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);
    Task WriteLineAsync(string line, CancellationToken cancellationToken);
}
