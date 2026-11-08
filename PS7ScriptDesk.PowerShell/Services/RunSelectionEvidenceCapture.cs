using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>
/// Best-effort, opt-in capture for Run Selection investigations. It writes outside
/// ordinary logs and never participates in execution or output decisions.
/// </summary>
internal static class RunSelectionEvidenceCapture
{
    public const string EnableEnvironmentVariable = "PS7SCRIPTDESK_CAPTURE_RUN_SELECTION_EVIDENCE";
    public const int MaximumRetainedAttempts = 10;
    public const int MaximumScriptBytes = 1024 * 1024;
    public const int MaximumWrapperBytes = 256 * 1024;

    private static readonly SemaphoreSlim CaptureSlot = new(1, 1);
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public static bool IsEnabled => IsCaptureEnabled(
        DeveloperDiagnostics.IsEnabled,
        Environment.GetEnvironmentVariable(EnableEnvironmentVariable));

    internal static bool IsCaptureEnabled(bool developerDiagnosticsEnabled, string? settingValue)
        => developerDiagnosticsEnabled && string.Equals(settingValue, "1", StringComparison.Ordinal);

    internal static bool IsWithinCaptureLimits(int originalBytes, int generatedBytes, int wrapperBytes)
        => originalBytes >= 0 && generatedBytes >= 0 && wrapperBytes >= 0 &&
           originalBytes <= MaximumScriptBytes && generatedBytes <= MaximumScriptBytes &&
           wrapperBytes <= MaximumWrapperBytes;

    public static void TryQueue(
        Guid requestId,
        int sessionGeneration,
        string documentDisplayName,
        string originalSelection,
        string generatedScript,
        string generatedScriptPath,
        EditorExecutionSourceInfo? sourceInfo,
        string? executionWrapper)
    {
        if (!IsEnabled) return;

        if (!CaptureSlot.Wait(0))
        {
            LogSkipped(requestId, "A previous evidence package is still being written.");
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Persist(requestId, sessionGeneration, documentDisplayName, originalSelection,
                    generatedScript, generatedScriptPath, sourceInfo, executionWrapper);
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogException("Execution", ex,
                    "Opt-in Run Selection evidence capture failed; execution was not affected.",
                    new Dictionary<string, object?> { ["requestId"] = requestId, ["contentOmitted"] = true });
            }
            finally
            {
                CaptureSlot.Release();
            }
        });
    }

    private static void Persist(
        Guid requestId,
        int sessionGeneration,
        string documentDisplayName,
        string originalSelection,
        string generatedScript,
        string generatedScriptPath,
        EditorExecutionSourceInfo? sourceInfo,
        string? executionWrapper)
    {
        var originalBytes = Utf8WithoutBom.GetBytes(originalSelection ?? string.Empty);
        var generatedBytes = Utf8WithoutBom.GetBytes(generatedScript ?? string.Empty);
        var wrapperBytes = Utf8WithoutBom.GetBytes(executionWrapper ?? string.Empty);
        if (!IsWithinCaptureLimits(originalBytes.Length, generatedBytes.Length, wrapperBytes.Length))
        {
            LogSkipped(requestId, "Evidence exceeded the bounded capture size.");
            return;
        }

        var root = Path.Combine(DeveloperDiagnostics.DeveloperDebuggingRootDirectory, "RunSelectionEvidence");
        EnsurePrivateDirectory(root);
        var packagePath = Path.Combine(root, $"{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}_{requestId:N}");
        EnsurePrivateDirectory(packagePath);
        try
        {
            WritePrivateFile(Path.Combine(packagePath, "original-selection.txt"), originalBytes);
            WritePrivateFile(Path.Combine(packagePath, "generated-script.ps1"), generatedBytes);
            if (executionWrapper is not null)
            {
                WritePrivateFile(Path.Combine(packagePath, "execution-wrapper.txt"), wrapperBytes);
            }

            var metadata = new
            {
                captureStrategy = "All opted-in Run Selection attempts; not failure-only because the execution pipeline cannot reliably classify parser failures.",
                capturePoint = "After staging text/path are known and before the execution request is evaluated.",
                requestId,
                sessionGeneration,
                documentDisplayName,
                documentId = sourceInfo?.DocumentId,
                documentRevision = sourceInfo?.DocumentRevision,
                selectionStart = sourceInfo?.SelectionStart,
                selectionLength = sourceInfo?.SelectionLength,
                generatedScriptPath,
                generatedScriptEncoding = "UTF-8 without BOM",
                originalSelectionEncoding = "UTF-8 without BOM",
                generatedScriptSha256 = Convert.ToHexString(SHA256.HashData(generatedBytes)),
                originalSelectionSha256 = Convert.ToHexString(SHA256.HashData(originalBytes)),
                executionWrapperCaptured = executionWrapper is not null,
                executionWrapperLength = executionWrapper?.Length ?? 0,
                executionWrapperSha256 = executionWrapper is null ? null : Convert.ToHexString(SHA256.HashData(wrapperBytes)),
                capturedAtUtc = DateTimeOffset.UtcNow
            };
            WritePrivateFile(Path.Combine(packagePath, "manifest.json"),
                Utf8WithoutBom.GetBytes(JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true })));
            EnforceRetention(root);
        }
        catch
        {
            try { Directory.Delete(packagePath, recursive: true); }
            catch { }
            throw;
        }

        DeveloperDiagnostics.LogInfo("Execution", "Opt-in Run Selection evidence package written locally.",
            new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["sessionGeneration"] = sessionGeneration,
                ["packagePath"] = DeveloperDiagnostics.SanitizePath(packagePath),
                ["originalLength"] = originalSelection?.Length ?? 0,
                ["generatedLength"] = generatedScript?.Length ?? 0,
                ["contentOmitted"] = true,
                ["automaticUpload"] = false
            });
    }

    private static void EnsurePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Run Selection evidence capture requires Windows file permissions.");
        }

        Directory.CreateDirectory(path);
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const FileSystemRights rights = FileSystemRights.FullControl;
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(currentUser, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(system, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void WritePrivateFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    private static void EnforceRetention(string root)
    {
        var packages = new DirectoryInfo(root).GetDirectories()
            .OrderByDescending(directory => directory.CreationTimeUtc)
            .ToList();
        foreach (var oldPackage in packages.Skip(MaximumRetainedAttempts))
        {
            try { oldPackage.Delete(recursive: true); }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogException("Execution", ex, "Old Run Selection evidence package could not be removed by retention.",
                    new Dictionary<string, object?> { ["packageName"] = oldPackage.Name, ["contentOmitted"] = true });
            }
        }
    }

    private static void LogSkipped(Guid requestId, string reason)
    {
        DeveloperDiagnostics.LogWarning("Execution", "Opt-in Run Selection evidence was not captured.",
            new Dictionary<string, object?>
            {
                ["requestId"] = requestId,
                ["reason"] = reason,
                ["contentOmitted"] = true
            });
    }
}
