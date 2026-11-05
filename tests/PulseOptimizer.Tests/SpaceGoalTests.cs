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

public class UpdaterTests
{
    [Theory]
    [InlineData("https://github.com/PulseOptimizer-app/PulseOptimizer/releases/tag/v0.3.1", "0.3.1")]
    [InlineData("https://github.com/PulseOptimizer-app/PulseOptimizer/releases/tag/0.4.0", "0.4.0")]
    [InlineData("https://github.com/PulseOptimizer-app/PulseOptimizer/releases", null)]
    public void Version_comes_from_the_latest_release_redirect(string url, string expected) =>
        Assert.Equal(expected, Pulse.Core.Updater.TagFromUrl(url));

    [Fact]
    public void Only_newer_versions_are_offered_and_dev_builds_never_update()
    {
        Assert.True(Pulse.Core.Updater.IsNewer("0.3.1", "0.3.0"));
        Assert.True(Pulse.Core.Updater.IsNewer("0.10.0", "0.9.9"));
        Assert.False(Pulse.Core.Updater.IsNewer("0.3.0", "0.3.0"));
        Assert.False(Pulse.Core.Updater.IsNewer("0.2.9", "0.3.0"));
        Assert.False(Pulse.Core.Updater.IsNewer("9.9.9", "0.0.0"));
    }
}

public class StretchTests
{
    [Fact]
    public void A_4_3_stretched_clip_on_a_16_9_screen_is_marked_to_show_16_9()
    {
        var sar = Pulse.Core.Clips.ClipLibrary.StretchSar(1440, 1080, 16 / 9.0);
        Assert.Equal("4/3", sar);
        Assert.Contains("h264_metadata=sample_aspect_ratio=4/3", Pulse.Core.Clips.ClipLibrary.StretchCopyArgs("h264", sar));
        Assert.Null(Pulse.Core.Clips.ClipLibrary.StretchSar(1920, 1080, 16 / 9.0));
        Assert.Equal("", Pulse.Core.Clips.ClipLibrary.StretchCopyArgs("h264", null));
    }
}

public class ClipSoundTests
{
    [Fact]
    public void Clip_sound_is_a_short_clean_stereo_wav()
    {
        var wav = Pulse.Ui.Feedback.Blip();
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(2, BitConverter.ToInt16(wav, 22));          // stereo
        Assert.Equal(48000, BitConverter.ToInt32(wav, 24));      // 48 kHz
        double seconds = BitConverter.ToInt32(wav, 40) / 4.0 / 48000;
        Assert.InRange(seconds, 0.5, 0.8);
        short last = BitConverter.ToInt16(wav, wav.Length - 2);
        Assert.InRange(Math.Abs((int)last), 0, 50);             // fades out: no click at the end
        var shot = Pulse.Ui.Feedback.Shutter();
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(shot, 0, 4));
        Assert.NotEqual(wav.Length, shot.Length);                 // a different sound from the clip one
    }
}

