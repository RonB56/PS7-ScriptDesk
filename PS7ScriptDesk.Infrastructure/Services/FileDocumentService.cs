using System.IO;
using System.Security.Cryptography;
using System.Text;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;

namespace PS7ScriptDesk.Infrastructure.Services
{
    public class FileDocumentService : IFileDocumentService
    {
        private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

        public string ReadAllText(string filePath)
        {
            return File.ReadAllText(filePath);
        }

        public DocumentFileSnapshot ReadSnapshot(string filePath)
        {
            var normalizedPath = Path.GetFullPath(filePath);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var stateBeforeRead = GetFileState(normalizedPath);
                if (!stateBeforeRead.Exists)
                {
                    throw new FileNotFoundException("The document was not found.", normalizedPath);
                }

                var content = File.ReadAllText(normalizedPath);
                var stateAfterRead = GetFileState(normalizedPath);
                if (AreSameFileState(stateBeforeRead, stateAfterRead))
                {
                    return new DocumentFileSnapshot(content, stateAfterRead, ComputeSha256(content));
                }
            }

            throw new IOException($"The document changed repeatedly while it was being read: {normalizedPath}");
        }

        public DocumentFileState GetFileState(string filePath)
        {
            var normalizedPath = Path.GetFullPath(filePath);
            var fileInfo = new FileInfo(normalizedPath);
            fileInfo.Refresh();

            return fileInfo.Exists
                ? new DocumentFileState(true, fileInfo.LastWriteTimeUtc, fileInfo.Length)
                : DocumentFileState.Missing;
        }

        public void WriteAllText(
            string filePath,
            string content,
            DocumentFileState? expectedDestinationState = null,
            string? operationId = null)
        {
            var normalizedPath = Path.GetFullPath(filePath);
            var directoryPath = Path.GetDirectoryName(normalizedPath);
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                throw new DirectoryNotFoundException($"The folder '{directoryPath ?? normalizedPath}' does not exist.");
            }

            var temporaryPath = Path.Combine(
                directoryPath,
                $".{Path.GetFileName(normalizedPath)}.ps7scriptdesk-{Guid.NewGuid():N}.tmp");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var replacementMode = "NotStarted";

            DeveloperDiagnostics.LogOperationStart(
                "Save",
                "AtomicDocumentWrite",
                "Atomic document write started.",
                operationId,
                new Dictionary<string, object?>
                {
                    ["targetPath"] = normalizedPath,
                    ["temporaryFileName"] = Path.GetFileName(temporaryPath),
                    ["contentLength"] = content?.Length ?? 0,
                    ["expectedExists"] = expectedDestinationState?.Exists,
                    ["expectedLastWriteTimeUtc"] = expectedDestinationState?.LastWriteTimeUtc,
                    ["expectedLength"] = expectedDestinationState?.Length
                });

            try
            {
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 64 * 1024,
                           options: FileOptions.SequentialScan))
                using (var writer = new StreamWriter(stream, Utf8WithoutBom, bufferSize: 64 * 1024, leaveOpen: true))
                {
                    writer.Write(content ?? string.Empty);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                DeveloperDiagnostics.LogInfo(
                    "Save",
                    "Atomic document temporary file was fully written and flushed.",
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["targetPath"] = normalizedPath,
                        ["temporaryFileName"] = Path.GetFileName(temporaryPath),
                        ["temporaryLength"] = new FileInfo(temporaryPath).Length
                    });

                var currentState = GetFileState(normalizedPath);
                if (expectedDestinationState is not null &&
                    !AreSameFileState(expectedDestinationState, currentState))
                {
                    throw new DocumentFileChangedException(normalizedPath, expectedDestinationState, currentState);
                }

                if (currentState.Exists)
                {
                    replacementMode = "Replace";
                    File.Replace(temporaryPath, normalizedPath, destinationBackupFileName: null, ignoreMetadataErrors: false);
                }
                else
                {
                    replacementMode = "Move";
                    File.Move(temporaryPath, normalizedPath);
                }

                stopwatch.Stop();
                DeveloperDiagnostics.LogOperationStop(
                    "Save",
                    "AtomicDocumentWrite",
                    "Atomic document write completed.",
                    stopwatch.ElapsedMilliseconds,
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["targetPath"] = normalizedPath,
                        ["replacementMode"] = replacementMode,
                        ["temporaryCleanupRequired"] = false
                    });
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                DeveloperDiagnostics.LogException(
                    "Save",
                    ex,
                    "Atomic document write failed before a successful replacement or move.",
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["targetPath"] = normalizedPath,
                        ["temporaryFileName"] = Path.GetFileName(temporaryPath),
                        ["replacementMode"] = replacementMode,
                        ["elapsedMilliseconds"] = stopwatch.ElapsedMilliseconds
                    });
                throw;
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath, normalizedPath, operationId);
            }
        }

        private static bool AreSameFileState(DocumentFileState first, DocumentFileState second)
        {
            return first.Exists == second.Exists &&
                   first.LastWriteTimeUtc == second.LastWriteTimeUtc &&
                   first.Length == second.Length;
        }

        private static string ComputeSha256(string content)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty)));
        }

        private static void TryDeleteTemporaryFile(string temporaryPath, string targetPath, string? operationId)
        {
            try
            {
                if (!File.Exists(temporaryPath))
                {
                    return;
                }

                File.Delete(temporaryPath);
                DeveloperDiagnostics.LogInfo(
                    "Save",
                    "Atomic document temporary file was cleaned up.",
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["targetPath"] = targetPath,
                        ["temporaryFileName"] = Path.GetFileName(temporaryPath)
                    });
            }
            catch (Exception cleanupException)
            {
                DeveloperDiagnostics.LogException(
                    "Save",
                    cleanupException,
                    "Atomic document temporary-file cleanup failed.",
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["targetPath"] = targetPath,
                        ["temporaryFileName"] = Path.GetFileName(temporaryPath)
                    });
            }
        }
    }
}
