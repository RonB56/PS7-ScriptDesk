using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace PS7ScriptDesk.Shell.Editor;

public static class EditorContextMenuBuilder
{
    public static IReadOnlyList<EditorCommandDefinition> Populate(
        WpfMenuItem availableMenu,
        EditorCommandRegistry registry,
        bool hasNonEmptySelection,
        Action<EditorCommandDefinition> execute)
    {
        ArgumentNullException.ThrowIfNull(availableMenu);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(execute);

        availableMenu.Items.Clear();
        var commands = EditorContextCommandProvider.GetAvailableCommands(registry, hasNonEmptySelection);
        availableMenu.Visibility = commands.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        string? previousCategory = null;
        foreach (var group in commands.GroupBy(command => command.Category, StringComparer.OrdinalIgnoreCase))
        {
            if (previousCategory is not null)
            {
                availableMenu.Items.Add(new Separator());
            }

            foreach (var command in group)
            {
                var menuItem = new WpfMenuItem
                {
                    Header = string.IsNullOrWhiteSpace(command.Category)
                        ? command.DisplayName
                        : $"{command.Category}    {command.DisplayName}",
                    InputGestureText = command.ShortcutText,
                    Tag = command
                };
                menuItem.Click += (_, _) => execute(command);
                availableMenu.Items.Add(menuItem);
            }

            previousCategory = group.Key;
        }

        return commands;
    }
}
