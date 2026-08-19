using System.Windows;
using System.Windows.Input;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Shell.Help;

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

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ContextHelp.ValidateWindowTopics(this);
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.F1)
            {
                return;
            }

            e.Handled = true;
            ContextHelp.OpenForFocusedElement(this);
        }

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
