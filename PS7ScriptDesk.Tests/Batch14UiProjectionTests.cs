using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace PS7ScriptDesk.Tests;

public sealed class Batch14UiProjectionTests
{
    [Fact]
    public void PropertyChangedDispatchContract_RaisesPostedEventDirectlyWithoutRecursiveRepost()
    {
        var source = TestRepositoryPaths.ReadFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("var args = new PropertyChangedEventArgs(propertyName)", source, StringComparison.Ordinal);
        Assert.Contains("PropertyChanged?.Invoke(this, args)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Post(_ => OnPropertyChanged(propertyName)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WpfTextBlocks_ProjectTerminalAndGitStateAfterPropertyChanged()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var model = new ProjectionModel();
                var terminal = new TextBlock();
                var git = new TextBlock();
                var spinner = new ProgressBar();
                terminal.DataContext = model;
                git.DataContext = model;
                spinner.DataContext = model;
                BindingOperations.SetBinding(terminal, TextBlock.TextProperty, new Binding(nameof(ProjectionModel.ConsoleSessionText)) { NotifyOnTargetUpdated = true });
                BindingOperations.SetBinding(git, TextBlock.TextProperty, new Binding(nameof(ProjectionModel.GitStatusText)) { NotifyOnTargetUpdated = true });
                BindingOperations.SetBinding(spinner, UIElement.VisibilityProperty, new Binding(nameof(ProjectionModel.IsBusy)) { Converter = new BooleanToVisibilityConverter() });

                var terminalTargetUpdates = 0;
                var gitTargetUpdates = 0;
                terminal.TargetUpdated += (_, _) => terminalTargetUpdates++;
                git.TargetUpdated += (_, _) => gitTargetUpdates++;

                model.ConsoleSessionText = "ConPTY terminal: pwsh 7.6.5 running";
                model.GitStatusText = "Git: main";
                model.IsBusy = false;

                Assert.Same(model, terminal.DataContext);
                Assert.Same(model, git.DataContext);
                Assert.Same(model, spinner.DataContext);
                Assert.Equal(model.ConsoleSessionText, terminal.Text);
                Assert.Equal(model.GitStatusText, git.Text);
                Assert.Equal(Visibility.Collapsed, spinner.Visibility);
                Assert.True(terminalTargetUpdates >= 1);
                Assert.True(gitTargetUpdates >= 1);
                Assert.Equal(3, model.PropertyChangedCount);
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    private sealed class ProjectionModel : INotifyPropertyChanged
    {
        private string _consoleSessionText = "ConPTY terminal: not started";
        private string _gitStatusText = "Git: checking availability...";
        private bool _isBusy = true;

        public event PropertyChangedEventHandler? PropertyChanged;
        public int PropertyChangedCount { get; private set; }
        public string ConsoleSessionText { get => _consoleSessionText; set { _consoleSessionText = value; Raise(nameof(ConsoleSessionText)); } }
        public string GitStatusText { get => _gitStatusText; set { _gitStatusText = value; Raise(nameof(GitStatusText)); } }
        public bool IsBusy { get => _isBusy; set { _isBusy = value; Raise(nameof(IsBusy)); } }

        private void Raise(string propertyName)
        {
            PropertyChangedCount++;
            PropertyChanged?.Invoke(this, new(propertyName));
        }
    }
}
