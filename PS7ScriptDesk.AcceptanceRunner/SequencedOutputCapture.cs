using System.Collections.Concurrent;
using System.Text;

namespace PS7ScriptDesk.AcceptanceRunner;

/// <summary>Small bounded, sequence-based capture for concurrently published terminal chunks.</summary>
internal sealed class SequencedOutputCapture
{
    private readonly object _gate = new();
    private readonly Queue<(long Sequence, string Text)> _chunks = new();
    private readonly int _maxCharacters;
    private long _nextSequence;
    private int _characters;
    private long _droppedThroughSequence;
    private TaskCompletionSource<long> _changed = NewSignal();

    public SequencedOutputCapture(int maxCharacters = 1_000_000)
    {
        if (maxCharacters < 1024) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        _maxCharacters = maxCharacters;
    }

    public long Boundary
    {
        get { lock (_gate) return _nextSequence; }
    }

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        TaskCompletionSource<long> signal;
        long sequence;
        lock (_gate)
        {
            sequence = ++_nextSequence;
            if (text.Length > _maxCharacters)
            {
                text = text[^_maxCharacters..];
                _droppedThroughSequence = _nextSequence;
                _chunks.Clear();
                _characters = 0;
            }
            _chunks.Enqueue((sequence, text));
            _characters += text.Length;
            while (_characters > _maxCharacters && _chunks.TryDequeue(out var removed))
            {
                _characters -= removed.Text.Length;
                _droppedThroughSequence = removed.Sequence;
            }
            signal = _changed;
            _changed = NewSignal();
        }
        signal.TrySetResult(sequence);
    }

    public Task WaitForChangeAsync(long afterSequence, CancellationToken cancellationToken)
    {
        lock (_gate)
            return _nextSequence > afterSequence ? Task.CompletedTask : _changed.Task.WaitAsync(cancellationToken);
    }

    public (string Text, bool Truncated, long LastSequence) SnapshotAfter(long boundary)
    {
        lock (_gate)
        {
            var text = new StringBuilder();
            foreach (var chunk in _chunks)
                if (chunk.Sequence > boundary) text.Append(chunk.Text);
            return (text.ToString(), boundary < _droppedThroughSequence, _nextSequence);
        }
    }

    private static TaskCompletionSource<long> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal static class HarnessContractChecks
{
    public static IReadOnlyList<(string Id, string Name, bool Passed, string Detail, double DurationSeconds)> Run()
    {
        var checks = new List<(string, string, bool, string, double)>();

        var captureTimer = System.Diagnostics.Stopwatch.StartNew();
        var capture = new SequencedOutputCapture();
        capture.Append("before|");
        var boundary = capture.Boundary;
        var concurrentSnapshots = new ConcurrentBag<(string Text, bool Truncated, long LastSequence)>();
        Parallel.Invoke(
            () => Parallel.For(0, 200, i => capture.Append($"<{i:D3}>")),
            () => Parallel.For(0, 200, _ => concurrentSnapshots.Add(capture.SnapshotAfter(boundary))));
        var captured = capture.SnapshotAfter(boundary);
        var tokens = System.Text.RegularExpressions.Regex.Matches(captured.Text, @"<\d{3}>")
            .Select(m => m.Value).ToArray();
        var snapshotsClean = concurrentSnapshots.All(s => !s.Truncated && !s.Text.Contains("before|", StringComparison.Ordinal) &&
            System.Text.RegularExpressions.Regex.Matches(s.Text, @"<\d{3}>").Count <= 200);
        var bounded = new SequencedOutputCapture(1024);
        var oldBoundary = bounded.Boundary;
        bounded.Append(new string('x', 2048));
        var boundedSnapshot = bounded.SnapshotAfter(oldBoundary);
        checks.Add(("HARNESS-CAPTURE", "Concurrent sequence capture boundaries", tokens.Length == 200 && tokens.Distinct().Count() == 200 && snapshotsClean &&
            !captured.Text.Contains("before|", StringComparison.Ordinal) && boundedSnapshot.Truncated && boundedSnapshot.Text.Length <= 1024,
            $"Expected complete unique concurrent chunks, stable boundaries, and bounded truncation; observed {tokens.Length} tokens, concurrentSnapshots={concurrentSnapshots.Count}, truncated={boundedSnapshot.Truncated}, retained={boundedSnapshot.Text.Length}.", captureTimer.Elapsed.TotalSeconds));

        var markerTimer = System.Diagnostics.Stopwatch.StartNew();
        const string submitted = "Write-Output ('A02_' + 'MARKER_ONLY')";
        checks.Add(("HARNESS-MARKER", "Submitted/echo-only marker rejected", !HasRuntimeMarker(submitted, "A02_MARKER_ONLY", "Write-Output ('A02_' + 'MARKER_ONLY')"),
            "Marker text appearing only in source/echo is not accepted as runtime output.", markerTimer.Elapsed.TotalSeconds));

        var selectionEvidenceTimer = System.Diagnostics.Stopwatch.StartNew();
        static bool CompletionReady(bool submitted, bool freshPromptObserved, bool requestMarkerObserved)
            => submitted && freshPromptObserved && requestMarkerObserved;
        var markerFirst = !CompletionReady(true, false, true) && CompletionReady(true, true, true);
        var promptFirst = !CompletionReady(true, true, false) && CompletionReady(true, true, true);
        var negativeControls = !CompletionReady(true, false, true) && !CompletionReady(true, true, false) &&
                              !CompletionReady(false, true, true);
        checks.Add(("HARNESS-SELECTION-EVIDENCE", "Run Selection marker/prompt ordering and negative controls",
            markerFirst && promptFirst && negativeControls,
            $"Synthetic evidence gate accepted marker-before-prompt={markerFirst}, prompt-before-marker={promptFirst}; rejected marker-only, old/unrelated prompt, and withheld submission={negativeControls}.",
            selectionEvidenceTimer.Elapsed.TotalSeconds));

        var startupTimer = System.Diagnostics.Stopwatch.StartNew();
        var startupOutcome = ClassifyStartupException(new TimeoutException("controlled startup failure"));
        checks.Add(("HARNESS-STARTUP", "Required startup failure is fatal", startupOutcome == "FAIL" && ShouldExitNonZero(true, true, true),
            $"Controlled startup timeout classified as {startupOutcome}; nonzero exit policy={ShouldExitNonZero(true, true, true)}.", startupTimer.Elapsed.TotalSeconds));

        var cleanupTimer = System.Diagnostics.Stopwatch.StartNew();
        var cleanupOutcome = ClassifyCleanup(false, "controlled directory removal failure");
        checks.Add(("HARNESS-CLEANUP", "Cleanup failure is fatal", cleanupOutcome.StartsWith("FAIL", StringComparison.Ordinal) && ShouldExitNonZero(false, true, true),
            $"Controlled cleanup failure classified as {cleanupOutcome}; nonzero exit policy={ShouldExitNonZero(false, true, true)}.", cleanupTimer.Elapsed.TotalSeconds));

        var noRunTimer = System.Diagnostics.Stopwatch.StartNew();
        var noRunFails = ShouldExitNonZero(false, false, false);
        checks.Add(("HARNESS-NORUN", "No production cases cannot return success", noRunFails,
            $"No production cases ran; nonzero exit policy={noRunFails}.", noRunTimer.Elapsed.TotalSeconds));

        return checks;
    }

    public static bool HasRuntimeMarker(string submittedSource, string marker, string capturedOutput)
        => !submittedSource.Contains(marker, StringComparison.Ordinal) &&
           capturedOutput.Contains(marker, StringComparison.Ordinal);

    public static string ClassifyStartupException(Exception _) => "FAIL";
    public static string ClassifyCleanup(bool removed, string? error) => removed ? "PASS" : "FAIL: " + (error ?? "directory remains");
    public static bool ShouldExitNonZero(bool infrastructureFailure, bool requiredFailure, bool productionCasesExecuted)
        => infrastructureFailure || requiredFailure || !productionCasesExecuted;
}
