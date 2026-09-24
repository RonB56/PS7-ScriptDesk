using System.Text.RegularExpressions;

namespace PS7ScriptDesk.Application.Diagnostics;

internal readonly record struct MarkerSpanCompletion(
    long MarkerStartReadId,
    long MarkerEndReadId,
    long MarkerStartChunkId,
    long MarkerEndChunkId,
    int MarkerSpanReadCount,
    int Generation,
    bool MarkerStartedBeforeInterrupt,
    bool MarkerCompletedAfterInterrupt,
    bool MarkerCrossedPromptRead,
    bool MarkerEntirelyAfterPrompt,
    bool MarkerEntirelyAfterInterrupt);

internal readonly record struct RollingMarkerObservation(
    bool MarkerFragmentStarted,
    bool MarkerFragmentContinued,
    bool MarkerFragmentCompleted,
    int MatchLengthBefore,
    int MatchLengthAfter,
    bool PromptReadObserved,
    bool GenerationReset,
    int CompletedMarkerCount,
    MarkerSpanCompletion? LastCompletion)
{
    public bool HasContinuitySignal =>
        MarkerFragmentStarted ||
        MarkerFragmentContinued ||
        MarkerFragmentCompleted ||
        GenerationReset;
}

/// <summary>
/// Bounded, privacy-preserving matcher for the exact C.2C fixture marker.
/// It retains only a KMP prefix length and marker identity metadata; payload text
/// and terminal chunks are never retained.
/// </summary>
internal sealed class RollingInterruptMarkerClassifier
{
    internal const string Marker = "PHASEB_INTERRUPT_TICK";
    private static readonly Regex PromptMarker = new(@"PS\s+[^>\r\n]{1,256}>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly int[] PrefixTable = BuildPrefixTable();

    private int _generation;
    private int _matchedPrefixLength;
    private long _markerStartReadId;
    private long _markerStartChunkId;
    private long _lastReadId;
    private int _markerSpanReadCount;
    private bool _markerStartedBeforeInterrupt;
    private bool _markerCompletedAfterInterrupt;
    private bool _markerCrossedPromptRead;
    private bool _markerEntirelyAfterPrompt;
    private bool _markerEntirelyAfterInterrupt;
    private bool _interruptObserved;
    private long? _lastPromptReadId;
    private bool _promptReadObservedForActiveMarker;

    public void ResetForSession(int generation)
    {
        _generation = generation;
        _matchedPrefixLength = 0;
        _markerStartReadId = 0;
        _markerStartChunkId = 0;
        _lastReadId = 0;
        _markerSpanReadCount = 0;
        _markerStartedBeforeInterrupt = false;
        _markerCompletedAfterInterrupt = false;
        _markerCrossedPromptRead = false;
        _markerEntirelyAfterPrompt = false;
        _markerEntirelyAfterInterrupt = false;
        _interruptObserved = false;
        _lastPromptReadId = null;
        _promptReadObservedForActiveMarker = false;
    }

    public void MarkInterruptRequested(int generation)
    {
        if (_generation != generation)
        {
            ResetForSession(generation);
        }

        _interruptObserved = true;
    }

    public void ObservePromptRead(int generation, long readId)
    {
        if (_generation != generation)
        {
            ResetForSession(generation);
        }

        _lastPromptReadId = readId;
        if (_matchedPrefixLength > 0)
        {
            _promptReadObservedForActiveMarker = true;
        }
    }

    public RollingMarkerObservation ProcessRead(
        int generation,
        long readId,
        long chunkId,
        string payload,
        Action<MarkerSpanCompletion>? onCompleted = null)
    {
        var generationReset = false;
        if (_generation != generation)
        {
            ResetForSession(generation);
            generationReset = true;
        }

        var matchLengthBefore = _matchedPrefixLength;
        var promptReadObserved = PromptMarker.IsMatch(payload);
        var fragmentStarted = false;
        var hadActiveFragmentAtReadStart = _matchedPrefixLength > 0;
        var fragmentContinued = false;
        var fragmentCompleted = false;
        var completedMarkerCount = 0;
        MarkerSpanCompletion? lastCompletion = null;

        if (promptReadObserved)
        {
            ObservePromptRead(generation, readId);
        }

        for (var index = 0; index < payload.Length; index++)
        {
            var value = payload[index];
            while (_matchedPrefixLength > 0 && Marker[_matchedPrefixLength] != value)
            {
                _matchedPrefixLength = PrefixTable[_matchedPrefixLength - 1];
            }

            if (Marker[_matchedPrefixLength] == value)
            {
                if (_matchedPrefixLength == 0)
                {
                    fragmentStarted = true;
                    _markerStartReadId = readId;
                    _markerStartChunkId = chunkId;
                    _markerSpanReadCount = 1;
                    _markerStartedBeforeInterrupt = !_interruptObserved;
                    _markerCompletedAfterInterrupt = false;
                    _markerCrossedPromptRead = false;
                    _markerEntirelyAfterPrompt = _lastPromptReadId.HasValue && _lastPromptReadId.Value < readId;
                    _markerEntirelyAfterInterrupt = _interruptObserved;
                    _promptReadObservedForActiveMarker = false;
                }

                _matchedPrefixLength++;
                if (_lastReadId != 0 && _lastReadId != readId && _matchedPrefixLength > 1)
                {
                    _markerSpanReadCount++;
                }
            }

            if (_matchedPrefixLength == Marker.Length)
            {
                fragmentCompleted = true;
                completedMarkerCount++;
                _markerCompletedAfterInterrupt = _interruptObserved;
                _markerCrossedPromptRead = _promptReadObservedForActiveMarker;
                var completion = new MarkerSpanCompletion(
                    _markerStartReadId,
                    readId,
                    _markerStartChunkId,
                    chunkId,
                    _markerSpanReadCount,
                    generation,
                    _markerStartedBeforeInterrupt,
                    _markerCompletedAfterInterrupt,
                    _markerCrossedPromptRead,
                    _markerEntirelyAfterPrompt,
                    _markerEntirelyAfterInterrupt);
                lastCompletion = completion;
                onCompleted?.Invoke(completion);

                _matchedPrefixLength = PrefixTable[^1];
                _markerSpanReadCount = 0;
                _promptReadObservedForActiveMarker = false;
            }

            _lastReadId = readId;
        }

        if (hadActiveFragmentAtReadStart && _matchedPrefixLength > 0 && _lastReadId != 0 && _lastReadId != readId)
        {
            fragmentContinued = true;
            _markerSpanReadCount = Math.Max(_markerSpanReadCount, 2);
        }

        return new RollingMarkerObservation(
            fragmentStarted,
            fragmentContinued,
            fragmentCompleted,
            matchLengthBefore,
            _matchedPrefixLength,
            promptReadObserved,
            generationReset,
            completedMarkerCount,
            lastCompletion);
    }

    private static int[] BuildPrefixTable()
    {
        var prefix = new int[Marker.Length];
        for (var index = 1; index < Marker.Length; index++)
        {
            var candidate = prefix[index - 1];
            while (candidate > 0 && Marker[index] != Marker[candidate])
            {
                candidate = prefix[candidate - 1];
            }

            if (Marker[index] == Marker[candidate])
            {
                candidate++;
            }

            prefix[index] = candidate;
        }

        return prefix;
    }
}
