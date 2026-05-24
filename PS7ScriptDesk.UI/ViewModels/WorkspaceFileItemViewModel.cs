using System.IO;

namespace PS7ScriptDesk.UI.ViewModels
{
    public class WorkspaceFileItemViewModel
    {
        public WorkspaceFileItemViewModel(string filePath)
        {
            FilePath = filePath;
            DisplayName = Path.GetFileName(filePath);
        }

        public string FilePath { get; }

        public string DisplayName { get; }
    }
}