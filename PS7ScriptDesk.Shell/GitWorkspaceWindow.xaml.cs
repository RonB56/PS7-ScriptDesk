using System.Windows;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Shell;

public partial class GitWorkspaceWindow : Window
{
    public GitWorkspaceWindow(GitWorkspaceViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        if (viewModel.History is not null) viewModel.History.CopyHashRequested += History_CopyHashRequested;
        Closed += (_, _) => viewModel.Dispose();
    }

    private static void History_CopyHashRequested(object? sender, string hash) => System.Windows.Clipboard.SetText(hash);

    private void ChangeSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is GitWorkspaceViewModel { Changes: not null } viewModel &&
            sender is System.Windows.Controls.ListBox listBox)
        {
            viewModel.Changes.SelectFile(listBox.SelectedItem as PS7ScriptDesk.Domain.Models.GitFileStatus);
        }
    }

    private void HistoryCommitSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is GitWorkspaceViewModel { History: not null } viewModel && sender is System.Windows.Controls.ListBox listBox)
            _ = viewModel.History.SelectCommitAsync(listBox.SelectedItem as PS7ScriptDesk.Domain.Models.GitCommit);
    }

    private void HistoryFileSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is GitWorkspaceViewModel { History: not null } viewModel && sender is System.Windows.Controls.ListBox listBox)
            _ = viewModel.History.SelectFileAsync(listBox.SelectedItem as PS7ScriptDesk.Domain.Models.GitCommitFileChange);
    }
}
