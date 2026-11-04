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
