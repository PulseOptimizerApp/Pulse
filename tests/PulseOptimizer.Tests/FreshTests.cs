using Pulse.Core.Fresh;
using Xunit;

namespace PulseOptimizer.Tests;

public class FreshTests
{
    const long GB = 1024L * 1024 * 1024;

    static Snapshot Snap(string[] startup, double boot, int procs, long freeGb, long clutterGb = 0) => new()
    {
        Date = DateTime.Now, StartupApps = startup.ToList(), BootSeconds = boot, Processes = procs,
        FreeBytes = freeGb * GB, TotalBytes = 500 * GB, ClutterBytes = clutterGb * GB,
    };

    [Fact]
    public void Same_pc_reports_nothing_wrong()
    {
        var b = Snap(new[] { "Discord", "Steam" }, 20, 180, 200);
        Assert.All(Freshness.Compare(b, Snap(new[] { "Discord", "Steam" }, 21, 185, 198)), d => Assert.Equal(DriftKind.Good, d.Kind));
    }

    [Fact]
    public void New_startup_apps_are_named()
    {
        var b = Snap(new[] { "Discord" }, 20, 180, 200);
        var d = Freshness.Compare(b, Snap(new[] { "Discord", "Spotify", "Epic Games Launcher", "Razer Synapse" }, 20, 180, 200)).First();
        Assert.Equal(DriftKind.Problem, d.Kind);
        Assert.Contains("3 new apps", d.Title);
        Assert.Contains("Spotify", d.Detail);
        Assert.Equal("page:startup", d.ActionKey);
    }

    [Fact]
    public void Slower_boot_and_full_drive_are_flagged()
    {
        var b = Snap(new[] { "A" }, 20, 180, 200);
        var drifts = Freshness.Compare(b, Snap(new[] { "A" }, 34, 240, 40, 3));
        Assert.Contains(drifts, d => d.Title.StartsWith("Boot time is 70% slower") && d.Kind == DriftKind.Problem);
        Assert.Contains(drifts, d => d.Title == "Your drive is nearly full");
        Assert.Contains(drifts, d => d.Title.Contains("more background processes"));
        Assert.Contains(drifts, d => d.Title.Contains("of temporary files"));
        Assert.Equal(DriftKind.Problem, drifts[0].Kind); // most important first
    }

    [Fact]
    public void No_baseline_means_no_drift() => Assert.Empty(Freshness.Compare(null, Snap(new string[0], 20, 100, 100)));
}
