using Pulse.Core.Clips;
using Xunit;

namespace PulseOptimizer.Tests;

public class ClipsTests
{
    // ───────── MPEG-TS replay buffer ─────────

    static byte[] Packet(int pid, bool start = false, bool keyframe = false, byte fill = 0)
    {
        var p = new byte[TsRing.PacketSize];
        p[0] = 0x47;
        p[1] = (byte)((start ? 0x40 : 0) | ((pid >> 8) & 0x1F));
        p[2] = (byte)(pid & 0xFF);
        if (keyframe)
        {
            p[3] = 0x30;          // adaptation field + payload
            p[4] = 7;             // adaptation field length
            p[5] = 0x40;          // random access indicator
        }
        else p[3] = 0x10;
        for (int i = 12; i < p.Length; i++) p[i] = fill;
        return p;
    }

    static byte[] Pat()
    {
        var p = Packet(0, start: true);
        p[4] = 0;                 // pointer field
        // table_id, length(2), ts_id(2), version, section, last section, then program 1 → PMT PID 0x1000
        byte[] t = { 0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00, 0x00, 0x01, 0xF0, 0x00 };
        t.CopyTo(p, 5);
        return p;
    }

    [Fact]
    public void Keyframes_are_found_from_the_random_access_flag()
    {
        Assert.True(TsRing.IsKeyframeStart(Packet(TsRing.VideoPid, start: true, keyframe: true)));
        Assert.False(TsRing.IsKeyframeStart(Packet(TsRing.VideoPid, start: true)));
        Assert.False(TsRing.IsKeyframeStart(Packet(TsRing.VideoPid, keyframe: true))); // not the start of a frame
    }

    [Fact]
    public void A_saved_clip_starts_at_a_keyframe_with_the_stream_tables_first()
    {
        var ring = new TsRing { KeepSeconds = 100 };
        ring.Write(Pat(), 0);
        ring.Write(Packet(0x1000, start: true), 0);                      // PMT
        ring.Write(Packet(TsRing.VideoPid, fill: 9), 0);                 // before any keyframe: dropped
        ring.Write(Packet(TsRing.VideoPid, true, true, fill: 1), 1);     // keyframe at t=1
        ring.Write(Packet(TsRing.VideoPid, fill: 2), 2);
        ring.Write(Packet(TsRing.VideoPid, true, true, fill: 3), 10);    // keyframe at t=10
        ring.Write(Packet(TsRing.VideoPid, fill: 4), 11);

        using var ms = new MemoryStream();
        long n = ring.SaveTo(ms, seconds: 5, now: 12);
        var bytes = ms.ToArray();
        Assert.Equal(n, bytes.Length);
        Assert.Equal(0, bytes[2]);                                        // PAT first
        // 5 s back from t=12 is t=7: the last keyframe at or before that is t=1, so the clip is at least 5 s long.
        Assert.Equal(2 + 4, bytes.Length / TsRing.PacketSize);
        Assert.Equal(1, bytes[2 * TsRing.PacketSize + 12]);
        Assert.DoesNotContain(bytes.Chunk(TsRing.PacketSize), p => p[12] == 9);
    }

    [Fact]
    public void Old_recording_is_dropped_but_a_full_length_clip_stays_possible()
    {
        var ring = new TsRing { KeepSeconds = 5 };
        ring.Write(Pat(), 0);
        ring.Write(Packet(0x1000, start: true), 0);
        for (int t = 0; t <= 20; t++) ring.Write(Packet(TsRing.VideoPid, true, true, fill: (byte)t), t);
        Assert.InRange(ring.HeldSeconds(20), 5, 6);
        using var ms = new MemoryStream();
        ring.SaveTo(ms, 5, 20);
        Assert.Equal(15, ms.ToArray()[2 * TsRing.PacketSize + 12]);      // starts exactly 5 s back
    }

    [Fact]
    public void Packets_split_across_reads_are_put_back_together()
    {
        var ring = new TsRing { KeepSeconds = 100 };
        var all = Pat().Concat(Packet(0x1000, true)).Concat(Packet(TsRing.VideoPid, true, true, 5)).Concat(Packet(TsRing.VideoPid, fill: 6)).ToArray();
        foreach (var chunk in all.Chunk(50)) ring.Write(chunk, 1);
        using var ms = new MemoryStream();
        Assert.Equal(4 * TsRing.PacketSize, ring.SaveTo(ms, 10, 2));
    }

    [Fact]
    public void A_stalled_picture_does_not_turn_a_short_clip_into_minutes_of_sound()
    {
        // The reported bug: the picture stopped when a match loaded, sound kept going, and a 30 s clip came out 2:20.
        var ring = new TsRing { KeepSeconds = 62 };
        ring.Write(Pat(), 0);
        ring.Write(Packet(0x1000, start: true), 0);
        ring.Write(Packet(TsRing.VideoPid, true, true, fill: 1), 0);       // last keyframe before the picture stopped
        for (int t = 1; t <= 140; t++) ring.Write(Packet(0x101, start: true, fill: 7), t); // 140 s of sound only
        using var ms = new MemoryStream();
        ring.SaveTo(ms, 30, 140);
        int audio = ms.ToArray().Chunk(TsRing.PacketSize).Count(p => p[12] == 7);
        Assert.InRange(audio, 0, (int)TsRing.StaleGroupSeconds);
        Assert.True(ring.SecondsSinceKeyframe(140) > 100);
    }

