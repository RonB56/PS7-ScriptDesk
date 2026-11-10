using System.Windows.Input;
using ICSharpCode.AvalonEdit.Editing;

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

    internal static bool TryGetAddCaretDirection(Key key, Key systemKey, ModifierKeys modifiers, out int direction)
    {
        var resolvedKey = ResolveKey(key, systemKey);
        direction = Matches(EditorShortcutDefaults.AddCaretAbove, resolvedKey, modifiers)
            ? -1
            : Matches(EditorShortcutDefaults.AddCaretBelow, resolvedKey, modifiers)
                ? 1
                : 0;
        return direction != 0;
    }

    private static bool Matches(KeyGesture gesture, Key key, ModifierKeys modifiers)
        => gesture.Key == key && gesture.Modifiers == modifiers;

    internal static bool TryAddCaret(TextArea textArea, int direction)
        => textArea is not null && textArea.AddCaretOnAdjacentLineFromExtremeCaret(direction);
}
