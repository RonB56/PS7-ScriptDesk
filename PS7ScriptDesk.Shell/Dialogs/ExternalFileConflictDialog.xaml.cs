using System.Windows;
using PS7ScriptDesk.Application.Interfaces;

namespace PS7ScriptDesk.Shell.Dialogs
{
    public partial class ExternalFileConflictDialog : Window
    {
        public ExternalFileConflictDialog(string filePath, string conflictReason)
        {
            InitializeComponent();
            FilePathText.Text = filePath;
            ConflictReasonText.Text = conflictReason;
        }

        public ExternalFileConflictDecision Decision { get; private set; } = ExternalFileConflictDecision.Cancel;

        private void ReloadButton_Click(object sender, RoutedEventArgs e) => Complete(ExternalFileConflictDecision.ReloadFromDisk);

        private void OverwriteButton_Click(object sender, RoutedEventArgs e) => Complete(ExternalFileConflictDecision.OverwriteDisk);

        private void SaveAsButton_Click(object sender, RoutedEventArgs e) => Complete(ExternalFileConflictDecision.SaveAs);

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Complete(ExternalFileConflictDecision.Cancel);

        private void Complete(ExternalFileConflictDecision decision)
        {
            Decision = decision;
            DialogResult = decision != ExternalFileConflictDecision.Cancel;
        }
    }
}
