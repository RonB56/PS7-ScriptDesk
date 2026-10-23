using System.Threading;
using System.Reflection;
using System.Windows.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class CommandPaletteBindingDiagnosticsTests
{
    [Fact]
    public void PaletteDisplayTextBindingMustResolveOnTheActualResultObject()
    {
        string? visibleText = null;
        WpfTestDispatcher.Run(() =>
        {
            var definition = new EditorCommandDefinition("test.visible", "TEST COMMAND VISIBLE", string.Empty, string.Empty, Array.Empty<string>(), () => true, () => { });
            var item = new CommandPaletteItem(definition);
            var textBlock = new TextBlock();
            textBlock.SetBinding(TextBlock.TextProperty, new Binding("DisplayText"));
            textBlock.DataContext = item;
            WpfTestDispatcher.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
            visibleText = textBlock.Text;
        });
        Assert.Equal("TEST COMMAND VISIBLE", visibleText);
    }

    [Fact]
    public void PaletteItemTemplateMustRenderThroughTheApplicationListBoxItemTemplate()
    {
        string? visibleText = null;
        WpfTestDispatcher.Run(() =>
        {
            var definition = new EditorCommandDefinition("test.visible", "TEST COMMAND VISIBLE", string.Empty, string.Empty, Array.Empty<string>(), () => true, () => { });
            var owner = new Window
            {
                Width = 800,
                Height = 600,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -2000,
                Top = -2000,
                ShowInTaskbar = false
            };
            CommandPaletteWindow? palette = null;
            try
            {
                owner.Show();
                var registry = new EditorCommandRegistry(new[] { definition });
                palette = new CommandPaletteWindow(owner, registry);
                var resultsField = typeof(CommandPaletteWindow).GetField("_results", BindingFlags.Instance | BindingFlags.NonPublic);
                var results = Assert.IsType<ListBox>(resultsField?.GetValue(palette));
                Assert.IsType<DataTemplate>(results.ItemTemplate);
                palette.Show();
                WpfTestDispatcher.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                WpfTestDispatcher.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                palette.UpdateLayout();
                var item = Assert.IsType<ListBoxItem>(results.ItemContainerGenerator.ContainerFromIndex(0));
                visibleText = FindVisualChildren<TextBlock>(item)
                    .Select(textBlock => textBlock.Text)
                    .FirstOrDefault(text => text == "TEST COMMAND VISIBLE");
            }
            finally
            {
                if (palette?.IsVisible is true) palette.Close();
                if (owner.IsVisible) owner.Close();
            }
        });
        Assert.Equal("TEST COMMAND VISIBLE", visibleText);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) return descendant;
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
}
