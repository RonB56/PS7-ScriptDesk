using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Shell;

public sealed class SourceControlDiffRequestedEventArgs(GitFileStatus file, GitDiffScope scope) : EventArgs
{
    public GitFileStatus File { get; } = file;
    public GitDiffScope Scope { get; } = scope;
}

    public partial class SourceControlView : UserControl
{
    public event EventHandler? PopOutRequested;
    public event EventHandler? BranchPickerRequested;
    public event EventHandler<SourceControlDiffRequestedEventArgs>? DiffRequested;

    public SourceControlView()
    {
        InitializeComponent();
    }

    public void SetViewModel(MainWindowViewModel viewModel)
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    public void SetFloating(bool isFloating)
    {
        PopOutButton.Visibility = isFloating ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PopOutButton_Click(object sender, RoutedEventArgs e)
    {
        PopOutRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BranchButton_Click(object sender, RoutedEventArgs e) => BranchPickerRequested?.Invoke(this, EventArgs.Empty);

    private MainWindowViewModel? GetViewModel() => DataContext as MainWindowViewModel;

    private void SourceControlFile_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: GitFileStatus status })
        {
            if (status.IsConflicted)
            {
                if (GetViewModel() is { } viewModel && !viewModel.TryOpenFileFromPath(status.FullPath, out var failureReason))
                    viewModel.StatusText = failureReason ?? "Unable to open the selected conflict file.";
                return;
            }

            DiffRequested?.Invoke(this, new SourceControlDiffRequestedEventArgs(status, GetScope(e.OriginalSource as DependencyObject)));
        }
    }

    private void SourceControlOpenDiff_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GitFileStatus status } menuItem)
        {
            var target = (menuItem.Parent as ContextMenu)?.PlacementTarget;
            DiffRequested?.Invoke(this, new SourceControlDiffRequestedEventArgs(status, GetScope(target)));
        }
    }

    private GitDiffScope GetScope(DependencyObject? source)
    {
        var expander = source is null ? null : FindVisualParent<Expander>(source);
        return (expander?.DataContext as SourceControlGroupViewModel)?.Title == "STAGED CHANGES"
            ? GitDiffScope.Staged
            : GitDiffScope.Unstaged;
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
        while (parent is not null)
        {
            if (parent is T match) return match;
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private void SourceControlOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GitFileStatus status } && GetViewModel() is { } viewModel &&
            !viewModel.TryOpenFileFromPath(status.FullPath, out var failureReason))
        {
            viewModel.StatusText = failureReason ?? "Unable to open the selected repository file.";
        }
    }

    private void SourceControlStage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GitFileStatus status } && GetViewModel() is { } viewModel)
        {
            _ = viewModel.StageFileAsync(status);
        }
    }

    private void SourceControlUnstage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GitFileStatus status } && GetViewModel() is { } viewModel)
        {
            _ = viewModel.UnstageFileAsync(status);
        }
    }

    private void SourceControlDiscard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GitFileStatus status } && GetViewModel() is { } viewModel)
        {
            _ = viewModel.DiscardFileAsync(status);
        }
    }

    private void SourceControlGroupAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string title } || GetViewModel() is not { } viewModel)
        {
            return;
        }

        if (title == "STAGED CHANGES")
        {
            _ = viewModel.UnstageAllAsync();
        }
        else if (title is "CHANGES" or "UNTRACKED")
        {
            _ = viewModel.StageAllAsync();
        }
    }

    private void CommitMessage_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && GetViewModel() is { } viewModel && viewModel.CommitCommand.CanExecute(null))
        {
            e.Handled = true;
            viewModel.CommitCommand.Execute(null);
        }
    }
}
