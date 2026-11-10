using System.Windows.Input;

namespace PS7ScriptDesk.Shell.Editor;

/// <summary>Default editor gestures in one location for future user customization.</summary>
internal static class EditorShortcutDefaults
{
    internal static KeyGesture AddCaretAbove { get; } = new(Key.Up, ModifierKeys.Alt | ModifierKeys.Shift);
    internal static KeyGesture AddCaretBelow { get; } = new(Key.Down, ModifierKeys.Alt | ModifierKeys.Shift);

    internal static KeyGesture DuplicateLinesAbove { get; } = new(Key.Up, ModifierKeys.Control | ModifierKeys.Shift);
    internal static KeyGesture DuplicateLinesBelow { get; } = new(Key.Down, ModifierKeys.Control | ModifierKeys.Shift);

    // Keep the former duplicate-line gestures working as aliases while the arrow gestures become the defaults.
    internal static IReadOnlyList<KeyGesture> DuplicateLinesAboveAliases { get; } =
        new[] { new KeyGesture(Key.D, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift) };

    internal static IReadOnlyList<KeyGesture> DuplicateLinesBelowAliases { get; } =
        new[] { new KeyGesture(Key.D, ModifierKeys.Control | ModifierKeys.Shift) };
}
