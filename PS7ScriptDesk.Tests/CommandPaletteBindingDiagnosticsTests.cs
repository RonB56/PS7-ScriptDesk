using System.Threading;
using System.Windows.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class CommandPaletteBindingDiagnosticsTests
{
    [Fact]
    public void PaletteDisplayTextBindingMustResolveOnTheActualResultObject()
    {
        string? visibleText = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var definition = new EditorCommandDefinition("test.visible", "TEST COMMAND VISIBLE", string.Empty, string.Empty, Array.Empty<string>(), () => true, () => { });
                var item = new CommandPaletteItem(definition);
                var textBlock = new TextBlock();
                textBlock.SetBinding(TextBlock.TextProperty, new Binding("DisplayText"));
                textBlock.DataContext = item;
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                visibleText = textBlock.Text;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw failure;
        Assert.Equal("TEST COMMAND VISIBLE", visibleText);
    }

    [Fact]
    public void PaletteItemTemplateMustRenderThroughTheApplicationListBoxItemTemplate()
    {
        string? visibleText = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = System.Windows.Application.Current as PS7ScriptDesk.Shell.App ?? new PS7ScriptDesk.Shell.App();
                app.InitializeComponent();
                var definition = new EditorCommandDefinition("test.visible", "TEST COMMAND VISIBLE", string.Empty, string.Empty, Array.Empty<string>(), () => true, () => { });
                var list = new ListBox { ItemsSource = new[] { new CommandPaletteItem(definition) } };
                var itemText = new FrameworkElementFactory(typeof(TextBlock));
                itemText.SetBinding(TextBlock.TextProperty, new Binding(nameof(CommandPaletteItem.DisplayText)));
                list.ItemTemplate = new DataTemplate { VisualTree = itemText };
                var host = new Window { Content = list, Width = 400, Height = 100 };
                host.Show();
                host.UpdateLayout();
                visibleText = FindVisualChild<TextBlock>(list)?.Text;
                host.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw failure;
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
}