    [Fact]
    public void Keyframe_age_is_tracked()
    {
        var ring = new TsRing();
        Assert.Equal(-1, ring.SecondsSinceKeyframe(5));
        ring.Write(Packet(TsRing.VideoPid, true, true), 3);
        Assert.Equal(2, ring.SecondsSinceKeyframe(5));
    }

    [Fact]
    public void Memory_cap_drops_old_groups()
    {
        var ring = new TsRing { KeepSeconds = 1000, MaxBytes = 10 * TsRing.PacketSize };
        ring.Write(Pat(), 0);
        ring.Write(Packet(0x1000, start: true), 0);
        for (int t = 0; t < 50; t++) ring.Write(Packet(TsRing.VideoPid, true, true, fill: (byte)t), t);
        Assert.InRange(ring.Bytes, 1, 10 * TsRing.PacketSize);
    }

    [Fact]
    public void Nothing_to_save_before_the_first_keyframe()
    {
        var ring = new TsRing();
        ring.Write(Pat(), 0);
        Assert.Equal(0, ring.SaveTo(new MemoryStream(), 10, 1));
    }

    // ───────── FFmpeg command ─────────

    static readonly Encoder Nvenc = new("h264_nvenc", "h264", "NVIDIA", true);
    static readonly Encoder Qsv = new("h264_qsv", "h264", "Intel", true);

    [Fact]
    public void Native_resolution_on_NVIDIA_stays_on_the_graphics_card()
    {
        var a = ReplayRecorder.Arguments(new ClipSettings(), Nvenc, @"\\.\pipe\g", null);
        Assert.Contains("ddagrab=output_idx=0:framerate=60", a);
        Assert.Contains("[0:v]null[v]", a);
        Assert.DoesNotContain("hwdownload", a);
        Assert.Contains("-c:v h264_nvenc", a);
        Assert.Contains("-map 1:a", a);
        Assert.Contains("-f mpegts", a);
    }

    [Fact]
    public void Lower_resolution_scales_and_mic_is_mixed_into_the_first_track()
    {
        var s = new ClipSettings { Resolution = "720", Microphone = true };
        var a = ReplayRecorder.Arguments(s, Nvenc, "G", "M");
        Assert.Contains("scale=-2:720", a);
        Assert.Contains("[1:a][2:a]amix=inputs=2", a);
        Assert.Contains("-map [mix]", a);
        Assert.DoesNotContain("asplit", a);
    }

    [Fact]
    public void Separate_mic_track_adds_a_second_audio_track()
    {
        var s = new ClipSettings { Microphone = true, SeparateMicTrack = true };
        var a = ReplayRecorder.Arguments(s, Nvenc, "G", "M");
        Assert.Contains("asplit=2[m1][m2]", a);
        Assert.Contains("-map [mix] -map [g2] -map [m2]", a);
    }

    [Fact]
    public void No_audio_means_no_audio_encoder()
    {
        var a = ReplayRecorder.Arguments(new ClipSettings { GameAudio = false }, Qsv, null, null);
        Assert.DoesNotContain("-c:a", a);
        Assert.Contains("hwmap=derive_device=qsv", a);
    }

    [Fact]
    public void Keyframe_every_second_so_clips_can_start_anywhere()
    {
        Assert.Contains("-g 144", Encoders.Options(Nvenc, 40, 144));
        Assert.Contains("-force_key_frames", Encoders.Options(Nvenc, 40, 144));
        Assert.Contains("-forced-idr 1", Encoders.Options(Nvenc, 40, 144));
        Assert.Contains("-force_key_frames", Encoders.Options(Qsv, 40, 60));
        Assert.Contains("-b:v 40M", Encoders.Options(Nvenc, 40, 60));
    }

    [Fact]
    public void Picks_the_graphics_card_encoder_before_the_processor()
    {
        var cpu = new Encoder("libx264", "h264", "CPU", false);
        var hevc = new Encoder("hevc_nvenc", "hevc", "NVIDIA", true);
        Assert.Equal(Nvenc, Encoders.Pick(new[] { cpu, Nvenc, hevc }, "h264"));
        Assert.Equal(hevc, Encoders.Pick(new[] { cpu, Nvenc, hevc }, "hevc"));
        Assert.Equal(Nvenc, Encoders.Pick(new[] { cpu, Nvenc }, "av1"));   // falls back to H.264 on the card
        Assert.Equal(cpu, Encoders.Pick(new[] { cpu }, "hevc"));
        Assert.Null(Encoders.Pick(Array.Empty<Encoder>(), "h264"));
    }

