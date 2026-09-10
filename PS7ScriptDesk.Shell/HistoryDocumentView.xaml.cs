using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Shell;

public partial class HistoryDocumentView : UserControl
{
    public HistoryDocumentView() => InitializeComponent();
    private MainWindowViewModel? Vm => Window.GetWindow(this)?.DataContext as MainWindowViewModel;
    private void Refresh_Click(object sender, RoutedEventArgs e) { if (DataContext is GitHistoryTabViewModel tab) _ = Vm?.RefreshHistoryAsync(tab); }
    private void LoadMore_Click(object sender, RoutedEventArgs e) { if (DataContext is GitHistoryTabViewModel tab) _ = Vm?.LoadMoreHistoryAsync(tab); }
    private void Commit_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (DataContext is GitHistoryTabViewModel tab) _ = Vm?.SelectHistoryCommitAsync(tab, tab.SelectedCommit); }
    private void File_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (DataContext is GitHistoryTabViewModel tab && ((FrameworkElement)e.OriginalSource).DataContext is PS7ScriptDesk.Domain.Models.GitCommitFileChange file) _ = Vm?.OpenHistoricalDiffAsync(tab, file); }
    private void CopyHash_Click(object sender, RoutedEventArgs e) { if (DataContext is GitHistoryTabViewModel tab && tab.SelectedDetails?.Commit.Hash is string hash) Clipboard.SetText(hash); }
}
