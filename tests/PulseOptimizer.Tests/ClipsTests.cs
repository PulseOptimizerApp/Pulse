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
    public void A_long_recording_starts_at_a_keyframe_and_gets_everything_after()
    {
        var ring = new TsRing { KeepSeconds = 2 };
        ring.Write(Pat(), 0);
        ring.Write(Packet(0x1000, start: true), 0);
        ring.Write(Packet(TsRing.VideoPid, true, true, fill: 1), 0);
        using var disk = new MemoryStream();
        ring.StartTap(disk);
        ring.Write(Packet(TsRing.VideoPid, fill: 2), 1);                 // mid-group: skipped until a keyframe
        for (int t = 2; t < 50; t++) ring.Write(Packet(TsRing.VideoPid, true, true, fill: 3), t);
        Assert.Same(disk, ring.StopTap());
        var packets = disk.ToArray().Chunk(TsRing.PacketSize).ToList();
        Assert.Equal(2 + 48, packets.Count);                              // tables + every keyframe, far beyond the 2 s replay
        Assert.DoesNotContain(packets, p => p[12] == 2);
    }

    [Fact]
    public void Clip_start_time_matches_the_keyframe_the_clip_starts_on()
    {
        var ring = new TsRing { KeepSeconds = 100 };
        ring.Write(Pat(), 0);
        ring.Write(Packet(0x1000, start: true), 0);
        for (int t = 0; t <= 20; t += 2) ring.Write(Packet(TsRing.VideoPid, true, true), t);
        Assert.Equal(14, ring.StartTimeFor(5, 20));
    }

    [Fact]
    public void Renaming_moves_the_bookmarks_with_the_clip()
    {
        var dir = Directory.CreateTempSubdirectory("pulse-clips-").FullName;
        try
        {
            var clip = Path.Combine(dir, "VALORANT 2026-11-04 01-30-22.mp4");
            File.WriteAllText(clip, "x");
            ClipLibrary.SaveMarks(clip, new List<double> { 3.5, 12 });
            var renamed = ClipLibrary.Rename(clip, "ace clutch");
            Assert.Equal(Path.Combine(dir, "ace clutch.mp4"), renamed);
            Assert.False(File.Exists(clip));
            Assert.Equal(new[] { 3.5, 12.0 }, ClipLibrary.LoadMarks(renamed));
            ClipLibrary.Delete(renamed);
            Assert.Empty(ClipLibrary.LoadMarks(renamed));
        }
        finally { Directory.Delete(dir, true); }
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
    [Fact]
    public void Game_window_capture_follows_the_window_at_a_steady_frame_rate()
    {
        var a = ReplayRecorder.Arguments(new ClipSettings { Fps = 120 }, Nvenc, null, null, (IntPtr)0x1234);
        Assert.Contains("gfxcapture=hwnd=4660", a);
        Assert.Contains("display_border=0", a);
        Assert.Contains("[0:v]fps=120,null[v]", a);
        Assert.DoesNotContain("ddagrab", a);
        Assert.Contains("ddagrab", ReplayRecorder.Arguments(new ClipSettings(), Nvenc, null, null));
    }

    [Fact]
    public void Encoders_use_quality_settings_and_step_down_for_very_high_pixel_rates()
    {
        Assert.Contains("-preset p5", Encoders.Options(Nvenc, 40, 60));
        Assert.Contains("-spatial-aq 1", Encoders.Options(Nvenc, 40, 60));
        Assert.Contains("-preset p4", Encoders.Options(Nvenc, 80, 144, 3840L * 2160 * 144));
        var amf = new Encoder("h264_amf", "h264", "AMD", true);
        Assert.Contains("-quality balanced", Encoders.Options(amf, 40, 60));
        Assert.Contains("-quality speed", Encoders.Options(amf, 80, 144, 3840L * 2160 * 144));
    }

    [Fact]
    public void Each_app_gets_its_own_named_track_after_the_mix()
    {
        var titles = new List<string>();
        var a = ReplayRecorder.Arguments(new ClipSettings(), Nvenc, "G", "M", default, new List<(string, string)> { ("Game", "A0"), ("Discord", "A1") }, titles);
        Assert.Equal(new[] { "All sound", "Game", "Discord", "Microphone" }, titles);
        Assert.Contains("-i A0", a);
        Assert.Contains("-i A1", a);
        Assert.Contains("[1:a][m1]amix", a);
        Assert.Contains("-map [mix] -map 3:a -map 4:a -map [m2]", a);
    }

    [Fact]
    public void Track_names_are_read_back_from_the_clip()
    {
        const string probe = """
              Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p, 1920x1080, 40000 kb/s, 60 fps
              Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 192 kb/s
                Metadata:
                  handler_name    : All sound
              Stream #0:2[0x3](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 192 kb/s
                Metadata:
                  title           : Discord
                  handler_name    : Discord
              Stream #0:3[0x4](und): Audio: aac (LC), 48000 Hz, stereo, fltp
                Metadata:
                  handler_name    : SoundHandler
            """;
        Assert.Equal(new[] { "All sound", "Discord", "Track 3" }, ClipLibrary.TrackNames(probe));
    }

    [Fact]
    public void Muting_some_parts_mixes_only_the_rest()
    {
        Assert.Equal(new[] { 0 }, ClipLibrary.ChooseTracks(4, new[] { true, true, true }));
        Assert.Equal(new[] { 1, 3 }, ClipLibrary.ChooseTracks(4, new[] { true, false, true }));
        Assert.Empty(ClipLibrary.ChooseTracks(4, new[] { false, false, false }));
        Assert.Equal(new[] { 0 }, ClipLibrary.ChooseTracks(1, new[] { true }));
        Assert.Contains("[0:a:1][0:a:3]amix=inputs=2", ClipLibrary.AudioArgs(new[] { 1, 3 }));
        Assert.Equal("-an", ClipLibrary.AudioArgs(new int[0]));
    }

    [Theory]
    [InlineData("NBA 2K26 2026-11-04 02-38-47", "NBA 2K26")]
    [InlineData("NBA 2K26 2026-11-04 02-38-47 (trimmed)", "NBA 2K26")]
    [InlineData("NBA 2K26 2026-11-04 02-38-47 (2) (Discord)", "NBA 2K26")]
    [InlineData("NBA 2K26 2026-11-04 02-38-47 (trimmed) 2", "NBA 2K26")]
    [InlineData("VALORANT session 2026-11-04 01-30-22", "VALORANT")]
    [InlineData("ace clutch", "ace clutch")]
    public void Copies_and_sessions_group_with_their_game(string name, string game) =>
        Assert.Equal(game, ClipFile.GameOf(name));

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
        // Video + 160 kbit/s audio must stay under the target for any length.
        foreach (var secs in new[] { 10.0, 30, 60, 120 })
        {
            int kbps = ClipLibrary.DiscordVideoKbps(secs);
            double bytes = (kbps + 160) * 1000 / 8.0 * secs;
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

public class ReplayStoreTests
{
    static byte[] Packet(int pid, bool key = false)
    {
        var p = new byte[Pulse.Core.Clips.TsRing.PacketSize];
        p[0] = 0x47; p[1] = (byte)(((pid >> 8) & 0x1F) | (key ? 0x40 : 0)); p[2] = (byte)pid;
        if (key) { p[3] = 0x30; p[4] = 1; p[5] = 0x40; } else p[3] = 0x10;
        return p;
    }

    static byte[] Pat()
    {
        var p = new byte[Pulse.Core.Clips.TsRing.PacketSize];
        p[0] = 0x47; p[1] = 0x40; p[2] = 0; p[3] = 0x10; p[4] = 0;
        p[5 + 8 + 2] = 0x10; p[5 + 8 + 3] = 0x00; // program map on PID 0x1000
        return p;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Saved_replay_has_every_byte_in_memory_and_on_disk(bool disk)
    {
        using var ring = new Pulse.Core.Clips.TsRing { KeepSeconds = 1000 };
        if (disk) ring.UseDisk(Path.Combine(Path.GetTempPath(), "pulse-test", Guid.NewGuid() + ".tmp"));
        Assert.Equal(disk, ring.OnDisk);
        ring.Write(Pat(), 0);
        ring.Write(Packet(0x1000), 0);
        // 3 keyframe groups, each bigger than one 64 KB block, so blocks and the unfinished block are both used.
        for (int g = 0; g < 3; g++)
        {
            ring.Write(Packet(Pulse.Core.Clips.TsRing.VideoPid, key: true), g);
            for (int i = 0; i < 500; i++) ring.Write(Packet(Pulse.Core.Clips.TsRing.VideoPid), g + 0.5);
        }
        using var ms = new MemoryStream();
        long n = ring.SaveTo(ms, 100, 3);
        Assert.Equal(n, ms.Length);
        Assert.Equal((2 + 3 * 501) * Pulse.Core.Clips.TsRing.PacketSize, ms.Length);
        var all = ms.ToArray();
        for (int i = 0; i < all.Length; i += Pulse.Core.Clips.TsRing.PacketSize) Assert.Equal(0x47, all[i]);
        if (disk) Assert.True(ring.MemoryBytes < 1 << 20, "on disk the replay shouldn't sit in memory");
    }

    [Fact]
    public void Old_groups_give_their_blocks_back()
    {
        using var ring = new Pulse.Core.Clips.TsRing { KeepSeconds = 2 };
        ring.Write(Pat(), 0);
        ring.Write(Packet(0x1000), 0);
        for (int g = 0; g < 60; g++)
        {
            ring.Write(Packet(Pulse.Core.Clips.TsRing.VideoPid, key: true), g);
            for (int i = 0; i < 400; i++) ring.Write(Packet(Pulse.Core.Clips.TsRing.VideoPid), g + 0.5);
        }
        // About 3 groups of ~75 KB are kept: memory stays small instead of growing with every second recorded.
        Assert.True(ring.MemoryBytes < 2 << 20, $"memory grew to {ring.MemoryBytes}");
    }

    [Theory]
    [InlineData("ScreenClippingHost")]
    [InlineData("SnippingTool")]
    [InlineData("ShareX")]
    public void Screenshot_tools_are_never_games(string name) =>
        Assert.True(Pulse.Core.Measurement.GameDetector.IsIgnored(name));

    [Fact]
    public void A_compatible_video_path_is_used_after_the_fast_one_fails()
    {
        var s = new Pulse.Core.Clips.ClipSettings { Resolution = "native" };
        var e = new Pulse.Core.Clips.Encoder("h264_nvenc", "h264", "NVIDIA", true);
        var fast = Pulse.Core.Clips.ReplayRecorder.Arguments(s, e, null, null);
        var safe = Pulse.Core.Clips.ReplayRecorder.Arguments(s, e, null, null, videoLevel: 1);
        var small = Pulse.Core.Clips.ReplayRecorder.Arguments(s, e, null, null, videoLevel: 2);
        Assert.Contains("null[v]", fast);
        Assert.Contains("trunc(iw/2)*2", safe);
        Assert.Contains("scale=-2:1080", small);
    }
}
