using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PS7ScriptDesk.Shell;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class LayoutForensicsCaptureTests
{
    [Fact]
    public void Snapshot_IsSafeWithMissingElements_AndDoesNotMutateLayout()
    {
        RunOnStaThread(() =>
        {
            var window = new Window
            {
                Width = 420,
                Height = 240,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -2000,
                Top = -2000
            };
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120, GridUnitType.Pixel), MinWidth = 80 });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var pane = new Border { Background = System.Windows.Media.Brushes.White };
            Grid.SetColumn(pane, 1);
            root.Children.Add(pane);
            window.Content = root;

            window.Show();
            window.UpdateLayout();
            var firstWidth = root.ColumnDefinitions[0].ActualWidth;
            var firstHeight = root.RowDefinitions[0].ActualHeight;
            var columns = new Dictionary<string, ColumnDefinition>
            {
                ["LeftColumn"] = root.ColumnDefinitions[0],
                ["MainColumn"] = root.ColumnDefinitions[1]
            };
            var rows = new Dictionary<string, RowDefinition>
            {
                ["MainRow"] = root.RowDefinitions[0]
            };
            var elements = new Dictionary<string, FrameworkElement?>
            {
                ["EditorPaneBorder"] = pane,
                ["ConsolePaneBorder"] = null,
                ["DebugPanelBorder"] = null
            };

            var snapshot = LayoutForensicsCapture.BuildSnapshot(
                window,
                root,
                elements,
                columns,
                rows,
                new Dictionary<string, object?> { ["workspaceLayoutMode"] = "Test" },
                "UnitTest");

            Assert.Contains("Column[0] Name=LeftColumn", snapshot, StringComparison.Ordinal);
            Assert.Contains("RootColumnActualWidthSum=", snapshot, StringComparison.Ordinal);
            Assert.Contains("Element=ConsolePaneBorder; Missing=true", snapshot, StringComparison.Ordinal);
            Assert.Contains("ParentChainStart=EditorPaneBorder", snapshot, StringComparison.Ordinal);
            Assert.Contains("HitTest=InsideEditorPaneBorder", snapshot, StringComparison.Ordinal);
            Assert.Equal(firstWidth, root.ColumnDefinitions[0].ActualWidth);
            Assert.Equal(firstHeight, root.RowDefinitions[0].ActualHeight);

            var path = LayoutForensicsCapture.TryCapture(
                window,
                root,
                elements,
                columns,
                rows,
                new Dictionary<string, object?> { ["workspaceLayoutMode"] = "Test" },
                "UnitTest.FileCreation");

            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            Assert.Contains("Trigger=UnitTest.FileCreation", File.ReadAllText(path!), StringComparison.Ordinal);
            File.Delete(path!);
            window.Close();
        });
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
