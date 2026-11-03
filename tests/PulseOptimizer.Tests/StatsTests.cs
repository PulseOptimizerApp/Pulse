using Pulse.Core.Measurement;
using Xunit;

namespace PulseOptimizer.Tests;

/// <summary>The measurement verdicts must match the protocol in the research doc.</summary>
public class StatsTests
{
    [Fact]
    public void Percentile_interpolates()
    {
        var s = new List<double> { 1, 2, 3, 4, 5 };
        Assert.Equal(3, Stats.Percentile(s, 0.5));
        Assert.Equal(5, Stats.Percentile(s, 1.0));
        Assert.Equal(1.5, Stats.Percentile(s, 0.125), 3);
    }

    [Fact]
    public void One_percent_low_comes_from_the_slowest_frames()
    {
        // 980 frames at 5 ms (200 FPS) and 20 slow frames at 20 ms (50 FPS).
        var frames = Enumerable.Repeat(5.0, 980).Concat(Enumerable.Repeat(20.0, 20)).ToList();
        var r = Stats.Summarize(frames, null);
        Assert.InRange(r.Low1Fps, 49, 51);
        Assert.True(r.Low1Fps < r.AvgFps);
        Assert.Equal(1000, r.Frames);
    }

    [Fact]
    public void Clear_improvement_is_helped()
    {
        var before = new List<double> { 100, 101, 99, 100, 100 };
        var after = new List<double> { 110, 111, 109, 110, 110 };
        Assert.Equal(Verdict.Helped, Stats.Compare("FPS", before, after, true, "FPS").Verdict);
    }

    [Fact]
    public void Clear_regression_is_hurt()
    {
        var before = new List<double> { 110, 111, 109, 110, 110 };
        var after = new List<double> { 100, 101, 99, 100, 100 };
        Assert.Equal(Verdict.Hurt, Stats.Compare("FPS", before, after, true, "FPS").Verdict);
    }

    [Fact]
    public void Difference_inside_the_noise_is_no_measurable_change()
    {
        var before = new List<double> { 100, 104, 97, 102, 99 };
        var after = new List<double> { 101, 98, 103, 100, 102 };
        Assert.Equal(Verdict.NoMeasurableChange, Stats.Compare("FPS", before, after, true, "FPS").Verdict);
    }

    [Fact]
    public void Small_but_consistent_gain_below_two_percent_is_not_a_win()
    {
        var before = new List<double> { 100, 100, 100, 100, 100 };
        var after = new List<double> { 101, 101, 101, 101, 101 }; // +1%: under the 2% floor
        Assert.Equal(Verdict.NoMeasurableChange, Stats.Compare("FPS", before, after, true, "FPS").Verdict);
    }

    [Fact]
    public void Lower_latency_counts_as_better()
    {
        var before = new List<double> { 30, 31, 29, 30, 30 };
        var after = new List<double> { 20, 21, 19, 20, 20 };
        Assert.Equal(Verdict.Helped, Stats.Compare("Latency", before, after, false, "ms").Verdict);
    }

    [Fact]
    public void Too_few_runs_is_not_enough_data()
    {
        Assert.Equal(Verdict.NotEnoughData, Stats.Compare("FPS", new List<double> { 1, 2 }, new List<double> { 3, 4 }, true, "FPS").Verdict);
    }
}
