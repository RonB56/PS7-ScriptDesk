using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PowerShellStudio.Application.Diagnostics;
using PowerShellStudio.Application.Utilities;

namespace PowerShellStudio.Shell.Editor
{
    internal sealed class EditorMetadataBuilderStatusMessage
    {
        public EditorMetadataWarmupPhase Phase { get; set; }
        public string Message { get; set; } = string.Empty;
        public string RuntimePath { get; set; } = string.Empty;
        public int ProcessedCount { get; set; }
        public int TotalCount { get; set; }
        public string DetailText { get; set; } = string.Empty;
    }

    internal static class EditorMetadataBuilderProtocol
    {
        public const string StatusPrefix = "PSSTUDIO_METADATA_STATUS:";

        private static readonly JsonSerializerOptions StatusSerializerOptions = new(JsonSerializerDefaults.Web);

        public static string CreateStatusLine(EditorMetadataBuilderStatusMessage message)
        {
            var json = JsonSerializer.Serialize(message, StatusSerializerOptions);
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            return StatusPrefix + payload;
        }

        public static bool TryParseStatusLine(string? line, out EditorMetadataBuilderStatusMessage? message)
        {
            message = null;
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith(StatusPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                var payload = line.Substring(StatusPrefix.Length).Trim();
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                message = JsonSerializer.Deserialize<EditorMetadataBuilderStatusMessage>(json, StatusSerializerOptions);
                return message is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    internal static class EditorMetadataBuilderHost
    {
        public const string BuilderSwitch = "--metadata-builder";
        private const string RuntimeSwitch = "--runtime";
        private const string PerformanceLogSwitch = "--performance-log";
        private const string TracePrefix = "PSSTUDIO_METADATA_TRACE:";

        public static bool IsMetadataBuilderInvocation(string[] args)
        {
            return args.Any(argument => string.Equals(argument, BuilderSwitch, StringComparison.OrdinalIgnoreCase));
        }

        public static int RunFromArguments(string[] args)
        {
            try
            {
                if (!TryGetRuntimePath(args, out var runtimePath))
                {
                    WriteStatus(new EditorMetadataBuilderStatusMessage
                    {
                        Phase = EditorMetadataWarmupPhase.Failed,
                        Message = "Editor metadata builder did not receive a runtime path.",
                        DetailText = "The helper process requires a --runtime argument.",
                    });
                    AppLogger.Error("EditorMetadataBuilder", "Builder invocation failed because no runtime path was supplied.");
                    return 2;
                }

                TryGetPerformanceLogPath(args, out var performanceLogPath);
                return RunAsync(runtimePath!, performanceLogPath, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                WriteStatus(new EditorMetadataBuilderStatusMessage
                {
                    Phase = EditorMetadataWarmupPhase.Failed,
                    Message = "Editor metadata builder crashed.",
                    DetailText = ex.Message,
                });
                AppLogger.Error("EditorMetadataBuilder", "Metadata builder helper crashed.", ex);
                return 1;
            }
        }

        private static async Task<int> RunAsync(string runtimePath, string? performanceLogPath, CancellationToken cancellationToken)
        {
            var normalizedRuntimePath = EditorMetadataCacheStore.NormalizeRuntimePath(runtimePath);
            var totalStopwatch = Stopwatch.StartNew();
            AppLogger.Info("EditorMetadataBuilder", $"Metadata build started for runtime '{normalizedRuntimePath}'.");
            MetadataPerformanceLog.AppendSection(performanceLogPath, "Builder helper started");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata refresh started UTC: {DateTime.UtcNow:O}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"PowerShell path used: {normalizedRuntimePath}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Helper process path: {Environment.ProcessPath ?? string.Empty}");

            try
            {
                WriteStatus(new EditorMetadataBuilderStatusMessage
                {
                    Phase = EditorMetadataWarmupPhase.BuildingCommandCatalog,
                    RuntimePath = normalizedRuntimePath,
                    Message = "Building first-run editor metadata",
                    DetailText = "Scanning the selected PowerShell runtime for commands, aliases, and installed module fingerprints.",
                });

                var snapshotResult = await BuildFullSnapshotAsync(normalizedRuntimePath, performanceLogPath, cancellationToken).ConfigureAwait(false);
                var snapshot = new EditorMetadataCacheSnapshot(snapshotResult.Catalog, snapshotResult.QuickInfos);
                var validation = EditorMetadataSnapshotValidator.Validate(snapshot);
                if (!validation.IsHealthy)
                {
                    MetadataPerformanceLog.AppendLine(performanceLogPath, $"Final merged snapshot health validation: FAILED. {validation.Message} {EditorMetadataSnapshotValidator.Describe(validation.Health)}");
                    WriteStatus(new EditorMetadataBuilderStatusMessage
                    {
                        Phase = EditorMetadataWarmupPhase.Failed,
                        RuntimePath = normalizedRuntimePath,
                        Message = "Editor metadata failed; see log",
                        DetailText = validation.Message,
                    });
                    AppLogger.Error(
                        "EditorMetadataBuilder",
                        $"Metadata build failed health validation for runtime '{normalizedRuntimePath}'. {validation.Message} {EditorMetadataSnapshotValidator.Describe(validation.Health)}");
                    return 3;
                }

                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Final merged snapshot health validation: PASSED. {EditorMetadataSnapshotValidator.Describe(validation.Health)}");

                var saveStopwatch = Stopwatch.StartNew();
                var moduleFingerprintHash = EditorMetadataCacheStore.ComputeModuleFingerprintHash(snapshotResult.ModuleFingerprint);
                EditorMetadataCacheStore.SaveSnapshot(
                    normalizedRuntimePath,
                    snapshot,
                    snapshotResult.RuntimeVersion,
                    snapshotResult.PowerShellEdition,
                    snapshotResult.RuntimeArchitecture,
                    moduleFingerprintHash);
                saveStopwatch.Stop();
                var savedParameterCount = snapshotResult.QuickInfos.Values.Sum(quickInfo => quickInfo.Parameters.Count);
                MetadataPerformanceLog.AppendSection(performanceLogPath, "Cache serialization/write summary");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Serialization/cache write time: {saveStopwatch.ElapsedMilliseconds:N0} ms.");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Cache write command count: {snapshotResult.Catalog.Commands.Count:N0}.");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Cache write quick-info count: {snapshotResult.QuickInfos.Count:N0}.");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Cache write parameter count: {savedParameterCount:N0}.");

                WriteStatus(new EditorMetadataBuilderStatusMessage
                {
                    Phase = EditorMetadataWarmupPhase.Completed,
                    RuntimePath = normalizedRuntimePath,
                    Message = "Editor metadata ready",
                    ProcessedCount = snapshotResult.QuickInfos.Count,
                    TotalCount = snapshotResult.QuickInfos.Count,
                    DetailText = $"Saved a full editor metadata snapshot for {snapshotResult.QuickInfos.Count:N0} commands in {saveStopwatch.ElapsedMilliseconds:N0} ms.",
                });

                totalStopwatch.Stop();
                MetadataPerformanceLog.AppendSection(performanceLogPath, "Builder helper completed");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"PowerShell version: {snapshotResult.RuntimeVersion}");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"PowerShell edition: {snapshotResult.PowerShellEdition}");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Runtime architecture: {snapshotResult.RuntimeArchitecture}");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata refresh finished UTC: {DateTime.UtcNow:O}");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Total elapsed time: {totalStopwatch.ElapsedMilliseconds:N0} ms.");
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Output path for this performance log: {performanceLogPath}");
                AppLogger.Info(
                    "EditorMetadataBuilder",
                    $"Metadata build completed for runtime '{normalizedRuntimePath}'. {EditorMetadataSnapshotValidator.Describe(validation.Health)}, RuntimeVersion={snapshotResult.RuntimeVersion}, Edition={snapshotResult.PowerShellEdition}, Architecture={snapshotResult.RuntimeArchitecture}, Total={totalStopwatch.ElapsedMilliseconds:N0} ms, Save={saveStopwatch.ElapsedMilliseconds:N0} ms.");
                return 0;
            }
            catch (OperationCanceledException)
            {
                totalStopwatch.Stop();
                WriteStatus(new EditorMetadataBuilderStatusMessage
                {
                    Phase = EditorMetadataWarmupPhase.Canceled,
                    RuntimePath = normalizedRuntimePath,
                    Message = "Editor metadata loading was canceled.",
                    DetailText = "A new runtime was selected or PS7 ScriptDesk is shutting down.",
                });
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata build canceled after {totalStopwatch.ElapsedMilliseconds:N0} ms.");
                AppLogger.Warning("EditorMetadataBuilder", $"Metadata build canceled for runtime '{normalizedRuntimePath}' after {totalStopwatch.ElapsedMilliseconds:N0} ms.");
                return 4;
            }
            catch (Exception ex)
            {
                totalStopwatch.Stop();
                WriteStatus(new EditorMetadataBuilderStatusMessage
                {
                    Phase = EditorMetadataWarmupPhase.Failed,
                    RuntimePath = normalizedRuntimePath,
                    Message = "Editor metadata failed; see log",
                    DetailText = ex.Message,
                });
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"Metadata build failed after {totalStopwatch.ElapsedMilliseconds:N0} ms. {ex}");
                AppLogger.Error("EditorMetadataBuilder", $"Metadata build failed for runtime '{normalizedRuntimePath}' after {totalStopwatch.ElapsedMilliseconds:N0} ms.", ex);
                return 1;
            }
        }

        private static async Task<FullSnapshotBuildResult> BuildFullSnapshotAsync(string runtimePath, string? performanceLogPath, CancellationToken cancellationToken)
        {
            var workerArtifacts = CreateWorkerArtifacts();
            var succeeded = false;
            var executeStopwatch = Stopwatch.StartNew();
            try
            {
                var result = await ExecutePowerShellSnapshotBuildAsync(runtimePath, workerArtifacts, performanceLogPath, cancellationToken).ConfigureAwait(false);
                executeStopwatch.Stop();
                AppLogger.Info("EditorMetadataBuilder", $"External PowerShell metadata scan completed for runtime '{runtimePath}' in {executeStopwatch.ElapsedMilliseconds:N0} ms.");
                succeeded = true;
                return result;
            }
            finally
            {
                CleanupWorkerArtifacts(workerArtifacts, succeeded);
            }
        }

        private static async Task<FullSnapshotBuildResult> ExecutePowerShellSnapshotBuildAsync(
            string runtimePath,
            MetadataWorkerArtifacts workerArtifacts,
            string? performanceLogPath,
            CancellationToken cancellationToken)
        {
            var workerStopwatch = Stopwatch.StartNew();
            var script = BuildFullSnapshotCommand(workerArtifacts.OutputPath, performanceLogPath);
            await File.WriteAllTextAsync(workerArtifacts.ScriptPath, script, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = runtimePath,
                    WorkingDirectory = workerArtifacts.WorkingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                },
                EnableRaisingEvents = true,
            };
            process.StartInfo.ArgumentList.Add("-NoLogo");
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(workerArtifacts.ScriptPath);
            var backgroundEnvironmentApplied = PowerShellBackgroundProcessEnvironment.Apply(process.StartInfo, "MetadataBuilder", runtimePath);
            var configuredModulePath = GetEnvironmentValue(process.StartInfo, "PSModulePath");

            MetadataPerformanceLog.AppendSection(performanceLogPath, "External PowerShell worker process");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Worker script path: {workerArtifacts.ScriptPath}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Worker snapshot output path: {workerArtifacts.OutputPath}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Worker working directory: {workerArtifacts.WorkingDirectory}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"PowerShellBackgroundProcessEnvironment applied: {backgroundEnvironmentApplied}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Environment HOME: {GetEnvironmentValue(process.StartInfo, "HOME")}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Environment USERPROFILE: {GetEnvironmentValue(process.StartInfo, "USERPROFILE")}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Environment TEMP: {GetEnvironmentValue(process.StartInfo, "TEMP")}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Environment TMP: {GetEnvironmentValue(process.StartInfo, "TMP")}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Environment PSModuleAnalysisCachePath: {GetEnvironmentValue(process.StartInfo, "PSModuleAnalysisCachePath")}");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"PSModulePath contains Documents\\PowerShell: {PowerShellBackgroundProcessEnvironment.ModulePathContainsUserDocumentsPowerShell(configuredModulePath)}");
            WriteModulePathEntries(performanceLogPath, configuredModulePath);

            AppLogger.Info("EditorMetadataBuilder", $"Starting external PowerShell metadata scan using '{runtimePath}'. ScriptPath='{workerArtifacts.ScriptPath}', OutputPath='{workerArtifacts.OutputPath}'.");
            var processStartStopwatch = Stopwatch.StartNew();
            process.Start();
            processStartStopwatch.Stop();
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"PowerShell process Start() elapsed: {processStartStopwatch.ElapsedMilliseconds:N0} ms. ProcessId={process.Id}.");

            var standardErrorBuffer = new StringBuilder();
            var firstOutputStopwatch = Stopwatch.StartNew();
            var stdoutTask = ForwardStatusOutputAsync(process.StandardOutput, runtimePath, performanceLogPath, firstOutputStopwatch, cancellationToken);
            var stderrTask = DrainStandardErrorAsync(process.StandardError, standardErrorBuffer, cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await stdoutTask.ConfigureAwait(false);
                await stderrTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                throw;
            }

            if (standardErrorBuffer.Length > 0)
            {
                await File.WriteAllTextAsync(workerArtifacts.StandardErrorPath, standardErrorBuffer.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }

            if (process.ExitCode != 0)
            {
                var errorText = standardErrorBuffer.ToString().Trim();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText)
                    ? $"PowerShell metadata worker exited with code {process.ExitCode}."
                    : $"PowerShell metadata worker exited with code {process.ExitCode}: {errorText}");
            }

            if (!File.Exists(workerArtifacts.OutputPath))
            {
                var errorText = standardErrorBuffer.ToString().Trim();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText)
                    ? "PowerShell metadata worker did not produce a metadata snapshot file."
                    : $"PowerShell metadata worker did not produce a metadata snapshot file. {errorText}");
            }

            var parseStopwatch = Stopwatch.StartNew();
            var json = await File.ReadAllTextAsync(workerArtifacts.OutputPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var result = ParseFullSnapshotJson(json);
            parseStopwatch.Stop();
            workerStopwatch.Stop();
            var outputFileLength = new FileInfo(workerArtifacts.OutputPath).Length;
            var totalParameterCount = result.QuickInfos.Values.Sum(quickInfo => quickInfo.Parameters.Count);
            MetadataPerformanceLog.AppendSection(performanceLogPath, "Snapshot parse summary");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Cache JSON read+parse time: {parseStopwatch.ElapsedMilliseconds:N0} ms.");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Snapshot JSON bytes: {outputFileLength:N0}.");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Total command count: {result.Catalog.Commands.Count:N0}.");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Total quick-info count: {result.QuickInfos.Count:N0}.");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"Total parameter count: {totalParameterCount:N0}.");
            MetadataPerformanceLog.AppendLine(performanceLogPath, $"External worker total elapsed: {workerStopwatch.ElapsedMilliseconds:N0} ms.");
            AppLogger.Info("EditorMetadataBuilder", $"Parsed metadata snapshot file in {parseStopwatch.ElapsedMilliseconds:N0} ms. OutputPath='{workerArtifacts.OutputPath}', OutputBytes={outputFileLength:N0}, Catalog={result.Catalog.Commands.Count:N0}, QuickInfo={result.QuickInfos.Count:N0}, Parameters={totalParameterCount:N0}, WorkerTotal={workerStopwatch.ElapsedMilliseconds:N0} ms.");
            return result;
        }

        private static async Task ForwardStatusOutputAsync(StreamReader reader, string runtimePath, string? performanceLogPath, Stopwatch firstOutputStopwatch, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (firstOutputStopwatch.IsRunning)
                {
                    firstOutputStopwatch.Stop();
                    MetadataPerformanceLog.AppendLine(performanceLogPath, $"Time to first worker stdout/status line: {firstOutputStopwatch.ElapsedMilliseconds:N0} ms.");
                }

                if (EditorMetadataBuilderProtocol.TryParseStatusLine(line, out var message) && message is not null)
                {
                    if (string.IsNullOrWhiteSpace(message.RuntimePath))
                    {
                        message.RuntimePath = runtimePath;
                    }

                    if (message.Phase != EditorMetadataWarmupPhase.Completed)
                    {
                        WriteStatus(message);
                    }

                    if (message.Phase == EditorMetadataWarmupPhase.Failed)
                    {
                        AppLogger.Warning("EditorMetadataBuilder", $"Worker reported failure status: {message.DetailText}");
                    }
                    else if (message.Phase == EditorMetadataWarmupPhase.BuildingCommandCatalog ||
                             message.Phase == EditorMetadataWarmupPhase.LoadingCommandMetadata ||
                             message.Phase == EditorMetadataWarmupPhase.Completed)
                    {
                        var progressText = message.TotalCount > 0
                            ? $"{message.ProcessedCount:N0}/{message.TotalCount:N0}"
                            : "n/a";
                        AppLogger.Info(
                            "EditorMetadataBuilder",
                            $"Worker phase={message.Phase} runtime='{runtimePath}' progress={progressText}. Message='{message.Message}' Detail='{message.DetailText}'.");
                    }

                    continue;
                }

                if (line.StartsWith(TracePrefix, StringComparison.Ordinal))
                {
                    AppLogger.Debug("EditorMetadataBuilder", line.Substring(TracePrefix.Length).Trim());
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    AppLogger.Debug("EditorMetadataBuilder", $"Worker stdout: {line.Trim()}");
                }
            }
        }

        private static async Task DrainStandardErrorAsync(StreamReader reader, StringBuilder buffer, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                buffer.AppendLine(line);
                AppLogger.Debug("EditorMetadataBuilder", $"Worker stderr: {line.Trim()}");
            }
        }

        private static FullSnapshotBuildResult ParseFullSnapshotJson(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var okElement) || !okElement.GetBoolean())
            {
                var message = root.TryGetProperty("msg", out var messageElement)
                    ? messageElement.GetString()
                    : "The metadata builder failed to parse the snapshot response.";
                throw new InvalidOperationException(message ?? "The metadata builder failed to parse the snapshot response.");
            }

            var commands = new List<PowerShellCommandReference>();
            if (root.TryGetProperty("commands", out var commandsElement) && commandsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in commandsElement.EnumerateArray())
                {
                    commands.Add(new PowerShellCommandReference(
                        element.TryGetProperty("n", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty,
                        element.TryGetProperty("t", out var kindElement) ? kindElement.GetString() ?? string.Empty : string.Empty,
                        element.TryGetProperty("m", out var moduleElement) ? moduleElement.GetString() ?? string.Empty : string.Empty,
                        element.TryGetProperty("a", out var aliasElement) && aliasElement.GetBoolean(),
                        element.TryGetProperty("r", out var resolvedElement) ? resolvedElement.GetString() ?? string.Empty : string.Empty));
                }
            }

            var quickInfos = new Dictionary<string, PowerShellQuickInfo>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var itemElement in itemsElement.EnumerateArray())
                {
                    var title = itemElement.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    var parameters = new List<PowerShellParameterQuickInfo>();
                    if (itemElement.TryGetProperty("parameters", out var parametersElement) && parametersElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var parameterElement in parametersElement.EnumerateArray())
                        {
                            parameters.Add(new PowerShellParameterQuickInfo(
                                parameterElement.TryGetProperty("n", out var parameterNameElement) ? parameterNameElement.GetString() ?? string.Empty : string.Empty,
                                parameterElement.TryGetProperty("t", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty,
                                parameterElement.TryGetProperty("m", out var mandatoryElement) && mandatoryElement.GetBoolean(),
                                parameterElement.TryGetProperty("p", out var positionElement) && positionElement.ValueKind == JsonValueKind.Number
                                    ? positionElement.GetInt32()
                                    : (int?)null,
                                ReadStringArray(parameterElement, "a"),
                                ReadStringArray(parameterElement, "v"),
                                ReadStringArray(parameterElement, "e"),
                                parameterElement.TryGetProperty("s", out var switchElement) && switchElement.GetBoolean()));
                        }
                    }

                    quickInfos[title] = new PowerShellQuickInfo(
                        title,
                        itemElement.TryGetProperty("kind", out var itemKindElement) ? itemKindElement.GetString() ?? string.Empty : string.Empty,
                        itemElement.TryGetProperty("module", out var itemModuleElement) ? itemModuleElement.GetString() ?? string.Empty : string.Empty,
                        itemElement.TryGetProperty("synopsis", out var synopsisElement) ? synopsisElement.GetString() ?? string.Empty : string.Empty,
                        itemElement.TryGetProperty("syntax", out var syntaxElement) ? syntaxElement.GetString() ?? string.Empty : string.Empty,
                        parameters);
                }
            }

            return new FullSnapshotBuildResult(
                new PowerShellCommandCatalog(commands),
                quickInfos,
                root.TryGetProperty("rv", out var versionElement) ? versionElement.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("ed", out var editionElement) ? editionElement.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("ra", out var architectureElement) ? architectureElement.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("mf", out var fingerprintElement) ? fingerprintElement.GetString() ?? string.Empty : string.Empty);
        }

        private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var values = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    values.Add(item.GetString() ?? string.Empty);
                }
            }

            return values;
        }

        private static void WriteStatus(EditorMetadataBuilderStatusMessage message)
        {
            Console.Out.WriteLine(EditorMetadataBuilderProtocol.CreateStatusLine(message));
            Console.Out.Flush();
        }

        private static bool TryGetRuntimePath(string[] args, out string? runtimePath)
        {
            runtimePath = null;
            for (var index = 0; index < args.Length; index++)
            {
                if (!string.Equals(args[index], RuntimeSwitch, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (index + 1 >= args.Length)
                {
                    return false;
                }

                runtimePath = args[index + 1];
                return !string.IsNullOrWhiteSpace(runtimePath);
            }

            return false;
        }

        private static bool TryGetPerformanceLogPath(string[] args, out string? performanceLogPath)
        {
            performanceLogPath = null;
            for (var index = 0; index < args.Length; index++)
            {
                if (!string.Equals(args[index], PerformanceLogSwitch, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (index + 1 >= args.Length)
                {
                    return false;
                }

                performanceLogPath = args[index + 1];
                return !string.IsNullOrWhiteSpace(performanceLogPath);
            }

            return false;
        }

        private static string GetEnvironmentValue(ProcessStartInfo startInfo, string name)
        {
            try
            {
                return startInfo.Environment.TryGetValue(name, out var value) ? value ?? string.Empty : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void WriteModulePathEntries(string? performanceLogPath, string modulePath)
        {
            MetadataPerformanceLog.AppendLine(performanceLogPath, "PSModulePath entries used by metadata worker:");
            if (string.IsNullOrWhiteSpace(modulePath))
            {
                MetadataPerformanceLog.AppendLine(performanceLogPath, "  (empty)");
                return;
            }

            var entries = modulePath
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            for (var index = 0; index < entries.Count; index++)
            {
                MetadataPerformanceLog.AppendLine(performanceLogPath, $"  [{index}] {entries[index]}");
            }
        }

        private static string BuildFullSnapshotCommand(string outputPath, string? performanceLogPath)
        {
            var escapedOutputPath = outputPath.Replace("'", "''", StringComparison.Ordinal);
            var escapedPerformanceLogPath = (performanceLogPath ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
            return $$"""
& {
$ErrorActionPreference = 'Stop'
$WarningPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$statusPrefix = '{{EditorMetadataBuilderProtocol.StatusPrefix}}'
$outputPath = '{{escapedOutputPath}}'
$performanceLogPath = '{{escapedPerformanceLogPath}}'
$debugTraceEnabled = {{(AppLogger.IsDebugEnabled ? "$true" : "$false")}}
$backgroundHome = [Environment]::GetEnvironmentVariable('HOME')
$backgroundUserProfile = [Environment]::GetEnvironmentVariable('USERPROFILE')
$backgroundTemp = [Environment]::GetEnvironmentVariable('TEMP')
$backgroundTmp = [Environment]::GetEnvironmentVariable('TMP')
$backgroundPsModulePath = [Environment]::GetEnvironmentVariable('PSModulePath')
$backgroundPsModuleAnalysisCachePath = [Environment]::GetEnvironmentVariable('PSModuleAnalysisCachePath')

function Write-Status {
    param(
        [int]$Phase,
        [string]$Message,
        [int]$ProcessedCount = 0,
        [int]$TotalCount = 0,
        [string]$DetailText = ''
    )

    try {
        $status = [PSCustomObject]@{
            phase = $Phase
            message = $Message
            runtimePath = ''
            processedCount = $ProcessedCount
            totalCount = $TotalCount
            detailText = $DetailText
        }

        $json = $status | ConvertTo-Json -Compress -Depth 4
        $payload = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($json))
        [Console]::Out.WriteLine($statusPrefix + $payload)
        [Console]::Out.Flush()
    }
    catch {
        # Best effort only.
    }
}

function Write-Trace {
    param([string]$Message)

    if (-not $debugTraceEnabled -or [string]::IsNullOrWhiteSpace($Message)) {
        return
    }

    try {
        [Console]::Out.WriteLine('{{TracePrefix}}' + $Message)
        [Console]::Out.Flush()
    }
    catch {
        # Best effort only.
    }
}

function Write-PerformanceLog {
    param([string]$Message)

    if ([string]::IsNullOrWhiteSpace($performanceLogPath)) {
        return
    }

    try {
        $timestamp = [DateTimeOffset]::Now.ToString('yyyy-MM-dd HH:mm:ss.fff zzz', [Globalization.CultureInfo]::InvariantCulture)
        [System.IO.File]::AppendAllText($performanceLogPath, ('[{0}] [Worker] {1}{2}' -f $timestamp, $Message, [Environment]::NewLine), [System.Text.UTF8Encoding]::new($false))
    }
    catch {
        # The profiler must not affect metadata loading.
    }
}

function Test-ModulePathContainsUserDocumentsPowerShell {
    param([string]$ModulePath)

    if ([string]::IsNullOrWhiteSpace($ModulePath)) {
        return $false
    }

    $documentsPowerShellPath = [System.IO.Path]::Combine([Environment]::GetFolderPath([System.Environment+SpecialFolder]::MyDocuments), 'PowerShell')
    if ([string]::IsNullOrWhiteSpace($documentsPowerShellPath)) {
        return $false
    }

    foreach ($entry in @($ModulePath -split [System.IO.Path]::PathSeparator)) {
        if ([string]::IsNullOrWhiteSpace($entry)) {
            continue
        }

        try {
            $normalizedEntry = [System.IO.Path]::GetFullPath($entry.Trim()).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        }
        catch {
            $normalizedEntry = $entry.Trim().TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        }

        try {
            $normalizedProtectedPath = [System.IO.Path]::GetFullPath($documentsPowerShellPath).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        }
        catch {
            $normalizedProtectedPath = $documentsPowerShellPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        }

        if ($normalizedEntry.Equals($normalizedProtectedPath, [System.StringComparison]::OrdinalIgnoreCase) -or
            $normalizedEntry.StartsWith($normalizedProtectedPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -or
            $normalizedEntry.StartsWith($normalizedProtectedPath + [System.IO.Path]::AltDirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

$loadedMetadataModules = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

function Ensure-CommandModuleLoaded {
    param([string]$ModuleName)

    if ([string]::IsNullOrWhiteSpace($ModuleName)) {
        return
    }

    if ($loadedMetadataModules.Contains($ModuleName)) {
        return
    }

    $importStopwatch = if ($debugTraceEnabled) { [System.Diagnostics.Stopwatch]::StartNew() } else { $null }
    try {
        if ($null -eq (Get-Module -Name $ModuleName -ErrorAction SilentlyContinue | Select-Object -First 1)) {
            Import-Module -Name $ModuleName -ErrorAction SilentlyContinue | Out-Null
        }
    }
    catch {
        # Best effort only. We still fall back to generic resolution below.
    }
    finally {
        if ($null -ne $importStopwatch) {
            $importStopwatch.Stop()
            if ($importStopwatch.ElapsedMilliseconds -ge 750) {
                Write-Trace ("Slow module import during metadata build: Module='{0}', ElapsedMs={1}" -f $ModuleName, $importStopwatch.ElapsedMilliseconds)
            }
        }
    }

    [void]$loadedMetadataModules.Add($ModuleName)
}

function Build-SyntaxText {
    param(
        [string]$CommandName,
        $ParameterSets
    )

    $lines = New-Object 'System.Collections.Generic.List[string]'
    $resolvedParameterSets = @($ParameterSets | Select-Object -First 6)
    if ($resolvedParameterSets.Count -eq 0) {
        $lines.Add([string]$CommandName)
        return ($lines -join [Environment]::NewLine)
    }

    foreach ($parameterSet in $resolvedParameterSets) {
        $segments = New-Object 'System.Collections.Generic.List[string]'
        $segments.Add([string]$CommandName)

        $orderedParameters = @(
            $parameterSet.Parameters |
                Sort-Object @{ Expression = { if ($_.IsMandatory) { 0 } else { 1 } } }, Position, Name
        )

        foreach ($parameter in ($orderedParameters | Select-Object -First 16)) {
            $segment = '-' + [string]$parameter.Name
            $isSwitch = $false
            if ($null -ne $parameter.ParameterType) {
                $isSwitch = $parameter.ParameterType.FullName -eq 'System.Management.Automation.SwitchParameter'
            }

            if (-not $isSwitch) {
                $typeName = if ($null -ne $parameter.ParameterType -and -not [string]::IsNullOrWhiteSpace([string]$parameter.ParameterType.Name)) {
                    [string]$parameter.ParameterType.Name
                }
                else {
                    'Object'
                }

                $segment += ' <' + $typeName + '>'
            }

            if (-not $parameter.IsMandatory) {
                $segment = '[' + $segment + ']'
            }

            $segments.Add($segment)
        }

        $lines.Add(($segments -join ' '))
    }

    return ($lines -join [Environment]::NewLine)
}

function Get-CommandMetadataView {
    param($Command)

    if ($null -eq $Command) {
        return $null
    }

    $parameterSets = @()
    $parameterDictionary = $null

    try {
        $commandMetadata = [System.Management.Automation.CommandMetadata]::new($Command)
        if ($null -ne $commandMetadata) {
            $parameterSets = @($commandMetadata.ParameterSets)
            $parameterDictionary = $commandMetadata.Parameters
        }
    }
    catch {
        # Fall back to the original command metadata below.
    }

    if (($null -eq $parameterDictionary -or $parameterDictionary.Count -eq 0)) {
        try {
            $parameterDictionary = $Command.Parameters
        }
        catch {
            $parameterDictionary = $null
        }
    }

    if ($parameterSets.Count -eq 0) {
        try {
            $parameterSets = @($Command.ParameterSets)
        }
        catch {
            $parameterSets = @()
        }
    }

    return [PSCustomObject]@{
        Command = $Command
        ParameterSets = $parameterSets
        Parameters = $parameterDictionary
    }
}

function Resolve-CommandMetadataSource {
    param($CatalogCommand)

    if ($null -eq $CatalogCommand) {
        return $null
    }

    $commandName = [string]$CatalogCommand.Name
    if ([string]::IsNullOrWhiteSpace($commandName)) {
        return $CatalogCommand
    }

    $desiredType = [string]$CatalogCommand.CommandType
    $desiredModule = [string]$CatalogCommand.ModuleName

    Ensure-CommandModuleLoaded -ModuleName $desiredModule

    $resolvedCommands = @()
    if (-not [string]::IsNullOrWhiteSpace($desiredModule)) {
        $resolvedCommands = @(
            Get-Command -ListImported -Module $desiredModule -Name $commandName -ErrorAction SilentlyContinue |
                Where-Object { $_.CommandType -ne [System.Management.Automation.CommandTypes]::Alias }
        )
    }

    if ($resolvedCommands.Count -eq 0) {
        $resolvedCommands = @(
            Get-Command -Name $commandName -ErrorAction SilentlyContinue |
                Where-Object { $_.CommandType -ne [System.Management.Automation.CommandTypes]::Alias }
        )
    }

    if ($resolvedCommands.Count -eq 0) {
        return $CatalogCommand
    }

    $bestMatch = @(
        $resolvedCommands |
            Where-Object {
                [string]$_.CommandType -eq $desiredType -and
                [string]$_.ModuleName -eq $desiredModule
            } |
            Select-Object -First 1
    )

    if ($bestMatch.Count -gt 0 -and $null -ne $bestMatch[0]) {
        return $bestMatch[0]
    }

    return $resolvedCommands[0]
}

function Build-ParameterInfo {
    param(
        [string]$ParameterName,
        $ParameterMetadata
    )

    $parameterAttributes = @($ParameterMetadata.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] })
    $mandatory = [bool](@($parameterAttributes | Where-Object { $_.Mandatory } | Select-Object -First 1))
    $positionAttribute = @($parameterAttributes | Where-Object { $_.Position -ge 0 } | Select-Object -First 1)
    $position = if ($positionAttribute.Count -gt 0) { [int]$positionAttribute[0].Position } else { $null }
    $aliases = @($ParameterMetadata.Aliases)

    $validateValues = New-Object 'System.Collections.Generic.List[string]'
    foreach ($validateSetAttribute in @($ParameterMetadata.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] })) {
        foreach ($value in @($validateSetAttribute.ValidValues)) {
            if ($null -ne $value) {
                [void]$validateValues.Add([string]$value)
            }
        }
    }

    $parameterType = $ParameterMetadata.ParameterType
    $typeName = ''
    $isSwitch = $false
    if ($null -ne $parameterType) {
        $typeName = [string]$parameterType.Name
        $isSwitch = $parameterType.FullName -eq 'System.Management.Automation.SwitchParameter'
    }

    $enumValues = if ($null -ne $parameterType -and $parameterType.IsEnum) {
        @([System.Enum]::GetNames($parameterType))
    }
    else {
        @()
    }

    return [PSCustomObject]@{
        n = [string]$ParameterName
        t = $typeName
        m = [bool]$mandatory
        p = $position
        a = @($aliases)
        v = @($validateValues)
        e = @($enumValues)
        s = [bool]$isSwitch
    }
}

function Get-ElapsedMillisecondsFromTimestamp {
    param([long]$StartTimestamp)

    $elapsedTicks = [System.Diagnostics.Stopwatch]::GetTimestamp() - $StartTimestamp
    return [long](($elapsedTicks * 1000.0) / [System.Diagnostics.Stopwatch]::Frequency)
}

function Add-ModuleTiming {
    param(
        [hashtable]$ModuleTimings,
        [string]$ModuleName,
        [long]$ElapsedMs,
        [string]$CommandName
    )

    $key = if ([string]::IsNullOrWhiteSpace($ModuleName)) { '<no module>' } else { $ModuleName }
    if (-not $ModuleTimings.ContainsKey($key)) {
        $ModuleTimings[$key] = [PSCustomObject]@{ Module = $key; Count = 0; TotalMs = [long]0; MaxMs = [long]0; SlowestCommand = '' }
    }

    $entry = $ModuleTimings[$key]
    $entry.Count++
    $entry.TotalMs = [long]$entry.TotalMs + [long]$ElapsedMs
    if ([long]$ElapsedMs -gt [long]$entry.MaxMs) {
        $entry.MaxMs = [long]$ElapsedMs
        $entry.SlowestCommand = $CommandName
    }
}

function Add-SlowestCommand {
    param(
        [System.Collections.ArrayList]$SlowestCommands,
        [string]$CommandName,
        [string]$ModuleName,
        [long]$ElapsedMs,
        [int]$ParameterCount
    )

    [void]$SlowestCommands.Add([PSCustomObject]@{ Command = $CommandName; Module = $ModuleName; ElapsedMs = [long]$ElapsedMs; ParameterCount = [int]$ParameterCount })
}

function Get-CommandIdentityKey {
    param(
        [string]$CommandName,
        [string]$ModuleName
    )

    $safeName = if ([string]::IsNullOrWhiteSpace($CommandName)) { '' } else { $CommandName.Trim() }
    $safeModule = if ([string]::IsNullOrWhiteSpace($ModuleName)) { '' } else { $ModuleName.Trim() }
    return ('{0}||PSSTUDIO||{1}' -f $safeName, $safeModule).ToUpperInvariant()
}

try {
    $buildStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    Write-PerformanceLog "Worker script started."
    Write-PerformanceLog ("PowerShell version: {0}" -f [string]$PSVersionTable.PSVersion)
    Write-PerformanceLog ("PowerShell edition: {0}" -f [string]$PSEdition)
    Write-PerformanceLog ("Runtime architecture: {0}" -f [string]([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture))
    Write-PerformanceLog ("Process id: {0}" -f $PID)
    Write-PerformanceLog ("Current directory: {0}" -f [Environment]::CurrentDirectory)
    Write-PerformanceLog ("HOME: {0}" -f [Environment]::GetEnvironmentVariable('HOME'))
    Write-PerformanceLog ("USERPROFILE: {0}" -f [Environment]::GetEnvironmentVariable('USERPROFILE'))
    Write-PerformanceLog ("TEMP: {0}" -f [Environment]::GetEnvironmentVariable('TEMP'))
    Write-PerformanceLog ("TMP: {0}" -f [Environment]::GetEnvironmentVariable('TMP'))
    Write-PerformanceLog ("PSModuleAnalysisCachePath: {0}" -f [Environment]::GetEnvironmentVariable('PSModuleAnalysisCachePath'))
    Write-PerformanceLog ("PSModulePath contains Documents\PowerShell: {0}" -f (Test-ModulePathContainsUserDocumentsPowerShell -ModulePath ([Environment]::GetEnvironmentVariable('PSModulePath'))))
    $psModulePathEntries = @(([Environment]::GetEnvironmentVariable('PSModulePath') -split [System.IO.Path]::PathSeparator) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    Write-PerformanceLog ("PSModulePath entry count: {0}" -f $psModulePathEntries.Count)
    for ($modulePathIndex = 0; $modulePathIndex -lt $psModulePathEntries.Count; $modulePathIndex++) {
        Write-PerformanceLog ("PSModulePath[{0}]: {1}" -f $modulePathIndex, $psModulePathEntries[$modulePathIndex])
    }
    Write-Status -Phase {{(int)EditorMetadataWarmupPhase.BuildingCommandCatalog}} -Message 'Building first-run editor metadata' -DetailText 'Collecting the PowerShell command catalog.'

    $internalMetadataCommandNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($internalCommandName in @(
        'Write-Status',
        'Write-Trace',
        'Write-PerformanceLog',
        'Ensure-CommandModuleLoaded',
        'Build-SyntaxText',
        'Get-CommandMetadataView',
        'Resolve-CommandMetadataSource',
        'Build-ParameterInfo',
        'Get-ElapsedMillisecondsFromTimestamp',
        'Add-ModuleTiming',
        'Add-SlowestCommand',
        'Get-CommandIdentityKey')) {
        [void]$internalMetadataCommandNames.Add($internalCommandName)
    }

    $catalogStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $rawCommands = @(Get-Command -ErrorAction SilentlyContinue | Sort-Object Name, CommandType | Select-Object -First 12000)
    $allCommands = @(
        $rawCommands |
            Where-Object {
                -not (
                    $_.CommandType -eq [System.Management.Automation.CommandTypes]::Function -and
                    [string]::IsNullOrWhiteSpace([string]$_.ModuleName) -and
                    $internalMetadataCommandNames.Contains([string]$_.Name)
                )
            }
    )
    $catalogStopwatch.Stop()
    $filteredInternalCommandCount = [int]$rawCommands.Count - [int]$allCommands.Count
    Write-PerformanceLog ("Command discovery time: {0} ms. RawCommands={1}; Commands kept={2}; Internal helpers filtered={3}" -f $catalogStopwatch.ElapsedMilliseconds, $rawCommands.Count, $allCommands.Count, $filteredInternalCommandCount)
    $catalog = New-Object System.Collections.ArrayList
    foreach ($command in $allCommands) {
        $isAlias = $command.CommandType -eq [System.Management.Automation.CommandTypes]::Alias
        $resolved = if ($isAlias) { [string]$command.Definition } else { '' }
        [void]$catalog.Add([PSCustomObject]@{
            n = [string]$command.Name
            t = [string]$command.CommandType
            m = [string]$command.ModuleName
            a = [bool]$isAlias
            r = $resolved
        })
    }
    Write-Status -Phase {{(int)EditorMetadataWarmupPhase.BuildingCommandCatalog}} -Message 'Building first-run editor metadata' -ProcessedCount $allCommands.Count -TotalCount $allCommands.Count -DetailText ("Collected {0} commands in {1} ms." -f $allCommands.Count, $catalogStopwatch.ElapsedMilliseconds)

    $moduleFingerprintStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $moduleFingerprint = @(
        Get-Module -ListAvailable -ErrorAction SilentlyContinue |
            Sort-Object Name, Version, Path |
            ForEach-Object { '{0}|{1}|{2}' -f $_.Name, [string]$_.Version, [string]$_.Path }
    ) -join [Environment]::NewLine
    $moduleFingerprintStopwatch.Stop()
    Write-PerformanceLog ("Module discovery/fingerprint time: {0} ms." -f $moduleFingerprintStopwatch.ElapsedMilliseconds)
    Write-Status -Phase {{(int)EditorMetadataWarmupPhase.BuildingCommandCatalog}} -Message 'Building first-run editor metadata' -ProcessedCount $allCommands.Count -TotalCount $allCommands.Count -DetailText ("Computed module fingerprint in {0} ms." -f $moduleFingerprintStopwatch.ElapsedMilliseconds)

    $distinctCommandStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $distinctCommands = New-Object System.Collections.ArrayList
    $seenNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($command in $allCommands) {
        if ($command.CommandType -eq [System.Management.Automation.CommandTypes]::Alias) {
            continue
        }

        $commandName = [string]$command.Name
        if ([string]::IsNullOrWhiteSpace($commandName)) {
            continue
        }

        if ($seenNames.Add($commandName)) {
            [void]$distinctCommands.Add($command)
        }
    }
    $distinctCommandStopwatch.Stop()
    Write-PerformanceLog ("Distinct command preparation time: {0} ms. Distinct commands: {1}" -f $distinctCommandStopwatch.ElapsedMilliseconds, $distinctCommands.Count)

    $totalCount = $distinctCommands.Count
    $publishInterval = if ($totalCount -le 100) { 10 } elseif ($totalCount -le 500) { 25 } else { 50 }
    Write-Status -Phase {{(int)EditorMetadataWarmupPhase.LoadingCommandMetadata}} -Message 'Building first-run editor metadata' -ProcessedCount 0 -TotalCount $totalCount -DetailText ("Prepared {0} distinct commands in {1} ms." -f $totalCount, $distinctCommandStopwatch.ElapsedMilliseconds)

    $distinctCommandRecords = New-Object System.Collections.ArrayList
    foreach ($command in $distinctCommands) {
        $modulePath = ''
        try {
            if ($null -ne $command.Module -and -not [string]::IsNullOrWhiteSpace([string]$command.Module.Path)) {
                $modulePath = [string]$command.Module.Path
            }
        }
        catch {
            $modulePath = ''
        }

        $sourceName = ''
        try {
            $sourceName = [string]$command.Source
        }
        catch {
            $sourceName = ''
        }

        [void]$distinctCommandRecords.Add([PSCustomObject]@{
            Name = [string]$command.Name
            CommandType = [string]$command.CommandType
            ModuleName = [string]$command.ModuleName
            Source = $sourceName
            ModulePath = $modulePath
        })
    }

    $originalCommandLookup = @{}
    foreach ($command in $distinctCommands) {
        $lookupKey = Get-CommandIdentityKey -CommandName ([string]$command.Name) -ModuleName ([string]$command.ModuleName)
        if (-not $originalCommandLookup.ContainsKey($lookupKey)) {
            $originalCommandLookup[$lookupKey] = $command
        }
    }

    $totalCount = $distinctCommandRecords.Count
    $cpuCount = [Environment]::ProcessorCount
    $workerCap = 16
    $metadataWorkerCount = [Math]::Max(1, [Math]::Min($workerCap, $cpuCount))
    $workerOverrideText = [Environment]::GetEnvironmentVariable('PSSTUDIO_METADATA_WORKERS')
    $workerOverride = 0
    if (-not [string]::IsNullOrWhiteSpace($workerOverrideText) -and [int]::TryParse($workerOverrideText, [ref]$workerOverride)) {
        if ($workerOverride -gt 0) {
            $metadataWorkerCount = [Math]::Max(1, [Math]::Min(64, $workerOverride))
        }
    }

    if ($totalCount -gt 0) {
        $metadataWorkerCount = [Math]::Min($metadataWorkerCount, $totalCount)
    }

    $workerOutputRoot = Join-Path ([System.IO.Path]::GetDirectoryName($outputPath)) 'parameter-workers'
    [System.IO.Directory]::CreateDirectory($workerOutputRoot) | Out-Null
    Write-PerformanceLog ("PowerShellBackgroundProcessEnvironment applied in metadata parent: {0}" -f (-not (Test-ModulePathContainsUserDocumentsPowerShell -ModulePath $backgroundPsModulePath)))
    Write-PerformanceLog ("Metadata parent environment template: HOME='{0}'; USERPROFILE='{1}'; TEMP='{2}'; TMP='{3}'; PSModulePathContainsDocumentsPowerShell={4}" -f $backgroundHome, $backgroundUserProfile, $backgroundTemp, $backgroundTmp, (Test-ModulePathContainsUserDocumentsPowerShell -ModulePath $backgroundPsModulePath))
    Write-PerformanceLog ("Parallel parameter metadata extraction selected. WorkerCount={0}; CpuCount={1}; DefaultCap={2}; Override='{3}'; DistinctCommands={4}" -f $metadataWorkerCount, $cpuCount, $workerCap, $workerOverrideText, $totalCount)

    $workerBuckets = @()
    for ($workerIndex = 0; $workerIndex -lt $metadataWorkerCount; $workerIndex++) {
        $workerBuckets += [PSCustomObject]@{
            Index = $workerIndex
            Commands = (New-Object System.Collections.ArrayList)
            Count = 0
        }
    }

    $moduleGroups = @($distinctCommandRecords | Group-Object -Property ModuleName | Sort-Object -Property Count -Descending)
    foreach ($moduleGroup in $moduleGroups) {
        $targetBucket = @($workerBuckets | Sort-Object Count, Index | Select-Object -First 1)[0]
        foreach ($record in @($moduleGroup.Group)) {
            [void]$targetBucket.Commands.Add($record)
            $targetBucket.Count = [int]$targetBucket.Count + 1
        }
    }

    foreach ($bucket in $workerBuckets) {
        Write-PerformanceLog ("Worker bucket {0}: Commands={1}" -f ([int]$bucket.Index + 1), [int]$bucket.Count)
    }

    $parameterWorkerScript = {
        param(
            [int]$WorkerIndex,
            [string]$CommandInputPath,
            [string]$WorkerOutputPath,
            [string]$WorkerLogPath,
            [bool]$WorkerDebugTraceEnabled,
            [string]$BackgroundHome,
            [string]$BackgroundUserProfile,
            [string]$BackgroundTemp,
            [string]$BackgroundTmp,
            [string]$BackgroundPsModulePath,
            [string]$BackgroundPsModuleAnalysisCachePath
        )

        $ErrorActionPreference = 'Stop'
        $WarningPreference = 'SilentlyContinue'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8

        function Test-WorkerModulePathContainsUserDocumentsPowerShell {
            param([string]$ModulePath)

            if ([string]::IsNullOrWhiteSpace($ModulePath)) {
                return $false
            }

            $documentsPowerShellPath = [System.IO.Path]::Combine([Environment]::GetFolderPath([System.Environment+SpecialFolder]::MyDocuments), 'PowerShell')
            if ([string]::IsNullOrWhiteSpace($documentsPowerShellPath)) {
                return $false
            }

            foreach ($entry in @($ModulePath -split [System.IO.Path]::PathSeparator)) {
                if ([string]::IsNullOrWhiteSpace($entry)) {
                    continue
                }

                try {
                    $normalizedEntry = [System.IO.Path]::GetFullPath($entry.Trim()).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
                }
                catch {
                    $normalizedEntry = $entry.Trim().TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
                }

                try {
                    $normalizedProtectedPath = [System.IO.Path]::GetFullPath($documentsPowerShellPath).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
                }
                catch {
                    $normalizedProtectedPath = $documentsPowerShellPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
                }

                if ($normalizedEntry.Equals($normalizedProtectedPath, [System.StringComparison]::OrdinalIgnoreCase) -or
                    $normalizedEntry.StartsWith($normalizedProtectedPath + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -or
                    $normalizedEntry.StartsWith($normalizedProtectedPath + [System.IO.Path]::AltDirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $true
                }
            }

            return $false
        }

        function Write-WorkerPerformanceLog {
            param([string]$Message)

            if ([string]::IsNullOrWhiteSpace($WorkerLogPath)) {
                return
            }

            try {
                $timestamp = [DateTimeOffset]::Now.ToString('yyyy-MM-dd HH:mm:ss.fff zzz', [Globalization.CultureInfo]::InvariantCulture)
                [System.IO.File]::AppendAllText($WorkerLogPath, ('[{0}] [Worker {1}] {2}{3}' -f $timestamp, $WorkerIndex, $Message, [Environment]::NewLine), [System.Text.UTF8Encoding]::new($false))
            }
            catch {
                # Profiling must never break metadata loading.
            }
        }

        $workerTempRoot = Join-Path ([System.IO.Path]::GetDirectoryName($WorkerOutputPath)) ('temp-{0:D2}' -f $WorkerIndex)
        try {
            if (-not [string]::IsNullOrWhiteSpace($BackgroundHome)) {
                [Environment]::SetEnvironmentVariable('HOME', $BackgroundHome, 'Process')
            }

            if (-not [string]::IsNullOrWhiteSpace($BackgroundUserProfile)) {
                [Environment]::SetEnvironmentVariable('USERPROFILE', $BackgroundUserProfile, 'Process')
            }

            if (-not [string]::IsNullOrWhiteSpace($BackgroundTemp)) {
                [Environment]::SetEnvironmentVariable('TEMP', $BackgroundTemp, 'Process')
            }

            if (-not [string]::IsNullOrWhiteSpace($BackgroundTmp)) {
                [Environment]::SetEnvironmentVariable('TMP', $BackgroundTmp, 'Process')
            }

            if (-not [string]::IsNullOrWhiteSpace($BackgroundPsModulePath)) {
                [Environment]::SetEnvironmentVariable('PSModulePath', $BackgroundPsModulePath, 'Process')
            }

            if (-not [string]::IsNullOrWhiteSpace($BackgroundPsModuleAnalysisCachePath)) {
                [Environment]::SetEnvironmentVariable('PSModuleAnalysisCachePath', $BackgroundPsModuleAnalysisCachePath, 'Process')
            }

            [System.IO.Directory]::CreateDirectory($workerTempRoot) | Out-Null
            [Environment]::SetEnvironmentVariable('TEMP', $workerTempRoot, 'Process')
            [Environment]::SetEnvironmentVariable('TMP', $workerTempRoot, 'Process')
            $workerModuleCacheRoot = Join-Path $workerTempRoot 'ModuleAnalysisCache'
            [System.IO.Directory]::CreateDirectory($workerModuleCacheRoot) | Out-Null
            [Environment]::SetEnvironmentVariable('PSModuleAnalysisCachePath', $workerModuleCacheRoot, 'Process')
        }
        catch {
            # Best effort only.
        }

        $loadedMetadataModules = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

        function Ensure-CommandModuleLoaded {
            param(
                [string]$ModuleName,
                [string]$ModulePath
            )

            if ([string]::IsNullOrWhiteSpace($ModuleName)) {
                return
            }

            if ($loadedMetadataModules.Contains($ModuleName)) {
                return
            }

            $importStopwatch = if ($WorkerDebugTraceEnabled) { [System.Diagnostics.Stopwatch]::StartNew() } else { $null }
            try {
                if ($null -eq (Get-Module -Name $ModuleName -ErrorAction SilentlyContinue | Select-Object -First 1)) {
                    Import-Module -Name $ModuleName -ErrorAction SilentlyContinue | Out-Null
                }
            }
            catch {
                # Best effort only. We still fall back to generic resolution below and finally to parent-process fallback.
            }
            finally {
                if ($null -ne $importStopwatch) {
                    $importStopwatch.Stop()
                    if ($importStopwatch.ElapsedMilliseconds -ge 750) {
                        Write-WorkerPerformanceLog ("Slow module import during metadata build: Module='{0}', ElapsedMs={1}" -f $ModuleName, $importStopwatch.ElapsedMilliseconds)
                    }
                }
            }

            [void]$loadedMetadataModules.Add($ModuleName)
        }

        function Build-SyntaxText {
            param(
                [string]$CommandName,
                $ParameterSets
            )

            $lines = New-Object 'System.Collections.Generic.List[string]'
            $resolvedParameterSets = @($ParameterSets | Select-Object -First 6)
            if ($resolvedParameterSets.Count -eq 0) {
                $lines.Add([string]$CommandName)
                return ($lines -join [Environment]::NewLine)
            }

            foreach ($parameterSet in $resolvedParameterSets) {
                $segments = New-Object 'System.Collections.Generic.List[string]'
                $segments.Add([string]$CommandName)

                $orderedParameters = @(
                    $parameterSet.Parameters |
                        Sort-Object @{ Expression = { if ($_.IsMandatory) { 0 } else { 1 } } }, Position, Name
                )

                foreach ($parameter in ($orderedParameters | Select-Object -First 16)) {
                    $segment = '-' + [string]$parameter.Name
                    $isSwitch = $false
                    if ($null -ne $parameter.ParameterType) {
                        $isSwitch = $parameter.ParameterType.FullName -eq 'System.Management.Automation.SwitchParameter'
                    }

                    if (-not $isSwitch) {
                        $typeName = if ($null -ne $parameter.ParameterType -and -not [string]::IsNullOrWhiteSpace([string]$parameter.ParameterType.Name)) {
                            [string]$parameter.ParameterType.Name
                        }
                        else {
                            'Object'
                        }

                        $segment += ' <' + $typeName + '>'
                    }

                    if (-not $parameter.IsMandatory) {
                        $segment = '[' + $segment + ']'
                    }

                    $segments.Add($segment)
                }

                $lines.Add(($segments -join ' '))
            }

            return ($lines -join [Environment]::NewLine)
        }

        function Get-CommandMetadataView {
            param($Command)

            if ($null -eq $Command) {
                return $null
            }

            $parameterSets = @()
            $parameterDictionary = $null

            try {
                $commandMetadata = [System.Management.Automation.CommandMetadata]::new($Command)
                if ($null -ne $commandMetadata) {
                    $parameterSets = @($commandMetadata.ParameterSets)
                    $parameterDictionary = $commandMetadata.Parameters
                }
            }
            catch {
                # Fall back to the original command metadata below.
            }

            if (($null -eq $parameterDictionary -or $parameterDictionary.Count -eq 0)) {
                try {
                    $parameterDictionary = $Command.Parameters
                }
                catch {
                    $parameterDictionary = $null
                }
            }

            if ($parameterSets.Count -eq 0) {
                try {
                    $parameterSets = @($Command.ParameterSets)
                }
                catch {
                    $parameterSets = @()
                }
            }

            return [PSCustomObject]@{
                Command = $Command
                ParameterSets = $parameterSets
                Parameters = $parameterDictionary
            }
        }

        function Resolve-CommandMetadataSource {
            param($CatalogCommand)

            if ($null -eq $CatalogCommand) {
                return $null
            }

            $commandName = [string]$CatalogCommand.n
            if ([string]::IsNullOrWhiteSpace($commandName) -and $CatalogCommand.PSObject.Properties.Match('Name').Count -gt 0) {
                $commandName = [string]$CatalogCommand.Name
            }

            if ([string]::IsNullOrWhiteSpace($commandName)) {
                return $CatalogCommand
            }

            $desiredType = [string]$CatalogCommand.t
            if ([string]::IsNullOrWhiteSpace($desiredType) -and $CatalogCommand.PSObject.Properties.Match('CommandType').Count -gt 0) {
                $desiredType = [string]$CatalogCommand.CommandType
            }

            $desiredModule = [string]$CatalogCommand.m
            if ([string]::IsNullOrWhiteSpace($desiredModule) -and $CatalogCommand.PSObject.Properties.Match('ModuleName').Count -gt 0) {
                $desiredModule = [string]$CatalogCommand.ModuleName
            }

            $desiredSource = ''
            if ($CatalogCommand.PSObject.Properties.Match('s').Count -gt 0) {
                $desiredSource = [string]$CatalogCommand.s
            }
            elseif ($CatalogCommand.PSObject.Properties.Match('Source').Count -gt 0) {
                $desiredSource = [string]$CatalogCommand.Source
            }

            $desiredModulePath = ''
            if ($CatalogCommand.PSObject.Properties.Match('mp').Count -gt 0) {
                $desiredModulePath = [string]$CatalogCommand.mp
            }
            elseif ($CatalogCommand.PSObject.Properties.Match('ModulePath').Count -gt 0) {
                $desiredModulePath = [string]$CatalogCommand.ModulePath
            }

            Ensure-CommandModuleLoaded -ModuleName $desiredModule -ModulePath $desiredModulePath

            $resolvedCommands = @()
            if (-not [string]::IsNullOrWhiteSpace($desiredModule)) {
                $resolvedCommands = @(
                    Get-Command -ListImported -Module $desiredModule -Name $commandName -ErrorAction SilentlyContinue |
                        Where-Object { $_.CommandType -ne [System.Management.Automation.CommandTypes]::Alias }
                )
            }

            if ($resolvedCommands.Count -eq 0) {
                $resolvedCommands = @(
                    Get-Command -Name $commandName -ErrorAction SilentlyContinue |
                        Where-Object { $_.CommandType -ne [System.Management.Automation.CommandTypes]::Alias }
                )
            }

            if ($resolvedCommands.Count -eq 0) {
                return $null
            }

            $bestMatch = @(
                $resolvedCommands |
                    Where-Object {
                        [string]$_.CommandType -eq $desiredType -and
                        [string]$_.ModuleName -eq $desiredModule
                    } |
                    Select-Object -First 1
            )

            if ($bestMatch.Count -gt 0 -and $null -ne $bestMatch[0]) {
                return $bestMatch[0]
            }

            return $resolvedCommands[0]
        }

        function Build-ParameterInfo {
            param(
                [string]$ParameterName,
                $ParameterMetadata
            )

            $parameterAttributes = @($ParameterMetadata.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] })
            $mandatory = [bool](@($parameterAttributes | Where-Object { $_.Mandatory } | Select-Object -First 1))
            $positionAttribute = @($parameterAttributes | Where-Object { $_.Position -ge 0 } | Select-Object -First 1)
            $position = if ($positionAttribute.Count -gt 0) { [int]$positionAttribute[0].Position } else { $null }
            $aliases = @($ParameterMetadata.Aliases)

            $validateValues = New-Object 'System.Collections.Generic.List[string]'
            foreach ($validateSetAttribute in @($ParameterMetadata.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] })) {
                foreach ($value in @($validateSetAttribute.ValidValues)) {
                    if ($null -ne $value) {
                        [void]$validateValues.Add([string]$value)
                    }
                }
            }

            $parameterType = $ParameterMetadata.ParameterType
            $typeName = ''
            $isSwitch = $false
            if ($null -ne $parameterType) {
                $typeName = [string]$parameterType.Name
                $isSwitch = $parameterType.FullName -eq 'System.Management.Automation.SwitchParameter'
            }

            $enumValues = if ($null -ne $parameterType -and $parameterType.IsEnum) {
                @([System.Enum]::GetNames($parameterType))
            }
            else {
                @()
            }

            return [PSCustomObject]@{
                n = [string]$ParameterName
                t = $typeName
                m = [bool]$mandatory
                p = $position
                a = @($aliases)
                v = @($validateValues)
                e = @($enumValues)
                s = [bool]$isSwitch
            }
        }

        function Get-ElapsedMillisecondsFromTimestamp {
            param([long]$StartTimestamp)

            $elapsedTicks = [System.Diagnostics.Stopwatch]::GetTimestamp() - $StartTimestamp
            return [long](($elapsedTicks * 1000.0) / [System.Diagnostics.Stopwatch]::Frequency)
        }

        function Add-ModuleTiming {
            param(
                [hashtable]$ModuleTimings,
                [string]$ModuleName,
                [long]$ElapsedMs,
                [string]$CommandName
            )

            $key = if ([string]::IsNullOrWhiteSpace($ModuleName)) { '<no module>' } else { $ModuleName }
            if (-not $ModuleTimings.ContainsKey($key)) {
                $ModuleTimings[$key] = [PSCustomObject]@{ Module = $key; Count = 0; TotalMs = [long]0; MaxMs = [long]0; SlowestCommand = '' }
            }

            $entry = $ModuleTimings[$key]
            $entry.Count++
            $entry.TotalMs = [long]$entry.TotalMs + [long]$ElapsedMs
            if ([long]$ElapsedMs -gt [long]$entry.MaxMs) {
                $entry.MaxMs = [long]$ElapsedMs
                $entry.SlowestCommand = $CommandName
            }
        }

        function Add-SlowestCommand {
            param(
                [System.Collections.ArrayList]$SlowestCommands,
                [string]$CommandName,
                [string]$ModuleName,
                [long]$ElapsedMs,
                [int]$ParameterCount
            )

            [void]$SlowestCommands.Add([PSCustomObject]@{ Command = $CommandName; Module = $ModuleName; ElapsedMs = [long]$ElapsedMs; ParameterCount = [int]$ParameterCount })
        }

        function Convert-ErrorMessageForLog {
            param([string]$Message)

            if ([string]::IsNullOrWhiteSpace($Message)) {
                return ''
            }

            return (([string]$Message) -replace "[\r\n]+", ' ').Trim()
        }

        function Add-CommandMetadataError {
            param(
                [System.Collections.ArrayList]$CommandErrors,
                [string]$CommandName,
                [string]$ModuleName,
                [string]$Phase,
                [string]$Message
            )

            [void]$CommandErrors.Add([PSCustomObject]@{
                Command = [string]$CommandName
                Module = [string]$ModuleName
                Phase = [string]$Phase
                Message = (Convert-ErrorMessageForLog -Message $Message)
            })
        }

        function Add-ParameterMetadataError {
            param(
                [System.Collections.ArrayList]$ParameterErrors,
                [string]$CommandName,
                [string]$ModuleName,
                [string]$ParameterName,
                [string]$Message
            )

            [void]$ParameterErrors.Add([PSCustomObject]@{
                Command = [string]$CommandName
                Module = [string]$ModuleName
                Parameter = [string]$ParameterName
                Message = (Convert-ErrorMessageForLog -Message $Message)
            })
        }

        function Get-WorkerCommandName {
            param($CommandRecord)

            if ($null -eq $CommandRecord) {
                return ''
            }

            if ($CommandRecord -is [string]) {
                return [string]$CommandRecord
            }

            $commandName = ''
            if ($CommandRecord.PSObject.Properties.Match('Name').Count -gt 0) {
                $commandName = [string]$CommandRecord.Name
            }

            if ([string]::IsNullOrWhiteSpace($commandName) -and $CommandRecord.PSObject.Properties.Match('n').Count -gt 0) {
                $commandName = [string]$CommandRecord.n
            }

            return $commandName
        }

        function Test-WorkerCommandNameLooksFlattened {
            param([string]$CommandName)

            if ([string]::IsNullOrWhiteSpace($CommandName)) {
                return $false
            }

            return [regex]::IsMatch($CommandName, '[\r\n]') -or
                [regex]::IsMatch($CommandName, '\S+\s+\S+') -or
                [regex]::IsMatch($CommandName, '[,;]')
        }

        $workerStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $workerCommands = New-Object System.Collections.ArrayList
        $expectedWorkerCommandCount = -1
        $decodedWorkerCommandCount = 0
        try {
            $workerCommandsJson = [System.IO.File]::ReadAllText($CommandInputPath, [System.Text.Encoding]::UTF8)
            $decodedWorkerInput = ConvertFrom-Json -InputObject $workerCommandsJson
            $rawWorkerCommands = $decodedWorkerInput

            if ($null -ne $decodedWorkerInput -and $decodedWorkerInput.PSObject.Properties.Match('commands').Count -gt 0) {
                if ($decodedWorkerInput.PSObject.Properties.Match('expectedCommandCount').Count -gt 0) {
                    $expectedWorkerCommandCount = [int]$decodedWorkerInput.expectedCommandCount
                }

                $rawWorkerCommands = $decodedWorkerInput.commands
            }

            foreach ($workerCommand in @($rawWorkerCommands)) {
                if ($null -ne $workerCommand) {
                    [void]$workerCommands.Add($workerCommand)
                }
            }

            $decodedWorkerCommandCount = [int]$workerCommands.Count
        }
        catch {
            throw "Worker $WorkerIndex could not read assigned command list '$CommandInputPath'. $($_.Exception.Message)"
        }

        $workerPreviewNames = @($workerCommands | ForEach-Object { Get-WorkerCommandName -CommandRecord $_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 5)
        $workerPreviewText = if ($workerPreviewNames.Count -gt 0) { $workerPreviewNames -join ', ' } else { '<none>' }
        $flattenedWorkerCommandCount = @($workerCommands | Where-Object { Test-WorkerCommandNameLooksFlattened -CommandName (Get-WorkerCommandName -CommandRecord $_) }).Count

        Write-WorkerPerformanceLog ("Started. RuntimePath='{0}'; Worker={1}; InputPath='{2}'; ExpectedCommands={3}; ActualDecodedCommands={4}; Preview='{5}'; FlattenedNames={6}; ProcessId={7}; HOME='{8}'; USERPROFILE='{9}'; TEMP='{10}'; TMP='{11}'; PSModuleAnalysisCachePath='{12}'; PSModulePathContainsDocumentsPowerShell={13}" -f [Environment]::ProcessPath, $WorkerIndex, $CommandInputPath, $expectedWorkerCommandCount, $decodedWorkerCommandCount, $workerPreviewText, $flattenedWorkerCommandCount, $PID, [Environment]::GetEnvironmentVariable('HOME'), [Environment]::GetEnvironmentVariable('USERPROFILE'), [Environment]::GetEnvironmentVariable('TEMP'), [Environment]::GetEnvironmentVariable('TMP'), [Environment]::GetEnvironmentVariable('PSModuleAnalysisCachePath'), (Test-WorkerModulePathContainsUserDocumentsPowerShell -ModulePath ([Environment]::GetEnvironmentVariable('PSModulePath'))))

        if ($expectedWorkerCommandCount -gt 1 -and $decodedWorkerCommandCount -eq 1 -and $workerPreviewNames.Count -gt 0 -and (Test-WorkerCommandNameLooksFlattened -CommandName $workerPreviewNames[0])) {
            $flattenedMessage = "Worker input serialization/deserialization failure. RuntimePath='$([Environment]::ProcessPath)'; Worker=$WorkerIndex; InputPath='$CommandInputPath'; Expected=$expectedWorkerCommandCount; Actual=$decodedWorkerCommandCount; FlattenedCommandName='$($workerPreviewNames[0])'."
            Write-WorkerPerformanceLog $flattenedMessage
            throw $flattenedMessage
        }

        if ($expectedWorkerCommandCount -ge 0 -and $decodedWorkerCommandCount -ne $expectedWorkerCommandCount) {
            $mismatchMessage = "Worker input command count mismatch. RuntimePath='$([Environment]::ProcessPath)'; Worker=$WorkerIndex; InputPath='$CommandInputPath'; Expected=$expectedWorkerCommandCount; Actual=$decodedWorkerCommandCount."
            Write-WorkerPerformanceLog $mismatchMessage
            throw $mismatchMessage
        }

        if ($flattenedWorkerCommandCount -gt 0) {
            $invalidMessage = "Worker rejected decoded command records containing whitespace or list-flattening artifacts. RuntimePath='$([Environment]::ProcessPath)'; Worker=$WorkerIndex; InputPath='$CommandInputPath'; InvalidRecords=$flattenedWorkerCommandCount; Preview='$workerPreviewText'."
            Write-WorkerPerformanceLog $invalidMessage
            throw $invalidMessage
        }

        $items = New-Object System.Collections.ArrayList
        $totalParameterCount = 0
        $commandErrorCount = 0
        $parameterErrorCount = 0
        $timedOutCommandCount = 0
        $moduleTimings = @{}
        $slowestCommands = New-Object System.Collections.ArrayList
        $commandErrors = New-Object System.Collections.ArrayList
        $parameterErrors = New-Object System.Collections.ArrayList

        foreach ($command in $workerCommands) {
            $commandStartTimestamp = [System.Diagnostics.Stopwatch]::GetTimestamp()
            $title = Get-WorkerCommandName -CommandRecord $command
            $kind = if ($command.PSObject.Properties.Match('CommandType').Count -gt 0) { [string]$command.CommandType } else { [string]$command.t }
            $moduleName = if ($command.PSObject.Properties.Match('ModuleName').Count -gt 0) { [string]$command.ModuleName } else { [string]$command.m }
            $commandMetadataSource = $null
            $resolveErrorMessage = ''

            try {
                $commandMetadataSource = Resolve-CommandMetadataSource -CatalogCommand $command
            }
            catch {
                $resolveErrorMessage = $_.Exception.Message
                $commandMetadataSource = $null
            }

            if ($null -eq $commandMetadataSource) {
                $commandErrorCount++
                $resolveMessage = if ([string]::IsNullOrWhiteSpace($resolveErrorMessage)) { 'Get-Command could not resolve this command in the worker process.' } else { $resolveErrorMessage }
                Add-CommandMetadataError -CommandErrors $commandErrors -CommandName $title -ModuleName $moduleName -Phase 'ResolveCommand' -Message $resolveMessage
                $parameterArray = @()
                $syntaxText = $title
            }
            else {
                $syntaxText = $title
                $parameterArray = @()
                $metadataView = $null

                try {
                    $metadataView = Get-CommandMetadataView -Command $commandMetadataSource
                }
                catch {
                    $metadataView = $null
                }

                try {
                    $parameterSets = if ($null -ne $metadataView) { @($metadataView.ParameterSets) } else { @($commandMetadataSource.ParameterSets) }
                    $syntaxText = Build-SyntaxText -CommandName $title -ParameterSets $parameterSets
                }
                catch {
                    $syntaxText = $title
                }

                try {
                    $parameterItems = New-Object System.Collections.ArrayList
                    $parameterDictionary = $null
                    try {
                        $parameterDictionary = if ($null -ne $metadataView) { $metadataView.Parameters } else { $commandMetadataSource.Parameters }
                    }
                    catch {
                        $parameterDictionary = $null
                    }

                    if ($null -ne $parameterDictionary) {
                        foreach ($parameterEntry in @($parameterDictionary.GetEnumerator() | Sort-Object Key | Select-Object -First 250)) {
                            try {
                                [void]$parameterItems.Add((Build-ParameterInfo -ParameterName ([string]$parameterEntry.Key) -ParameterMetadata $parameterEntry.Value))
                            }
                            catch {
                                $parameterErrorCount++
                                Add-ParameterMetadataError -ParameterErrors $parameterErrors -CommandName $title -ModuleName $moduleName -ParameterName ([string]$parameterEntry.Key) -Message $_.Exception.Message
                                # Keep processing the remaining parameters for this command.
                            }
                        }
                    }

                    $parameterArray = @($parameterItems | ForEach-Object { $_ })
                }
                catch {
                    $parameterArray = @()
                    $commandErrorCount++
                    Add-CommandMetadataError -CommandErrors $commandErrors -CommandName $title -ModuleName $moduleName -Phase 'ParameterExtraction' -Message $_.Exception.Message
                }
            }

            [void]$items.Add([PSCustomObject]@{
                title = $title
                kind = $kind
                module = $moduleName
                synopsis = ''
                syntax = $syntaxText
                parameters = $parameterArray
            })

            $commandElapsedMs = Get-ElapsedMillisecondsFromTimestamp -StartTimestamp $commandStartTimestamp
            $parameterCountForCommand = @($parameterArray).Count
            $totalParameterCount += $parameterCountForCommand
            Add-ModuleTiming -ModuleTimings $moduleTimings -ModuleName $moduleName -ElapsedMs $commandElapsedMs -CommandName $title
            Add-SlowestCommand -SlowestCommands $slowestCommands -CommandName $title -ModuleName $moduleName -ElapsedMs $commandElapsedMs -ParameterCount $parameterCountForCommand

            if ($commandElapsedMs -ge 750) {
                Write-WorkerPerformanceLog ("Slow command metadata build: Command='{0}', Module='{1}', ElapsedMs={2}, Parameters={3}" -f $title, $moduleName, $commandElapsedMs, $parameterCountForCommand)
            }
        }

        $workerStopwatch.Stop()
        $workerResponse = @{
            ok = $true
            workerIndex = $WorkerIndex
            expectedCommandCount = $expectedWorkerCommandCount
            decodedCommandCount = $decodedWorkerCommandCount
            commandCount = $items.Count
            totalParameterCount = $totalParameterCount
            commandErrorCount = $commandErrorCount
            parameterErrorCount = $parameterErrorCount
            timedOutCommandCount = $timedOutCommandCount
            elapsedMs = $workerStopwatch.ElapsedMilliseconds
            items = @($items | ForEach-Object { $_ })
            moduleTimings = @($moduleTimings.Values | ForEach-Object { $_ })
            slowestCommands = @($slowestCommands | ForEach-Object { $_ })
            commandErrors = @($commandErrors | ForEach-Object { $_ })
            parameterErrors = @($parameterErrors | ForEach-Object { $_ })
        }

        $workerJson = $workerResponse | ConvertTo-Json -Compress -Depth 8
        [System.IO.File]::WriteAllText($WorkerOutputPath, $workerJson, [System.Text.UTF8Encoding]::new($false))
        Write-WorkerPerformanceLog ("Completed. RuntimePath='{0}'; Worker={1}; CommandsProcessed={2}; Parameters={3}; Errors={4}; ParameterErrors={5}; ElapsedMs={6}; OutputPath='{7}'" -f [Environment]::ProcessPath, $WorkerIndex, $items.Count, $totalParameterCount, $commandErrorCount, $parameterErrorCount, $workerStopwatch.ElapsedMilliseconds, $WorkerOutputPath)

        [PSCustomObject]@{
            WorkerIndex = $WorkerIndex
            OutputPath = $WorkerOutputPath
            ExpectedCommandCount = $expectedWorkerCommandCount
            DecodedCommandCount = $decodedWorkerCommandCount
            CommandCount = $items.Count
            ParameterCount = $totalParameterCount
            ErrorCount = $commandErrorCount
            ParameterErrorCount = $parameterErrorCount
            ElapsedMs = $workerStopwatch.ElapsedMilliseconds
        }
    }

    $items = New-Object System.Collections.ArrayList
    $processedCount = 0
    $totalParameterCount = 0
    $commandErrorCount = 0
    $parameterErrorCount = 0
    $timedOutCommandCount = 0
    $moduleTimings = @{}
    $slowestCommands = New-Object System.Collections.ArrayList
    $commandErrors = New-Object System.Collections.ArrayList
    $parameterErrors = New-Object System.Collections.ArrayList
    $parameterLoopStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    Write-Status -Phase {{(int)EditorMetadataWarmupPhase.LoadingCommandMetadata}} -Message 'Building first-run editor metadata' -ProcessedCount 0 -TotalCount $totalCount -DetailText ("Starting {0} parallel metadata workers for {1} distinct commands." -f $metadataWorkerCount, $totalCount)

    $metadataJobs = New-Object System.Collections.ArrayList
    $workerStartStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    foreach ($bucket in @($workerBuckets | Where-Object { [int]$_.Count -gt 0 })) {
        $workerNumber = [int]$bucket.Index + 1
        $workerInputPath = Join-Path $workerOutputRoot ('worker-{0:D2}-commands.json' -f $workerNumber)
        $workerOutputPath = Join-Path $workerOutputRoot ('worker-{0:D2}-metadata.json' -f $workerNumber)
        $bucketRecords = @($bucket.Commands | ForEach-Object { $_ })
        # Use an explicit wrapper so worker processes can validate array shape across
        # Windows PowerShell 5.1 and PowerShell 7 without depending on ConvertFrom-Json quirks.
        $bucketPayload = [ordered]@{
            schema = 'PSStudio.MetadataWorkerCommands/1'
            runtimePath = [string]$runtimePath
            workerIndex = $workerNumber
            expectedCommandCount = [int]$bucket.Count
            commands = @($bucketRecords)
        }
        $bucketJson = ConvertTo-Json -InputObject $bucketPayload -Compress -Depth 6
        [System.IO.File]::WriteAllText($workerInputPath, $bucketJson, [System.Text.UTF8Encoding]::new($false))
        $bucketPreviewNames = @($bucketRecords | ForEach-Object { [string]$_.Name } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 5)
        $bucketPreviewText = if ($bucketPreviewNames.Count -gt 0) { $bucketPreviewNames -join ', ' } else { '<none>' }

        $job = Start-Job -Name ('PS7ScriptDesk.Metadata.{0:D2}' -f $workerNumber) -ScriptBlock $parameterWorkerScript -ArgumentList $workerNumber, $workerInputPath, $workerOutputPath, $performanceLogPath, $debugTraceEnabled, $backgroundHome, $backgroundUserProfile, $backgroundTemp, $backgroundTmp, $backgroundPsModulePath, $backgroundPsModuleAnalysisCachePath
        [void]$metadataJobs.Add([PSCustomObject]@{
            Job = $job
            WorkerIndex = $workerNumber
            CommandCount = [int]$bucket.Count
            InputPath = $workerInputPath
            OutputPath = $workerOutputPath
        })
        Write-PerformanceLog ("Started metadata worker {0}. RuntimePath='{1}'; ProcessJobId={2}; ExpectedCommands={3}; InputPath='{4}'; OutputPath='{5}'; Preview='{6}'" -f $workerNumber, $runtimePath, $job.Id, [int]$bucket.Count, $workerInputPath, $workerOutputPath, $bucketPreviewText)
    }

    $workerStartStopwatch.Stop()
    Write-PerformanceLog ("Started {0} metadata workers in {1} ms." -f $metadataJobs.Count, $workerStartStopwatch.ElapsedMilliseconds)

    $pendingJobs = @($metadataJobs)
    $completedWorkerOutputs = New-Object System.Collections.ArrayList
    while ($pendingJobs.Count -gt 0) {
        $finishedEntries = @(
            $pendingJobs |
                Where-Object {
                    $_.Job.State -eq 'Completed' -or
                    $_.Job.State -eq 'Failed' -or
                    $_.Job.State -eq 'Stopped'
                }
        )

        if ($finishedEntries.Count -eq 0) {
            Start-Sleep -Milliseconds 500
            continue
        }

        foreach ($entry in $finishedEntries) {
            $job = $entry.Job
            $jobOutput = @()
            try {
                $jobOutput = @(Receive-Job -Job $job -ErrorAction SilentlyContinue)
            }
            catch {
                # Error details are collected from the job state below.
            }

            if ($job.State -ne 'Completed') {
                $jobErrorText = ''
                try {
                    $jobErrorText = (($job.ChildJobs | ForEach-Object { $_.Error }) | Out-String).Trim()
                }
                catch {
                    $jobErrorText = ''
                }

                throw ("Metadata worker {0} failed. State={1}. {2}" -f $entry.WorkerIndex, $job.State, $jobErrorText)
            }

            if (-not [System.IO.File]::Exists($entry.OutputPath)) {
                throw ("Metadata worker {0} completed but did not write its output file: {1}" -f $entry.WorkerIndex, $entry.OutputPath)
            }

            $summary = @($jobOutput | Select-Object -Last 1)[0]
            $processedCount += [int]$entry.CommandCount
            [void]$completedWorkerOutputs.Add($entry.OutputPath)
            $summaryText = if ($null -ne $summary) {
                "Expected=$($summary.ExpectedCommandCount); Decoded=$($summary.DecodedCommandCount); Commands=$($summary.CommandCount); Parameters=$($summary.ParameterCount); Errors=$($summary.ErrorCount); ParameterErrors=$($summary.ParameterErrorCount); ElapsedMs=$($summary.ElapsedMs)"
            }
            else {
                "Expected=$($entry.CommandCount)"
            }

            Write-PerformanceLog ("Metadata worker {0} completed. {1}" -f $entry.WorkerIndex, $summaryText)
            Write-Status -Phase {{(int)EditorMetadataWarmupPhase.LoadingCommandMetadata}} -Message 'Building first-run editor metadata' -ProcessedCount $processedCount -TotalCount $totalCount -DetailText ("Completed metadata worker {0} of {1}. Processed {2} of {3} commands." -f $completedWorkerOutputs.Count, $metadataJobs.Count, $processedCount, $totalCount)

            try {
                Remove-Job -Job $job -Force
            }
            catch {
                # Best effort only.
            }
        }

        $pendingJobs = @($pendingJobs | Where-Object { $finishedEntries.Job.Id -notcontains $_.Job.Id })
    }

    foreach ($workerOutputPath in @($completedWorkerOutputs | Sort-Object)) {
        $partialJson = [System.IO.File]::ReadAllText($workerOutputPath, [System.Text.Encoding]::UTF8)
        $partial = $partialJson | ConvertFrom-Json
        if ($null -eq $partial -or -not [bool]$partial.ok) {
            throw ("Metadata worker output was invalid: {0}" -f $workerOutputPath)
        }

        foreach ($item in @($partial.items)) {
            if ($null -ne $item) {
                [void]$items.Add($item)
            }
        }

        $totalParameterCount += [int]$partial.totalParameterCount
        $commandErrorCount += [int]$partial.commandErrorCount
        if ($partial.PSObject.Properties.Match('parameterErrorCount').Count -gt 0) {
            $parameterErrorCount += [int]$partial.parameterErrorCount
        }
        $timedOutCommandCount += [int]$partial.timedOutCommandCount

        foreach ($commandError in @($partial.commandErrors)) {
            if ($null -ne $commandError) {
                [void]$commandErrors.Add($commandError)
            }
        }

        foreach ($parameterError in @($partial.parameterErrors)) {
            if ($null -ne $parameterError) {
                [void]$parameterErrors.Add($parameterError)
            }
        }

        foreach ($moduleEntry in @($partial.moduleTimings)) {
            if ($null -eq $moduleEntry) {
                continue
            }

            $moduleName = [string]$moduleEntry.Module
            if ([string]::IsNullOrWhiteSpace($moduleName)) {
                $moduleName = '<no module>'
            }

            if (-not $moduleTimings.ContainsKey($moduleName)) {
                $moduleTimings[$moduleName] = [PSCustomObject]@{ Module = $moduleName; Count = 0; TotalMs = [long]0; MaxMs = [long]0; SlowestCommand = '' }
            }

            $mergedModule = $moduleTimings[$moduleName]
            $mergedModule.Count = [int]$mergedModule.Count + [int]$moduleEntry.Count
            $mergedModule.TotalMs = [long]$mergedModule.TotalMs + [long]$moduleEntry.TotalMs
            if ([long]$moduleEntry.MaxMs -gt [long]$mergedModule.MaxMs) {
                $mergedModule.MaxMs = [long]$moduleEntry.MaxMs
                $mergedModule.SlowestCommand = [string]$moduleEntry.SlowestCommand
            }
        }

        foreach ($slowCommand in @($partial.slowestCommands)) {
            if ($null -ne $slowCommand) {
                [void]$slowestCommands.Add($slowCommand)
            }
        }
    }

    $fallbackStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $fallbackResolvedCommandCount = 0
    $fallbackParameterCount = 0
    $fallbackResolvedKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

    if ($commandErrors.Count -gt 0 -and $originalCommandLookup.Count -gt 0) {
        foreach ($commandError in @($commandErrors | Where-Object { [string]$_.Phase -eq 'ResolveCommand' })) {
            $fallbackTitle = [string]$commandError.Command
            $fallbackModuleName = [string]$commandError.Module
            $fallbackKey = Get-CommandIdentityKey -CommandName $fallbackTitle -ModuleName $fallbackModuleName
            if (-not $originalCommandLookup.ContainsKey($fallbackKey)) {
                continue
            }

            $sourceCommand = $originalCommandLookup[$fallbackKey]
            if ($null -eq $sourceCommand) {
                continue
            }

            $fallbackCommandStartTimestamp = [System.Diagnostics.Stopwatch]::GetTimestamp()
            try {
                $fallbackKind = [string]$sourceCommand.CommandType
                if ([string]::IsNullOrWhiteSpace($fallbackTitle)) {
                    $fallbackTitle = [string]$sourceCommand.Name
                }

                if ([string]::IsNullOrWhiteSpace($fallbackModuleName)) {
                    $fallbackModuleName = [string]$sourceCommand.ModuleName
                }

                $metadataView = $null
                try {
                    $metadataView = Get-CommandMetadataView -Command $sourceCommand
                }
                catch {
                    $metadataView = $null
                }

                $syntaxText = $fallbackTitle
                try {
                    $parameterSets = if ($null -ne $metadataView) { @($metadataView.ParameterSets) } else { @($sourceCommand.ParameterSets) }
                    $syntaxText = Build-SyntaxText -CommandName $fallbackTitle -ParameterSets $parameterSets
                }
                catch {
                    $syntaxText = $fallbackTitle
                }

                $parameterItems = New-Object System.Collections.ArrayList
                $parameterDictionary = $null
                try {
                    $parameterDictionary = if ($null -ne $metadataView) { $metadataView.Parameters } else { $sourceCommand.Parameters }
                }
                catch {
                    $parameterDictionary = $null
                }

                if ($null -ne $parameterDictionary) {
                    foreach ($parameterEntry in @($parameterDictionary.GetEnumerator() | Sort-Object Key | Select-Object -First 250)) {
                        try {
                            [void]$parameterItems.Add((Build-ParameterInfo -ParameterName ([string]$parameterEntry.Key) -ParameterMetadata $parameterEntry.Value))
                        }
                        catch {
                            $parameterErrorCount++
                            Add-ParameterMetadataError -ParameterErrors $parameterErrors -CommandName $fallbackTitle -ModuleName $fallbackModuleName -ParameterName ([string]$parameterEntry.Key) -Message $_.Exception.Message
                        }
                    }
                }

                $parameterArray = @($parameterItems | ForEach-Object { $_ })

                for ($itemIndex = [int]$items.Count - 1; $itemIndex -ge 0; $itemIndex--) {
                    $existingItem = $items[$itemIndex]
                    if ([string]$existingItem.title -eq $fallbackTitle -and [string]$existingItem.module -eq $fallbackModuleName) {
                        $existingParameterCount = @($existingItem.parameters).Count
                        $totalParameterCount -= [int]$existingParameterCount
                        $items.RemoveAt($itemIndex)
                    }
                }

                [void]$items.Add([PSCustomObject]@{
                    title = $fallbackTitle
                    kind = $fallbackKind
                    module = $fallbackModuleName
                    synopsis = ''
                    syntax = $syntaxText
                    parameters = $parameterArray
                })

                $fallbackElapsedMs = Get-ElapsedMillisecondsFromTimestamp -StartTimestamp $fallbackCommandStartTimestamp
                $fallbackParameterCountForCommand = @($parameterArray).Count
                $totalParameterCount += $fallbackParameterCountForCommand
                $fallbackParameterCount += $fallbackParameterCountForCommand
                $fallbackResolvedCommandCount++
                [void]$fallbackResolvedKeys.Add($fallbackKey)
                Add-ModuleTiming -ModuleTimings $moduleTimings -ModuleName $fallbackModuleName -ElapsedMs $fallbackElapsedMs -CommandName $fallbackTitle
                Add-SlowestCommand -SlowestCommands $slowestCommands -CommandName $fallbackTitle -ModuleName $fallbackModuleName -ElapsedMs $fallbackElapsedMs -ParameterCount $fallbackParameterCountForCommand

                if ($fallbackElapsedMs -ge 750) {
                    Write-PerformanceLog ("Parent fallback metadata build: Command='{0}', Module='{1}', ElapsedMs={2}, Parameters={3}" -f $fallbackTitle, $fallbackModuleName, $fallbackElapsedMs, $fallbackParameterCountForCommand)
                }
            }
            catch {
                Add-CommandMetadataError -CommandErrors $commandErrors -CommandName $fallbackTitle -ModuleName $fallbackModuleName -Phase 'ParentFallback' -Message $_.Exception.Message
            }
        }

        if ($fallbackResolvedCommandCount -gt 0) {
            $retainedCommandErrors = New-Object System.Collections.ArrayList
            foreach ($commandError in @($commandErrors)) {
                $errorKey = Get-CommandIdentityKey -CommandName ([string]$commandError.Command) -ModuleName ([string]$commandError.Module)
                if ([string]$commandError.Phase -eq 'ResolveCommand' -and $fallbackResolvedKeys.Contains($errorKey)) {
                    continue
                }

                [void]$retainedCommandErrors.Add($commandError)
            }

            $commandErrors = $retainedCommandErrors
            $commandErrorCount = $commandErrors.Count
        }
    }

    $fallbackStopwatch.Stop()
    if ($fallbackResolvedCommandCount -gt 0 -or $commandErrors.Count -gt 0) {
        Write-PerformanceLog ("Parent-process unresolved-command fallback: Resolved={0}; AdditionalParameters={1}; RemainingCommandErrors={2}; ElapsedMs={3}" -f $fallbackResolvedCommandCount, $fallbackParameterCount, $commandErrors.Count, $fallbackStopwatch.ElapsedMilliseconds)
    }

    $parameterLoopStopwatch.Stop()
    $items = @($items | Sort-Object title, kind, module)
    Write-PerformanceLog ("Parameter metadata extraction time: {0} ms." -f $parameterLoopStopwatch.ElapsedMilliseconds)
    Write-PerformanceLog ("Parallel metadata workers completed: {0}" -f $metadataJobs.Count)
    Write-PerformanceLog ("Help/description lookup time: 0 ms. Get-Help/synopsis lookup is not used in this metadata build path.")
    Write-PerformanceLog ("Total command count: {0}" -f $allCommands.Count)
    Write-PerformanceLog ("Distinct command metadata count: {0}" -f $items.Count)
    Write-PerformanceLog ("Total parameter count: {0}" -f $totalParameterCount)
    Write-PerformanceLog ("Commands skipped: 0. Commands timed out: {0}. Commands with recoverable metadata errors: {1}" -f $timedOutCommandCount, $commandErrorCount)
    Write-PerformanceLog ("Recoverable parameter conversion errors: {0}" -f $parameterErrorCount)
    if ($commandErrors.Count -gt 0) {
        Write-PerformanceLog "Recoverable command metadata error details:"
        foreach ($commandError in @($commandErrors | Select-Object -First 100)) {
            Write-PerformanceLog ("  Command='{0}', Module='{1}', Phase='{2}', Message='{3}'" -f $commandError.Command, $commandError.Module, $commandError.Phase, $commandError.Message)
        }
        if ($commandErrors.Count -gt 100) {
            Write-PerformanceLog ("  ... {0} additional command metadata errors omitted from log." -f ([int]$commandErrors.Count - 100))
        }
    }
    if ($parameterErrors.Count -gt 0) {
        Write-PerformanceLog "Recoverable parameter conversion error details:"
        foreach ($parameterError in @($parameterErrors | Select-Object -First 100)) {
            Write-PerformanceLog ("  Command='{0}', Module='{1}', Parameter='{2}', Message='{3}'" -f $parameterError.Command, $parameterError.Module, $parameterError.Parameter, $parameterError.Message)
        }
        if ($parameterErrors.Count -gt 100) {
            Write-PerformanceLog ("  ... {0} additional parameter conversion errors omitted from log." -f ([int]$parameterErrors.Count - 100))
        }
    }
    Write-PerformanceLog "Slowest modules by metadata extraction time:"
    foreach ($moduleEntry in @($moduleTimings.Values | Sort-Object TotalMs -Descending | Select-Object -First 25)) {
        Write-PerformanceLog ("  Module='{0}', Commands={1}, TotalMs={2}, MaxCommandMs={3}, SlowestCommand='{4}'" -f $moduleEntry.Module, $moduleEntry.Count, $moduleEntry.TotalMs, $moduleEntry.MaxMs, $moduleEntry.SlowestCommand)
    }
    Write-PerformanceLog "Slowest commands by metadata extraction time:"
    foreach ($slowCommand in @($slowestCommands | Sort-Object ElapsedMs -Descending | Select-Object -First 25)) {
        Write-PerformanceLog ("  Command='{0}', Module='{1}', ElapsedMs={2}, Parameters={3}" -f $slowCommand.Command, $slowCommand.Module, $slowCommand.ElapsedMs, $slowCommand.ParameterCount)
    }
    $serializationStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $response = @{
        ok = $true
        rv = [string]$PSVersionTable.PSVersion
        ed = [string]$PSEdition
        ra = [string]([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture)
        mf = [string]$moduleFingerprint
        commands = @($catalog | ForEach-Object { $_ })
        items = @($items | ForEach-Object { $_ })
    }
    $responseJson = $response | ConvertTo-Json -Compress -Depth 8
    $serializationStopwatch.Stop()
    Write-PerformanceLog ("JSON serialization time: {0} ms." -f $serializationStopwatch.ElapsedMilliseconds)
    $writeStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    [System.IO.File]::WriteAllText($outputPath, $responseJson, [System.Text.UTF8Encoding]::new($false))
    $writeStopwatch.Stop()
    Write-PerformanceLog ("Snapshot JSON write time: {0} ms. OutputPath='{1}'" -f $writeStopwatch.ElapsedMilliseconds, $outputPath)
    $buildStopwatch.Stop()
    Write-PerformanceLog ("Worker total elapsed time: {0} ms." -f $buildStopwatch.ElapsedMilliseconds)
    Write-Status -Phase {{(int)EditorMetadataWarmupPhase.Completed}} -Message 'Editor metadata ready' -ProcessedCount $items.Count -TotalCount $items.Count -DetailText ("Parameter metadata loop={0} ms; JSON serialization={1} ms; snapshot write={2} ms; worker total={3} ms." -f $parameterLoopStopwatch.ElapsedMilliseconds, $serializationStopwatch.ElapsedMilliseconds, $writeStopwatch.ElapsedMilliseconds, $buildStopwatch.ElapsedMilliseconds)
}
catch {
    Write-PerformanceLog ("Worker failed: {0}" -f $_.Exception.Message)
    Write-Status -Phase {{(int)EditorMetadataWarmupPhase.Failed}} -Message 'Editor metadata failed; see log' -DetailText $_.Exception.Message
    throw
}
}
""";
        }

        private static MetadataWorkerArtifacts CreateWorkerArtifacts()
        {
            var rootDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ApplicationBranding.InternalName,
                "MetadataWorker");
            Directory.CreateDirectory(rootDirectory);

            var workingDirectory = Path.Combine(rootDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);

            return new MetadataWorkerArtifacts(
                workingDirectory,
                Path.Combine(workingDirectory, "worker.ps1"),
                Path.Combine(workingDirectory, "snapshot.json"),
                Path.Combine(workingDirectory, "stderr.log"));
        }

        private static void CleanupWorkerArtifacts(MetadataWorkerArtifacts workerArtifacts, bool succeeded)
        {
            if (workerArtifacts is null)
            {
                return;
            }

            if (!succeeded && AppLogger.IsDebugEnabled)
            {
                AppLogger.Info(
                    "EditorMetadataBuilder",
                    $"Preserving failed metadata worker artifacts at '{workerArtifacts.WorkingDirectory}' because debug logging is enabled.");
                return;
            }

            try
            {
                if (Directory.Exists(workerArtifacts.WorkingDirectory))
                {
                    Directory.Delete(workerArtifacts.WorkingDirectory, recursive: true);
                }
            }
            catch
            {
                // Best effort only.
            }
        }

        private static void TryKillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort only.
            }
        }

        private sealed class FullSnapshotBuildResult
        {
            public FullSnapshotBuildResult(
                PowerShellCommandCatalog catalog,
                IReadOnlyDictionary<string, PowerShellQuickInfo> quickInfos,
                string runtimeVersion,
                string powerShellEdition,
                string runtimeArchitecture,
                string moduleFingerprint)
            {
                Catalog = catalog;
                QuickInfos = quickInfos;
                RuntimeVersion = runtimeVersion;
                PowerShellEdition = powerShellEdition;
                RuntimeArchitecture = runtimeArchitecture;
                ModuleFingerprint = moduleFingerprint;
            }

            public PowerShellCommandCatalog Catalog { get; }
            public IReadOnlyDictionary<string, PowerShellQuickInfo> QuickInfos { get; }
            public string RuntimeVersion { get; }
            public string PowerShellEdition { get; }
            public string RuntimeArchitecture { get; }
            public string ModuleFingerprint { get; }
        }

        private sealed class MetadataWorkerArtifacts
        {
            public MetadataWorkerArtifacts(string workingDirectory, string scriptPath, string outputPath, string standardErrorPath)
            {
                WorkingDirectory = workingDirectory;
                ScriptPath = scriptPath;
                OutputPath = outputPath;
                StandardErrorPath = standardErrorPath;
            }

            public string WorkingDirectory { get; }
            public string ScriptPath { get; }
            public string OutputPath { get; }
            public string StandardErrorPath { get; }
        }
    }
}
