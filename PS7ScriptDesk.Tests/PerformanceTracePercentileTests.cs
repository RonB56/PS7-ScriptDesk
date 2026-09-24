using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Tests;

public sealed class PerformanceTracePercentileTests
{
    [Fact]
    public void Percentile_UsesSortedNearestRankForSmallSamples()
    {
        var values = new[] { 50d, 10d, 30d, 20d, 40d };

        Assert.Equal(30d, PerformanceTrace.Percentile(values, 0.50));
        Assert.Equal(50d, PerformanceTrace.Percentile(values, 0.95));
        Assert.Equal(50d, PerformanceTrace.Percentile(values, 0.99));
    }

    [Fact]
    public void Percentile_EmptySampleReturnsZero()
    {
        Assert.Equal(0d, PerformanceTrace.Percentile(Array.Empty<double>(), 0.95));
    }
}
