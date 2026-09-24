using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Tests;

public sealed class RollingInterruptMarkerClassifierTests
{
    private const string Marker = "PHASEB_INTERRUPT_TICK";

    [Fact]
    public void CompleteMarkerInOneRead_IsCompletedWithOneReadSpan()
    {
        var classifier = CreateClassifier();
        var completions = new List<MarkerSpanCompletion>();

        var result = classifier.ProcessRead(1, 1, 1, Marker, completions.Add);

        Assert.True(result.MarkerFragmentStarted);
        Assert.True(result.MarkerFragmentCompleted);
        var completion = Assert.Single(completions);
        Assert.Equal(1, completion.MarkerStartReadId);
        Assert.Equal(1, completion.MarkerEndReadId);
        Assert.Equal(1, completion.MarkerSpanReadCount);
    }

    [Theory]
    [MemberData(nameof(AllSplitPositions))]
    public void EveryTwoReadSplit_ReconstructsExactlyOneMarker(int split)
    {
        var classifier = CreateClassifier();
        var completions = new List<MarkerSpanCompletion>();

        classifier.ProcessRead(1, 1, 1, Marker[..split], completions.Add);
        var result = classifier.ProcessRead(1, 2, 2, Marker[split..], completions.Add);

        Assert.True(result.MarkerFragmentCompleted);
        var completion = Assert.Single(completions);
        Assert.Equal(1, completion.MarkerStartReadId);
        Assert.Equal(2, completion.MarkerEndReadId);
        Assert.Equal(2, completion.MarkerSpanReadCount);
    }

    [Fact]
    public void ThreeReadsAndSeveralSmallReads_ReconstructMarker()
    {
        var classifier = CreateClassifier();
        var completions = new List<MarkerSpanCompletion>();

        classifier.ProcessRead(1, 1, 1, "PHASEB_", completions.Add);
        classifier.ProcessRead(1, 2, 2, "INTERRUPT_", completions.Add);
        classifier.ProcessRead(1, 3, 3, "TICK", completions.Add);

        Assert.Single(completions);
        Assert.Equal(3, completions[0].MarkerSpanReadCount);

        classifier.ResetForSession(1);
        completions.Clear();
        var readId = 0L;
        foreach (var value in Marker)
        {
            readId++;
            classifier.ProcessRead(1, readId, readId, value.ToString(), completions.Add);
        }

        Assert.Single(completions);
        Assert.Equal(Marker.Length, completions[0].MarkerSpanReadCount);
    }

    [Fact]
    public void NoiseAndMultipleMarkers_AreClassifiedWithoutPayloadRetention()
    {
        var classifier = CreateClassifier();
        var completions = new List<MarkerSpanCompletion>();

        var result = classifier.ProcessRead(1, 1, 1, $"noise-{Marker}-middle-{Marker}-tail", completions.Add);

        Assert.Equal(2, result.CompletedMarkerCount);
        Assert.Equal(2, completions.Count);
        Assert.All(completions, completion => Assert.Equal(1, completion.MarkerSpanReadCount));
    }

    [Fact]
    public void MarkerEndingAndAnotherBeginningAcrossBoundary_CompletesBoth()
    {
        var classifier = CreateClassifier();
        var completions = new List<MarkerSpanCompletion>();

        classifier.ProcessRead(1, 1, 1, Marker + "PHASEB_INTER", completions.Add);
        classifier.ProcessRead(1, 2, 2, "RUPT_TICK", completions.Add);

        Assert.Equal(2, completions.Count);
        Assert.Equal(2, completions[1].MarkerEndReadId);
    }

    [Fact]
    public void PrefixLikeAndMismatchingText_DoesNotFalseComplete()
    {
        var classifier = CreateClassifier();
        var completions = new List<MarkerSpanCompletion>();

        classifier.ProcessRead(1, 1, 1, "PHASEB_INTERX", completions.Add);
        classifier.ProcessRead(1, 2, 2, "RUPT_TICK", completions.Add);

        Assert.Empty(completions);
    }

