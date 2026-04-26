using System.Collections.Generic;

namespace PowerShellStudio.Domain.Models
{
    public class WorkspaceFolderLoadResult
    {
        public WorkspaceFolderLoadResult(
            IReadOnlyList<WorkspaceItem> items,
            IReadOnlyList<string>? warnings = null)
        {
            Items = items;
            Warnings = warnings ?? new List<string>();
        }

        public IReadOnlyList<WorkspaceItem> Items { get; }

        public IReadOnlyList<string> Warnings { get; }

        public bool HasWarnings => Warnings.Count > 0;
    }
}
