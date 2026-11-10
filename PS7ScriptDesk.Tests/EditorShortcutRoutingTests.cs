using System.Windows.Input;
using System.Threading;
using ICSharpCode.AvalonEdit;
using PS7ScriptDesk.Shell;
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
    public void SystemKeyF10ResolvesToF10ForDebuggerRouting()
    {
        Assert.Equal(Key.F10, DebuggerCommandRouting.ResolveKey(Key.System, Key.F10));
        Assert.True(DebuggerCommandRouting.IsGesture(
            Key.System,
            Key.F10,
            ModifierKeys.None,
            Key.F10));
    }

    [Fact]
    public void SystemKeyF10WithModifiersDoesNotMatchUnmodifiedStepOver()
    {
        Assert.False(DebuggerCommandRouting.IsGesture(
            Key.System,
            Key.F10,
            ModifierKeys.Alt,
            Key.F10));
        Assert.False(DebuggerCommandRouting.IsGesture(
            Key.System,
            Key.F10,
            ModifierKeys.Shift,
            Key.F10));
        Assert.False(DebuggerCommandRouting.IsGesture(
            Key.F10,
            Key.None,
            ModifierKeys.Control,
            Key.F10));
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

    [Fact]
    public void AddCaretDefaultsUseAltShiftArrowsAndRejectOtherModifierCombinations()
    {
        Assert.True(EditorShortcutRouting.TryGetAddCaretDirection(Key.System, Key.Down, ModifierKeys.Alt | ModifierKeys.Shift, out var down));
        Assert.Equal(1, down);
        Assert.True(EditorShortcutRouting.TryGetAddCaretDirection(Key.System, Key.Up, ModifierKeys.Alt | ModifierKeys.Shift, out var up));
        Assert.Equal(-1, up);
        Assert.False(EditorShortcutRouting.TryGetAddCaretDirection(Key.Down, Key.None, ModifierKeys.Alt, out _));
        Assert.False(EditorShortcutRouting.TryGetAddCaretDirection(Key.Down, Key.None, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, out _));
    }

    [Fact]
    public void FourDirectionalDefaultsRouteEachGestureToOnlyItsAssignedCommand()
    {
        var executions = new List<string>();
        var registry = new EditorCommandRegistry(new[]
        {
            Definition("editor.duplicateUp", "Duplicate Up", "Ctrl+Shift+Up", () => true, () => executions.Add("duplicateUp"), EditorShortcutDefaults.DuplicateLinesAbove),
            Definition("editor.duplicateDown", "Duplicate Down", "Ctrl+Shift+Down", () => true, () => executions.Add("duplicateDown"), EditorShortcutDefaults.DuplicateLinesBelow)
        });

        Assert.True(EditorShortcutRouting.TryGetAddCaretDirection(Key.System, Key.Up, ModifierKeys.Alt | ModifierKeys.Shift, out var addUp));
        Assert.Equal(-1, addUp);
        Assert.True(EditorShortcutRouting.TryGetAddCaretDirection(Key.System, Key.Down, ModifierKeys.Alt | ModifierKeys.Shift, out var addDown));
        Assert.Equal(1, addDown);
        Assert.False(EditorShortcutRouting.TryGetRegisteredCommand(registry, Key.Up, Key.None, ModifierKeys.Alt | ModifierKeys.Shift, out _));
        Assert.False(EditorShortcutRouting.TryGetRegisteredCommand(registry, Key.Down, Key.None, ModifierKeys.Alt | ModifierKeys.Shift, out _));

        Assert.True(EditorShortcutRouting.TryGetRegisteredCommand(registry, Key.Up, Key.None, ModifierKeys.Control | ModifierKeys.Shift, out var duplicateUp));
        duplicateUp!.Execute();
        Assert.True(EditorShortcutRouting.TryGetRegisteredCommand(registry, Key.Down, Key.None, ModifierKeys.Control | ModifierKeys.Shift, out var duplicateDown));
        duplicateDown!.Execute();
        Assert.Equal(new[] { "duplicateUp", "duplicateDown" }, executions);
    }

    [Fact]
    public void EditorCommandRegistryRejectsGestureCollisionsAcrossPrimaryAndAliasBindings()
    {
        var gesture = new KeyGesture(Key.Down, ModifierKeys.Control | ModifierKeys.Shift);
        var first = Definition("editor.first", "First", "Ctrl+Shift+Down", () => true, () => { }, gesture);
        var second = Definition("editor.second", "Second", "Ctrl+Shift+Down", () => true, () => { },
            new KeyGesture(Key.D, ModifierKeys.Control | ModifierKeys.Shift)) with
        {
            ShortcutAliases = new[] { gesture }
        };

        Assert.Throws<ArgumentException>(() => new EditorCommandRegistry(new[] { first, second }));
    }

    [Fact]
    public void RepeatedAltShiftArrowRoutingAddsSuccessiveCaretsThroughTheNativeTextArea()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var editor = new TextEditor { Text = string.Join("\n", Enumerable.Repeat("$item = 1", 12)) };
                editor.CaretOffset = editor.Document.GetOffset(1, 1);
                for (var index = 0; index < 10; index++)
                {
                    Assert.True(EditorShortcutRouting.TryGetAddCaretDirection(Key.System, Key.Down, ModifierKeys.Alt | ModifierKeys.Shift, out var direction));
                    Assert.True(EditorShortcutRouting.TryAddCaret(editor.TextArea, direction));
                    Assert.Equal(index + 2, editor.TextArea.MultiCaretCount);
                }

                var reverseEditor = new TextEditor { Text = string.Join("\n", Enumerable.Repeat("$item = 1", 12)) };
                reverseEditor.CaretOffset = reverseEditor.Document.GetOffset(12, 1);
                for (var index = 0; index < 10; index++)
                {
                    Assert.True(EditorShortcutRouting.TryGetAddCaretDirection(Key.System, Key.Up, ModifierKeys.Alt | ModifierKeys.Shift, out var direction));
                    Assert.True(EditorShortcutRouting.TryAddCaret(reverseEditor.TextArea, direction));
                    Assert.Equal(index + 2, reverseEditor.TextArea.MultiCaretCount);
                }
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("Repeated multi-caret shortcut routing failed.", failure);
    }

    [Fact]
    public void DuplicateLineReplacementShortcutsAreDistinctAndOldGesturesRemainAliases()
    {
        var registry = new EditorCommandRegistry(new[]
        {
            Definition("editor.duplicateDown", "Duplicate Down", "Ctrl+Shift+Down", () => true, () => { },
                EditorShortcutDefaults.DuplicateLinesBelow) with
            {
                ShortcutAliases = EditorShortcutDefaults.DuplicateLinesBelowAliases
            },
            Definition("editor.duplicateUp", "Duplicate Up", "Ctrl+Shift+Up", () => true, () => { },
                EditorShortcutDefaults.DuplicateLinesAbove) with
            {
                ShortcutAliases = EditorShortcutDefaults.DuplicateLinesAboveAliases
            }
        });

        Assert.True(EditorShortcutRouting.TryGetRegisteredCommand(registry, Key.Down, Key.None, ModifierKeys.Control | ModifierKeys.Shift, out var down));
        Assert.Equal("editor.duplicateDown", down!.Id);
        Assert.True(EditorShortcutRouting.TryGetRegisteredCommand(registry, Key.Up, Key.None, ModifierKeys.Control | ModifierKeys.Shift, out var up));
        Assert.Equal("editor.duplicateUp", up!.Id);
        Assert.True(EditorShortcutRouting.TryGetRegisteredCommand(registry, Key.D, Key.None, ModifierKeys.Control | ModifierKeys.Shift, out var oldDown));
        Assert.Equal("editor.duplicateDown", oldDown!.Id);
        Assert.True(EditorShortcutRouting.TryGetRegisteredCommand(registry, Key.D, Key.None, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, out var oldUp));
        Assert.Equal("editor.duplicateUp", oldUp!.Id);
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
