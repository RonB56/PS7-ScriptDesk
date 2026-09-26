using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows.Data;
using System.Windows.Media;

namespace PS7ScriptDesk.Shell.Debug;

public enum DebugOutputFilterGroup
{
    Debugger,
    Breakpoints,
    Steps,
    Output,
    Host,
    Warnings,
    Verbose,
    Debug,
    Information,
    Errors,
    Exceptions
}

public sealed class DebugOutputItem
{
    private DebugOutputItem(DebuggerEvent? debuggerEvent, string? sessionLabel, bool isSessionBoundary)
    {
        Event = debuggerEvent;
        SessionLabel = sessionLabel;
        IsSessionBoundary = isSessionBoundary;
    }

    public DebuggerEvent? Event { get; }
    public bool IsSessionBoundary { get; }
    public string? SessionLabel { get; }
    public Guid SessionId => Event?.SessionId ?? Guid.Empty;
    public long Sequence => Event?.Sequence ?? 0;
    public DebuggerEventCategory Category => Event?.Category ?? DebuggerEventCategory.DebuggerLifecycle;
    public string LocalTime => Event?.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? string.Empty;
    public string CategoryLabel => Event?.Category.ToString() ?? "Session";
    public string SeverityLabel => Event?.Severity.ToString() ?? string.Empty;
    public string SourceLabel => Event?.Source ?? string.Empty;
    public string DisplayText => Event?.DisplayText ?? SessionLabel ?? string.Empty;
    public string? FilePath => Event?.FilePath;
    public int? LineNumber => Event?.LineNumber;
    public string? LocationLabel => Event is { FilePath: not null, LineNumber: > 0 }
        ? $"{System.IO.Path.GetFileName(Event.FilePath)}:{Event.LineNumber}"
        : null;
    public string? Details => Event?.Details;
    public bool IsNavigable => Event?.IsNavigable == true;
    public bool IsExpandable => Event?.IsExpandable == true;
    public DebuggerPauseReason? PauseReason => Event?.PauseReason;
    public DebuggerTerminationReason? TerminationReason => Event?.TerminationReason;
    public bool IsProtocol => Event?.Category == DebuggerEventCategory.Protocol;

    public static DebugOutputItem ForEvent(DebuggerEvent debuggerEvent) =>
        new(debuggerEvent, sessionLabel: null, isSessionBoundary: false);

    public static DebugOutputItem ForSessionBoundary(Guid sessionId, string label) =>
        new(
            new DebuggerEvent(
                sessionId,
                DateTimeOffset.UtcNow,
                1,
                DebuggerEventCategory.DebuggerLifecycle,
                DebuggerEventSeverity.Information,
                "Session",
                label,
                details: null,
                isNavigable: false,
                isExpandable: false),
            label,
            isSessionBoundary: true);

    public static string FormatForExport(DebugOutputItem item)
    {
        if (item.IsSessionBoundary)
        {
            return $"{item.LocalTime} [Session] {item.DisplayText}";
        }

        var location = item.LocationLabel is null ? string.Empty : $" {item.LocationLabel}";
        return $"{item.LocalTime} [{item.CategoryLabel}]{location} {item.DisplayText}".TrimEnd();
    }
}

public sealed class DebugOutputPresentationModel : INotifyPropertyChanged
{
    public const int DefaultMaximumItemCount = 5000;
    public const int DefaultMaximumTextFootprint = 500000;

    private readonly Dictionary<DebugOutputFilterGroup, bool> _filters = new()
    {
        [DebugOutputFilterGroup.Debugger] = true,
        [DebugOutputFilterGroup.Breakpoints] = true,
        [DebugOutputFilterGroup.Steps] = true,
        [DebugOutputFilterGroup.Output] = true,
        [DebugOutputFilterGroup.Host] = true,
        [DebugOutputFilterGroup.Warnings] = true,
        [DebugOutputFilterGroup.Verbose] = false,
        [DebugOutputFilterGroup.Debug] = false,
        [DebugOutputFilterGroup.Information] = false,
        [DebugOutputFilterGroup.Errors] = true,
        [DebugOutputFilterGroup.Exceptions] = true
    };

    private readonly ListCollectionView _view;
    private int _retainedTextFootprint;
    private bool _includeScriptOutput = true;

