using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

internal static class RecoverableArtifactTransaction
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DestinationLocks = new(StringComparer.OrdinalIgnoreCase);

    private static readonly AsyncLocal<Func<string, Exception?>?> FaultInjectorSlot = new();
    internal static Func<string, Exception?>? FaultInjector { get => FaultInjectorSlot.Value; set => FaultInjectorSlot.Value = value; }

    internal sealed record Result(bool Succeeded, bool RecoveryRequired, string? BackupPath, string Message);

    internal static async Task<IDisposable> AcquireCoordinationAsync(string boundary, CancellationToken cancellationToken)
    {
        var key = NormalizeDestination(boundary);
        var gate = DestinationLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var processLock = await AcquireProcessLockAsync(key, cancellationToken).ConfigureAwait(false);
            return new CoordinationLease(gate, processLock);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private sealed class CoordinationLease(SemaphoreSlim gate, IDisposable processLock) : IDisposable
    {
        public void Dispose()
        {
            processLock.Dispose();
            gate.Release();
        }
    }

    internal static string CreateSiblingPath(string destination, string kind)
    {
        var fullDestination = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(fullDestination)
            ?? throw new InvalidOperationException("The artifact destination has no parent directory.");
        return Path.Combine(parent, $".{Path.GetFileName(fullDestination)}.ps7-{kind}-{Guid.NewGuid():N}");
    }

    internal static async Task<Result> CommitFileAsync(
        string stagedFile,
        string destination,
        Func<string, bool> validate,
        CancellationToken cancellationToken,
        ExeExportReplacementAuthorization? replacementAuthorization = null)
    {
        var staged = Path.GetFullPath(stagedFile);
        var target = NormalizeDestination(destination);
        EnsureSafePaths(staged, target);
        if (!File.Exists(staged) || Directory.Exists(staged) || !validate(staged))
            return new Result(false, false, null, "The staged file did not pass validation.");
        var stagedHash = HashFile(staged);

        var coordinationKey = Path.GetDirectoryName(target)!;
        var gate = DestinationLocks.GetOrAdd(coordinationKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = await AcquireProcessLockAsync(coordinationKey, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafePaths(staged, target);
            var existed = File.Exists(target);
            if (Directory.Exists(target))
                return new Result(false, false, null, "The destination is a directory and cannot be replaced by a file.");
            if (replacementAuthorization is not null && !replacementAuthorization.Matches(target))
                return new Result(false, false, null, existed
                    ? "The destination exists or changed after replacement was reviewed; renewed authorization is required."
                    : "The destination state changed after replacement was reviewed; renewed authorization is required.");
            var original = existed ? HashFile(target) : null;
            var backup = existed ? CreateSiblingPath(target, "backup") : null;
            var commitStarted = false;
            var backupMoved = false;
            var replacementInstalled = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowInjected("before-commit");
                if (replacementAuthorization is not null && !replacementAuthorization.Matches(target))
                    throw new IOException("The destination changed after replacement was reviewed; renewed authorization is required.");
                EnsureDestinationUnchanged(target, existed, original);
                commitStarted = true;
                if (existed)
                {
                    File.Move(target, backup!);
                    backupMoved = true;
                }
                ThrowInjected("after-backup");
                File.Move(staged, target);
                replacementInstalled = true;
                ThrowInjected("after-commit");
                ThrowInjected("before-final-validation");
                if (!validate(target))
                    throw new IOException("The committed file did not pass final validation.");
                if (backup is not null)
                {
                    try { File.Delete(backup); }
                    catch (Exception cleanupError) { return new Result(true, false, backup, $"The file was committed and validated, but obsolete backup cleanup failed: {cleanupError.Message}"); }
                }
                return new Result(true, false, null, "The file was committed and validated.");
            }
            catch (Exception commitError) when (commitStarted)
            {
                try
                {
                    ThrowInjected("before-rollback");
                    if (replacementInstalled)
                    {
                        if (!File.Exists(target) || !string.Equals(HashFile(target), stagedHash, StringComparison.Ordinal))
                            throw new IOException("The transaction-installed file changed during rollback and was preserved.");
                        File.Delete(target);
                    }
                    else if (File.Exists(target))
                    {
                        if (!existed || !string.Equals(HashFile(target), original, StringComparison.Ordinal))
                            throw new IOException("An unexpected destination appeared during rollback and was preserved.");
                    }
                    if (backupMoved)
                    {
                        if (backup is null || !File.Exists(backup))
                        {
                            if (!existed || !File.Exists(target) || !string.Equals(HashFile(target), original, StringComparison.Ordinal))
                                throw new IOException("The expected original-file backup is unavailable and the original state cannot be verified.");
                        }
                        else
                        {
                            if (!string.Equals(HashFile(backup), original, StringComparison.Ordinal))
                                throw new IOException("The original-file backup failed integrity verification and was retained.");
                            if (File.Exists(target)) throw new IOException("An unexpected file occupies the destination; it was preserved.");
                            File.Move(backup, target);
                        }
                    }
                    VerifyOriginalFileState(target, existed, original);
                    return new Result(false, false, null, existed
                        ? $"Replacement failed and the previous file was restored and verified: {commitError.Message}"
                        : $"Replacement failed and the originally absent destination was verified absent: {commitError.Message}");
                }
                catch (Exception rollbackError)
                {
                    var location = backup is not null && File.Exists(backup) ? backup : "(backup unavailable)";
                    return new Result(false, true, backup is not null && File.Exists(backup) ? backup : null, $"Replacement failed and automatic recovery also failed. Recover the previous file from {location}. Commit error: {commitError.Message} Recovery error: {rollbackError.Message}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception commitError) when (!commitStarted)
            {
                return new Result(false, false, null, commitError.Message);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal static async Task<Result> CommitDirectoryAsync(
        string stagedDirectory,
        string destination,
        Func<string, bool> validate,
        Func<string, bool> mayReplace,
        CancellationToken cancellationToken,
        bool coordinationHeld = false)
    {
        var staged = Path.GetFullPath(stagedDirectory);
        var target = NormalizeDestination(destination);
        EnsureSafePaths(staged, target);
        if (!Directory.Exists(staged) || !validate(staged))
            return new Result(false, false, null, "The staged artifact directory did not pass validation.");

        var gate = coordinationHeld ? null : DestinationLocks.GetOrAdd(target, static _ => new SemaphoreSlim(1, 1));
        if (gate is not null) await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = gate is null ? null : await AcquireProcessLockAsync(target, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafePaths(staged, target);
            var existed = Directory.Exists(target);
            if (File.Exists(target))
                return new Result(false, false, null, "The destination is a file and cannot be replaced by a directory.");
            if (existed && !mayReplace(target))
                return new Result(false, false, null, "The existing destination is modified or its ownership cannot be verified. Choose a new destination.");
            var original = existed ? SnapshotTree(target) : null;
            var stagedSnapshot = SnapshotTree(staged);
            var backup = existed ? CreateSiblingPath(target, "backup") : null;
            var commitStarted = false;
            var backupMoved = false;
            var replacementInstalled = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowInjected("before-commit");
                if (Directory.Exists(target) != existed || (existed && !TreeEquals(original!, SnapshotTree(target))))
                    return new Result(false, false, null, "The destination changed during generation. No replacement was made.");
                if (existed && !mayReplace(target))
                    return new Result(false, false, null, "The destination ownership changed during generation. No replacement was made.");
                commitStarted = true;
                if (existed)
                {
                    Directory.Move(target, backup!);
                    backupMoved = true;
                }
                ThrowInjected("after-backup");
                Directory.Move(staged, target);
                replacementInstalled = true;
                ThrowInjected("after-commit");
                ThrowInjected("before-final-validation");
                if (!validate(target))
                    throw new IOException("The committed artifact directory did not pass final validation.");
                if (backup is not null)
                {
                    try { Directory.Delete(backup, recursive: true); }
                    catch (Exception cleanupError) { return new Result(true, false, backup, $"The artifact was committed and validated, but obsolete backup cleanup failed: {cleanupError.Message}"); }
                }
                return new Result(true, false, null, "The directory was committed and validated.");
            }
            catch (Exception commitError) when (commitStarted)
            {
                try
                {
                    ThrowInjected("before-rollback");
                    if (replacementInstalled)
                    {
                        if (!Directory.Exists(target) || !TreeEquals(stagedSnapshot, SnapshotTree(target)))
                            throw new IOException("The transaction-installed directory changed during rollback and was preserved.");
                        Directory.Delete(target, recursive: true);
                    }
                    else if (Directory.Exists(target) && (!existed || !TreeEquals(original!, SnapshotTree(target))))
                    {
                        throw new IOException("An unexpected directory appeared during rollback and was preserved.");
                    }
                    if (backupMoved)
                    {
                        if (backup is null || !Directory.Exists(backup))
                        {
                            if (!existed || !Directory.Exists(target) || !TreeEquals(original!, SnapshotTree(target)))
                                throw new IOException("The expected original-directory backup is unavailable and the original state cannot be verified.");
                        }
                        else
                        {
                            if (!TreeEquals(original!, SnapshotTree(backup)))
                                throw new IOException("The original-directory backup failed inventory or content verification and was retained.");
                            if (Directory.Exists(target) || File.Exists(target))
                                throw new IOException("An unexpected destination artifact occupies the original path; it was preserved.");
                            Directory.Move(backup, target);
                        }
                    }
                    VerifyOriginalDirectoryState(target, existed, original);
                    return new Result(false, false, null, existed
                        ? $"Replacement failed and the previous artifact was restored and verified: {commitError.Message}"
                        : $"Replacement failed and the originally absent destination was verified absent: {commitError.Message}");
                }
                catch (Exception rollbackError)
                {
                    var location = backup is not null && Directory.Exists(backup) ? backup : "(backup unavailable)";
                    return new Result(false, true, backup is not null && Directory.Exists(backup) ? backup : null, $"Replacement failed and automatic recovery also failed. Recover the previous artifact from {location}. Commit error: {commitError.Message} Recovery error: {rollbackError.Message}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception commitError) when (!commitStarted)
            {
                return new Result(false, false, null, commitError.Message);
            }
        }
        finally
        {
            gate?.Release();
        }
    }

    internal static async Task<Result> CommitFileSetAsync(
        string stagedDirectory,
        string destinationDirectory,
        Func<string, bool> validate,
        CancellationToken cancellationToken)
    {
        var staged = Path.GetFullPath(stagedDirectory);
        var target = NormalizeDestination(destinationDirectory);
        EnsureSafePaths(staged, target);
        if (!IsSafeDirectoryTree(staged))
            return new Result(false, false, null, "The staged application folder contains an unsafe path.");

        var manifestName = ".ps7scriptdesk-exe-owned-files.json";
        var stagedSnapshot = SnapshotTree(staged);
        var newFiles = stagedSnapshot.Files
            .Where(file => !string.Equals(Path.GetFileName(file), manifestName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(file => file, file => stagedSnapshot.FileHashes[file], StringComparer.OrdinalIgnoreCase);
        if (newFiles.Count == 0)
            return new Result(false, false, null, "The staged application folder is empty.");
        if (!validate(staged))
            return new Result(false, false, null, "The staged application folder did not pass validation; the existing output was not modified.");

        var gate = DestinationLocks.GetOrAdd(target, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var processLock = await AcquireProcessLockAsync(target, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafePaths(staged, target);
            if (File.Exists(target))
                return new Result(false, false, null, "The application folder destination is an existing file.");
            var targetExisted = Directory.Exists(target);
            var originalTree = targetExisted ? SnapshotTree(target) : new TreeSnapshot(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            var hasPriorManifest = targetExisted && File.Exists(Path.Combine(target, manifestName));
            var priorOwned = targetExisted ? ReadOwnedFiles(target, manifestName) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var priorDirectories = targetExisted ? ReadOwnedDirectories(target, manifestName) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (targetExisted && priorDirectories is null)
            {
                if (!Directory.EnumerateFileSystemEntries(target).Any())
                    priorDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                else
                    return new Result(false, false, null, "The existing application folder manifest does not verify directory ownership. Preserve it or choose a new destination.");
            }
            foreach (var name in newFiles.Keys)
            {
                var existingPath = Path.Combine(target, name);
                if (Directory.Exists(existingPath) || (File.Exists(existingPath) && !priorOwned.ContainsKey(name)))
                    return new Result(false, false, null, $"The application folder already contains an unowned file named '{name}'. Choose a different destination.");
            }

            var backup = targetExisted ? CreateSiblingPath(target, "backup") : null;
            var commitStarted = false;
            var backedUpFiles = new List<string>();
            var installedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var partialFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var createdDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var oldManifestBackedUp = false;
            var newManifestInstalled = false;
            string? newManifestHash = null;
            var createdTargetDirectory = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowInjected("before-commit");
                if (Directory.Exists(target) != targetExisted || (targetExisted && !TreeEquals(originalTree, SnapshotTree(target))) ||
                    (hasPriorManifest && !OwnedFilesUnchanged(target, manifestName, priorOwned)))
                    return new Result(false, false, null, "The application folder changed during generation. No replacement was made.");
                commitStarted = true;
                if (targetExisted)
                    Directory.CreateDirectory(backup!);
                else
                {
                    Directory.CreateDirectory(target);
                    createdTargetDirectory = true;
                }
                foreach (var name in priorOwned.Keys)
                {
                    var priorPath = Path.Combine(target, name);
                    if (File.Exists(priorPath))
                    {
                        var backupPath = Path.Combine(backup!, name);
                        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                        File.Move(priorPath, backupPath);
                        backedUpFiles.Add(name);
                        ThrowInjected($"after-backup-file:{name}");
                    }
                }
                if (targetExisted && File.Exists(Path.Combine(target, manifestName)))
                {
                    File.Move(Path.Combine(target, manifestName), Path.Combine(backup!, manifestName));
                    oldManifestBackedUp = true;
                }
                ThrowInjected("after-backup");
                EnsureDirectories(target, stagedSnapshot.Directories, createdDirectories);
                foreach (var relative in newFiles.Keys)
                {
                    var targetPath = Path.Combine(target, relative);
                    var temporaryPath = CreateSiblingPath(targetPath, "install");
                    partialFiles.Add(temporaryPath);
                    ThrowInjected($"before-install-file:{relative}");
                    File.Copy(Path.Combine(staged, relative), temporaryPath, overwrite: false);
                    ThrowInjected($"after-install-copy:{relative}");
                    File.Move(temporaryPath, targetPath);
                    partialFiles.Remove(temporaryPath);
                    installedFiles.Add(relative, newFiles[relative]);
                    ThrowInjected($"after-install-file:{relative}");
                }
                var manifestDirectories = originalTree.Directories.Concat(stagedSnapshot.Directories).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var manifest = JsonSerializer.Serialize(new { version = 2, files = newFiles.Select(pair => new { name = pair.Key.Replace(Path.DirectorySeparatorChar, '/'), sha256 = pair.Value }), directories = manifestDirectories.Select(path => path.Replace(Path.DirectorySeparatorChar, '/')) });
                var manifestTemp = CreateSiblingPath(Path.Combine(target, manifestName), "install");
                partialFiles.Add(manifestTemp);
                File.WriteAllText(manifestTemp, manifest);
                File.Move(manifestTemp, Path.Combine(target, manifestName));
                partialFiles.Remove(manifestTemp);
                newManifestInstalled = true;
                newManifestHash = HashFile(Path.Combine(target, manifestName));
                ThrowInjected("after-manifest-install");
                ThrowInjected("after-commit");
                ThrowInjected("before-final-validation");
                if (!OwnedFilesUnchanged(target, manifestName, newFiles) || !validate(target))
                    throw new IOException("The committed application folder failed structural validation.");
                if (backup is not null)
                {
                    try { Directory.Delete(backup, recursive: true); }
                    catch (Exception cleanupError) { return new Result(true, false, backup, $"The application was committed and validated, but obsolete backup cleanup failed: {cleanupError.Message}"); }
                }
                return new Result(true, false, null, "The application files were committed and validated.");
            }
            catch (Exception commitError) when (commitStarted)
            {
                try
                {
                    ThrowInjected("before-rollback");
                    foreach (var temporary in partialFiles.ToArray())
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                    foreach (var pair in installedFiles)
                    {
                        var path = Path.Combine(target, pair.Key);
                        if (File.Exists(path) && string.Equals(HashFile(path), pair.Value, StringComparison.Ordinal)) File.Delete(path);
                        else if (File.Exists(path)) throw new IOException($"The output file changed during rollback and was preserved: {path}");
                    }
                    var currentManifest = Path.Combine(target, manifestName);
                    if (newManifestInstalled && File.Exists(currentManifest))
                    {
                        if (!string.Equals(HashFile(currentManifest), newManifestHash, StringComparison.Ordinal))
                            throw new IOException("The application ownership manifest changed during rollback and was preserved.");
                        File.Delete(currentManifest);
                    }
                    if (backup is not null && oldManifestBackedUp && File.Exists(Path.Combine(backup, manifestName)))
                        File.Move(Path.Combine(backup, manifestName), currentManifest);
                    if (backup is not null)
                    {
                        foreach (var relative in backedUpFiles)
                        {
                            var file = Path.Combine(backup, relative);
                            var targetPath = Path.Combine(target, relative);
                            if (!File.Exists(file)) throw new IOException($"A required backup is unavailable: {file}");
                            if (File.Exists(targetPath)) throw new IOException($"An unrelated file appeared during rollback and was preserved: {targetPath}");
                            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                            File.Move(file, targetPath);
                        }
                    }
                    RemoveCreatedEmptyDirectories(createdDirectories);
                    if (createdTargetDirectory && Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any())
                    {
                        Directory.Delete(target);
                    }
                    ThrowInjected("before-final-rollback-verification");
                    if (targetExisted)
                    {
                        if (!Directory.Exists(target) || !TreeEquals(originalTree, SnapshotTree(target)))
                            throw new IOException("The restored application file set failed original inventory, content, or directory verification.");
                    }
                    else if (Directory.Exists(target) || File.Exists(target))
                    {
                        throw new IOException("The destination was originally absent, but an unexpected artifact remains and was preserved.");
                    }
                    if (backup is not null && Directory.Exists(backup))
                    {
                        try
                        {
                            ThrowInjected("before-final-backup-cleanup");
                            Directory.Delete(backup, recursive: true);
                        }
                        catch (Exception cleanupError)
                        {
                            return new Result(false, false, backup, $"Replacement failed, but the original application files were restored and verified. Obsolete backup cleanup failed; the restored state is valid and cleanup can be retried at {backup}: {cleanupError.Message}");
                        }
                    }
                    return new Result(false, false, null, $"Replacement failed and the previous application files were restored: {commitError.Message}");
                }
                catch (Exception rollbackError)
                {
                    var inspection = InspectRecoveryMaterial(backup);
                    var recoveryPath = inspection.State == RecoveryMaterialState.Located ? backup : null;
                    var recoveryDescription = inspection.State switch
                    {
                        RecoveryMaterialState.Located => $"Recovery material was located at {backup}, but its completeness and restorability were not verified.",
                        RecoveryMaterialState.Unavailable => "No recovery files were found in the transaction backup location. Use the current destination only as evidence; its original state was not verified.",
                        _ => $"Recovery material at {backup ?? "the transaction backup location"} could not be inspected ({inspection.Error}). Its availability is unknown."
                    };
                    return new Result(false, true, recoveryPath, $"Replacement failed and automatic recovery could not establish the original state. {recoveryDescription} Commit error: {commitError.Message} Recovery error: {rollbackError.Message}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception commitError) when (!commitStarted)
            {
                return new Result(false, false, null, commitError.Message);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static Dictionary<string, string> ReadOwnedFiles(string directory, string manifestName)
    {
        var manifest = Path.Combine(directory, manifestName);
        if (!File.Exists(manifest))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var entries = document.RootElement.GetProperty("files").EnumerateArray()
                .ToDictionary(
                    entry => ValidateRelativePath(entry.GetProperty("name").GetString()),
                    entry => entry.GetProperty("sha256").GetString()!,
                    StringComparer.OrdinalIgnoreCase);
            foreach (var key in entries.Keys) _ = ValidateRelativePath(key);
            return entries;
        }
        catch
        {
            throw new IOException("The application folder ownership manifest is invalid; choose a new destination.");
        }
    }

    private static bool OwnedFilesUnchanged(string directory, string manifestName, IReadOnlyDictionary<string, string> expected)
    {
        try
        {
            var actual = ReadOwnedFiles(directory, manifestName);
            if (!actual.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected.Keys))
                return false;
            foreach (var pair in expected)
            {
                var path = Path.Combine(directory, pair.Key);
                if (!File.Exists(path) || !string.Equals(HashFile(path), pair.Value, StringComparison.Ordinal))
                    return false;
            }
            var directories = ReadOwnedDirectories(directory, manifestName);
            return directories is not null && directories.SetEquals(SnapshotTree(directory).Directories);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsSafeDirectoryTree(string path)
    {
        if (!Directory.Exists(path))
            return false;
        try
        {
            RejectReparsePoints(path);
            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static IReadOnlyDictionary<string, string> SnapshotDirectory(string path)
        => SnapshotTree(path).FileHashes;

    private sealed record TreeSnapshot(Dictionary<string, string> FileHashes, HashSet<string> Directories)
    {
        public IEnumerable<string> Files => FileHashes.Keys;
    }

    private static TreeSnapshot SnapshotTree(string path)
    {
        if (!IsSafeDirectoryTree(path))
            throw new IOException("The artifact contains a reparse point or could not be safely inspected.");
        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(path, file), HashFile, StringComparer.OrdinalIgnoreCase);
        var directories = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
            .Select(directory => Path.GetRelativePath(path, directory)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new TreeSnapshot(files, directories);
    }

    private static bool TreeEquals(TreeSnapshot left, TreeSnapshot right)
        => SnapshotEquals(left.FileHashes, right.FileHashes) && left.Directories.SetEquals(right.Directories);

    private static HashSet<string>? ReadOwnedDirectories(string directory, string manifestName)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, manifestName)));
            if (document.RootElement.GetProperty("version").GetInt32() < 2 || !document.RootElement.TryGetProperty("directories", out var directories)) return null;
            return directories.EnumerateArray().Select(item => ValidateRelativePath(item.GetString())).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { return null; }
    }

    private static void EnsureDirectories(string target, IEnumerable<string> relativeDirectories, HashSet<string> created)
    {
        foreach (var relative in relativeDirectories.OrderBy(path => path.Count(ch => ch == Path.DirectorySeparatorChar)))
        {
            var path = Path.Combine(target, relative);
            if (!Directory.Exists(path)) { Directory.CreateDirectory(path); created.Add(path); }
        }
    }

    private static void RemoveCreatedEmptyDirectories(IEnumerable<string> directories)
    {
        foreach (var path in directories.OrderByDescending(item => item.Length))
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
    }

    internal static bool SnapshotEquals(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count && left.All(entry => right.TryGetValue(entry.Key, out var value) && string.Equals(entry.Value, value, StringComparison.Ordinal));

    internal static void WriteOwnedDirectoryManifest(string directory, string manifestName)
    {
        var tree = SnapshotTree(directory);
        var files = tree.FileHashes
            .Where(entry => !string.Equals(entry.Key, manifestName, StringComparison.OrdinalIgnoreCase))
            .Select(entry => new { path = entry.Key.Replace(Path.DirectorySeparatorChar, '/'), sha256 = entry.Value })
            .OrderBy(entry => entry.path, StringComparer.Ordinal)
            .ToArray();
        var directories = tree.Directories.Select(path => path.Replace(Path.DirectorySeparatorChar, '/')).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        File.WriteAllText(Path.Combine(directory, manifestName), JsonSerializer.Serialize(new { version = 2, files, directories }));
    }

    internal static bool IsOwnedDirectoryUnmodified(string directory, string manifestName)
    {
        try
        {
            var manifestPath = Path.Combine(directory, manifestName);
            if (!File.Exists(manifestPath))
                return false;
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (document.RootElement.GetProperty("version").GetInt32() < 2 || !document.RootElement.TryGetProperty("directories", out var directoryEntries))
                return false;
            var expected = document.RootElement.GetProperty("files").EnumerateArray()
                .ToDictionary(entry => ValidateRelativePath(entry.GetProperty("path").GetString()), entry => entry.GetProperty("sha256").GetString()!, StringComparer.OrdinalIgnoreCase);
            var actual = SnapshotDirectory(directory)
                .Where(entry => !string.Equals(entry.Key, manifestName, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
            var expectedDirectories = directoryEntries.EnumerateArray().Select(entry => ValidateRelativePath(entry.GetString())).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return SnapshotEquals(expected, actual) && expectedDirectories.SetEquals(SnapshotTree(directory).Directories);
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureSafePaths(string staged, string target)
    {
        if (string.Equals(staged, target, StringComparison.OrdinalIgnoreCase) || IsWithin(staged, target) || IsWithin(target, staged))
            throw new InvalidOperationException("The staged artifact and destination overlap.");
        var root = Path.GetPathRoot(target);
        if (string.IsNullOrWhiteSpace(root) || string.Equals(Path.TrimEndingDirectorySeparator(target), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A drive or filesystem root cannot be an export destination.");
        var parent = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException("The destination must have a parent directory.");
        Directory.CreateDirectory(parent);
        RejectReparsePoints(parent);
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0 ||
            Directory.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The export destination is a reparse point and cannot be safely replaced.");
    }

    private static bool IsWithin(string candidate, string parent)
        => candidate.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RejectReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"The path contains a reparse point: {current}");
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }
    }

    private static void EnsureDestinationUnchanged(string target, bool existed, string? original)
    {
        if (File.Exists(target) != existed || Directory.Exists(target))
            throw new IOException("The destination changed before commit; no replacement was made.");
        if (existed && !string.Equals(HashFile(target), original, StringComparison.Ordinal))
            throw new IOException("The destination changed before commit; no replacement was made.");
    }

    private static void VerifyOriginalFileState(string target, bool existed, string? originalHash)
    {
        if (!existed)
        {
            if (File.Exists(target) || Directory.Exists(target))
                throw new IOException("The destination was originally absent, but an artifact remains and was preserved.");
            return;
        }

        if (!File.Exists(target) || Directory.Exists(target) || !string.Equals(HashFile(target), originalHash, StringComparison.Ordinal))
            throw new IOException("The restored file does not match its recorded original state.");
    }

    private static void VerifyOriginalDirectoryState(string target, bool existed, TreeSnapshot? original)
    {
        if (!existed)
        {
            if (Directory.Exists(target) || File.Exists(target))
                throw new IOException("The destination was originally absent, but an artifact remains and was preserved.");
            return;
        }

        if (original is null || !Directory.Exists(target) || !TreeEquals(original, SnapshotTree(target)))
            throw new IOException("The restored directory does not match its recorded original inventory and contents.");
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private enum RecoveryMaterialState { Located, Unavailable, InspectionFailed }

    private sealed record RecoveryMaterialInspection(RecoveryMaterialState State, string? Error = null);

    private static RecoveryMaterialInspection InspectRecoveryMaterial(string? backup)
    {
        try
        {
            ThrowInjected("before-recovery-material-inspection");
            if (string.IsNullOrWhiteSpace(backup) || !Directory.Exists(backup))
                return new RecoveryMaterialInspection(RecoveryMaterialState.Unavailable);
            return Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories).Any()
                ? new RecoveryMaterialInspection(RecoveryMaterialState.Located)
                : new RecoveryMaterialInspection(RecoveryMaterialState.Unavailable);
        }
        catch (Exception exception)
        {
            return new RecoveryMaterialInspection(RecoveryMaterialState.InspectionFailed, exception.Message);
        }
    }

    private static string NormalizeDestination(string destination)
    {
        var fullPath = Path.GetFullPath(destination);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static async Task<IDisposable> AcquireProcessLockAsync(string destination, CancellationToken cancellationToken)
    {
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(applicationData))
            throw new IOException("Could not establish ScriptDesk coordination because the current Windows user's Local AppData path is unavailable.");
        var lockRoot = Path.Combine(applicationData, "PS7ScriptDesk", "ArtifactLocks");
        try
        {
            Directory.CreateDirectory(lockRoot);
            RejectReparsePoints(lockRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Could not establish ScriptDesk coordination for '{destination}' because the authoritative per-user lock directory is unavailable: {lockRoot}. No alternate lock namespace was used. {error.Message}", error);
        }
        var lockName = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(destination).ToUpperInvariant()))) + ".lock";
        var lockPath = Path.Combine(lockRoot, lockName);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(50);
        while (started.Elapsed < TimeSpan.FromSeconds(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new ProcessLock(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (Exception error) when (error is DirectoryNotFoundException or DriveNotFoundException or PathTooLongException)
            {
                throw new IOException($"The ScriptDesk coordination lock path is unavailable for '{destination}': {error.Message}", error);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(250, delay.TotalMilliseconds * 1.5));
            }
            catch (IOException error)
            {
                throw new IOException($"Could not establish ScriptDesk coordination for '{destination}' because the lock store failed: {error.Message}", error);
            }
        }
        throw new IOException($"Could not establish ScriptDesk coordination for '{destination}' within 20 seconds. Another export may be active, or the lock directory may be unavailable: {lockPath}");
    }

    private sealed class ProcessLock(FileStream stream) : IDisposable
    {
        public void Dispose()
        {
            stream.Dispose();
        }
    }

    private static void ThrowInjected(string point)
    {
        var error = FaultInjector?.Invoke(point);
        if (error is not null)
            throw error;
    }

    private static string ValidateRelativePath(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new IOException("The application folder ownership manifest contains an unsafe file name.");
        var normalized = name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || normalized.Split(Path.DirectorySeparatorChar).Any(segment => segment is "" or "." or ".."))
            throw new IOException("The application folder ownership manifest contains an unsafe file name.");
        return normalized;
    }
}
