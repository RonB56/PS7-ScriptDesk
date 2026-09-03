using System.Windows.Input;

namespace PS7ScriptDesk.Shell.Editor;

internal static class EditorShortcutRouting
{
    internal static Key ResolveKey(Key key, Key systemKey) => key == Key.System ? systemKey : key;

    internal static bool TryGetRegisteredCommand(
        EditorCommandRegistry registry,
        Key key,
        Key systemKey,
        ModifierKeys modifiers,
        out EditorCommandDefinition? command)
    {
        command = registry.FindByShortcut(ResolveKey(key, systemKey), modifiers);
        return command is not null;
    }

    internal static bool TryGetMoveLineDirection(Key key, ModifierKeys modifiers, out int direction)
    {
        direction = key switch
        {
            Key.Up when modifiers == ModifierKeys.Alt => -1,
            Key.Down when modifiers == ModifierKeys.Alt => 1,
            _ => 0
        };

        return direction != 0;
    }
}