    public DebugOutputPresentationModel(
        int maximumItemCount = DefaultMaximumItemCount,
        int maximumTextFootprint = DefaultMaximumTextFootprint)
    {
        if (maximumItemCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItemCount));
        }

        if (maximumTextFootprint < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTextFootprint));
        }

        MaximumItemCount = maximumItemCount;
        MaximumTextFootprint = maximumTextFootprint;
        Items = new ObservableCollection<DebugOutputItem>();
        _view = new ListCollectionView(Items);
        _view.Filter = IsVisible;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DebugOutputItem> Items { get; }
    public ICollectionView VisibleItems => _view;
    public int MaximumItemCount { get; }
    public int MaximumTextFootprint { get; }
    public int RetainedTextFootprint => _retainedTextFootprint;

    public bool IncludeScriptOutput
    {
        get => _includeScriptOutput;
        set
        {
            if (_includeScriptOutput == value)
            {
                return;
            }

            _includeScriptOutput = value;
            RefreshFilter();
            PropertyChanged?.Invoke(this, new(nameof(IncludeScriptOutput)));
        }
    }

    public bool GetFilter(DebugOutputFilterGroup group) =>
        _filters.TryGetValue(group, out var enabled) && enabled;

    public void SetFilter(DebugOutputFilterGroup group, bool enabled)
    {
        if (!_filters.ContainsKey(group) || _filters[group] == enabled)
        {
            return;
        }

        _filters[group] = enabled;
        RefreshFilter();
    }

    public void BeginSession(Guid sessionId, string launchPath)
    {
        AddCore(DebugOutputItem.ForSessionBoundary(
            sessionId,
            $"Debug session started — {System.IO.Path.GetFileName(launchPath)} ({sessionId.ToString("N")[..8]})"));
    }

    public void Append(DebuggerEvent debuggerEvent)
    {
        ArgumentNullException.ThrowIfNull(debuggerEvent);
        if (debuggerEvent.Category == DebuggerEventCategory.Protocol)
        {
            return;
        }

        AddCore(DebugOutputItem.ForEvent(debuggerEvent));
    }

    public void Clear()
    {
        Items.Clear();
        _retainedTextFootprint = 0;
        PropertyChanged?.Invoke(this, new(nameof(RetainedTextFootprint)));
    }

    public IReadOnlyList<DebugOutputItem> GetVisibleItems()
    {
        var result = new List<DebugOutputItem>(_view.Cast<DebugOutputItem>());
        return result;
    }

    public string FormatVisibleItems()
    {
        var builder = new StringBuilder();
        foreach (var item in GetVisibleItems())
        {
            builder.AppendLine(DebugOutputItem.FormatForExport(item));
        }

        return builder.ToString().TrimEnd();
    }

    private void AddCore(DebugOutputItem item)
    {
        Items.Add(item);
        _retainedTextFootprint += EstimateFootprint(item);

        while (Items.Count > MaximumItemCount || _retainedTextFootprint > MaximumTextFootprint)
        {
            if (Items.Count == 0)
            {
                _retainedTextFootprint = 0;
                break;
            }

            var removed = Items[0];
            Items.RemoveAt(0);
            _retainedTextFootprint = Math.Max(0, _retainedTextFootprint - EstimateFootprint(removed));
        }

        PropertyChanged?.Invoke(this, new(nameof(RetainedTextFootprint)));
    }

    private static int EstimateFootprint(DebugOutputItem item) =>
        item.DisplayText.Length +
        (item.Details?.Length ?? 0) +
        (item.FilePath?.Length ?? 0) +
        96;

    private bool IsVisible(object value)
    {
        if (value is not DebugOutputItem item || item.Event is null)
        {
            return false;
        }

        if (item.IsProtocol || !_includeScriptOutput && IsScriptOutput(item.Event.Category))
        {
            return false;
        }

        return item.Event.Category switch
        {
            DebuggerEventCategory.DebuggerLifecycle or DebuggerEventCategory.AdapterWarning => GetFilter(DebugOutputFilterGroup.Debugger),
            DebuggerEventCategory.Breakpoint => GetFilter(DebugOutputFilterGroup.Breakpoints),
            DebuggerEventCategory.Step => GetFilter(DebugOutputFilterGroup.Steps),
            DebuggerEventCategory.ScriptOutput or DebuggerEventCategory.NativeStdout or DebuggerEventCategory.NativeStderr => GetFilter(DebugOutputFilterGroup.Output),
            DebuggerEventCategory.HostOutput => GetFilter(DebugOutputFilterGroup.Host),
            DebuggerEventCategory.Warning => GetFilter(DebugOutputFilterGroup.Warnings),
            DebuggerEventCategory.Verbose => GetFilter(DebugOutputFilterGroup.Verbose),
            DebuggerEventCategory.Debug => GetFilter(DebugOutputFilterGroup.Debug),
            DebuggerEventCategory.Information => GetFilter(DebugOutputFilterGroup.Information),
            DebuggerEventCategory.Error => GetFilter(DebugOutputFilterGroup.Errors),
            DebuggerEventCategory.Exception => GetFilter(DebugOutputFilterGroup.Exceptions),
            _ => false
        };
    }

    private static bool IsScriptOutput(DebuggerEventCategory category) =>
        category is DebuggerEventCategory.ScriptOutput or DebuggerEventCategory.NativeStdout or DebuggerEventCategory.NativeStderr;

    private void RefreshFilter() => _view.Refresh();
}

public sealed class DebugOutputCategoryBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var category = value is DebuggerEventCategory typedCategory
            ? typedCategory
            : DebuggerEventCategory.Information;
        var resourceKey = GetResourceKey(category);
        return System.Windows.Application.Current?.TryFindResource(resourceKey) as Brush
            ?? Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public static string GetResourceKey(DebuggerEventCategory category) =>
        category switch
        {
            DebuggerEventCategory.DebuggerLifecycle or DebuggerEventCategory.Step => "Theme.Debugger.Debugger.Foreground",
            DebuggerEventCategory.Breakpoint => "Theme.Debugger.Breakpoint.Foreground",
            DebuggerEventCategory.Warning or DebuggerEventCategory.AdapterWarning => "Theme.Debugger.Warning.Foreground",
            DebuggerEventCategory.Error or DebuggerEventCategory.Exception => "Theme.Debugger.Error.Foreground",
            DebuggerEventCategory.Debug => "Theme.Debugger.Debug.Foreground",
            DebuggerEventCategory.Verbose => "Theme.Debugger.Verbose.Foreground",
            DebuggerEventCategory.Information => "Theme.Debugger.Information.Foreground",
            _ => "Theme.Debugger.Output.Foreground"
        };
}
