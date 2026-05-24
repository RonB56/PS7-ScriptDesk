using System.Collections.Generic;

namespace PS7ScriptDesk.Domain.Models
{
    public class WorkspaceItem
    {
        public WorkspaceItem(
            string name,
            string fullPath,
            string relativePath,
            bool isDirectory,
            IReadOnlyList<WorkspaceItem>? children = null,
            bool canExpand = false)
        {
            Name = name;
            FullPath = fullPath;
            RelativePath = relativePath;
            IsDirectory = isDirectory;
            Children = children ?? new List<WorkspaceItem>();
            CanExpand = isDirectory && (canExpand || Children.Count > 0);
        }

        public string Name { get; }

        public string FullPath { get; }

        public string RelativePath { get; }

        public bool IsDirectory { get; }

        public IReadOnlyList<WorkspaceItem> Children { get; }

        public bool CanExpand { get; }
    }
}
