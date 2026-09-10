using System.Windows;
using System.Windows.Controls;

namespace PS7ScriptDesk.Shell;

public partial class DiffDocumentView : UserControl
{
    public DiffDocumentView() => InitializeComponent();

    private void Inline_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PS7ScriptDesk.UI.ViewModels.DiffTabViewModel tab) tab.IsInline = true;
    }

    private void SideBySide_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PS7ScriptDesk.UI.ViewModels.DiffTabViewModel tab) tab.IsInline = false;
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PS7ScriptDesk.UI.ViewModels.DiffTabViewModel tab && tab.MovePreviousChange()) ScrollToCurrentHunk(tab);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PS7ScriptDesk.UI.ViewModels.DiffTabViewModel tab && tab.MoveNextChange()) ScrollToCurrentHunk(tab);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PS7ScriptDesk.UI.ViewModels.DiffTabViewModel tab && Window.GetWindow(this)?.DataContext is PS7ScriptDesk.UI.ViewModels.MainWindowViewModel vm)
            _ = vm.RefreshDiffAsync(tab);
    }

    private void LoadAnyway_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PS7ScriptDesk.UI.ViewModels.DiffTabViewModel tab && Window.GetWindow(this)?.DataContext is PS7ScriptDesk.UI.ViewModels.MainWindowViewModel vm)
            _ = vm.RefreshDiffAsync(tab, allowLarge: true);
    }

    private void ScrollToCurrentHunk(PS7ScriptDesk.UI.ViewModels.DiffTabViewModel tab)
    {
        if (tab.CurrentHunkIndex < 0) return;
        Dispatcher.BeginInvoke(() => DiffScrollViewer.ScrollToVerticalOffset(tab.CurrentHunkIndex * 120));
    }

}
