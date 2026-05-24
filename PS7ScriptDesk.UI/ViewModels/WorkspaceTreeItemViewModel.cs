using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.UI.ViewModels
{
    public class WorkspaceTreeItemViewModel
    {
        private bool _areChildrenLoaded;
        private bool _isLoadingChildren;

        public WorkspaceTreeItemViewModel(WorkspaceItem item)
        {
            DisplayName = item.Name;
            FullPath = item.FullPath;
            RelativePath = item.RelativePath;
            IsDirectory = item.IsDirectory;
            CanExpand = item.CanExpand;
            Children = new ObservableCollection<WorkspaceTreeItemViewModel>(
                item.Children.Select(child => new WorkspaceTreeItemViewModel(child)));

            _areChildrenLoaded = item.Children.Count > 0 || !CanExpand;
            if (!_areChildrenLoaded)
            {
                Children.Add(new WorkspaceTreeItemViewModel(isPlaceholder: true));
            }
        }

        private WorkspaceTreeItemViewModel(bool isPlaceholder)
        {
            IsPlaceholder = isPlaceholder;
            DisplayName = isPlaceholder ? "Loading..." : string.Empty;
            FullPath = string.Empty;
            RelativePath = string.Empty;
            Children = new ObservableCollection<WorkspaceTreeItemViewModel>();
            _areChildrenLoaded = true;
        }

        public string DisplayName { get; }

        public string DisplayText => IsPlaceholder ? DisplayName : (IsDirectory ? $"{DisplayName}\\" : DisplayName);

        public string FullPath { get; }

        public string RelativePath { get; }

        public bool IsDirectory { get; }

        public bool CanExpand { get; }

        public bool IsPlaceholder { get; }

        public bool AreChildrenLoaded => _areChildrenLoaded;

        public ObservableCollection<WorkspaceTreeItemViewModel> Children { get; }

        public bool TryBeginChildLoad()
        {
            if (!IsDirectory || IsPlaceholder || _areChildrenLoaded || _isLoadingChildren)
            {
                return false;
            }

            _isLoadingChildren = true;
            return true;
        }

        public void SetChildren(IEnumerable<WorkspaceItem> children)
        {
            Children.Clear();
            foreach (var child in children ?? Enumerable.Empty<WorkspaceItem>())
            {
                Children.Add(new WorkspaceTreeItemViewModel(child));
            }

            _areChildrenLoaded = true;
            _isLoadingChildren = false;
        }

        public void CompleteChildLoadWithoutChanges()
        {
            _isLoadingChildren = false;
        }
    }
}
