namespace PS7ScriptDesk.UI.ViewModels;

/// <summary>Serializes editor breakpoint changes and commits them only after required debugger synchronization.</summary>
public sealed class BreakpointMutationCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<BreakpointMutationResult> MutateAsync(
        IReadOnlyList<EditorTabViewModel> openTabs,
        EditorTabViewModel? target,
        int lineNumber,
        BreakpointMutationKind kind,
        Func<BreakpointDebugContext> currentContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openTabs);
        ArgumentNullException.ThrowIfNull(currentContext);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (kind != BreakpointMutationKind.ClearAll &&
                (target is null || !openTabs.Contains(target) || lineNumber < 1 && kind != BreakpointMutationKind.ClearDocument))
                return BreakpointMutationResult.Rejected;

            var context = currentContext();
            var activeTab = context.ActiveTab;
            if (context.SessionId.HasValue && activeTab is null)
                return BreakpointMutationResult.Rejected;
            var affectsActiveSource = context.SessionId.HasValue && activeTab is not null &&
                (kind == BreakpointMutationKind.ClearAll || ReferenceEquals(target, activeTab));
            if (affectsActiveSource && (!context.CanChangeActiveSource || context.Synchronize is null ||
                !openTabs.Contains(activeTab)))
                return BreakpointMutationResult.Rejected;

            var existing = target?.HasBreakpoint(lineNumber) == true;
            if (kind == BreakpointMutationKind.Remove && !existing)
                return BreakpointMutationResult.Rejected;
            if (kind == BreakpointMutationKind.ClearDocument && target!.BreakpointLineNumbers.Count == 0 ||
                kind == BreakpointMutationKind.ClearAll && openTabs.All(tab => tab.BreakpointLineNumbers.Count == 0))
                return new BreakpointMutationResult(true, 0, false);

            if (affectsActiveSource)
            {
                var nextLines = activeTab!.GetEnabledBreakpointLines().ToHashSet();
                if (kind == BreakpointMutationKind.ClearAll || kind == BreakpointMutationKind.ClearDocument)
                    nextLines.Clear();
                else if (kind == BreakpointMutationKind.Remove || existing)
                    nextLines.Remove(lineNumber);
                else
                    nextLines.Add(lineNumber);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await context.Synchronize!(nextLines.Order().ToArray(), timeout.Token);
                var current = currentContext();
                if (current.SessionId != context.SessionId || !ReferenceEquals(current.ActiveTab, activeTab) ||
                    !current.CanChangeActiveSource || !openTabs.Contains(activeTab))
                    return BreakpointMutationResult.Rejected;
            }

            if (kind == BreakpointMutationKind.ClearAll)
                return new BreakpointMutationResult(true, BreakpointBulkOperations.ClearAll(openTabs), false);
            if (kind == BreakpointMutationKind.ClearDocument)
                return new BreakpointMutationResult(true, target!.ClearBreakpoints(), false);
            if (kind == BreakpointMutationKind.Remove)
                return new BreakpointMutationResult(true, target!.RemoveBreakpoint(lineNumber) ? 1 : 0, false);
            var added = target!.ToggleBreakpoint(lineNumber);
            return new BreakpointMutationResult(true, 1, added);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public enum BreakpointMutationKind { Toggle, Remove, ClearDocument, ClearAll }

public sealed record BreakpointDebugContext(
    Guid? SessionId,
    EditorTabViewModel? ActiveTab,
    bool CanChangeActiveSource,
    Func<IReadOnlyList<int>, CancellationToken, Task>? Synchronize);

public sealed record BreakpointMutationResult(bool Accepted, int ChangedCount, bool Added)
{
    public static BreakpointMutationResult Rejected { get; } = new(false, 0, false);
}