    [Fact]
    public void PartialMarkerFollowedByMismatchingRead_DoesNotCompleteOrClaimContinuation()
    {
        var classifier = CreateClassifier();
        var completions = new List<MarkerSpanCompletion>();

        classifier.ProcessRead(1, 1, 1, "PHASEB_INTER", completions.Add);
        var result = classifier.ProcessRead(1, 2, 2, "X", completions.Add);

        Assert.False(result.MarkerFragmentContinued);
        Assert.Empty(completions);
    }

    [Fact]
    public void GenerationChangeBetweenFragments_BreaksContinuity()
    {
        var classifier = CreateClassifier();
        var completions = new List<MarkerSpanCompletion>();

        classifier.ProcessRead(1, 1, 1, "PHASEB_INTER", completions.Add);
        var result = classifier.ProcessRead(2, 2, 2, "RUPT_TICK", completions.Add);

        Assert.True(result.GenerationReset);
        Assert.Empty(completions);
    }

    [Fact]
    public void ExplicitSessionResetBetweenFragments_BreaksContinuity()
    {
        var classifier = CreateClassifier();
        var completions = new List<MarkerSpanCompletion>();

        classifier.ProcessRead(1, 1, 1, "PHASEB_INTER", completions.Add);
        classifier.ResetForSession(2);
        classifier.ProcessRead(2, 2, 2, "RUPT_TICK", completions.Add);

        Assert.Empty(completions);
    }

    [Fact]
    public void InterruptBetweenFragments_RecordsLifecycleRelationship()
    {
        var classifier = CreateClassifier();
        var completions = new List<MarkerSpanCompletion>();

        classifier.ProcessRead(1, 1, 1, "PHASEB_INTER", completions.Add);
        classifier.MarkInterruptRequested(1);
        classifier.ProcessRead(1, 2, 2, "RUPT_TICK", completions.Add);

        var completion = Assert.Single(completions);
        Assert.True(completion.MarkerStartedBeforeInterrupt);
        Assert.True(completion.MarkerCompletedAfterInterrupt);
        Assert.False(completion.MarkerEntirelyAfterInterrupt);
    }

    [Fact]
    public void PromptReadBetweenFragments_RecordsPromptCrossing()
    {
        var classifier = CreateClassifier();
        var completions = new List<MarkerSpanCompletion>();

        classifier.ProcessRead(1, 1, 1, "PHASEB_INTER", completions.Add);
        classifier.ObservePromptRead(1, 2);
        classifier.ProcessRead(1, 3, 3, "RUPT_TICK", completions.Add);

        var completion = Assert.Single(completions);
        Assert.True(completion.MarkerCrossedPromptRead);
        Assert.False(completion.MarkerEntirelyAfterPrompt);
    }

    [Fact]
    public void RepeatedHighVolumeMarkers_DoNotAccumulateUnboundedMatches()
    {
        var classifier = CreateClassifier();
        var completions = new List<MarkerSpanCompletion>();

        classifier.ProcessRead(1, 1, 1, string.Concat(Enumerable.Repeat(Marker, 10_000)), completions.Add);

        Assert.Equal(10_000, completions.Count);
        Assert.All(completions, completion => Assert.Equal(1, completion.MarkerSpanReadCount));
    }

    [Fact]
    public void TraceDisabledRawReadEntryPoint_IsBehaviorNeutral()
    {
        TerminalOutputCorrelationTrace.RecordRawRead(1, Marker, 1);
        TerminalOutputCorrelationTrace.ResetForSession(1);
        TerminalOutputCorrelationTrace.MarkInterruptRequested(1);
    }

    public static IEnumerable<object[]> AllSplitPositions() =>
        Enumerable.Range(1, Marker.Length - 1).Select(position => new object[] { position });

    private static RollingInterruptMarkerClassifier CreateClassifier()
    {
        var classifier = new RollingInterruptMarkerClassifier();
        classifier.ResetForSession(1);
        return classifier;
    }
}
