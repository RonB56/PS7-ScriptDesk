using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using PS7ScriptDesk.Shell.Help;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Shell;

public partial class SourceControlToolWindow : Window
{
    private bool _allowClose;

    public SourceControlToolWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        SourceControlContent.SetViewModel(viewModel);
        SourceControlContent.DiffRequested += SourceControlContent_DiffRequested;
        SourceControlContent.BranchPickerRequested += SourceControlContent_BranchPickerRequested;
    }

    public event EventHandler? DockRequested;
    public event EventHandler? BranchPickerRequested;
    public event EventHandler<SourceControlDiffRequestedEventArgs>? DiffRequested;

    public void SetFloating(bool isFloating) => SourceControlContent.SetFloating(isFloating);

    private void SourceControlContent_DiffRequested(object? sender, SourceControlDiffRequestedEventArgs e)
        => DiffRequested?.Invoke(this, e);

    private void SourceControlContent_BranchPickerRequested(object? sender, EventArgs e)
        => BranchPickerRequested?.Invoke(this, e);

    public void CloseForDock()
    {
        _allowClose = true;
        Close();
    }

    public void CloseForOwnerShutdown()
    {
        _allowClose = true;
        Close();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ContextHelp.ValidateWindowTopics(this);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F1)
        {
            return;
        }

        e.Handled = true;
        ContextHelp.OpenForFocusedElement(this);
    }

    private void DockButton_Click(object sender, RoutedEventArgs e)
    {
        DockRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // Close is an explicit hide action for Source Control. It must not dock.
            base.OnClosing(e);
            return;
        }

        base.OnClosing(e);
    }
}
