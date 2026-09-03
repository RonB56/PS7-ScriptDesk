using System.Windows.Input;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class EditorShortcutRoutingTests
{
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
}
