using System.Windows.Input;

namespace PS7ScriptDesk.Shell;

internal static class DebuggerCommandRouting
{
    internal static Key ResolveKey(Key key, Key systemKey) => key == Key.System ? systemKey : key;

    internal static bool IsGesture(
        Key key,
        Key systemKey,
        ModifierKeys modifiers,
        Key expectedKey,
        ModifierKeys expectedModifiers = ModifierKeys.None) =>
        ResolveKey(key, systemKey) == expectedKey &&
        modifiers == expectedModifiers;
}
