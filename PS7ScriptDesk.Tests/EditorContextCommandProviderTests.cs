using ICSharpCode.AvalonEdit.Document;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class EditorContextCommandProviderTests
{
    [Fact]
    public void EligibleFutureCommandPropagatesWithoutMenuRegistration()
    {
        var executions = 0;
        var command = Definition("future.command", "Future Command", "Future", "Ctrl+Alt+F", () => true, () => executions++,
            CommandSurfaces.CommandPalette | CommandSurfaces.EditorContextMenu);
        var registry = new EditorCommandRegistry(new[] { command });

        var available = EditorContextCommandProvider.GetAvailableCommands(registry, hasNonEmptySelection: true);

        Assert.Single(available);
        Assert.Same(command, available[0]);
        Assert.Equal("Future Command", available[0].DisplayName);
        Assert.Equal("Future", available[0].Category);
        Assert.Equal("Ctrl+Alt+F", available[0].ShortcutText);
        available[0].Execute();
        Assert.Equal(1, executions);
    }

    [Fact]
    public void PaletteOnlyCommandIsExcludedFromContextSurface()
    {
        var registry = new EditorCommandRegistry(new[]
        {
            Definition("palette", "Palette Only", "Editor", string.Empty, () => true, () => { }, CommandSurfaces.CommandPalette)
        });

        Assert.Empty(EditorContextCommandProvider.GetAvailableCommands(registry, true));
    }

    [Fact]
    public void FalseAvailabilityAndMissingSelectionAreExcluded()
    {
        var registry = new EditorCommandRegistry(new[]
        {
            Definition("available", "Available", "Editor", string.Empty, () => true, () => { }, CommandSurfaces.EditorContextMenu),
            Definition("unavailable", "Unavailable", "Editor", string.Empty, () => false, () => { }, CommandSurfaces.EditorContextMenu)
        });

        Assert.Empty(EditorContextCommandProvider.GetAvailableCommands(registry, false));
        Assert.Equal("Available", Assert.Single(EditorContextCommandProvider.GetAvailableCommands(registry, true)).DisplayName);
    }

    [Fact]
    public void ReadOnlyStateIsHandledByTheRegisteredAvailabilityPredicate()
    {
        var writable = false;
        var registry = new EditorCommandRegistry(new[]
        {
            Definition("edit", "Edit Selection", "Transform", string.Empty, () => writable, () => { }, CommandSurfaces.EditorContextMenu)
        });

        Assert.Empty(EditorContextCommandProvider.GetAvailableCommands(registry, true));
        writable = true;
        Assert.Single(EditorContextCommandProvider.GetAvailableCommands(registry, true));
    }

    [Fact]
    public void RealTransformCallbackAndUndoRemainOnTheRegisteredCommandPath()
    {
        var document = new TextDocument("b\na");
        var command = Definition("transform", "Sort Lines", "Transform", string.Empty, () => true, () =>
        {
            using (document.RunUpdate())
            {
                document.Replace(0, document.TextLength, "a\nb");
            }
        }, CommandSurfaces.EditorContextMenu);
        var registry = new EditorCommandRegistry(new[] { command });
        var selected = Assert.Single(EditorContextCommandProvider.GetAvailableCommands(registry, true));

        selected.Execute();
        Assert.Equal("a\nb", document.Text);
        document.UndoStack.Undo();
        Assert.Equal("b\na", document.Text);
    }

    [Fact]
    public void BuilderCreatesDefinitionBackedMenuEntryWithCategoryAndShortcut()
    {
        RunOnStaThread(() =>
        {
            var executions = 0;
            var command = Definition("future", "Future Command", "Future", "Ctrl+Alt+F", () => true, () => executions++,
                CommandSurfaces.EditorContextMenu);
            var registry = new EditorCommandRegistry(new[] { command });
            var parent = new MenuItem { Header = "Available Commands" };

            var commands = EditorContextMenuBuilder.Populate(parent, registry, true, selected => selected.Execute());
            var item = Assert.IsType<MenuItem>(Assert.Single(parent.Items));

            Assert.Single(commands);
            Assert.Equal("Future    Future Command", item.Header);
            Assert.Equal("Ctrl+Alt+F", item.InputGestureText);
            Assert.Same(command, item.Tag);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(1, executions);
        });
    }

    private static EditorCommandDefinition Definition(
        string id,
        string name,
        string category,
        string shortcut,
        Func<bool> canExecute,
        Action execute,
        CommandSurfaces surfaces) =>
        new(id, name, category, shortcut, Array.Empty<string>(), canExecute, execute, surfaces);

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
