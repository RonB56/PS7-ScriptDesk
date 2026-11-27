using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace PS7ScriptDesk.Shell.Help;

/// <summary>Applies long-form, wizard-local hover help without changing application-wide tooltip behavior.</summary>
internal static class ContextualWizardHelp
{
    private static readonly IReadOnlyDictionary<string, string> LabelCaptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ApplicationNameBox"] = "Application name",
        ["ApplicationTypeBox"] = "Application type",
        ["DescriptionBox"] = "Description",
        ["IconBox"] = "Icon (.ico)",
        ["AdministratorBox"] = "",
        ["ArchitectureBox"] = "Architecture",
        ["DeploymentBox"] = "Deployment",
        ["PowerShellBox"] = "PowerShell runtime",
        ["PackageBox"] = "Package format",
        ["OptimizationBox"] = "Optimization",
        ["OutputBox"] = "Output executable",
        ["FileVersionBox"] = "File version",
        ["CompanyBox"] = "Company",
        ["ApiTitleBox"] = "Title",
        ["ApiVersionBox"] = "Version",
        ["RoutePrefixBox"] = "Route prefix",
        ["SecurityModeBox"] = "Security",
        ["ApiKeyEnvironmentVariableBox"] = "API key variable",
        ["RunspaceMinBox"] = "Runspace minimum",
        ["RunspaceMaxBox"] = "Runspace maximum",
        ["ConcurrencyBox"] = "Maximum concurrent requests",
        ["QueueLimitBox"] = "Queue limit",
        ["TimeoutBox"] = "Request timeout seconds",
        ["ResponseByteLimitBox"] = "Response byte limit",
        ["OutputDirectoryBox"] = "Generated project folder",
        ["LocalTestEndpointBox"] = "Endpoint",
        ["LocalTestApiKeyBox"] = "API key",
        ["TargetArchitectureBox"] = "Target architecture"
    };

    private static readonly ConditionalWeakTable<DataGrid, GridHelpRegistration> GridRegistrations = new();

    public static void Apply(Window window, IReadOnlyDictionary<string, string> helpByControlName,
        IReadOnlyDictionary<string, string>? helpByGridColumn = null)
    {
        if (helpByControlName.ContainsKey("SectionList"))
        {
            ApplyPageTabs(window.FindName("Pages") as TabControl, ExportWizardHelpDefinitions.ApiPages);
            ApplyPageTabs(window.FindName("LocalTestViews") as TabControl, ExportWizardHelpDefinitions.ApiPages);
            if (window.FindName("SectionList") is ListBox sectionList)
            {
                foreach (var item in sectionList.Items.OfType<ListBoxItem>())
                {
                    if (item.Content?.ToString() is { } name && ExportWizardHelpDefinitions.ApiPages.TryGetValue(name, out var pageHelp))
                    {
                        SetHelp(item, pageHelp);
                    }
                }
            }
        }
        else
        {
            ApplyOptionHelp(window, ExportWizardHelpDefinitions.ExeOptions);
        }

        foreach (var (name, text) in helpByControlName)
        {
            if (window.FindName(name) is not FrameworkElement element)
            {
                continue;
            }

            SetHelp(element, text);
            ApplyToAssociatedLabel(window, name, text);
        }

        if (helpByGridColumn is null)
        {
            return;
        }

        foreach (var grid in helpByControlName.Keys
                     .Select(name => window.FindName(name))
                     .OfType<DataGrid>())
        {
            RegisterGridHelp(grid, helpByGridColumn);
        }
    }

    private static void ApplyOptionHelp(Window window, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> optionHelp)
    {
        foreach (var (controlName, helpByOption) in optionHelp)
        {
            switch (window.FindName(controlName))
            {
                case ComboBox combo:
                    foreach (var item in combo.Items.OfType<ComboBoxItem>())
                    {
                        var optionKey = item.Tag?.ToString();
                        if (optionKey is not null && helpByOption.TryGetValue(optionKey, out var text))
                        {
                            SetHelp(item, text);
                        }
                    }
                    break;
                case ListBox list:
                    foreach (var item in list.Items.OfType<ListBoxItem>())
                    {
                        var optionKey = item.Tag?.ToString();
                        if (optionKey is not null && helpByOption.TryGetValue(optionKey, out var text))
                        {
                            SetHelp(item, text);
                        }
                    }
                    break;
            }
        }
    }

    private static void ApplyPageTabs(TabControl? tabs, IReadOnlyDictionary<string, string> pageHelp)
    {
        if (tabs is null)
        {
            return;
        }

        foreach (var tab in tabs.Items.OfType<TabItem>())
        {
            if (tab.Header?.ToString() is { } name && pageHelp.TryGetValue(name, out var text))
            {
                SetHelp(tab, text);
            }
        }
    }

    private static void ApplyToAssociatedLabel(Window window, string controlName, string text)
    {
        if (!LabelCaptions.TryGetValue(controlName, out var caption) || string.IsNullOrEmpty(caption))
        {
            return;
        }

        var matches = FindVisualChildren<TextBlock>(window)
            .Where(candidate => string.Equals(candidate.Text, caption, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length == 1 && string.IsNullOrEmpty(AutomationProperties.GetHelpText(matches[0])))
        {
            SetHelp(matches[0], text);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void RegisterGridHelp(DataGrid grid, IReadOnlyDictionary<string, string> helpByColumn)
    {
        var registration = GridRegistrations.GetValue(grid, currentGrid =>
        {
            var state = new GridHelpRegistration();
            currentGrid.PreparingCellForEdit += (_, eventArgs) =>
            {
                if (eventArgs.Column.Header?.ToString() is { } header && state.HelpByColumn.TryGetValue(header, out var help))
                {
                    SetHelp(eventArgs.EditingElement, help);
                }
            };
            return state;
        });

        foreach (var pair in helpByColumn)
        {
            registration.HelpByColumn[pair.Key] = pair.Value;
        }

        foreach (var column in grid.Columns)
        {
            if (column.Header?.ToString() is not { } header || !helpByColumn.TryGetValue(header, out var columnHelp))
            {
                continue;
            }

            var originalStyles = registration.GetOriginalStyles(column);
            column.HeaderStyle = CreateTooltipStyle(typeof(DataGridColumnHeader), columnHelp,
                originalStyles.Header ?? grid.ColumnHeaderStyle ?? grid.TryFindResource(typeof(DataGridColumnHeader)) as Style);
            column.CellStyle = CreateTooltipStyle(typeof(DataGridCell), columnHelp,
                originalStyles.Cell ?? grid.CellStyle ?? grid.TryFindResource(typeof(DataGridCell)) as Style);
            // Leave each column's element and editing styles intact so its editor keeps the
            // standard DataGrid defaults and all existing theme, validation, and focus behavior.
        }
    }

    private static void SetHelp(FrameworkElement element, string text)
    {
        element.ToolTip = CreateTooltip(text);
        element.SetValue(AutomationProperties.HelpTextProperty, text);
        ToolTipService.SetInitialShowDelay(element, 500);
        ToolTipService.SetShowDuration(element, 60000);
        ToolTipService.SetBetweenShowDelay(element, 150);
        ToolTipService.SetShowOnDisabled(element, true);
    }

    private static Style CreateTooltipStyle(Type targetType, string text, Style? baseStyle)
    {
        var style = new Style(targetType, baseStyle);
        style.Setters.Add(new Setter(AutomationProperties.HelpTextProperty, text));
        style.Setters.Add(new Setter(ToolTipService.ToolTipProperty, new Binding
        {
            Path = new PropertyPath(AutomationProperties.HelpTextProperty),
            RelativeSource = new RelativeSource(RelativeSourceMode.Self),
            Converter = TooltipFactoryConverter.Instance
        }));
        style.Setters.Add(new Setter(ToolTipService.InitialShowDelayProperty, 500));
        style.Setters.Add(new Setter(ToolTipService.ShowDurationProperty, 60000));
        style.Setters.Add(new Setter(ToolTipService.ShowOnDisabledProperty, true));
        return style;
    }

    private sealed class GridHelpRegistration
    {
        private readonly Dictionary<DataGridColumn, (Style? Header, Style? Cell)> _originalStyles = new();

        public Dictionary<string, string> HelpByColumn { get; } = new(StringComparer.Ordinal);

        public (Style? Header, Style? Cell) GetOriginalStyles(DataGridColumn column)
        {
            if (!_originalStyles.TryGetValue(column, out var styles))
            {
                styles = (column.HeaderStyle, column.CellStyle);
                _originalStyles.Add(column, styles);
            }

            return styles;
        }
    }

    private sealed class TooltipFactoryConverter : IValueConverter
    {
        public static TooltipFactoryConverter Instance { get; } = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => CreateTooltip(value?.ToString() ?? string.Empty);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    internal static ToolTip CreateTooltip(string text)
    {
        var lines = text.Split(new[] { "\r\n", "\n" }, 2, StringSplitOptions.None);
        var title = new TextBlock
        {
            Text = lines[0],
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 5)
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text.Primary");
        var body = new TextBlock
        {
            Text = lines.Length > 1 ? lines[1] : string.Empty,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 440
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text.Primary");
        var content = new StackPanel { MaxWidth = 460 };
        content.Children.Add(title);
        content.Children.Add(body);
        var border = new Border
        {
            Child = content,
            Padding = new Thickness(12),
            MaxWidth = 484,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5)
        };
        border.SetResourceReference(Border.BackgroundProperty, "Theme.Surface.Secondary");
        border.SetResourceReference(Border.BorderBrushProperty, "Theme.Border.Strong");
        return new ToolTip { Content = border, Focusable = false, MaxWidth = 500, StaysOpen = true };
    }
}
