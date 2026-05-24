using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Application.Diagnostics
{
    public static class DeveloperDiagnostics
    {
        private const int DefaultPreviewCharacterLimit = 300;
        private const int DefaultRetentionHours = 72;
        private const int MaximumSessionsToKeep = 10;
        private const int MaximumPropertyValueLength = 2048;
        private const int MaximumStoredHighLevelEvents = 200;
        private const int MaximumReadableExceptionLength = 8192;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        private static readonly AsyncLocal<ScopeState?> CurrentScope = new();
        private static readonly Stopwatch ProcessStopwatch = Stopwatch.StartNew();
        private static readonly Regex AuthorizationRegex = new(@"(?i)\b(authorization\s*[:=]\s*)(bearer\s+)?[^\s,;]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SecretAssignmentRegex = new(@"(?i)\b(password|passwd|pwd|token|api[_-]?key|secret|client[_-]?secret|access[_-]?token|refresh[_-]?token)\b\s*[:=]\s*([^\r\n;]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex PrivateKeyRegex = new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        private static readonly object SyncRoot = new();
        private static readonly string RootDirectory = Path.Combine(ApplicationBranding.LocalApplicationDataRoot, "DeveloperDebugging");
        private static readonly string SessionsDirectory = Path.Combine(RootDirectory, "Sessions");
        private static readonly string PackagesDirectory = Path.Combine(RootDirectory, "Packages");
        private static readonly string LatestSessionPointerPath = Path.Combine(RootDirectory, "latest-session.txt");
        private static readonly ConcurrentQueue<HighLevelEventRecord> HighLevelEvents = new();
        private static volatile SessionRuntimeState? _sessionState;
        private static volatile DeveloperDiagnosticsConfiguration _configuration = DeveloperDiagnosticsConfiguration.Disabled;
        private static Func<DeveloperDiagnosticsStateSnapshot>? _summaryProvider;
        private static Func<bool?>? _uiThreadChecker;
        private static long _sequenceNumber;
        private static long _droppedEventCount;
        private static ExceptionRecord? _lastException;

        public static string DeveloperDebuggingRootDirectory => RootDirectory;

        public static string DeveloperDebuggingSessionsDirectory => SessionsDirectory;

        public static string DeveloperDebuggingPackagesDirectory => PackagesDirectory;

        public static string LatestSessionPointerFilePath => LatestSessionPointerPath;

        public static bool IsEnabled => _sessionState is not null;

        public static string? CurrentSessionDirectory => _sessionState?.SessionDirectoryPath;

        public static string? CurrentSessionId => _sessionState?.SessionId;

        public static void RegisterSummaryProvider(Func<DeveloperDiagnosticsStateSnapshot> provider)
        {
            _summaryProvider = provider;
        }

        public static void RegisterUiThreadChecker(Func<bool?> uiThreadChecker)
        {
            _uiThreadChecker = uiThreadChecker;
        }

        public static void TryPreconfigureFromPersistedSettings()
        {
            try
            {
                var settingsPath = GetSettingsFilePath();
                if (!File.Exists(settingsPath))
                {
                    _configuration = DeveloperDiagnosticsConfiguration.Disabled;
                    return;
                }

                using var stream = File.OpenRead(settingsPath);
                using var document = JsonDocument.Parse(stream);
                var config = DeveloperDiagnosticsConfiguration.FromJson(document.RootElement);
                ApplyConfiguration(config, reason: "Persisted settings preload");
            }
            catch
            {
                _configuration = DeveloperDiagnosticsConfiguration.Disabled;
            }
        }

        public static void ConfigureFromSettings(ApplicationSettings? settings, string reason = "Settings applied")
        {
            var configuration = DeveloperDiagnosticsConfiguration.FromSettings(settings);
            ApplyConfiguration(configuration, reason);
        }

        public static void Enable(ApplicationSettings? settings = null, string reason = "Enabled in app")
        {
            var configuration = DeveloperDiagnosticsConfiguration.FromSettings(settings);
            ApplyConfiguration(configuration.Clone(isEnabled: true), reason);
        }

        public static void Disable(string reason = "Disabled in app")
        {
            ApplyConfiguration(_configuration.Clone(isEnabled: false), reason);
        }

        public static IDisposable BeginScope(string? correlationId = null, string? operationId = null, string? parentOperationId = null, IReadOnlyDictionary<string, object?>? additionalProperties = null)
        {
            var previous = CurrentScope.Value;
            var scopeState = new ScopeState(
                correlationId ?? previous?.CorrelationId ?? Guid.NewGuid().ToString("N"),
                operationId ?? previous?.OperationId,
                parentOperationId ?? previous?.ParentOperationId,
                additionalProperties,
                previous);
            CurrentScope.Value = scopeState;
            return new ScopePopper(previous);
        }

        public static TimedOperationScope BeginTimedOperation(
            string category,
            string eventName,
            string message,
            string eventType = "Performance",
            string level = "Info",
            string? correlationId = null,
            string? operationId = null,
            IReadOnlyDictionary<string, object?>? additionalProperties = null,
            [CallerMemberName] string? methodName = null,
            [CallerFilePath] string? sourceFile = null)
        {
            if (!IsEnabled)
            {
                return TimedOperationScope.Disabled;
            }

            var resolvedOperationId = operationId ?? $"{SanitizeForFileName(eventName)}-{Guid.NewGuid():N}";
            var scope = BeginScope(correlationId: correlationId, operationId: resolvedOperationId, parentOperationId: CurrentScope.Value?.OperationId, additionalProperties: additionalProperties);
            LogOperationStart(category, eventName, message, resolvedOperationId, additionalProperties, methodName, sourceFile);
            return new TimedOperationScope(scope, category, eventName, message, level, methodName, sourceFile);
        }

        public static void LogTrace(string category, string message, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
            => LogCore("Trace", category, methodName, "Trace", "Trace", message, null, additionalProperties, sourceFile);

        public static void LogDebug(string category, string message, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
            => LogCore("Debug", category, methodName, "Debug", "Trace", message, null, additionalProperties, sourceFile);

        public static void LogInfo(string category, string message, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
            => LogCore("Info", category, methodName, "Info", "Info", message, null, additionalProperties, sourceFile);

        public static void LogWarning(string category, string message, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
            => LogCore("Warning", category, methodName, "Warning", "Warning", message, null, additionalProperties, sourceFile);

        public static void LogError(string category, string message, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
            => LogCore("Error", category, methodName, "Error", "Error", message, null, additionalProperties, sourceFile);

        public static void LogException(string category, Exception exception, string message, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
            => LogCore("Error", category, methodName, "Exception", "Exception", message, exception, additionalProperties, sourceFile);

        public static void LogUserAction(string category, string eventName, string message, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
            => LogCore("Info", category, methodName, eventName, "UserAction", message, null, additionalProperties, sourceFile);

        public static void LogMethodEntry(string category, string message, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
            => LogCore("Debug", category, methodName, "MethodEntry", "MethodEntry", message, null, additionalProperties, sourceFile);

        public static void LogMethodExit(string category, string message, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
            => LogCore("Debug", category, methodName, "MethodExit", "MethodExit", message, null, additionalProperties, sourceFile);

        public static void LogStateTransition(string category, string eventName, string fromState, string toState, string message, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
        {
            var properties = MergeProperties(
                additionalProperties,
                new Dictionary<string, object?>
                {
                    ["fromState"] = fromState,
                    ["toState"] = toState
                });
            LogCore("Info", category, methodName, eventName, "StateTransition", message, null, properties, sourceFile);
        }

        public static void LogOperationStart(string category, string eventName, string message, string? operationId = null, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
        {
            IDisposable? scope = null;
            try
            {
                if (operationId is not null)
                {
                    scope = BeginScope(operationId: operationId, parentOperationId: CurrentScope.Value?.OperationId);
                }

                LogCore("Info", category, methodName, eventName, "OperationStart", message, null, additionalProperties, sourceFile);
            }
            finally
            {
                scope?.Dispose();
            }
        }

        public static void LogOperationStop(string category, string eventName, string message, long? elapsedMilliseconds = null, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
        {
            var properties = elapsedMilliseconds.HasValue
                ? MergeProperties(additionalProperties, new Dictionary<string, object?> { ["elapsedMilliseconds"] = elapsedMilliseconds.Value })
                : additionalProperties;
            LogCore("Info", category, methodName, eventName, "OperationStop", message, null, properties, sourceFile);
        }

        public static void LogOperationFailure(string category, string eventName, string message, Exception exception, long? elapsedMilliseconds = null, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
        {
            var properties = elapsedMilliseconds.HasValue
                ? MergeProperties(additionalProperties, new Dictionary<string, object?> { ["elapsedMilliseconds"] = elapsedMilliseconds.Value })
                : additionalProperties;
            LogCore("Error", category, methodName, eventName, "OperationFailure", message, exception, properties, sourceFile);
        }

        public static void LogValue(string category, string eventName, string message, string valueName, object? value, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
            => LogCore("Debug", category, methodName, eventName, "Value", message, null, new Dictionary<string, object?> { [valueName] = value }, sourceFile);

        public static void LogDecision(string category, string eventName, string message, string decision, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
        {
            var properties = MergeProperties(additionalProperties, new Dictionary<string, object?> { ["decision"] = decision });
            LogCore("Debug", category, methodName, eventName, "Decision", message, null, properties, sourceFile);
        }

        public static void LogAsyncBoundary(string category, string eventName, string message, string boundary, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
        {
            var properties = MergeProperties(additionalProperties, new Dictionary<string, object?> { ["boundary"] = boundary });
            LogCore("Debug", category, methodName, eventName, boundary, message, null, properties, sourceFile);
        }

        public static void LogUiThreadDispatch(string category, string eventName, string message, bool dispatcherAccess, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
        {
            var properties = MergeProperties(additionalProperties, new Dictionary<string, object?> { ["dispatcherAccess"] = dispatcherAccess });
            LogCore("Debug", category, methodName, eventName, "UiThreadDispatch", message, null, properties, sourceFile);
        }

        public static void LogEventHandlerEntry(string category, string eventName, string message, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
            => LogCore("Debug", category, methodName, eventName, "EventHandlerEntry", message, null, additionalProperties, sourceFile);

        public static void LogEventHandlerExit(string category, string eventName, string message, IReadOnlyDictionary<string, object?>? additionalProperties = null, [CallerMemberName] string? methodName = null, [CallerFilePath] string? sourceFile = null)
            => LogCore("Debug", category, methodName, eventName, "EventHandlerExit", message, null, additionalProperties, sourceFile);

        public static bool IsVerboseUiEnabled() => _configuration.IsVerboseUiEnabled;

        public static bool IsVerboseDebuggerEnabled() => _configuration.IsVerboseDebuggerEnabled;

        public static bool IsVerboseTerminalEnabled() => _configuration.IsVerboseTerminalEnabled;

        public static bool IsVerboseEditorEnabled() => _configuration.IsVerboseEditorEnabled;

        public static bool IsVerboseExecutionEnabled() => _configuration.IsVerbosePowerShellExecutionEnabled;

        public static int PreviewCharacterLimit => _configuration.PreviewCharacterLimit;

        public static string SanitizePath(string? path) => SanitizePreview(path, Math.Max(PreviewCharacterLimit, 260));

        public static string SanitizePreview(string? text, int maxLength = DefaultPreviewCharacterLimit)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var redacted = RedactSecrets(text);
            var builder = new StringBuilder(Math.Min(redacted.Length, maxLength) + 8);
            foreach (var ch in redacted)
            {
                if (builder.Length >= maxLength)
                {
                    break;
                }

                builder.Append(ch switch
                {
                    '\r' => "\\r",
                    '\n' => "\\n",
                    '\t' => "\\t",
                    _ when char.IsControl(ch) => '?',
                    _ => ch
                });
            }

            return builder.ToString();
        }

        public static Dictionary<string, object?> CreateTextMetadata(string? text, int? previewLimitOverride = null, bool includeHash = true)
        {
            var previewLimit = previewLimitOverride.GetValueOrDefault(PreviewCharacterLimit);
            var normalizedText = text ?? string.Empty;
            var lineCount = string.IsNullOrEmpty(normalizedText)
                ? 0
                : normalizedText.Split(['\n'], StringSplitOptions.None).Length;
            var preview = SanitizePreview(normalizedText, previewLimit);
            var properties = new Dictionary<string, object?>
            {
                ["length"] = normalizedText.Length,
                ["lineCount"] = lineCount,
                ["preview"] = preview,
                ["previewLimit"] = previewLimit,
                ["previewTruncated"] = normalizedText.Length > preview.Length
            };

            if (includeHash && normalizedText.Length > 0)
            {
                properties["sha256"] = ComputeSha256(normalizedText);
            }

            return properties;
        }

        public static string BuildSummaryText()
        {
            var session = _sessionState;
            var snapshot = SafeGetSummarySnapshot();
            var builder = new StringBuilder(4096);
            builder.AppendLine($"App: {ApplicationBranding.PublicName} {TryGetAppVersion()}");
            builder.AppendLine($"Session ID: {session?.SessionId ?? "(not active)"}");
            builder.AppendLine($"Diagnostics Enabled: {IsEnabled}");
            builder.AppendLine($"Developer Debugging Folder: {RootDirectory}");
            builder.AppendLine($"Latest Session Folder: {session?.SessionDirectoryPath ?? ReadLatestSessionPointer() ?? "(none)"}");
            builder.AppendLine($"OS Version: {RuntimeInformation.OSDescription}");
            builder.AppendLine($".NET Version: {Environment.Version}");
            builder.AppendLine($"Process Architecture: {RuntimeInformation.ProcessArchitecture}");
            builder.AppendLine($"Process ID: {Environment.ProcessId}");
            builder.AppendLine($"PowerShell Runtime: {snapshot.SelectedRuntimeDisplayName ?? "(unknown)"}");
            builder.AppendLine($"PowerShell Path: {snapshot.PowerShellExecutablePath ?? "(unknown)"}");
            builder.AppendLine($"Open Tab Count: {snapshot.OpenTabCount?.ToString(CultureInfo.InvariantCulture) ?? "(unknown)"}");
            builder.AppendLine($"Active Document Path: {snapshot.ActiveDocumentPath ?? "(unknown)"}");
            builder.AppendLine($"Active Document Dirty: {snapshot.ActiveDocumentDirtyState?.ToString() ?? "(unknown)"}");
            builder.AppendLine($"Debug Session Active: {snapshot.IsDebugSessionActive?.ToString() ?? "(unknown)"}");
            builder.AppendLine($"Debug Session State: {snapshot.DebugSessionState ?? "(unknown)"}");
            builder.AppendLine($"Terminal State: {snapshot.TerminalState ?? "(unknown)"}");
            builder.AppendLine();
            builder.AppendLine("Last 20 high-level events:");
            foreach (var line in GetRecentHighLevelEvents(20))
            {
                builder.AppendLine(line);
            }

            builder.AppendLine();
            builder.AppendLine("Last exception:");
            if (_lastException is null)
            {
                builder.AppendLine("(none)");
            }
            else
            {
                builder.AppendLine($"{_lastException.ExceptionType}: {_lastException.ExceptionMessage}");
                builder.AppendLine(_lastException.Message);
            }

            return builder.ToString();
        }

        public static void RefreshSummaryFile()
        {
            var session = _sessionState;
            if (session is null)
            {
                return;
            }

            try
            {
                File.WriteAllText(session.SummaryFilePath, BuildSummaryText(), Encoding.UTF8);
            }
            catch
            {
                // Best effort only.
            }
        }

        public static IReadOnlyList<string> GetPackagedSupportFilePaths()
        {
            var result = new List<string>();
            try
            {
                var startupErrorPath = Path.Combine(ApplicationBranding.LocalApplicationDataRoot, "startup-error.log");
                if (File.Exists(startupErrorPath))
                {
                    result.Add(startupErrorPath);
                }
            }
            catch
            {
            }

            return result;
        }

        public static string CreateSupportPackage()
        {
            Directory.CreateDirectory(PackagesDirectory);
            var archivePath = Path.Combine(PackagesDirectory, $"PS7ScriptDesk_SupportLogs_{DateTime.Now:yyyy-MM-dd_HHmmss}_pid{Environment.ProcessId}.zip");
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            var addedEntryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);

            foreach (var sessionPath in GetSessionDirectoriesForPackaging())
            {
                if (!Directory.Exists(sessionPath))
                {
                    continue;
                }

                foreach (var filePath in Directory.EnumerateFiles(sessionPath, "*", SearchOption.TopDirectoryOnly))
                {
                    AddFileToArchive(
                        archive,
                        filePath,
                        Path.Combine("DeveloperDiagnostics", "Sessions", Path.GetFileName(sessionPath), Path.GetFileName(filePath)),
                        addedEntryPaths);
                }
            }

            var appLogDir = Path.Combine(ApplicationBranding.LocalApplicationDataRoot, "Logs");
            AddDirectoryToArchive(archive, appLogDir, Path.Combine("Support", "Logs"), addedEntryPaths);

            foreach (var supportFilePath in GetPackagedSupportFilePaths())
            {
                AddFileToArchive(archive, supportFilePath, Path.Combine("Support", Path.GetFileName(supportFilePath)), addedEntryPaths);
            }

            var summaryEntry = archive.CreateEntry("Support/diagnostics-summary.txt");
            using (var writer = new StreamWriter(summaryEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(BuildSummaryText());
            }

            var readmeEntry = archive.CreateEntry("Support/README-send-this-zip.txt");
            using (var writer = new StreamWriter(readmeEntry.Open(), new UTF8Encoding(false)))
            {
                writer.WriteLine("PS7 ScriptDesk support logs package");
                writer.WriteLine();
                writer.WriteLine("Send this entire ZIP file to support/developer for troubleshooting.");
                writer.WriteLine("It includes normal app logs, metadata startup/load logs, startup error logs, and the most recent developer diagnostics sessions when present.");
                writer.WriteLine();
                writer.WriteLine($"Created local time: {DateTimeOffset.Now:O}");
                writer.WriteLine($"App data root: {ApplicationBranding.LocalApplicationDataRoot}");
                writer.WriteLine($"App logs folder: {appLogDir}");
                writer.WriteLine($"Developer diagnostics folder: {RootDirectory}");
                writer.WriteLine($"Package path: {archivePath}");
            }

            try
            {
                if (File.Exists(LatestSessionPointerPath))
                {
                    AddFileToArchive(archive, LatestSessionPointerPath, "DeveloperDiagnostics/latest-session.txt", addedEntryPaths);
                }
            }
            catch
            {
            }

            LogUserAction(
                "Settings",
                "PackageSupportLogs",
                $"Support logs package created at '{archivePath}'.",
                new Dictionary<string, object?>
                {
                    ["packagePath"] = archivePath,
                    ["appLogDir"] = appLogDir,
                    ["developerDiagnosticsRoot"] = RootDirectory
                });

            RefreshSummaryFile();
            return archivePath;
        }

        public static void Shutdown()
        {
            var session = _sessionState;
            if (session is null)
            {
                return;
            }

            try
            {
                LogInfo("Startup", "Developer diagnostics shutting down.", new Dictionary<string, object?> { ["reason"] = "Process shutdown" });
            }
            catch
            {
            }

            StopSession(session, "Shutdown");
        }

        private static void ApplyConfiguration(DeveloperDiagnosticsConfiguration configuration, string reason)
        {
            lock (SyncRoot)
            {
                var previousSession = _sessionState;
                _configuration = configuration;

                if (!configuration.IsEnabled)
                {
                    if (previousSession is not null)
                    {
                        WriteSettingChangedEvent(reason, configuration, previousSession);
                        StopSession(previousSession, reason);
                    }

                    _sessionState = null;
                    return;
                }

                if (previousSession is null)
                {
                    _sessionState = StartSession(configuration, reason);
                    return;
                }

                previousSession.Configuration = configuration;
                WriteSettingChangedEvent(reason, configuration, previousSession);
                RefreshSummaryFile();
            }
        }

        private static SessionRuntimeState StartSession(DeveloperDiagnosticsConfiguration configuration, string reason)
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(SessionsDirectory);
            Directory.CreateDirectory(PackagesDirectory);

            CleanupOldSessions(configuration.RetentionHours);

            var startedUtc = DateTimeOffset.UtcNow;
            var sessionId = Guid.NewGuid().ToString("N");
            var folderName = $"{startedUtc.ToLocalTime():yyyy-MM-dd_HHmmss}_pid{Environment.ProcessId}";
            var sessionDirectory = Path.Combine(SessionsDirectory, folderName);
            Directory.CreateDirectory(sessionDirectory);

            var state = new SessionRuntimeState(
                sessionId,
                sessionDirectory,
                Path.Combine(sessionDirectory, "session-manifest.json"),
                Path.Combine(sessionDirectory, "developer-diagnostics.ndjson"),
                Path.Combine(sessionDirectory, "developer-diagnostics-readable.log"),
                Path.Combine(sessionDirectory, "errors.ndjson"),
                Path.Combine(sessionDirectory, "diagnostics-summary.txt"),
                CreateFilePathMap(sessionDirectory),
                configuration,
                Channel.CreateUnbounded<DeveloperDiagnosticEnvelope>(new UnboundedChannelOptions
                {
                    AllowSynchronousContinuations = false,
                    SingleReader = true,
                    SingleWriter = false
                }),
                startedUtc);

            _sequenceNumber = 0;
            _droppedEventCount = 0;
            _lastException = null;
            ClearHighLevelEvents();

            WriteManifest(state);
            WriteLatestSessionPointer(sessionDirectory);
            state.WriterTask = Task.Run(() => WriterLoopAsync(state));

            Enqueue(state, CreateEvent(
                "Info",
                "Startup",
                nameof(StartSession),
                "ProcessStart",
                "ProcessStart",
                $"Developer diagnostics session started. Reason={reason}",
                null,
                new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["sessionDirectory"] = sessionDirectory
                },
                sourceFile: null));

            RefreshSummaryFile();
            return state;
        }

        private static void StopSession(SessionRuntimeState state, string reason)
        {
            try
            {
                state.Channel.Writer.TryComplete();
            }
            catch
            {
            }

            try
            {
                state.WriterTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            try
            {
                File.WriteAllText(state.SummaryFilePath, BuildSummaryText(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static void WriteSettingChangedEvent(string reason, DeveloperDiagnosticsConfiguration configuration, SessionRuntimeState state)
        {
            Enqueue(state, CreateEvent(
                "Info",
                "Settings",
                "ConfigureFromSettings",
                "DeveloperDiagnosticsSettingChanged",
                "SettingsSave",
                $"Developer diagnostics settings changed. Enabled={configuration.IsEnabled}. Reason={reason}",
                null,
                configuration.ToDictionary(),
                sourceFile: null));
        }

        private static async Task WriterLoopAsync(SessionRuntimeState state)
        {
            try
            {
                await foreach (var envelope in state.Channel.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    WriteEventToFiles(state, envelope.Event);
                }
            }
            catch
            {
                // Diagnostics must never crash the app.
            }
            finally
            {
                RefreshSummaryFile();
            }
        }

        private static void WriteEventToFiles(SessionRuntimeState state, DeveloperDiagnosticEvent diagnosticEvent)
        {
            try
            {
                var json = JsonSerializer.Serialize(diagnosticEvent, JsonOptions);
                if (state.Configuration.WriteJsonLines)
                {
                    AppendLine(state.MainJsonLinesPath, json);
                    if (ShouldWriteCategoryFile(diagnosticEvent.Category, out var categoryFilePath))
                    {
                        AppendLine(categoryFilePath, json);
                    }

                    if (string.Equals(diagnosticEvent.EventType, "Exception", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(diagnosticEvent.Level, "Error", StringComparison.OrdinalIgnoreCase))
                    {
                        AppendLine(state.ErrorsJsonLinesPath, json);
                    }
                }

                if (state.Configuration.WriteReadableLog)
                {
                    AppendLine(state.ReadableLogPath, FormatReadableLine(diagnosticEvent));
                }

                TrackHighLevelEvent(diagnosticEvent);
                if (string.Equals(diagnosticEvent.EventType, "Exception", StringComparison.OrdinalIgnoreCase))
                {
                    _lastException = new ExceptionRecord(diagnosticEvent.ExceptionType, diagnosticEvent.ExceptionMessage, diagnosticEvent.Message);
                }

                if (diagnosticEvent.SequenceNumber % 25 == 0 || string.Equals(diagnosticEvent.Level, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    RefreshSummaryFile();
                }
            }
            catch
            {
            }
        }

        private static void LogCore(
            string level,
            string category,
            string? methodName,
            string eventName,
            string eventType,
            string message,
            Exception? exception,
            IReadOnlyDictionary<string, object?>? additionalProperties,
            string? sourceFile)
        {
            var session = _sessionState;
            if (session is null)
            {
                return;
            }

            var diagnosticEvent = CreateEvent(level, category, methodName, eventName, eventType, message, exception, additionalProperties, sourceFile);
            Enqueue(session, diagnosticEvent);
        }

        private static DeveloperDiagnosticEvent CreateEvent(
            string level,
            string category,
            string? methodName,
            string eventName,
            string eventType,
            string message,
            Exception? exception,
            IReadOnlyDictionary<string, object?>? additionalProperties,
            string? sourceFile)
        {
            var session = _sessionState;
            var scope = CurrentScope.Value;
            var sequenceNumber = Interlocked.Increment(ref _sequenceNumber);
            var timestamp = DateTimeOffset.UtcNow;
            var snapshot = SafeGetSummarySnapshot();
            var exceptionInfo = exception is null ? null : ExceptionInfo.Create(exception);
            return new DeveloperDiagnosticEvent
            {
                SequenceNumber = sequenceNumber,
                TimestampUtc = timestamp,
                TimestampLocal = timestamp.ToLocalTime(),
                ElapsedMillisecondsSinceAppStart = ProcessStopwatch.ElapsedMilliseconds,
                Level = level,
                Category = category,
                SourceFile = string.IsNullOrWhiteSpace(sourceFile) ? null : Path.GetFileName(sourceFile),
                ClassName = ResolveClassNameFromSourceFile(sourceFile),
                MethodName = methodName,
                EventName = eventName,
                EventType = eventType,
                Message = SanitizePreview(message, MaximumPropertyValueLength),
                ProcessId = Environment.ProcessId,
                ThreadId = Environment.CurrentManagedThreadId,
                ManagedThreadId = Environment.CurrentManagedThreadId,
                IsUiThread = TryResolveUiThread(),
                DispatcherAccess = TryResolveUiThread(),
                TaskId = Task.CurrentId,
                CorrelationId = scope?.CorrelationId,
                OperationId = scope?.OperationId,
                ParentOperationId = scope?.ParentOperationId,
                SessionId = session?.SessionId,
                AppVersion = TryGetAppVersion(),
                ActiveDocumentPath = snapshot.ActiveDocumentPath,
                ActiveDocumentDirtyState = snapshot.ActiveDocumentDirtyState,
                ActiveTabIndex = snapshot.ActiveTabIndex,
                DebugSessionState = snapshot.DebugSessionState,
                TerminalState = snapshot.TerminalState,
                ExceptionType = exceptionInfo?.Type,
                ExceptionMessage = exceptionInfo?.Message,
                ExceptionStackTrace = exceptionInfo?.StackTrace,
                AdditionalProperties = SanitizeProperties(MergeProperties(scope?.AdditionalProperties, additionalProperties))
            };
        }

        private static void Enqueue(SessionRuntimeState state, DeveloperDiagnosticEvent diagnosticEvent)
        {
            try
            {
                if (!state.Channel.Writer.TryWrite(new DeveloperDiagnosticEnvelope(diagnosticEvent)))
                {
                    Interlocked.Increment(ref _droppedEventCount);
                }
            }
            catch
            {
                Interlocked.Increment(ref _droppedEventCount);
            }
        }

        private static void WriteManifest(SessionRuntimeState state)
        {
            try
            {
                var snapshot = SafeGetSummarySnapshot();
                var manifest = new Dictionary<string, object?>
                {
                    ["sessionId"] = state.SessionId,
                    ["appName"] = ApplicationBranding.PublicName,
                    ["appVersion"] = TryGetAppVersion(),
                    ["processId"] = Environment.ProcessId,
                    ["startTimeUtc"] = state.StartedUtc,
                    ["startTimeLocal"] = state.StartedUtc.ToLocalTime(),
                    ["osVersion"] = RuntimeInformation.OSDescription,
                    [".netVersion"] = Environment.Version.ToString(),
                    ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
                    ["currentDirectory"] = SanitizePath(Environment.CurrentDirectory),
                    ["executablePath"] = SanitizePath(Environment.ProcessPath),
                    ["appBaseDirectory"] = SanitizePath(AppContext.BaseDirectory),
                    ["localAppDataRoot"] = ApplicationBranding.LocalApplicationDataRoot,
                    ["developerDiagnosticsRoot"] = RootDirectory,
                    ["settingsSnapshot"] = state.Configuration.ToDictionary(),
                    ["powerShellExecutablePath"] = snapshot.PowerShellExecutablePath,
                    ["selectedPowerShellRuntime"] = snapshot.SelectedRuntimeDisplayName,
                    ["isElevated"] = TryResolveElevation(),
                    ["uiCulture"] = CultureInfo.CurrentUICulture.Name,
                    ["currentCulture"] = CultureInfo.CurrentCulture.Name,
                    ["commandLineArgs"] = Environment.GetCommandLineArgs().Select(arg => SanitizePreview(arg, MaximumPropertyValueLength)).ToArray()
                };

                File.WriteAllText(state.ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static void AppendLine(string path, string line)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? RootDirectory);
            File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
        }

        private static string FormatReadableLine(DeveloperDiagnosticEvent diagnosticEvent)
        {
            var builder = new StringBuilder(1024);
            builder.Append('[').Append(diagnosticEvent.SequenceNumber.ToString(CultureInfo.InvariantCulture)).Append("] ");
            builder.Append('[').Append(diagnosticEvent.TimestampLocal.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)).Append("] ");
            builder.Append('[').Append(diagnosticEvent.Level).Append("] ");
            builder.Append('[').Append(diagnosticEvent.Category).Append("] ");
            builder.Append('[').Append(diagnosticEvent.EventType).Append("] ");
            if (!string.IsNullOrWhiteSpace(diagnosticEvent.ClassName) || !string.IsNullOrWhiteSpace(diagnosticEvent.MethodName))
            {
                builder.Append('[').Append(diagnosticEvent.ClassName).Append('.').Append(diagnosticEvent.MethodName).Append("] ");
            }

            builder.Append(diagnosticEvent.Message);
            if (!string.IsNullOrWhiteSpace(diagnosticEvent.OperationId))
            {
                builder.Append(" | operationId=").Append(diagnosticEvent.OperationId);
            }

            if (!string.IsNullOrWhiteSpace(diagnosticEvent.DebugSessionState))
            {
                builder.Append(" | debugState=").Append(diagnosticEvent.DebugSessionState);
            }

            if (!string.IsNullOrWhiteSpace(diagnosticEvent.TerminalState))
            {
                builder.Append(" | terminalState=").Append(diagnosticEvent.TerminalState);
            }

            if (!string.IsNullOrWhiteSpace(diagnosticEvent.ExceptionType))
            {
                builder.AppendLine();
                builder.Append("  Exception: ").Append(diagnosticEvent.ExceptionType).Append(": ").Append(diagnosticEvent.ExceptionMessage);
                if (!string.IsNullOrWhiteSpace(diagnosticEvent.ExceptionStackTrace))
                {
                    builder.AppendLine();
                    builder.Append(diagnosticEvent.ExceptionStackTrace.Length > MaximumReadableExceptionLength
                        ? diagnosticEvent.ExceptionStackTrace[..MaximumReadableExceptionLength]
                        : diagnosticEvent.ExceptionStackTrace);
                }
            }

            return builder.ToString();
        }

        private static IReadOnlyDictionary<string, object?>? SanitizeProperties(IReadOnlyDictionary<string, object?>? properties)
        {
            if (properties is null || properties.Count == 0)
            {
                return null;
            }

            var sanitized = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var pair in properties)
            {
                sanitized[pair.Key] = SanitizeValue(pair.Value);
            }

            return sanitized;
        }

        private static object? SanitizeValue(object? value)
        {
            if (value is null)
            {
                return null;
            }

            return value switch
            {
                string text => SanitizePreview(text, MaximumPropertyValueLength),
                Exception exception => ExceptionInfo.Create(exception),
                IReadOnlyDictionary<string, object?> dictionary => SanitizeProperties(dictionary),
                IDictionary<string, object?> dictionary => SanitizeProperties(new Dictionary<string, object?>(dictionary)),
                IEnumerable<string> strings => strings.Select(item => SanitizePreview(item, MaximumPropertyValueLength)).ToArray(),
                bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
                _ => SanitizePreview(Convert.ToString(value, CultureInfo.InvariantCulture), MaximumPropertyValueLength)
            };
        }

        private static IReadOnlyDictionary<string, object?>? MergeProperties(IReadOnlyDictionary<string, object?>? first, IReadOnlyDictionary<string, object?>? second)
        {
            if ((first is null || first.Count == 0) && (second is null || second.Count == 0))
            {
                return null;
            }

            var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (first is not null)
            {
                foreach (var pair in first)
                {
                    merged[pair.Key] = pair.Value;
                }
            }

            if (second is not null)
            {
                foreach (var pair in second)
                {
                    merged[pair.Key] = pair.Value;
                }
            }

            return merged;
        }

        private static string RedactSecrets(string text)
        {
            var redacted = AuthorizationRegex.Replace(text, "$1[REDACTED]");
            redacted = SecretAssignmentRegex.Replace(redacted, "$1=[REDACTED]");
            redacted = PrivateKeyRegex.Replace(redacted, "[REDACTED_PRIVATE_KEY]");
            return redacted;
        }

        private static string ComputeSha256(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }

        private static bool? TryResolveUiThread()
        {
            try
            {
                return _uiThreadChecker?.Invoke();
            }
            catch
            {
                return null;
            }
        }

        private static bool? TryResolveElevation()
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    return null;
                }

                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return null;
            }
        }

        private static DeveloperDiagnosticsStateSnapshot SafeGetSummarySnapshot()
        {
            try
            {
                return _summaryProvider?.Invoke() ?? new DeveloperDiagnosticsStateSnapshot();
            }
            catch
            {
                return new DeveloperDiagnosticsStateSnapshot();
            }
        }

        private static string ResolveClassNameFromSourceFile(string? sourceFile)
        {
            if (string.IsNullOrWhiteSpace(sourceFile))
            {
                return string.Empty;
            }

            return Path.GetFileNameWithoutExtension(sourceFile);
        }

        private static string? ReadLatestSessionPointer()
        {
            try
            {
                return File.Exists(LatestSessionPointerPath) ? File.ReadAllText(LatestSessionPointerPath).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteLatestSessionPointer(string sessionDirectory)
        {
            try
            {
                File.WriteAllText(LatestSessionPointerPath, sessionDirectory, Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static void CleanupOldSessions(int retentionHours)
        {
            try
            {
                Directory.CreateDirectory(SessionsDirectory);
                var cutoffUtc = DateTimeOffset.UtcNow.AddHours(-Math.Max(1, retentionHours));
                var directories = Directory.EnumerateDirectories(SessionsDirectory)
                    .Select(path => new DirectoryInfo(path))
                    .OrderByDescending(directory => directory.CreationTimeUtc)
                    .ToList();

                foreach (var directory in directories.Where(directory => directory.CreationTimeUtc < cutoffUtc.UtcDateTime).Skip(0))
                {
                    TryDeleteDirectory(directory.FullName);
                }

                for (var index = MaximumSessionsToKeep; index < directories.Count; index++)
                {
                    TryDeleteDirectory(directories[index].FullName);
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }

        private static void AddDirectoryToArchive(ZipArchive archive, string directoryPath, string archiveRootPath, ISet<string> addedEntryPaths)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                {
                    return;
                }

                foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(directoryPath, filePath);
                    var entryPath = Path.Combine(archiveRootPath, relativePath);
                    AddFileToArchive(archive, filePath, entryPath, addedEntryPaths);
                }
            }
            catch
            {
            }
        }

        private static void AddFileToArchive(ZipArchive archive, string filePath, string entryPath)
        {
            AddFileToArchive(archive, filePath, entryPath, null);
        }

        private static void AddFileToArchive(ZipArchive archive, string filePath, string entryPath, ISet<string>? addedEntryPaths)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || string.IsNullOrWhiteSpace(entryPath))
                {
                    return;
                }

                var normalizedEntryPath = entryPath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
                if (addedEntryPaths is not null && !addedEntryPaths.Add(normalizedEntryPath))
                {
                    return;
                }

                archive.CreateEntryFromFile(filePath, normalizedEntryPath, CompressionLevel.Fastest);
            }
            catch
            {
            }
        }

        private static IEnumerable<string> GetSessionDirectoriesForPackaging()
        {
            try
            {
                if (!Directory.Exists(SessionsDirectory))
                {
                    return Array.Empty<string>();
                }

                return Directory.EnumerateDirectories(SessionsDirectory)
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string[] GetRecentHighLevelEvents(int count)
        {
            return HighLevelEvents.Reverse().Take(count).Reverse()
                .Select(record => $"#{record.SequenceNumber} [{record.Level}] [{record.Category}] [{record.EventType}] {record.Message}")
                .ToArray();
        }

        private static void TrackHighLevelEvent(DeveloperDiagnosticEvent diagnosticEvent)
        {
            if (string.Equals(diagnosticEvent.Level, "Debug", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(diagnosticEvent.Level, "Trace", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            HighLevelEvents.Enqueue(new HighLevelEventRecord(diagnosticEvent.SequenceNumber, diagnosticEvent.Level, diagnosticEvent.Category, diagnosticEvent.EventType, diagnosticEvent.Message));
            while (HighLevelEvents.Count > MaximumStoredHighLevelEvents && HighLevelEvents.TryDequeue(out _))
            {
            }
        }

        private static void ClearHighLevelEvents()
        {
            while (HighLevelEvents.TryDequeue(out _))
            {
            }
        }

        private static bool ShouldWriteCategoryFile(string category, out string path)
        {
            var session = _sessionState;
            if (session is null)
            {
                path = string.Empty;
                return false;
            }

            if (session.CategoryFilePaths.TryGetValue(category, out path!))
            {
                return true;
            }

            path = string.Empty;
            return false;
        }

        private static Dictionary<string, string> CreateFilePathMap(string sessionDirectory)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["UI"] = Path.Combine(sessionDirectory, "ui-events.ndjson"),
                ["Debugger"] = Path.Combine(sessionDirectory, "debugger-events.ndjson"),
                ["Terminal"] = Path.Combine(sessionDirectory, "terminal-events.ndjson"),
                ["Execution"] = Path.Combine(sessionDirectory, "execution-events.ndjson"),
                ["Editor"] = Path.Combine(sessionDirectory, "editor-events.ndjson"),
                ["Settings"] = Path.Combine(sessionDirectory, "settings-events.ndjson"),
                ["Performance"] = Path.Combine(sessionDirectory, "performance-events.ndjson"),
                ["Startup"] = Path.Combine(sessionDirectory, "performance-events.ndjson")
            };
        }

        private static string GetSettingsFilePath()
        {
            return Path.Combine(ApplicationBranding.LocalApplicationDataRoot, "appsettings.json");
        }

        private static string TryGetAppVersion()
        {
            try
            {
                return System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        private static string SanitizeForFileName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return "operation";
            }

            var builder = new StringBuilder(input.Length);
            foreach (var ch in input)
            {
                builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            }

            return builder.ToString();
        }

        private sealed record DeveloperDiagnosticEnvelope(DeveloperDiagnosticEvent Event);

        private sealed class SessionRuntimeState
        {
            public SessionRuntimeState(
                string sessionId,
                string sessionDirectoryPath,
                string manifestPath,
                string mainJsonLinesPath,
                string readableLogPath,
                string errorsJsonLinesPath,
                string summaryFilePath,
                Dictionary<string, string> categoryFilePaths,
                DeveloperDiagnosticsConfiguration configuration,
                Channel<DeveloperDiagnosticEnvelope> channel,
                DateTimeOffset startedUtc)
            {
                SessionId = sessionId;
                SessionDirectoryPath = sessionDirectoryPath;
                ManifestPath = manifestPath;
                MainJsonLinesPath = mainJsonLinesPath;
                ReadableLogPath = readableLogPath;
                ErrorsJsonLinesPath = errorsJsonLinesPath;
                SummaryFilePath = summaryFilePath;
                CategoryFilePaths = categoryFilePaths;
                Configuration = configuration;
                Channel = channel;
                StartedUtc = startedUtc;
            }

            public string SessionId { get; }

            public string SessionDirectoryPath { get; }

            public string ManifestPath { get; }

            public string MainJsonLinesPath { get; }

            public string ReadableLogPath { get; }

            public string ErrorsJsonLinesPath { get; }

            public string SummaryFilePath { get; }

            public Dictionary<string, string> CategoryFilePaths { get; }

            public DeveloperDiagnosticsConfiguration Configuration { get; set; }

            public Channel<DeveloperDiagnosticEnvelope> Channel { get; }

            public DateTimeOffset StartedUtc { get; }

            public Task? WriterTask { get; set; }
        }

        private sealed class ScopeState
        {
            public ScopeState(string correlationId, string? operationId, string? parentOperationId, IReadOnlyDictionary<string, object?>? additionalProperties, ScopeState? previous)
            {
                CorrelationId = correlationId;
                OperationId = operationId ?? previous?.OperationId;
                ParentOperationId = parentOperationId ?? previous?.ParentOperationId;
                AdditionalProperties = additionalProperties;
            }

            public string CorrelationId { get; }

            public string? OperationId { get; }

            public string? ParentOperationId { get; }

            public IReadOnlyDictionary<string, object?>? AdditionalProperties { get; }
        }

        private sealed class ScopePopper : IDisposable
        {
            private readonly ScopeState? _previous;

            public ScopePopper(ScopeState? previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                CurrentScope.Value = _previous;
            }
        }

        public readonly struct TimedOperationScope : IDisposable
        {
            public static readonly TimedOperationScope Disabled = new(null, null, null, null, null, null, null);
            private readonly IDisposable? _scope;
            private readonly string? _category;
            private readonly string? _eventName;
            private readonly string? _message;
            private readonly string? _level;
            private readonly string? _methodName;
            private readonly string? _sourceFile;
            private readonly long _startTimestamp;

            internal TimedOperationScope(IDisposable? scope, string? category, string? eventName, string? message, string? level, string? methodName, string? sourceFile)
            {
                _scope = scope;
                _category = category;
                _eventName = eventName;
                _message = message;
                _level = level;
                _methodName = methodName;
                _sourceFile = sourceFile;
                _startTimestamp = Stopwatch.GetTimestamp();
            }

            public void Dispose()
            {
                if (_scope is null || _category is null || _eventName is null)
                {
                    return;
                }

                var elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
                LogOperationStop(
                    _category,
                    _eventName,
                    $"{_message} completed.",
                    (long)elapsed,
                    methodName: _methodName,
                    sourceFile: _sourceFile);
                _scope.Dispose();
            }
        }

        private sealed record HighLevelEventRecord(long SequenceNumber, string Level, string Category, string EventType, string Message);

        private sealed record ExceptionRecord(string? ExceptionType, string? ExceptionMessage, string Message);

        private sealed class ExceptionInfo
        {
            public string? Type { get; init; }

            public string? Message { get; init; }

            public string? StackTrace { get; init; }

            public ExceptionInfo? InnerException { get; init; }

            public static ExceptionInfo Create(Exception exception, int depth = 0)
            {
                return new ExceptionInfo
                {
                    Type = exception.GetType().FullName,
                    Message = SanitizePreview(exception.Message, MaximumPropertyValueLength),
                    StackTrace = SanitizePreview(exception.StackTrace, MaximumReadableExceptionLength),
                    InnerException = depth >= 3 || exception.InnerException is null ? null : Create(exception.InnerException, depth + 1)
                };
            }
        }

        private sealed class DeveloperDiagnosticsConfiguration
        {
            public static readonly DeveloperDiagnosticsConfiguration Disabled = new();

            public bool IsEnabled { get; init; }

            public bool IsVerboseUiEnabled { get; init; }

            public bool IsVerboseDebuggerEnabled { get; init; }

            public bool IsVerboseTerminalEnabled { get; init; }

            public bool IsVerboseEditorEnabled { get; init; }

            public bool IsVerbosePowerShellExecutionEnabled { get; init; }

            public int PreviewCharacterLimit { get; init; } = DefaultPreviewCharacterLimit;

            public int RetentionHours { get; init; } = DefaultRetentionHours;

            public bool WriteJsonLines { get; init; } = true;

            public bool WriteReadableLog { get; init; } = true;

            public static DeveloperDiagnosticsConfiguration FromSettings(ApplicationSettings? settings)
            {
                return new DeveloperDiagnosticsConfiguration
                {
                    IsEnabled = settings?.IsDeveloperDiagnosticsEnabled == true,
                    IsVerboseUiEnabled = settings?.IsDeveloperDiagnosticsVerboseUiEnabled ?? true,
                    IsVerboseDebuggerEnabled = settings?.IsDeveloperDiagnosticsVerboseDebuggerEnabled ?? true,
                    IsVerboseTerminalEnabled = settings?.IsDeveloperDiagnosticsVerboseTerminalEnabled ?? true,
                    IsVerboseEditorEnabled = settings?.IsDeveloperDiagnosticsVerboseEditorEnabled ?? true,
                    IsVerbosePowerShellExecutionEnabled = settings?.IsDeveloperDiagnosticsVerbosePowerShellExecutionEnabled ?? true,
                    PreviewCharacterLimit = NormalizePositive(settings?.DeveloperDiagnosticsPreviewCharacterLimit, DefaultPreviewCharacterLimit),
                    RetentionHours = NormalizePositive(settings?.DeveloperDiagnosticsRetentionHours, DefaultRetentionHours),
                    WriteJsonLines = settings?.DeveloperDiagnosticsWriteJsonLines ?? true,
                    WriteReadableLog = settings?.DeveloperDiagnosticsWriteReadableLog ?? true
                };
            }

            public static DeveloperDiagnosticsConfiguration FromJson(JsonElement root)
            {
                bool TryGetBoolean(string propertyName, bool defaultValue = false)
                {
                    return root.TryGetProperty(propertyName, out var property) &&
                           property.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? property.GetBoolean()
                        : defaultValue;
                }

                int TryGetInt(string propertyName, int defaultValue)
                {
                    return root.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
                        ? NormalizePositive(value, defaultValue)
                        : defaultValue;
                }

                return new DeveloperDiagnosticsConfiguration
                {
                    IsEnabled = TryGetBoolean(nameof(ApplicationSettings.IsDeveloperDiagnosticsEnabled)),
                    IsVerboseUiEnabled = TryGetBoolean(nameof(ApplicationSettings.IsDeveloperDiagnosticsVerboseUiEnabled), true),
                    IsVerboseDebuggerEnabled = TryGetBoolean(nameof(ApplicationSettings.IsDeveloperDiagnosticsVerboseDebuggerEnabled), true),
                    IsVerboseTerminalEnabled = TryGetBoolean(nameof(ApplicationSettings.IsDeveloperDiagnosticsVerboseTerminalEnabled), true),
                    IsVerboseEditorEnabled = TryGetBoolean(nameof(ApplicationSettings.IsDeveloperDiagnosticsVerboseEditorEnabled), true),
                    IsVerbosePowerShellExecutionEnabled = TryGetBoolean(nameof(ApplicationSettings.IsDeveloperDiagnosticsVerbosePowerShellExecutionEnabled), true),
                    PreviewCharacterLimit = TryGetInt(nameof(ApplicationSettings.DeveloperDiagnosticsPreviewCharacterLimit), DefaultPreviewCharacterLimit),
                    RetentionHours = TryGetInt(nameof(ApplicationSettings.DeveloperDiagnosticsRetentionHours), DefaultRetentionHours),
                    WriteJsonLines = TryGetBoolean(nameof(ApplicationSettings.DeveloperDiagnosticsWriteJsonLines), true),
                    WriteReadableLog = TryGetBoolean(nameof(ApplicationSettings.DeveloperDiagnosticsWriteReadableLog), true)
                };
            }

            public Dictionary<string, object?> ToDictionary()
            {
                return new Dictionary<string, object?>
                {
                    [nameof(ApplicationSettings.IsDeveloperDiagnosticsEnabled)] = IsEnabled,
                    [nameof(ApplicationSettings.IsDeveloperDiagnosticsVerboseUiEnabled)] = IsVerboseUiEnabled,
                    [nameof(ApplicationSettings.IsDeveloperDiagnosticsVerboseDebuggerEnabled)] = IsVerboseDebuggerEnabled,
                    [nameof(ApplicationSettings.IsDeveloperDiagnosticsVerboseTerminalEnabled)] = IsVerboseTerminalEnabled,
                    [nameof(ApplicationSettings.IsDeveloperDiagnosticsVerboseEditorEnabled)] = IsVerboseEditorEnabled,
                    [nameof(ApplicationSettings.IsDeveloperDiagnosticsVerbosePowerShellExecutionEnabled)] = IsVerbosePowerShellExecutionEnabled,
                    [nameof(ApplicationSettings.DeveloperDiagnosticsPreviewCharacterLimit)] = PreviewCharacterLimit,
                    [nameof(ApplicationSettings.DeveloperDiagnosticsRetentionHours)] = RetentionHours,
                    [nameof(ApplicationSettings.DeveloperDiagnosticsWriteJsonLines)] = WriteJsonLines,
                    [nameof(ApplicationSettings.DeveloperDiagnosticsWriteReadableLog)] = WriteReadableLog
                };
            }

            public DeveloperDiagnosticsConfiguration Clone(bool? isEnabled = null)
            {
                return new DeveloperDiagnosticsConfiguration
                {
                    IsEnabled = isEnabled ?? IsEnabled,
                    IsVerboseUiEnabled = IsVerboseUiEnabled,
                    IsVerboseDebuggerEnabled = IsVerboseDebuggerEnabled,
                    IsVerboseTerminalEnabled = IsVerboseTerminalEnabled,
                    IsVerboseEditorEnabled = IsVerboseEditorEnabled,
                    IsVerbosePowerShellExecutionEnabled = IsVerbosePowerShellExecutionEnabled,
                    PreviewCharacterLimit = PreviewCharacterLimit,
                    RetentionHours = RetentionHours,
                    WriteJsonLines = WriteJsonLines,
                    WriteReadableLog = WriteReadableLog
                };
            }
        }

        private static int NormalizePositive(int? value, int defaultValue)
        {
            if (!value.HasValue || value.Value <= 0)
            {
                return defaultValue;
            }

            return value.Value;
        }
    }

    public sealed class DeveloperDiagnosticsStateSnapshot
    {
        public string? ActiveDocumentPath { get; init; }

        public bool? ActiveDocumentDirtyState { get; init; }

        public int? ActiveTabIndex { get; init; }

        public int? OpenTabCount { get; init; }

        public bool? IsDebugSessionActive { get; init; }

        public string? DebugSessionState { get; init; }

        public string? TerminalState { get; init; }

        public string? PowerShellExecutablePath { get; init; }

        public string? SelectedRuntimeDisplayName { get; init; }
    }

    public sealed class DeveloperDiagnosticEvent
    {
        public long SequenceNumber { get; init; }

        public DateTimeOffset TimestampUtc { get; init; }

        public DateTimeOffset TimestampLocal { get; init; }

        public long ElapsedMillisecondsSinceAppStart { get; init; }

        public string Level { get; init; } = string.Empty;

        public string Category { get; init; } = string.Empty;

        public string? SourceFile { get; init; }

        public string? ClassName { get; init; }

        public string? MethodName { get; init; }

        public string EventName { get; init; } = string.Empty;

        public string EventType { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        public int ProcessId { get; init; }

        public int ThreadId { get; init; }

        public int ManagedThreadId { get; init; }

        public bool? IsUiThread { get; init; }

        public bool? DispatcherAccess { get; init; }

        public int? TaskId { get; init; }

        public string? CorrelationId { get; init; }

        public string? OperationId { get; init; }

        public string? ParentOperationId { get; init; }

        public string? SessionId { get; init; }

        public string? AppVersion { get; init; }

        public string? ActiveDocumentPath { get; init; }

        public bool? ActiveDocumentDirtyState { get; init; }

        public int? ActiveTabIndex { get; init; }

        public string? DebugSessionState { get; init; }

        public string? TerminalState { get; init; }

        public string? ExceptionType { get; init; }

        public string? ExceptionMessage { get; init; }

        public string? ExceptionStackTrace { get; init; }

        public IReadOnlyDictionary<string, object?>? AdditionalProperties { get; init; }
    }
}
