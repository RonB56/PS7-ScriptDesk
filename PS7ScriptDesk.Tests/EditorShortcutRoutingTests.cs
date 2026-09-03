using System.Windows.Input;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class EditorShortcutRoutingTests
{
    [Fact]
    public void RegisteredCtrlEnterRoutesToInsertLineBelowOnce()
    {
        var executions = 0;
        var registry = new EditorCommandRegistry(new[]
        {
            Definition("editor.insertLineBelow", "Insert Line Below", "Ctrl+Enter", () => true, () => executions++,
                new KeyGesture(Key.Enter, ModifierKeys.Control))
        });

        Assert.True(EditorShortcutRouting.TryGetRegisteredCommand(registry, Key.System, Key.Enter, ModifierKeys.Control, out var command));
        Assert.Equal("editor.insertLineBelow", command!.Id);
        Assert.True(command.CanExecute());
        command.Execute();
        Assert.Equal(1, executions);
    }

    [Fact]
    public void RegisteredCtrlShiftEnterRoutesToInsertLineAboveAndRespectsAvailability()
    {
        var executions = 0;
        var canExecute = false;
        var registry = new EditorCommandRegistry(new[]
        {
            Definition("editor.insertLineAbove", "Insert Line Above", "Ctrl+Shift+Enter", () => canExecute, () => executions++,
                new KeyGesture(Key.Enter, ModifierKeys.Control | ModifierKeys.Shift))
        });

        Assert.True(EditorShortcutRouting.TryGetRegisteredCommand(registry, Key.Enter, Key.Enter, ModifierKeys.Control | ModifierKeys.Shift, out var command));
        Assert.Equal("editor.insertLineAbove", command!.Id);
        Assert.False(command.CanExecute());
        canExecute = true;
        Assert.True(command.CanExecute());
        command.Execute();
        Assert.Equal(1, executions);
    }

    [Fact]
    public void RegisteredShortcutMatchingDoesNotTreatPageUpOrOtherModifiersAsInsertLine()
    {
        var registry = new EditorCommandRegistry(new[]
        {
            Definition("editor.insertLineBelow", "Insert Line Below", "Ctrl+Enter", () => true, () => { },
                new KeyGesture(Key.Enter, ModifierKeys.Control))
        });

        Assert.False(EditorShortcutRouting.TryGetRegisteredCommand(registry, Key.PageUp, Key.PageUp, ModifierKeys.Control, out _));
        Assert.False(EditorShortcutRouting.TryGetRegisteredCommand(registry, Key.Enter, Key.Enter, ModifierKeys.None, out _));
        Assert.False(EditorShortcutRouting.TryGetRegisteredCommand(registry, Key.Enter, Key.Enter, ModifierKeys.Control | ModifierKeys.Shift, out _));
    }

    [Fact]
    public void InsertLineShortcutKeepsDisplayMetadata()
    {
        var command = Definition("editor.insertLineBelow", "Insert Line Below", "Ctrl+Enter", () => true, () => { },
            new KeyGesture(Key.Enter, ModifierKeys.Control));

        Assert.Equal("Ctrl+Enter", command.ShortcutText);
        Assert.Equal(Key.Enter, command.ShortcutGesture!.Key);
        Assert.Equal(ModifierKeys.Control, command.ShortcutGesture.Modifiers);
    }

    [Fact]
    public void SystemKeyWithAltArrowResolvesToMoveDirection()
    {
        Assert.Equal(Key.Up, EditorShortcutRouting.ResolveKey(Key.System, Key.Up));
        Assert.Equal(Key.Down, EditorShortcutRouting.ResolveKey(Key.System, Key.Down));
        Assert.True(EditorShortcutRouting.TryGetMoveLineDirection(Key.Up, ModifierKeys.Alt, out var up));
        Assert.Equal(-1, up);
        Assert.True(EditorShortcutRouting.TryGetMoveLineDirection(Key.Down, ModifierKeys.Alt, out var down));
        Assert.Equal(1, down);
    }

    [Fact]
    public void PageUpAndNonAltCombinationsDoNotRouteToMoveLines()
    {
        Assert.False(EditorShortcutRouting.TryGetMoveLineDirection(Key.PageUp, ModifierKeys.Alt, out _));
        Assert.False(EditorShortcutRouting.TryGetMoveLineDirection(Key.Up, ModifierKeys.None, out _));
        Assert.False(EditorShortcutRouting.TryGetMoveLineDirection(Key.Down, ModifierKeys.Shift, out _));
        Assert.False(EditorShortcutRouting.TryGetMoveLineDirection(Key.Up, ModifierKeys.Control, out _));
        Assert.False(EditorShortcutRouting.TryGetMoveLineDirection(Key.Down, ModifierKeys.Alt | ModifierKeys.Shift, out _));
    }

    [Fact]
    public void ShiftAltArrowRemainsDistinctFromMoveLines()
    {
        Assert.False(EditorShortcutRouting.TryGetMoveLineDirection(Key.Up, ModifierKeys.Alt | ModifierKeys.Shift, out _));
        Assert.False(EditorShortcutRouting.TryGetMoveLineDirection(Key.Down, ModifierKeys.Alt | ModifierKeys.Shift, out _));
    }

    private static EditorCommandDefinition Definition(
        string id,
        string displayName,
        string shortcutText,
        Func<bool> canExecute,
        Action execute,
        KeyGesture gesture) =>
        new(id, displayName, "Editor", shortcutText, Array.Empty<string>(), canExecute, execute,
            CommandSurfaces.CommandPalette, gesture);
}
