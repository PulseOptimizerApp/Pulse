using Pulse.Core.Reaction;
using Xunit;

namespace PulseOptimizer.Tests;

public class ReactionTests
{
    [Fact]
    public void Waits_are_random_and_never_predictable_short()
    {
        var t = new ReactionTrainer(ReactionMode.Simple, seed: 42);
        var waits = Enumerable.Range(0, 200).Select(_ => t.Next().WaitMs).ToList();
        Assert.All(waits, w => Assert.InRange(w, 1200, 3500));
        Assert.True(waits.Distinct().Count() > 150);
    }

    [Fact]
    public void Warm_up_tries_do_not_count_and_the_median_ignores_outliers()
    {
        var t = new ReactionTrainer(ReactionMode.Simple, seed: 1);
        for (int i = 0; i < ReactionTrainer.WarmUp; i++) t.Record(t.Next(), 500);
        foreach (var ms in new double[] { 200, 210, 190, 205, 900 }) t.Record(t.Next(), ms);
        var s = t.Summary();
        Assert.Equal(5, s.Trials);
        Assert.Equal(205, s.MedianMs);
        Assert.Equal(190, s.BestMs);
    }

    [Fact]
    public void Clicking_before_the_signal_or_impossibly_fast_is_a_mistake()
    {
        var t = new ReactionTrainer(ReactionMode.Simple, seed: 1);
        for (int i = 0; i < ReactionTrainer.WarmUp; i++) t.Record(t.Next(), 300);
        Assert.Equal(TrialResult.TooEarly, t.Record(t.Next(), -1));
        Assert.Equal(TrialResult.TooEarly, t.Record(t.Next(), 60));
        Assert.Equal(2, t.Mistakes);
        Assert.Empty(t.Times);
    }

    [Fact]
    public void Choice_needs_the_right_side()
    {
        var t = new ReactionTrainer(ReactionMode.Choice, seed: 3);
        var trial = t.Next();
        Assert.NotEqual(0, trial.Side);
        Assert.Equal(TrialResult.WrongSide, t.Record(trial, 300, -trial.Side));
        var next = t.Next();
        Assert.Equal(TrialResult.Hit, t.Record(next, 300, next.Side));
    }

    [Fact]
    public void Go_no_go_rewards_holding_still_and_gets_harder()
    {
        var t = new ReactionTrainer(ReactionMode.GoNoGo, seed: 5);
        var nogo = new ReactionTrial(1500, 0, false, 0.5, 0.5, 100, 900);
        Assert.Equal(TrialResult.CorrectlyHeld, t.Record(nogo, 0, pressed: false));
        Assert.Equal(TrialResult.ClickedNoGo, t.Record(nogo, 300));
        var go = nogo with { Go = true };
        for (int i = 0; i < 9; i++) t.Record(go, 300);
        Assert.True(t.Level >= 3);
        Assert.True(t.Next().WindowMs < 900);
    }

    [Fact]
    public void Small_day_to_day_changes_are_called_normal()
    {
        var history = Enumerable.Range(0, 5).Select(i => new ReactionSession { Mode = ReactionMode.Simple, MedianMs = 200 + i % 2 * 6, Trials = 20, When = DateTime.Now.AddDays(-i - 1) }).ToList();
        Assert.Contains("normal range", ReactionTrainer.Compare(new ReactionSession { Mode = ReactionMode.Simple, MedianMs = 204, Trials = 20 }, history));
        Assert.Contains("Faster", ReactionTrainer.Compare(new ReactionSession { Mode = ReactionMode.Simple, MedianMs = 170, Trials = 20 }, history));
        Assert.Contains("Slower", ReactionTrainer.Compare(new ReactionSession { Mode = ReactionMode.Simple, MedianMs = 240, Trials = 20 }, history));
    }
}
