using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WpfBinding = System.Windows.Data.Binding;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfControl = System.Windows.Controls.Control;

namespace PS7ScriptDesk.Shell.Editor;

internal sealed class CommandPaletteWindow : Window
{
    internal const int MaxVisibleResults = 10;
    internal const double ResultRowHeight = 30;

    private readonly EditorCommandRegistry _registry;
    private readonly WpfTextBox _searchBox = new();
    private readonly WpfListBox _results = new();
    private readonly TextBlock _emptyState = new();
    private readonly Window _owner;

    internal CommandPaletteWindow(Window owner, EditorCommandRegistry registry)
    {
        _owner = owner;
        Owner = owner;
        _registry = registry;
        Title = "Command Palette";
        Width = 680;
        MinWidth = 600;
        MaxWidth = 750;
        MinHeight = 110;
        MaxHeight = 430;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Background = WpfBrushes.Transparent;

        var frame = new Border { Padding = new Thickness(12) };
        frame.SetResourceReference(Border.BackgroundProperty, "Theme.Surface.Primary");
        frame.SetResourceReference(Border.BorderBrushProperty, "Theme.Border.Subtle");
        frame.BorderThickness = new Thickness(1);
        frame.CornerRadius = new CornerRadius(8);

        var panel = new DockPanel();
        _searchBox.MinHeight = 32;
        _searchBox.Padding = new Thickness(10, 6, 10, 6);
        _searchBox.Margin = new Thickness(0, 0, 0, 8);
        _searchBox.ToolTip = "Type a command name or keyword";
        _searchBox.TextChanged += (_, _) => RefreshResults();
        DockPanel.SetDock(_searchBox, Dock.Top);
        panel.Children.Add(_searchBox);

        _results.SetResourceReference(WpfControl.BackgroundProperty, "Theme.Surface.Secondary");
        _results.SetResourceReference(WpfControl.ForegroundProperty, "Theme.Text.Primary");
        _results.BorderThickness = new Thickness(0);
        _results.Padding = new Thickness(0);
        _results.MaxHeight = GetResultsViewportHeight(MaxVisibleResults);
        ScrollViewer.SetVerticalScrollBarVisibility(_results, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_results, ScrollBarVisibility.Disabled);
        ConfigureResultStyle();
        _results.ItemTemplate = CreateResultTemplate();
        _results.SelectionChanged += (_, _) =>
        {
            if (_results.SelectedItem is not null) _results.ScrollIntoView(_results.SelectedItem);
        };
        _results.MouseDoubleClick += (_, _) => ExecuteSelected();
        DockPanel.SetDock(_results, Dock.Top);
        panel.Children.Add(_results);

        _emptyState.Text = "No matching commands";
        _emptyState.FontSize = 13;
        _emptyState.Padding = new Thickness(10, 7, 10, 7);
        _emptyState.Visibility = Visibility.Collapsed;
        _emptyState.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text.Secondary");
        DockPanel.SetDock(_emptyState, Dock.Top);
        panel.Children.Add(_emptyState);

        frame.Child = panel;
        Content = frame;

        PreviewKeyDown += Palette_PreviewKeyDown;
        Loaded += (_, _) =>
        {
            PositionRelativeToOwner();
            RefreshResults();
            _searchBox.Focus();
            _searchBox.SelectAll();
        };
        _owner.LocationChanged += OwnerLayoutChanged;
        _owner.SizeChanged += OwnerLayoutChanged;
        Closed += (_, _) =>
        {
            _owner.LocationChanged -= OwnerLayoutChanged;
            _owner.SizeChanged -= OwnerLayoutChanged;
        };
    }

    internal static double GetResultsViewportHeight(int resultCount) =>
        Math.Min(Math.Max(resultCount, 0), MaxVisibleResults) * ResultRowHeight;

