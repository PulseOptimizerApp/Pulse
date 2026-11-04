using Pulse.Core.Declutter;
using Xunit;

namespace PulseOptimizer.Tests;

public class SpaceGoalTests
{
    const long GB = 1L << 30;

    [Fact]
    public void Needed_is_what_is_missing_to_the_goal()
    {
        Assert.Equal(30 * GB, SpaceGoal.Needed(50 * GB, 20 * GB));
        Assert.Equal(0, SpaceGoal.Needed(50 * GB, 80 * GB));
    }

    [Fact]
    public void Nothing_is_picked_until_the_user_picks_it()
    {
        var item = new GoalItem { Kind = GoalKind.Safe, Title = "Temp", Bytes = GB };
        Assert.False(item.Selected);
        Assert.Equal(0, SpaceGoal.PickedBytes(new[] { item }));
    }

    [Fact]
    public void Recycled_files_only_count_when_the_bin_is_emptied_too()
    {
        var temp = new GoalItem { Kind = GoalKind.Safe, Bytes = 2 * GB, Selected = true };
        var video = new GoalItem { Kind = GoalKind.YourFiles, Bytes = 10 * GB, Selected = true };
        var bin = new GoalItem { Kind = GoalKind.RecycleBin, Bytes = 1 * GB };
        var all = new[] { temp, video, bin };
        Assert.Equal(2 * GB, SpaceGoal.PickedBytes(all));
        Assert.Equal(10 * GB, SpaceGoal.WaitingInBin(all));
        bin.Selected = true;
        Assert.Equal(13 * GB, SpaceGoal.PickedBytes(all));
        Assert.Equal(0, SpaceGoal.WaitingInBin(all));
    }

    [Fact]
    public async Task Cancelling_before_freeing_changes_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (done, failed, cancelled) = await SpaceGoal.Free(new[] { new GoalItem { Kind = GoalKind.Safe, Bytes = GB, Selected = true } }, null, cts.Token);
        Assert.True(cancelled);
        Assert.Equal(0, done + failed);
    }
}