    // ───────── Library ─────────

    [Theory]
    [InlineData("  Duration: 00:01:02.50, start: 0", 62.5)]
    [InlineData("Duration: 01:00:00.00", 3600)]
    [InlineData("nothing here", 0)]
    public void Reads_the_clip_length(string text, double seconds) =>
        Assert.Equal(seconds, ClipLibrary.ParseDuration(text), 3);

    [Fact]
    public void Discord_copies_fit_under_10_MB()
    {
        // Video + 128 kbit/s audio must stay under the target for any length.
        foreach (var secs in new[] { 10.0, 30, 60, 120 })
        {
            int kbps = ClipLibrary.DiscordVideoKbps(secs);
            double bytes = (kbps + 128) * 1000 / 8.0 * secs;
            Assert.True(bytes < ClipLibrary.DiscordLimitBytes, $"{secs}s at {kbps} kbps is {bytes} bytes");
        }
        Assert.True(ClipLibrary.DiscordVideoKbps(15) > ClipLibrary.DiscordVideoKbps(60));
    }

    [Fact]
    public void Discord_copies_use_the_cards_H264_encoder()
    {
        Assert.Equal("h264_nvenc", ClipLibrary.H264Of(new Encoder("hevc_nvenc", "hevc", "NVIDIA", true)));
        Assert.Equal("h264_amf", ClipLibrary.H264Of(new Encoder("av1_amf", "av1", "AMD", true)));
    }

    [Fact]
    public void Clip_names_are_safe_and_never_overwrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pulse-clip-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = ReplayRecorder.UniquePath(dir, "Game: <Best> Moment?");
            Assert.DoesNotContain(':', Path.GetFileName(a));
            Assert.StartsWith("Game Best Moment", Path.GetFileName(a));
            File.WriteAllText(a, "");
            Assert.NotEqual(a, ReplayRecorder.UniquePath(dir, "Game: <Best> Moment?"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Game_name_comes_from_the_file_name()
    {
        var c = new ClipFile { Path = @"C:\Clips\VALORANT 2026-11-04 01-30-22 (2).mp4" };
        Assert.Equal("VALORANT", c.Game);
    }

    [Fact]
    public void Oldest_clips_go_first_and_favorites_are_kept()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pulse-clip-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var s = new ClipSettings { Folder = dir, StorageLimitGb = 1 };
            // Three clips that together exceed 1 GB (sparse files keep the test fast).
            string Make(string name, int daysAgo)
            {
                var p = Path.Combine(dir, name);
                using (var f = File.Create(p)) f.SetLength(450L * 1024 * 1024);
                File.SetCreationTime(p, DateTime.Now.AddDays(-daysAgo));
                return p;
            }
            var oldFav = Make("old fav.mp4", 3);
            var old = Make("old.mp4", 2);
            var recent = Make("recent.mp4", 1);
            s.Favorites.Add("old fav.mp4");
            Assert.Equal(1, ClipLibrary.EnforceLimit(s));
            Assert.True(File.Exists(oldFav));
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(recent));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ───────── Audio ─────────

    [Fact]
    public void Audio_at_44_1_kHz_is_converted_to_48_kHz()
    {
        var r = new AudioCapture.Resampler(44100);
        int total = 0;
        for (int i = 0; i < 100; i++)
        {
            var l = Enumerable.Repeat(0.5f, 441).ToArray();
            total += r.Convert(l, l).Length;
        }
        // One second at 44.1 kHz → about one second at 48 kHz, stereo.
        Assert.InRange(total, 48000 * 2 - 8, 48000 * 2 + 8);
    }

    [Fact]
    public void Audio_at_48_kHz_passes_through_unchanged()
    {
        var r = new AudioCapture.Resampler(48000);
        var outp = r.Convert(new[] { 0.1f, 0.2f }, new[] { -0.1f, -0.2f });
        Assert.Equal(new[] { 0.1f, -0.1f, 0.2f, -0.2f }, outp);
    }

    [Fact]
    public void Counts_a_clips_audio_tracks()
    {
        const string one = "  Stream #0:0[0x1](und): Video: h264\n  Stream #0:1[0x2](und): Audio: aac (LC)";
        const string three = one + "\n  Stream #0:2[0x3](und): Audio: aac\n  Stream #0:3[0x4](und): Audio: aac";
        Assert.Equal(1, ClipLibrary.CountAudio(one));
        Assert.Equal(3, ClipLibrary.CountAudio(three));
        Assert.Equal(0, ClipLibrary.CountAudio("Stream #0:0: Video: h264"));
    }

    [Fact]
    public void Exports_keep_the_chosen_sound()
    {
        Assert.Equal("-an", ClipLibrary.AudioArgs(-1));
        Assert.Contains("-map 0:a:2?", ClipLibrary.AudioArgs(2));
    }
}