    private void ConfigureResultStyle()
    {
        var inheritedItemStyle = TryFindResource(typeof(ListBoxItem)) as Style;
        var paletteItemStyle = new Style(typeof(ListBoxItem), inheritedItemStyle);
        WpfBinding PaletteForegroundBinding() => new("Foreground")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(WpfListBox), 1)
        };
        paletteItemStyle.Setters.Add(new Setter(WpfControl.ForegroundProperty, PaletteForegroundBinding()));
        paletteItemStyle.Setters.Add(new Setter(TextElement.ForegroundProperty, PaletteForegroundBinding()));
        paletteItemStyle.Setters.Add(new Setter(WpfControl.PaddingProperty, new Thickness(10, 4, 10, 4)));
        paletteItemStyle.Setters.Add(new Setter(WpfControl.MinHeightProperty, ResultRowHeight));
        var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(WpfControl.ForegroundProperty, PaletteForegroundBinding()));
        selectedTrigger.Setters.Add(new Setter(TextElement.ForegroundProperty, PaletteForegroundBinding()));
        paletteItemStyle.Triggers.Add(selectedTrigger);
        _results.ItemContainerStyle = paletteItemStyle;
    }

    private static DataTemplate CreateResultTemplate()
    {
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.AppendChild(CreateColumn(92, GridUnitType.Pixel));
        grid.AppendChild(CreateColumn(1, GridUnitType.Star));
        grid.AppendChild(CreateColumn(0, GridUnitType.Auto));
        var category = CreateTextBlock(nameof(CommandPaletteItem.Category), 0, 12, 0.68);
        grid.AppendChild(category);
        var name = CreateTextBlock(nameof(CommandPaletteItem.DisplayName), 1, 13, 1.0);
        name.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 12, 0));
        grid.AppendChild(name);
        var shortcut = CreateTextBlock(nameof(CommandPaletteItem.ShortcutText), 2, 12, 0.78);
        shortcut.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
        grid.AppendChild(shortcut);
        return new DataTemplate { VisualTree = grid };
    }

    private static FrameworkElementFactory CreateColumn(double value, GridUnitType unit)
    {
        var column = new FrameworkElementFactory(typeof(ColumnDefinition));
        column.SetValue(ColumnDefinition.WidthProperty, new GridLength(value, unit));
        return column;
    }

    private static FrameworkElementFactory CreateTextBlock(string property, int column, double fontSize, double opacity)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new WpfBinding(property));
        text.SetBinding(TextBlock.ForegroundProperty, new WpfBinding("Foreground")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1)
        });
        text.SetValue(TextBlock.FontSizeProperty, fontSize);
        text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(UIElement.OpacityProperty, opacity);
        text.SetValue(Grid.ColumnProperty, column);
        return text;
    }

    private void Palette_PreviewKeyDown(object? sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ExecuteSelected();
            return;
        }
        if (e.Key is Key.Up or Key.Down)
        {
            e.Handled = true;
            var delta = e.Key == Key.Up ? -1 : 1;
            var index = Math.Clamp(_results.SelectedIndex + delta, 0, Math.Max(0, _results.Items.Count - 1));
            _results.SelectedIndex = index;
        }
    }

    private void RefreshResults()
    {
        var items = _registry.Search(_searchBox.Text).Select(command => new CommandPaletteItem(command)).ToArray();
        _results.ItemsSource = items;
        _results.SelectedIndex = items.Length == 0 ? -1 : 0;
        _results.Visibility = items.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _emptyState.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ExecuteSelected()
    {
        if (_results.SelectedItem is not CommandPaletteItem item) return;
        if (item.Command.CanExecute()) item.Command.Execute();
        Close();
    }

    private void OwnerLayoutChanged(object? sender, EventArgs e)
    {
        if (IsVisible) PositionRelativeToOwner();
    }

    private void PositionRelativeToOwner()
    {
        Left = _owner.Left + Math.Max(0, (_owner.ActualWidth - ActualWidth) / 2);
        Top = _owner.Top + 88;
    }
}

public sealed class CommandPaletteItem
{
    public CommandPaletteItem(EditorCommandDefinition command) => Command = command;

    public EditorCommandDefinition Command { get; }
    public string DisplayName => Command.DisplayName;
    public string Category => Command.Category;
    public string ShortcutText => Command.ShortcutText;
    public string DisplayText => string.IsNullOrWhiteSpace(Category)
        ? DisplayName
        : string.IsNullOrWhiteSpace(ShortcutText)
            ? $"{Category}: {DisplayName}"
            : $"{Category}: {DisplayName}    {ShortcutText}";
}
