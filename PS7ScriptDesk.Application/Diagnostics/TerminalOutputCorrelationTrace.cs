using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>Opt-in C.2C correlation for the synthetic interrupt workload; payload text is never recorded.</summary>
public static class TerminalOutputCorrelationTrace
{
    private const string InterruptFixtureMarker = "PHASEB_INTERRUPT_TICK";
    private static readonly Regex PromptMarker = new(@"PS\s+[^>\r\n]{1,256}>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly RollingInterruptMarkerClassifier RollingMarkerClassifier = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, NumberedFixtureParser> NumberedParsers = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, int[]> RendererNumberedSequences = new();

    public static void RecordRendererAcknowledgement(int generation, int rendererGeneration, long rendererSequence)
    {
        if (!PerformanceTrace.IsEnabled) return;
        if (RendererNumberedSequences.TryRemove(rendererSequence, out var numbers))
        {
            foreach (var number in numbers)
            {
                PerformanceTrace.Record("instant", "TerminalC2CCorrelation", "NumberedFixtureStage", generation: generation,
                    properties: new Dictionary<string, object?>
                    {
                        ["stage"] = "RendererAcknowledgement",
                        ["numberedSequence"] = number,
                        ["rendererSequence"] = rendererSequence,
                        ["rendererGeneration"] = rendererGeneration,
                        ["contentOmitted"] = true
                    });
            }
        }
    }

    public static void Record(
        string stage,
        int generation,
        string payload,
        long? readId = null,
        long? chunkId = null,
        long? envelopeSequence = null,
        long? rendererSequence = null,
        int? queueDepth = null,
        int? pendingCharacters = null,
        bool? promptHeuristicMatched = null)
    {
        RecordCore(stage, generation, payload, readId, chunkId, envelopeSequence, rendererSequence, queueDepth, pendingCharacters, promptHeuristicMatched, force: false, additionalProperties: null);
    }

    public static void RecordRawRead(int generation, string payload, long readId)
    {
        if (!PerformanceTrace.IsEnabled || string.IsNullOrEmpty(payload)) return;

        var completions = 0;
        var observation = RollingMarkerClassifier.ProcessRead(
            generation,
            readId,
            readId,
            payload,
            completion =>
            {
                completions++;
                PerformanceTrace.Record(
                    "instant",
                    "TerminalC2CCorrelation",
                    "MarkerSpanCompleted",
                    generation: generation,
                    properties: new Dictionary<string, object?>
                    {
                        ["markerStartReadId"] = completion.MarkerStartReadId,
                        ["markerEndReadId"] = completion.MarkerEndReadId,
                        ["markerStartChunkId"] = completion.MarkerStartChunkId,
                        ["markerEndChunkId"] = completion.MarkerEndChunkId,
                        ["markerSpanReadCount"] = completion.MarkerSpanReadCount,
                        ["markerStartedBeforeInterrupt"] = completion.MarkerStartedBeforeInterrupt,
                        ["markerCompletedAfterInterrupt"] = completion.MarkerCompletedAfterInterrupt,
                        ["markerCrossedPromptRead"] = completion.MarkerCrossedPromptRead,
                        ["markerEntirelyAfterPrompt"] = completion.MarkerEntirelyAfterPrompt,
                        ["markerEntirelyAfterInterrupt"] = completion.MarkerEntirelyAfterInterrupt,
                        ["contentOmitted"] = true
                    });
            });

        var containsMarker = payload.Contains(InterruptFixtureMarker, StringComparison.Ordinal);
        var containsPrompt = observation.PromptReadObserved;
        var properties = new Dictionary<string, object?>
        {
            ["markerContinuityMatchLengthBefore"] = observation.MatchLengthBefore,
            ["markerContinuityMatchLengthAfter"] = observation.MatchLengthAfter,
            ["markerFragmentStarted"] = observation.MarkerFragmentStarted,
            ["markerFragmentContinued"] = observation.MarkerFragmentContinued,
            ["markerFragmentCompleted"] = observation.MarkerFragmentCompleted,
            ["markerContinuityGenerationReset"] = observation.GenerationReset,
            ["markerCompletedCount"] = completions,
            ["contentOmitted"] = true
        };

        RecordCore(
            "ConPtyRead",
            generation,
            payload,
            readId,
            readId,
            envelopeSequence: null,
            rendererSequence: null,
            queueDepth: null,
            pendingCharacters: null,
            promptHeuristicMatched: containsPrompt,
            force: observation.HasContinuitySignal,
            additionalProperties: properties);

        if (observation.MarkerFragmentStarted)
        {
            PerformanceTrace.Record("instant", "TerminalC2CCorrelation", "MarkerSpanStarted", generation: generation,
                properties: new Dictionary<string, object?>
                {
                    ["markerStartReadId"] = readId,
                    ["markerStartChunkId"] = readId,
                    ["markerContinuityMatchLength"] = observation.MatchLengthAfter,
                    ["contentOmitted"] = true
                });
        }

        if (observation.MarkerFragmentContinued)
        {
            PerformanceTrace.Record("instant", "TerminalC2CCorrelation", "MarkerSpanContinued", generation: generation,
                properties: new Dictionary<string, object?>
                {
                    ["readId"] = readId,
                    ["chunkId"] = readId,
                    ["markerContinuityMatchLength"] = observation.MatchLengthAfter,
                    ["contentOmitted"] = true
                });
        }
    }

    public static void ResetForSession(int generation)
    {
        if (!PerformanceTrace.IsEnabled) return;
        RollingMarkerClassifier.ResetForSession(generation);
        NumberedParsers.Clear();
        RendererNumberedSequences.Clear();
        PerformanceTrace.Record("milestone", "TerminalC2CCorrelation", "MarkerContinuityState", generation: generation,
            properties: new Dictionary<string, object?>
            {
                ["state"] = "reset",
                ["reason"] = "session-generation",
                ["contentOmitted"] = true
            });
    }

    public static void MarkInterruptRequested(int generation)
    {
        if (!PerformanceTrace.IsEnabled) return;
        RollingMarkerClassifier.MarkInterruptRequested(generation);
        PerformanceTrace.Record("milestone", "TerminalC2CCorrelation", "MarkerContinuityState", generation: generation,
            properties: new Dictionary<string, object?>
            {
                ["state"] = "interrupt-observed",
                ["contentOmitted"] = true
            });
    }

    private static void RecordCore(
        string stage,
        int generation,
        string payload,
        long? readId,
        long? chunkId,
        long? envelopeSequence,
        long? rendererSequence,
        int? queueDepth,
        int? pendingCharacters,
        bool? promptHeuristicMatched,
        bool force,
        IReadOnlyDictionary<string, object?>? additionalProperties)
    {
        if (!PerformanceTrace.IsEnabled || string.IsNullOrEmpty(payload)) return;
        try
        {
            var containsInterruptMarker = payload.Contains(InterruptFixtureMarker, StringComparison.Ordinal);
            var containsPromptMarker = promptHeuristicMatched == true || PromptMarker.IsMatch(payload);
            if (!force && !containsInterruptMarker && !containsPromptMarker) return;
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..16];
            var numberedRecords = NumberedParsers.GetOrAdd(stage, static _ => new NumberedFixtureParser()).Process(payload);
            var properties = new Dictionary<string, object?>
            {
                ["readId"] = readId,
                ["chunkId"] = chunkId,
                ["envelopeSequence"] = envelopeSequence,
                ["rendererSequence"] = rendererSequence,
                ["queueDepth"] = queueDepth,
                ["pendingCharacters"] = pendingCharacters,
                ["payloadFingerprint"] = fingerprint,
                ["containsInterruptFixtureMarker"] = containsInterruptMarker,
                ["promptMarkerOrHeuristicMatched"] = containsPromptMarker,
                ["numberedFixtureCount"] = numberedRecords.Count,
                ["numberedFixtureSequences"] = numberedRecords.Count == 0 ? null : numberedRecords.Select(static item => item.SequenceNumber).ToArray(),
                ["contentOmitted"] = true
            };
            if (additionalProperties is not null)
            {
                foreach (var property in additionalProperties)
                {
                    properties[property.Key] = property.Value;
                }
            }

            PerformanceTrace.Record(
                "instant",
                "TerminalC2CCorrelation",
                stage,
                generation: generation,
                payloadChars: payload.Length,
                properties: properties);

            foreach (var numberedRecord in numberedRecords)
            {
                PerformanceTrace.Record(
                    "instant",
                    "TerminalC2CCorrelation",
                    "NumberedFixtureStage",
                    generation: generation,
                    payloadChars: payload.Length,
                    properties: new Dictionary<string, object?>
                    {
                        ["stage"] = stage,
                        ["numberedSequence"] = numberedRecord.SequenceNumber,
                        ["readId"] = readId,
                        ["chunkId"] = chunkId,
                        ["envelopeSequence"] = envelopeSequence,
                        ["rendererSequence"] = rendererSequence,
                        ["resizeGeneration"] = null,
                        ["outputWatermark"] = envelopeSequence ?? rendererSequence,
                        ["payloadFingerprint"] = fingerprint,
                        ["correlationIdentity"] = PerformanceTrace.RunId,
                        ["contentOmitted"] = true
                    });
            }
            if (rendererSequence is { } sequence && numberedRecords.Count > 0)
            {
                RendererNumberedSequences[sequence] = numberedRecords.Select(static item => item.SequenceNumber).ToArray();
            }
        }
        catch
        {
            // Correlation diagnostics must never affect terminal output.
        }
    }
}
