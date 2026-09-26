using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PS7ScriptDesk.Shell;

internal static class LayoutForensicsCapture
{
    public static string? TryCapture(
        Window window,
        FrameworkElement? workspace,
        IReadOnlyDictionary<string, FrameworkElement?> elements,
        IReadOnlyDictionary<string, ColumnDefinition> columns,
        IReadOnlyDictionary<string, RowDefinition> rows,
        IReadOnlyDictionary<string, object?> state,
        string trigger)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PS7ScriptDesk",
                "DeveloperDebugging",
                "LayoutForensics");
            Directory.CreateDirectory(directory);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var path = Path.Combine(directory, $"LayoutForensics_{timestamp}.txt");
            var text = BuildSnapshot(window, workspace, elements, columns, rows, state, trigger);
            File.WriteAllText(path, text, Encoding.UTF8);
            return path;
        }
        catch
        {
            // Forensics must never interfere with the application under test.
            return null;
        }
    }

    internal static string BuildSnapshot(
        Window window,
        FrameworkElement? workspace,
        IReadOnlyDictionary<string, FrameworkElement?> elements,
        IReadOnlyDictionary<string, ColumnDefinition> columns,
        IReadOnlyDictionary<string, RowDefinition> rows,
        IReadOnlyDictionary<string, object?> state,
        string trigger)
    {
        var builder = new StringBuilder(32_000);
        builder.AppendLine("PS7 ScriptDesk Layout Forensics Snapshot");
        builder.AppendLine($"CapturedLocal={DateTime.Now:O}");
        builder.AppendLine($"Trigger={trigger}");
        builder.AppendLine("ContentOmitted=true");
        builder.AppendLine();

        AppendWindow(builder, window);
        AppendState(builder, state);
        AppendElement(builder, "WorkspaceGrid", workspace, window);

        builder.AppendLine("== Major Elements ==");
        foreach (var element in elements)
        {
            AppendElement(builder, element.Key, element.Value, window);
        }

        builder.AppendLine("== Root Columns ==");
        var columnIndex = 0;
        foreach (var definition in columns)
        {
            var column = definition.Value;
            var widthSource = DependencyPropertyHelper.GetValueSource(column, ColumnDefinition.WidthProperty);
            var minWidthSource = DependencyPropertyHelper.GetValueSource(column, ColumnDefinition.MinWidthProperty);
            var maxWidthSource = DependencyPropertyHelper.GetValueSource(column, ColumnDefinition.MaxWidthProperty);
            builder.AppendLine(
                $"Column[{columnIndex++}] Name={definition.Key}; Unit={column.Width.GridUnitType}; Value={F(column.Width.Value)}; Min={F(column.MinWidth)}; Max={F(column.MaxWidth)}; Actual={F(column.ActualWidth)}; WidthSource={widthSource.BaseValueSource}; MinWidthSource={minWidthSource.BaseValueSource}; MaxWidthSource={maxWidthSource.BaseValueSource}");
        }

        var rootColumnWidthSum = columns.Sum(definition => definition.Value.ActualWidth);
        builder.AppendLine($"RootColumnActualWidthSum={F(rootColumnWidthSum)}; WorkspaceWidth={F(workspace?.ActualWidth ?? 0)}; Delta={F(rootColumnWidthSum - (workspace?.ActualWidth ?? 0))}");
        builder.AppendLine("== Root Rows ==");
        var rowIndex = 0;
        foreach (var definition in rows)
        {
            var row = definition.Value;
            builder.AppendLine(
                $"Row[{rowIndex++}] Name={definition.Key}; Unit={row.Height.GridUnitType}; Value={F(row.Height.Value)}; Min={F(row.MinHeight)}; Max={F(row.MaxHeight)}; Actual={F(row.ActualHeight)}");
        }

        var rootRowHeightSum = rows.Sum(definition => definition.Value.ActualHeight);
        builder.AppendLine($"RootRowActualHeightSum={F(rootRowHeightSum)}; WorkspaceHeight={F(workspace?.ActualHeight ?? 0)}; Delta={F(rootRowHeightSum - (workspace?.ActualHeight ?? 0))}");
        AppendDiagnosticSections(builder, state);
        AppendHitTests(builder, window, workspace, elements);

        return builder.ToString();
    }

    private static void AppendWindow(StringBuilder builder, Window window)
    {
        var dpi = PresentationSource.FromVisual(window) is not null
            ? VisualTreeHelper.GetDpi(window)
            : new DpiScale(1d, 1d);

        builder.AppendLine("== MainWindow ==");
        builder.AppendLine(
            $"Window Type={window.GetType().FullName}; Actual={F(window.ActualWidth)}x{F(window.ActualHeight)}; Width={F(window.Width)}; Height={F(window.Height)}; Min={F(window.MinWidth)}x{F(window.MinHeight)}; Max={F(window.MaxWidth)}x{F(window.MaxHeight)}; State={window.WindowState}; Dpi={F(dpi.DpiScaleX)}x{F(dpi.DpiScaleY)}; Content={FormatRect(TryGetBounds(window.Content as FrameworkElement, window))}");
        AppendElement(builder, "WindowContent", window.Content as FrameworkElement, window);
    }

    private static void AppendState(StringBuilder builder, IReadOnlyDictionary<string, object?> state)
    {
        builder.AppendLine("== Workspace State ==");
        foreach (var value in state)
        {
            builder.AppendLine($"{value.Key}={value.Value ?? "<null>"}");
        }
    }

    private static void AppendDiagnosticSections(StringBuilder builder, IReadOnlyDictionary<string, object?> state)
    {
        AppendSection(builder, "== [CONCEPTUAL STATE] ==", state, "conceptual.");
        builder.AppendLine("== [ACTUAL WPF STATE] ==");
        builder.AppendLine("Actual element, column, and row geometry is listed above.");
        AppendSection(builder, "== [INVARIANT RESULT] ==", state, "invariant.");
        AppendSection(builder, "== [BUDGET] ==", state, "budget.");
        AppendSection(builder, "== [STATE/PROJECTION MISMATCHES] ==", state, "mismatch.");
    }

    private static void AppendSection(StringBuilder builder, string title, IReadOnlyDictionary<string, object?> state, string prefix)
    {
        builder.AppendLine(title);
        foreach (var value in state.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            builder.AppendLine($"{value.Key}={value.Value ?? "<null>"}");
        }
    }

    private static void AppendElement(StringBuilder builder, string label, FrameworkElement? element, Window window)
    {
        if (element is null)
        {
            builder.AppendLine($"Element={label}; Missing=true");
            return;
        }

        var bounds = TryGetBounds(element, window);
        var margin = element.Margin;
        var padding = element is Control control ? control.Padding : element is Border border ? border.Padding : new Thickness();
        var background = element is Control backgroundControl ? backgroundControl.Background?.ToString() : element is Panel panel ? panel.Background?.ToString() : element is Border backgroundBorder ? backgroundBorder.Background?.ToString() : null;
        var widthSource = DependencyPropertyHelper.GetValueSource(element, FrameworkElement.WidthProperty);
        builder.AppendLine(
            $"Element={label}; Type={element.GetType().FullName}; Name={element.Name}; Bounds={FormatRect(bounds)}; Actual={F(element.ActualWidth)}x{F(element.ActualHeight)}; Width={F(element.Width)}; Height={F(element.Height)}; WidthSource={widthSource.BaseValueSource}; Min={F(element.MinWidth)}x{F(element.MinHeight)}; Max={F(element.MaxWidth)}x{F(element.MaxHeight)}; HAlign={element.HorizontalAlignment}; VAlign={element.VerticalAlignment}; Margin={FormatThickness(margin)}; Padding={FormatThickness(padding)}; Background={background ?? "<none>"}; Visibility={element.Visibility}; RenderSize={F(element.RenderSize.Width)}x{F(element.RenderSize.Height)}; DesiredSize={F(element.DesiredSize.Width)}x{F(element.DesiredSize.Height)}; ClipToBounds={element.ClipToBounds}; Grid=({Grid.GetRow(element)},{Grid.GetColumn(element)}) span=({Grid.GetRowSpan(element)},{Grid.GetColumnSpan(element)})");

        builder.AppendLine($"ParentChainStart={label}");
        AppendParentChain(builder, element, window);
        builder.AppendLine($"ParentChainEnd={label}");
    }

    private static void AppendParentChain(StringBuilder builder, DependencyObject element, Window window)
    {
        var current = element;
        var depth = 0;
        while (current is not null && depth++ < 64)
        {
            if (current is FrameworkElement frameworkElement)
            {
                builder.AppendLine(
                    $"Parent[{depth}] Type={frameworkElement.GetType().FullName}; Name={frameworkElement.Name}; Bounds={FormatRect(TryGetBounds(frameworkElement, window))}; Actual={F(frameworkElement.ActualWidth)}x{F(frameworkElement.ActualHeight)}; Width={F(frameworkElement.Width)}; Height={F(frameworkElement.Height)}; Min={F(frameworkElement.MinWidth)}x{F(frameworkElement.MinHeight)}; Max={F(frameworkElement.MaxWidth)}x{F(frameworkElement.MaxHeight)}; HAlign={frameworkElement.HorizontalAlignment}; VAlign={frameworkElement.VerticalAlignment}; Visibility={frameworkElement.Visibility}; ClipToBounds={frameworkElement.ClipToBounds}");
            }
            else
            {
                builder.AppendLine($"Parent[{depth}] Type={current.GetType().FullName}");
            }

            current = VisualTreeHelper.GetParent(current);
        }
    }

    private static void AppendHitTests(
        StringBuilder builder,
        Window window,
        FrameworkElement? workspace,
        IReadOnlyDictionary<string, FrameworkElement?> elements)
    {
        builder.AppendLine("== Hit Tests ==");
        var windowWidth = Math.Max(0, window.ActualWidth);
        var windowHeight = Math.Max(0, window.ActualHeight);
        var points = new List<(string Label, Point Point)>();

        foreach (var name in new[] { "EditorPaneBorder", "ConsolePaneBorder", "DebugPanelBorder" })
        {
            if (elements.TryGetValue(name, out var element) && TryGetBounds(element, window) is { } bounds && bounds.Width > 0 && bounds.Height > 0)
            {
                points.Add(($"Inside{name}", new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2)));
            }
        }

        var debugBounds = elements.TryGetValue("DebugPanelBorder", out var debug) ? TryGetBounds(debug, window) : null;
        var workspaceBounds = TryGetBounds(workspace, window);
        var debugRight = debugBounds?.Right ?? workspaceBounds?.Right ?? 0;
        var whiteStart = Math.Min(windowWidth, Math.Max(0, debugRight + 5));
        points.Add(("FivePixelsRightOfDebug", new Point(whiteStart, windowHeight / 2)));
        points.Add(("HalfwayAcrossRemainingRightRegion", new Point((whiteStart + windowWidth) / 2, windowHeight / 2)));
        points.Add(("NearMainWindowRightEdge", new Point(Math.Max(0, windowWidth - 5), windowHeight / 2)));

        foreach (var point in points)
        {
            var hit = VisualTreeHelper.HitTest(window, point.Point)?.VisualHit;
            builder.AppendLine($"HitTest={point.Label}; Point={F(point.Point.X)},{F(point.Point.Y)}; Visual={DescribeVisual(hit)}; Bounds={FormatRect(TryGetBounds(hit as FrameworkElement, window))}; ParentChain={DescribeParentChain(hit, window)}");
        }
    }

    private static string DescribeVisual(DependencyObject? visual)
    {
        return visual is FrameworkElement element
            ? $"{element.GetType().FullName} Name={element.Name}"
            : visual?.GetType().FullName ?? "<null>";
    }

    private static string DescribeParentChain(DependencyObject? visual, Window window)
    {
        var names = new List<string>();
        var current = visual;
        var depth = 0;
        while (current is not null && depth++ < 32)
        {
            names.Add(current is FrameworkElement element
                ? $"{element.GetType().Name}({element.Name})"
                : current.GetType().Name);
            current = VisualTreeHelper.GetParent(current);
        }

        return string.Join(" <- ", names);
    }

    private static Rect? TryGetBounds(FrameworkElement? element, Visual ancestor)
    {
        if (element is null || !element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return element is null ? null : new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        }

        try
        {
            return element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch
        {
            return new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        }
    }

    private static string FormatRect(Rect? rect)
    {
        return rect is Rect value
            ? $"{F(value.Left)},{F(value.Top)},{F(value.Width)},{F(value.Height)}"
            : "<unavailable>";
    }

    private static string FormatThickness(Thickness value)
        => $"{F(value.Left)},{F(value.Top)},{F(value.Right)},{F(value.Bottom)}";

    private static string F(double value)
        => double.IsNaN(value) ? "NaN" : double.IsInfinity(value) ? value.ToString(CultureInfo.InvariantCulture) : value.ToString("0.###", CultureInfo.InvariantCulture);
}