public class LeftoverAppTests
{
    [Fact]
    public void MissingUninstallerIsSpotted()
    {
        Assert.True(Pulse.Core.Declutter.DeclutterEngine.UninstallerMissing(@"""C:\Games\Gone Game 7Q2\unins000.exe"" /SILENT"));
        Assert.True(Pulse.Core.Declutter.DeclutterEngine.UninstallerMissing(@"C:\Games\Gone Game 7Q2\unins000.exe"));
    }

    [Fact]
    public void WindowsInstallerAndPathCommandsAreNotJudged()
    {
        Assert.False(Pulse.Core.Declutter.DeclutterEngine.UninstallerMissing("MsiExec.exe /X{12345678-1234-1234-1234-123456789012}"));
        Assert.False(Pulse.Core.Declutter.DeclutterEngine.UninstallerMissing("rundll32.exe something"));
        Assert.False(Pulse.Core.Declutter.DeclutterEngine.UninstallerMissing(""));
        Assert.False(Pulse.Core.Declutter.DeclutterEngine.UninstallerMissing("steam://uninstall/730"));
    }
}

public class CleanInUseTests
{
    [Fact]
    public void A_file_an_app_holds_open_isnt_counted_as_ready_to_free_again()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pulse-inuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "free.tmp"), new byte[1000]);
        var held = Path.Combine(dir, "held.tmp");
        File.WriteAllBytes(held, new byte[5000]);
        var cat = new Pulse.Core.CleanCategory { Id = "t", Name = "t", Targets = () => new[] { new Pulse.Core.CleanTarget(dir, "*.tmp", false) } };
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None)) // an app has it open
        {
            long progressSeen = -1;
            long freed = Pulse.Core.Cleaner.Clean(cat, b => progressSeen = b);
            Assert.Equal(1000, freed);
            Assert.Equal(1000, progressSeen);
            Pulse.Core.Cleaner.Scan(cat);
            Assert.Equal(0, cat.Size);          // nothing left that can be freed right now…
            Assert.Equal(5000, cat.InUseSize);  // …and the held file is shown as in use instead
        }
    }
}

public class ReactionPopulationTests
{
    [Theory]
    [InlineData(273, 0.50)]   // the median: half are faster, half slower
    [InlineData(240, 0.697)]  // worked example for this model: faster than about 70%
    [InlineData(350, 0.16)]
    public void Percentile_matches_the_log_normal_model(double ms, double faster) =>
        Assert.Equal(faster, Pulse.Core.Reaction.ReactionPopulation.FasterThan(ms), 2);

    [Fact]
    public void Faster_times_beat_more_people() =>
        Assert.True(Pulse.Core.Reaction.ReactionPopulation.FasterThan(200) > Pulse.Core.Reaction.ReactionPopulation.FasterThan(250));

    [Fact]
    public void The_curve_peaks_a_little_below_the_median()
    {
        var d = Pulse.Core.Reaction.ReactionPopulation.Density;
        Assert.True(d(256) > d(200) && d(256) > d(350));
    }
}

public class ClickMacroTests
{
    static List<(double Gap, double Hold)> Run(Pulse.Core.Input.MacroSettings s, int n)
    {
        var p = new Pulse.Core.Input.ClickPattern(s, seed: 7);
        return Enumerable.Range(0, n).Select(_ => p.Next()).ToList();
    }

    [Theory]
    [InlineData("steady", 12)]
    [InlineData("butterfly", 18)]
    [InlineData("jitter", 14)]
    public void Average_speed_matches_what_was_picked(string mode, double cps)
    {
        var clicks = Run(new Pulse.Core.Input.MacroSettings { Mode = mode, Cps = cps, Variation = 0.08 }, 2000);
        double actual = 1000 / clicks.Average(c => c.Gap);
        Assert.InRange(actual, cps * 0.95, cps * 1.05);
    }

    [Fact]
    public void Exact_timing_is_a_metronome()
    {
        var clicks = Run(new Pulse.Core.Input.MacroSettings { Mode = "steady", Cps = 10, Variation = 0 }, 50);
        Assert.All(clicks, c => Assert.Equal(100, c.Gap, 6));
    }

    [Fact]
    public void Butterfly_comes_in_pairs()
    {
        var clicks = Run(new Pulse.Core.Input.MacroSettings { Mode = "butterfly", Cps = 16, Variation = 0 }, 400);
        // Every other gap is short (the second finger) and the others long (both fingers coming back).
        double odd = clicks.Where((_, i) => i % 2 == 1).Average(c => c.Gap), even = clicks.Where((_, i) => i % 2 == 0).Average(c => c.Gap);
        Assert.True(Math.Max(odd, even) > 1.4 * Math.Min(odd, even));
    }

    [Fact]
    public void Each_press_is_released_before_the_next()
    {
        foreach (var mode in new[] { "steady", "butterfly", "jitter" })
            Assert.All(Run(new Pulse.Core.Input.MacroSettings { Mode = mode, Cps = 25, Variation = 0.15 }, 500), c => Assert.True(c.Hold < c.Gap));
    }

    [Fact]
    public void A_recorded_rhythm_is_replayed_gap_for_gap()
    {
        var rhythm = new List<double[]> { new[] { 40.0, 12 }, new[] { 90.0, 15 }, new[] { 45.0, 11 }, new[] { 85.0, 14 } };
        var clicks = Run(new Pulse.Core.Input.MacroSettings { Mode = "rhythm", Rhythm = rhythm }, 8);
        Assert.Equal(new[] { 40.0, 90, 45, 85, 40, 90, 45, 85 }, clicks.Select(c => c.Gap));
        Assert.Equal(1000 * 4 / 260.0, Pulse.Core.Input.ClickPattern.RhythmCps(rhythm), 3);
    }

    [Fact]
    public void Kernel_anti_cheats_are_on_the_block_list()
    {
        foreach (var name in new[] { "vgc", "EasyAntiCheat", "BEService", "FACEIT" })
            Assert.Contains(name, Pulse.Core.Input.MacroPolicy.AntiCheatProcesses);
    }
}
