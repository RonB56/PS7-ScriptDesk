using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PowerShellStudio.Application.Interfaces;
using PowerShellStudio.Domain.Models;

namespace PowerShellStudio.Infrastructure.Services
{
    public class WorkspaceFolderService : IWorkspaceFolderService
    {
        // Directories that are almost always uninteresting for PowerShell scripts and can
        // be enormous (node_modules alone can contain 100k+ files). Skipping them keeps
        // the workspace tree fast even inside large development repositories.
        private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".hg", ".svn",
            ".vs",
            "node_modules",
            ".nuget", "packages",
            "__pycache__", ".tox",
            "bin", "obj",
        };

        public WorkspaceFolderLoadResult GetWorkspaceItems(
            string folderPath,
            string? filterText = null,
            bool recursive = true,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return new WorkspaceFolderLoadResult(Array.Empty<WorkspaceItem>());
            }

            if (!Directory.Exists(folderPath))
            {
                return new WorkspaceFolderLoadResult(
                    Array.Empty<WorkspaceItem>(),
                    new[] { $"Workspace folder does not exist: {folderPath}" });
            }

            var normalizedFilter = string.IsNullOrWhiteSpace(filterText)
                ? null
                : filterText.Trim();

            var warnings = new List<string>();
            var items = BuildDirectoryItems(
                currentFolderPath: folderPath,
                rootFolderPath: folderPath,
                filterText: normalizedFilter,
                recursive: recursive,
                warnings: warnings,
                cancellationToken: cancellationToken);

            return new WorkspaceFolderLoadResult(items, warnings);
        }

        public WorkspaceFolderLoadResult GetWorkspaceChildItems(
            string workspaceRootPath,
            string directoryPath,
            string? filterText = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(workspaceRootPath) || string.IsNullOrWhiteSpace(directoryPath))
            {
                return new WorkspaceFolderLoadResult(Array.Empty<WorkspaceItem>());
            }

            if (!Directory.Exists(directoryPath))
            {
                return new WorkspaceFolderLoadResult(
                    Array.Empty<WorkspaceItem>(),
                    new[] { $"Workspace folder does not exist: {directoryPath}" });
            }

            var normalizedFilter = string.IsNullOrWhiteSpace(filterText)
                ? null
                : filterText.Trim();

            var warnings = new List<string>();
            var items = BuildDirectoryItems(
                currentFolderPath: directoryPath,
                rootFolderPath: workspaceRootPath,
                filterText: normalizedFilter,
                recursive: false,
                warnings: warnings,
                cancellationToken: cancellationToken);

            return new WorkspaceFolderLoadResult(items, warnings);
        }

        private static List<WorkspaceItem> BuildDirectoryItems(
            string currentFolderPath,
            string rootFolderPath,
            string? filterText,
            bool recursive,
            IList<string> warnings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var items = new List<WorkspaceItem>();

            IEnumerable<DirectoryInfo> directories;
            try
            {
                directories = new DirectoryInfo(currentFolderPath)
                    .EnumerateDirectories()
                    .Where(ShouldIncludeDirectory)
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is ArgumentException || ex is NotSupportedException)
            {
                warnings.Add($"Unable to read directory '{currentFolderPath}': {ex.Message}");
                directories = Array.Empty<DirectoryInfo>();
            }

            foreach (var directoryInfo in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var directoryName = directoryInfo.Name;
                var directoryPath = directoryInfo.FullName;
                var relativePath = Path.GetRelativePath(rootFolderPath, directoryPath);

                if (recursive)
                {
                    var childItems = BuildDirectoryItems(directoryPath, rootFolderPath, filterText, recursive: true, warnings, cancellationToken);
                    var includeDirectory = string.IsNullOrWhiteSpace(filterText)
                        || childItems.Count > 0
                        || MatchesFilter(directoryName, relativePath, filterText);

                    if (!includeDirectory)
                    {
                        continue;
                    }

                    items.Add(new WorkspaceItem(
                        directoryName,
                        directoryPath,
                        relativePath,
                        isDirectory: true,
                        children: childItems,
                        canExpand: childItems.Count > 0));
                }
                else
                {
                    if (!MatchesFilter(directoryName, relativePath, filterText))
                    {
                        continue;
                    }

                    items.Add(new WorkspaceItem(
                        directoryName,
                        directoryPath,
                        relativePath,
                        isDirectory: true,
                        children: Array.Empty<WorkspaceItem>(),
                        canExpand: DirectoryMayHaveVisibleChildren(directoryPath, filterText, warnings, cancellationToken)));
                }
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(currentFolderPath, "*", SearchOption.TopDirectoryOnly)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is ArgumentException || ex is NotSupportedException)
            {
                warnings.Add($"Unable to read files in '{currentFolderPath}': {ex.Message}");
                files = Array.Empty<string>();
            }

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = Path.GetFileName(filePath);
                var relativePath = Path.GetRelativePath(rootFolderPath, filePath);

                if (!MatchesFilter(fileName, relativePath, filterText))
                {
                    continue;
                }

                items.Add(new WorkspaceItem(
                    fileName,
                    filePath,
                    relativePath,
                    isDirectory: false));
            }

            return items;
        }

        private static bool ShouldIncludeDirectory(DirectoryInfo directoryInfo)
        {
            if (SkippedDirectoryNames.Contains(directoryInfo.Name))
            {
                return false;
            }

            var attributes = directoryInfo.Attributes;
            if ((attributes & FileAttributes.System) != 0)
            {
                return false;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            if ((attributes & FileAttributes.Offline) != 0)
            {
                return false;
            }

            return true;
        }

        private static bool DirectoryMayHaveVisibleChildren(
            string directoryPath,
            string? filterText,
            IList<string> warnings,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filterText))
            {
                // In normal tree-browse mode, do not probe every child folder just to
                // decide whether to draw an expander. On large roots/network drives like
                // Z:\ this turned startup into multi-minute enumeration. Mark readable
                // directories as expandable and load their children only when the user
                // expands them.
                return true;
            }

            try
            {
                var directoryInfo = new DirectoryInfo(directoryPath);

                foreach (var childDirectory in directoryInfo.EnumerateDirectories())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!ShouldIncludeDirectory(childDirectory))
                    {
                        continue;
                    }

                    var childRelativePath = Path.GetRelativePath(directoryPath, childDirectory.FullName);
                    if (MatchesFilter(childDirectory.Name, childRelativePath, filterText))
                    {
                        return true;
                    }
                }

                foreach (var childFile in directoryInfo.EnumerateFiles())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var childRelativePath = Path.GetRelativePath(directoryPath, childFile.FullName);
                    if (MatchesFilter(childFile.Name, childRelativePath, filterText))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is ArgumentException || ex is NotSupportedException)
            {
                warnings.Add($"Unable to inspect directory '{directoryPath}': {ex.Message}");
            }

            return false;
        }

        private static bool MatchesFilter(string itemName, string relativePath, string? filterText)
        {
            if (string.IsNullOrWhiteSpace(filterText))
            {
                return true;
            }

            return itemName.Contains(filterText, StringComparison.OrdinalIgnoreCase)
                || relativePath.Contains(filterText, StringComparison.OrdinalIgnoreCase);
        }
    }
}
