using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace PS7ScriptDesk.Shell.Editor;

[Flags]
public enum CommandSurfaces
{
    None = 0,
    CommandPalette = 1,
    EditorContextMenu = 2
}

public sealed record EditorCommandDefinition(
    string Id,
    string DisplayName,
    string Category,
    string ShortcutText,
    IReadOnlyList<string> Keywords,
    Func<bool> CanExecute,
    Action Execute,
    CommandSurfaces Surfaces = CommandSurfaces.CommandPalette,
    KeyGesture? ShortcutGesture = null);

public sealed class EditorCommandRegistry
{
    private readonly IReadOnlyList<EditorCommandDefinition> _commands;

    public EditorCommandRegistry(IEnumerable<EditorCommandDefinition> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var materialized = commands.ToArray();
        if (materialized.Any(command => string.IsNullOrWhiteSpace(command.Id)))
        {
            throw new ArgumentException("Command IDs must not be empty.", nameof(commands));
        }

        if (materialized.GroupBy(command => command.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Command IDs must be unique.", nameof(commands));
        }

        _commands = materialized;
    }

    public IReadOnlyList<EditorCommandDefinition> Commands => _commands;

    public EditorCommandDefinition? FindByShortcut(Key key, ModifierKeys modifiers)
    {
        return _commands.SingleOrDefault(command =>
            command.ShortcutGesture is { } gesture &&
            gesture.Key == key &&
            gesture.Modifiers == modifiers);
    }

    public IReadOnlyList<EditorCommandDefinition> Search(string? query)
    {
        var normalized = (query ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return _commands.Where(command => command.CanExecute()).ToArray();
        }

        return _commands
            .Where(command => command.CanExecute())
            .Select(command => (Command: command, Score: GetScore(command, normalized)))
            .Where(item => item.Score >= 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Command.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Command)
            .ToArray();
    }

    private static int GetScore(EditorCommandDefinition command, string query)
    {
        var name = command.DisplayName;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 300 : 200;
        }

        if (command.Keywords.Any(keyword => keyword.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            return 100;
        }

        return -1;
    }
}

public static class EditorContextCommandProvider
{
    public static IReadOnlyList<EditorCommandDefinition> GetAvailableCommands(
        EditorCommandRegistry registry,
        bool hasNonEmptySelection)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!hasNonEmptySelection)
        {
            return Array.Empty<EditorCommandDefinition>();
        }

        return registry.Commands
            .Where(command => (command.Surfaces & CommandSurfaces.EditorContextMenu) != 0)
            .Where(command => command.CanExecute())
            .ToArray();
    }
}
