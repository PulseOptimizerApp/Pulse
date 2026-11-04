using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;

namespace Pulse.Core.Clips;

/// <summary>
/// The instant replay. FFmpeg captures the screen with Windows' desktop capture (ddagrab, which stays on the graphics
/// card), encodes with the graphics card's own encoder, and streams the result into <see cref="TsRing"/> in memory.
/// Pulse captures game sound and the microphone itself and feeds them to FFmpeg through two private pipes.
/// </summary>
public sealed class ReplayRecorder : IDisposable
{
    readonly ClipSettings _s;
    readonly Encoder _encoder;
    readonly TsRing _ring = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    /// <summary>When the recorder's clock started, on <see cref="InputCapture.NowSeconds"/>'s clock.</summary>
    readonly double _startAbs = InputCapture.NowSeconds;
    /// <summary>The span of the last saved clip or recording, on <see cref="InputCapture.NowSeconds"/>'s clock.</summary>
    public (double From, double To) LastRange { get; private set; }
    readonly CancellationTokenSource _stop = new();
    readonly List<Task> _tasks = new();
    Process _ffmpeg;
    AudioCapture _game, _mic;
    readonly StringWriter _log = new();

    readonly IntPtr _window;
    readonly int _gamePid;
    readonly List<(string Title, AudioCapture Capture)> _apps = new();

    /// <param name="window">The game's window to record, or Zero to record the whole display.</param>
    /// <param name="gamePid">The game's process, for its own sound track (0 = unknown).</param>
    public ReplayRecorder(ClipSettings settings, Encoder encoder, IntPtr window = default, int gamePid = 0)
    {
        _s = settings;
        _encoder = encoder;
        _window = window;
        _gamePid = gamePid;
        _ring.KeepSeconds = settings.ReplaySeconds + 2;
        // On disk (the default) the replay lives in a temporary file instead of Pulse's memory, like NVIDIA's.
        if (settings.ReplayOnDisk)
            _ring.UseDisk(Path.Combine(Path.GetTempPath(), "Pulse", $"replay-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp"));
        // Memory cap: twice what the bitrate needs for the replay, plus room for sound. Stops runaway growth.
        _ring.MaxBytes = (long)Math.Clamp(settings.BitrateMbps, 2, 150) * 125_000L * (settings.ReplaySeconds + 2) * 2 + (64L << 20);
    }

    /// <summary>
    /// True when FFmpeg is still running but no new picture has arrived for a few seconds (the capture broke, for
    /// example when a game switches display mode as a match loads). The caller restarts the recording.
    /// </summary>
    public bool Stalled
    {
        get
        {
            if (!Running || Now < 10) return false;      // give FFmpeg time to start
            double k = _ring.SecondsSinceKeyframe(Now);
            return k < 0 || k > 4;
        }
    }

    public bool Running => _ffmpeg is { HasExited: false };
    public Encoder Encoder => _encoder;
    /// <summary>The game window being recorded, or Zero for the whole display.</summary>
    public IntPtr Window => _window;
    /// <summary>The screen's shape (width / height, from its native resolution), for stretched resolutions. 0 = unknown.</summary>
    public double ScreenAspect { get; set; }

    /// <summary>Stream-copy options that make a stretched-resolution recording play stretched, or "".</summary>
    async Task<string> StretchFor(string ts)
    {
        if (!_s.StretchToScreen || ScreenAspect <= 0) return "";
        var (w, h) = await ClipLibrary.VideoSize(ts);
        return ClipLibrary.StretchCopyArgs(_encoder.Codec, ClipLibrary.StretchSar(w, h, ScreenAspect));
    }

    /// <summary>Names of the sound tracks being recorded, in order (track 1 is always all sound).</summary>
    public List<string> TrackTitles { get; } = new();
    public long BufferBytes => _ring.Bytes;
    /// <summary>What the replay takes in Pulse's memory (small when it's kept on disk).</summary>
    public long MemoryBytes => _ring.MemoryBytes;
    public bool OnDisk => _ring.OnDisk;
    /// <summary>What FFmpeg (the recorder) takes, as Task Manager shows it.</summary>
    public long RecorderMemory => Native.TaskManagerMemory(_ffmpeg);
    /// <summary>True once any picture has arrived (false means the video path never worked on this PC).</summary>
    public bool HadVideo => _ring.SecondsSinceVideo(Now) >= 0;
    /// <summary>
    /// 0 = fastest (frames stay on the graphics card), 1 = frames copied once and converted (works with every
    /// driver and window size), 2 = that at 1080p. Pulse steps down when a level gives no picture.
    /// </summary>
    public int VideoLevel { get; init; }
    public double BufferSeconds => _ring.HeldSeconds(Now);
    public int ExitCode => _ffmpeg is { HasExited: true } p ? p.ExitCode : 0;
    /// <summary>The last lines FFmpeg printed (shown when recording fails).</summary>
    public string LastError
    {
        get { lock (_log) { var lines = _log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries); return string.Join("\n", lines.TakeLast(4)).Trim(); } }
    }

    double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>CPU time FFmpeg has used, for the "what Clips costs" readout.</summary>
    public TimeSpan CpuTime { get { try { return _ffmpeg?.TotalProcessorTime ?? TimeSpan.Zero; } catch { return TimeSpan.Zero; } } }

    public void Start()
    {
        string id = $"pulse-clips-{Environment.ProcessId}-{Guid.NewGuid():N}";
        NamedPipeServerStream gamePipe = null, micPipe = null;
        if (_s.GameAudio)
        {
            _game = AudioCapture.GameSound();
            _game.Volume = _s.GameVolume / 100f;
            _game.Start();
            gamePipe = new NamedPipeServerStream(id + "-game", PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 16);
        }
        if (_s.Microphone)
        {
            _mic = AudioCapture.Microphone();
            _mic.Volume = _s.MicVolume / 100f;
            _mic.Start();
            micPipe = new NamedPipeServerStream(id + "-mic", PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 16);
        }

        // Separate tracks per app (game, Discord, music) so a clip can mute any of them later.
        var appPipes = new List<(string Title, string Pipe, NamedPipeServerStream Server, AudioCapture Capture)>();
        if (_s.AppTracks && _s.GameAudio && AudioCapture.AppCaptureSupported)
        {
            foreach (var (title, pid) in AppAudio.Sources(_gamePid))
            {
                var cap = AudioCapture.App(pid);
                cap.Volume = _s.GameVolume / 100f;
                cap.Start();
                var name = $"{id}-app{appPipes.Count}";
                appPipes.Add((title, $@"\\.\pipe\{name}", new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 16), cap));
                _apps.Add((title, cap));
            }
        }

        var args = Arguments(_s, _encoder, gamePipe != null ? $@"\\.\pipe\{id}-game" : null, micPipe != null ? $@"\\.\pipe\{id}-mic" : null, _window,
            appPipes.Select(a => (a.Title, a.Pipe)).ToList(), TrackTitles, VideoLevel);
        _ffmpeg = Process.Start(Ffmpeg.Start(args, redirectOut: true))!;
        try { _ffmpeg.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } // the game always comes first

        if (gamePipe != null) _tasks.Add(Pump(gamePipe, _game));
        if (micPipe != null) _tasks.Add(Pump(micPipe, _mic));
        foreach (var a in appPipes) _tasks.Add(Pump(a.Server, a.Capture));
        _tasks.Add(Task.Run(ReadVideo));
        _tasks.Add(Task.Run(ReadLog));
    }

    /// <summary>The FFmpeg command line. Exposed for tests.</summary>
    internal static string Arguments(ClipSettings s, Encoder e, string gamePipe, string micPipe, IntPtr window = default,
        IReadOnlyList<(string Title, string Pipe)> apps = null, List<string> titles = null, int videoLevel = 0)
    {
        apps ??= Array.Empty<(string, string)>();
        titles ??= new List<string>();
        titles.Clear();
        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats" };
        int fps = Math.Clamp(s.Fps, 24, 240);
        int cursor = s.RecordCursor ? 1 : 0;

        // Input 0: grabbed on the graphics card. Either the game's own window (Windows Graphics Capture, which keeps
        // working when the game changes display mode or moves screens; no yellow border, size kept and rounded to even
        // numbers for the encoder), or the whole display (Desktop Duplication).
        if (window != IntPtr.Zero)
            a.Add($"-f lavfi -i gfxcapture=hwnd={(long)window}:capture_cursor={cursor}:capture_border=0:display_border=0:max_framerate={fps}:resize_mode=scale_aspect:width=-2:height=-2");
        else
            a.Add($"-f lavfi -i ddagrab=output_idx={Math.Max(0, s.Monitor)}:framerate={fps}:draw_mouse={cursor}");
        // Window capture sends a frame when the game draws one; make it a steady frame rate like the screen capture.
        string src = window != IntPtr.Zero ? $"[0:v]fps={fps}," : "[0:v]";
        int next = 1;
        int gameIn = -1, micIn = -1;
        int offset = Math.Clamp(s.AudioOffsetMs, -500, 500);
        string shift = offset == 0 ? "" : $"-itsoffset {(offset / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)} ";
        string Raw(string pipe) => $"-thread_queue_size 512 {shift}-f f32le -ar {AudioCapture.Rate} -ac 2 -i {pipe}";
        if (gamePipe != null) { a.Add(Raw(gamePipe)); gameIn = next++; }
        if (micPipe != null) { a.Add(Raw(micPipe)); micIn = next++; }

        // Video: stays on the graphics card for NVIDIA/AMD at native size; otherwise downloaded once and scaled.
        int h = s.Resolution switch { "1440" => 1440, "1080" => 1080, "720" => 720, _ => 0 };
        if (videoLevel >= 2 && (h == 0 || h > 1080)) h = 1080;
        string v;
        if (h == 0 && videoLevel == 0 && e.TakesGpuFrames) v = src + "null[v]";
        else if (h == 0 && videoLevel == 0 && e.Vendor == "Intel") v = src + "hwmap=derive_device=qsv,format=qsv[v]";
        // Native size, rounded to even numbers (a game window can be any size; encoders need even ones).
        else if (h == 0) v = src + "hwdownload,format=bgra,scale=trunc(iw/2)*2:trunc(ih/2)*2,format=nv12[v]";
        else v = src + $"hwdownload,format=bgra,scale=-2:{h}:flags=bilinear,format=nv12[v]";

        var appIn = new List<int>();
        foreach (var app in apps) { a.Add(Raw(app.Pipe)); appIn.Add(next++); }

        var filters = new List<string> { v };
        var maps = new List<string> { "-map", "[v]" };
        bool micTrack = micIn >= 0 && (s.SeparateMicTrack || appIn.Count > 0);
        bool gameTrack = gameIn >= 0 && micIn >= 0 && s.SeparateMicTrack && appIn.Count == 0;
        // Track 1: everything mixed (what Discord and players play).
        if (gameIn >= 0 && micIn >= 0)
        {
            string g = gameIn + ":a", m = micIn + ":a";
            if (gameTrack) { filters.Add($"[{gameIn}:a]asplit=2[g1][g2]"); g = "g1"; }
            if (micTrack) { filters.Add($"[{micIn}:a]asplit=2[m1][m2]"); m = "m1"; }
            // A gentle limiter: game and voice added together can go over full scale, which crackles in the clip.
            filters.Add($"[{g}][{m}]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,alimiter=limit=0.97:level=0[mix]");
            maps.AddRange(new[] { "-map", "[mix]" });
        }
        else if (gameIn >= 0) maps.AddRange(new[] { "-map", $"{gameIn}:a" });
        else if (micIn >= 0) maps.AddRange(new[] { "-map", $"{micIn}:a" });
        if (gameIn >= 0 || micIn >= 0) titles.Add("All sound");
        // Then one track per app, the game sound without the mic, and the mic alone.
        for (int i = 0; i < appIn.Count; i++) { maps.AddRange(new[] { "-map", $"{appIn[i]}:a" }); titles.Add(apps[i].Title); }
        if (gameTrack) { maps.AddRange(new[] { "-map", "[g2]" }); titles.Add("Game"); }
        if (micTrack) { maps.AddRange(new[] { "-map", micIn >= 0 && gameIn < 0 ? $"{micIn}:a" : "[m2]" }); titles.Add("Microphone"); }

        a.Add("-filter_complex \"" + string.Join(";", filters) + "\"");
        a.AddRange(maps);
        // Pixel rate decides how hard the encoder can work: native is assumed 1440p when not known.
        int outH = h > 0 ? h : 1440;
        a.Add(Encoders.Options(e, Math.Clamp(s.BitrateMbps, 2, 150), fps, (long)outH * outH * 16 / 9 * fps));
        // 256 kb/s per track: clean at any volume. Each extra app track is a copy of part of the sound.
        if (gameIn >= 0 || micIn >= 0) a.Add("-c:a aac -b:a 256k");
        a.Add("-f mpegts -mpegts_start_pid 0x100 -mpegts_flags +initial_discontinuity pipe:1");
        return string.Join(" ", a);
    }

    /// <summary>Feeds one audio source to FFmpeg at exactly real time, filling gaps with silence so sound stays in sync.</summary>
    Task Pump(NamedPipeServerStream pipe, AudioCapture source) => Task.Run(async () =>
    {
        try
        {
            await pipe.WaitForConnectionAsync(_stop.Token);
            source.Trim(0); // start fresh: drop anything captured before FFmpeg was ready
            var clock = Stopwatch.StartNew();
            long written = 0; // floats
            var buf = new float[AudioCapture.Rate * 2 / 5];
            var bytes = new byte[buf.Length * 4];
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(10, _stop.Token);
                long due = (long)(clock.Elapsed.TotalSeconds * AudioCapture.Rate) * 2 - written;
                if (due <= 0) continue;
                // Keep the capture queue short (under 100 ms) so the delay between picture and sound stays small.
                source.Trim(AudioCapture.Rate * 2 / 10);
                int want = (int)Math.Min(due, buf.Length);
                int got = source.Read(buf, want);
                if (got < want) Array.Clear(buf, got, want - got); // nothing playing: silence
                Buffer.BlockCopy(buf, 0, bytes, 0, want * 4);
                await pipe.WriteAsync(bytes.AsMemory(0, want * 4), _stop.Token);
                written += want;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { } // FFmpeg closed the pipe
        finally { try { pipe.Dispose(); } catch { } }
    });

    async Task ReadVideo()
    {
        var buf = new byte[1 << 16];
        try
        {
            var s = _ffmpeg.StandardOutput.BaseStream;
            int n;
            while ((n = await s.ReadAsync(buf, _stop.Token)) > 0) _ring.Write(buf.AsSpan(0, n), Now);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    async Task ReadLog()
    {
        try
        {
            string line;
            while ((line = await _ffmpeg.StandardError.ReadLineAsync(_stop.Token)) != null)
                lock (_log) { _log.WriteLine(line); if (_log.GetStringBuilder().Length > 20_000) _log.GetStringBuilder().Remove(0, 10_000); }
        }
        catch { }
    }

    /// <summary>
    /// Saves the last <paramref name="seconds"/> as an MP4 in <paramref name="folder"/>. The recording keeps running.
    /// Copying from memory and repackaging takes a second or two; nothing is re-encoded.
    /// </summary>
    public async Task<string> Save(string folder, string name, double seconds, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var ts = Path.Combine(Path.GetTempPath(), $"pulse-clip-{Guid.NewGuid():N}.ts");
        try
        {
            long n;
            double clipStart = _ring.StartTimeFor(seconds, Now);
            await using (var f = File.Create(ts)) n = _ring.SaveTo(f, seconds, Now);
            if (n == 0) throw new InvalidOperationException("There's nothing in the replay yet. It fills up a few seconds after a game starts.");
            var mp4 = UniquePath(folder, name);
            // -t caps the length even if keyframes were far apart (the clip starts at a keyframe, so it's never shorter).
            string len = (seconds + 1.5).ToString("0.###", CultureInfo.InvariantCulture);
            var (code, err) = await Ffmpeg.Run($"-hide_banner -loglevel error -y -i \"{ts}\" -map 0 -c copy{await StretchFor(ts)} -t {len}{TitleArgs(TrackTitles)} -movflags +faststart \"{mp4}\"", ct, 120_000);
            if (code != 0 || !File.Exists(mp4)) throw new InvalidOperationException("FFmpeg couldn't save the clip: " + err.Trim());
            ClipLibrary.SaveMarks(mp4, MarksBetween(clipStart, Now));
            LastRange = (_startAbs + clipStart, _startAbs + Now);
            return mp4;
        }
        finally { try { File.Delete(ts); } catch { } }
    }

    /// <summary>Saves the newest frame of the replay as a PNG (the last half second is decoded; the last frame wins).</summary>
    public async Task<string> SaveScreenshot(string folder, string name, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var ts = Path.Combine(Path.GetTempPath(), $"pulse-shot-{Guid.NewGuid():N}.ts");
        try
        {
            long n;
            await using (var f = File.Create(ts)) n = _ring.SaveTo(f, 0.5, Now);
            if (n == 0) throw new InvalidOperationException("There's nothing in the replay yet. It fills up a few seconds after a game starts.");
            var png = Path.ChangeExtension(UniquePath(folder, name), ".png");
            var (code, err) = await Ffmpeg.Run($"-hide_banner -loglevel error -y -i \"{ts}\" -an -update 1 \"{png}\"", ct, 60_000);
            if (code != 0 || !File.Exists(png)) throw new InvalidOperationException("FFmpeg couldn't save the screenshot: " + err.Trim());
            return png;
        }
        finally { try { File.Delete(ts); } catch { } }
    }

    // ───────── Bookmarks and long recordings ─────────

    readonly List<double> _marks = new();   // bookmark times on the recorder's clock
    FileStream _session;
    string _sessionTs, _sessionName;
    double _sessionStart;

    /// <summary>A long recording is being written (start/stop hotkey).</summary>
    public bool SessionActive => _session != null;
    public double SessionSeconds => _session != null ? Now - _sessionStart : 0;

    /// <summary>Marks this moment. Saved clips and long recordings that include it show a marker on the timeline.</summary>
    public void AddBookmark()
    {
        lock (_marks)
        {
            _marks.Add(Now);
            _marks.RemoveAll(m => Now - m > Math.Max(_s.ReplaySeconds, 600) + 60 && (_session == null || m < _sessionStart));
        }
    }

    List<double> MarksBetween(double from, double to)
    {
        lock (_marks) return _marks.Where(m => m >= from && m <= to).Select(m => Math.Round(m - from, 2)).ToList();
    }

    /// <summary>Starts writing everything to a file until <see cref="StopSession"/> (memory use stays the same).</summary>
    public void StartSession(string folder, string name)
    {
        if (_session != null) return;
        Directory.CreateDirectory(folder);
        _sessionTs = Path.Combine(folder, $".recording-{Guid.NewGuid():N}.ts");
        _sessionName = name;
        _session = new FileStream(_sessionTs, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        _sessionStart = Now;
        _ring.StartTap(_session);
    }

    /// <summary>Finishes the long recording as an MP4 next to the clips. Returns its path.</summary>
    public async Task<string> StopSession(string folder)
    {
        var f = _session;
        if (f == null) return null;
        _session = null;
        var tapped = _ring.StopTap();
        var marks = MarksBetween(_sessionStart, Now);
        LastRange = (_startAbs + _sessionStart, _startAbs + Now);
        try { await f.FlushAsync(); } catch { }
        f.Dispose();
        try
        {
            var mp4 = UniquePath(folder, _sessionName + " session");
            var (code, err) = await Ffmpeg.Run($"-hide_banner -loglevel error -y -i \"{_sessionTs}\" -map 0 -c copy{await StretchFor(_sessionTs)}{TitleArgs(TrackTitles)} -movflags +faststart \"{mp4}\"", default, 30 * 60_000);
            if (code != 0 || !File.Exists(mp4)) throw new InvalidOperationException("FFmpeg couldn't finish the recording: " + err.Trim());
            ClipLibrary.SaveMarks(mp4, marks);
            return mp4;
        }
        finally { try { File.Delete(_sessionTs); } catch { } }
    }

    /// <summary>Names each sound track in the MP4 so the trimmer can show them (MPEG-TS can't carry names).</summary>
    internal static string TitleArgs(IReadOnlyList<string> titles)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < titles.Count; i++)
        {
            var t = titles[i].Replace("\"", "");
            sb.Append($" -metadata:s:a:{i} title=\"{t}\" -metadata:s:a:{i} handler_name=\"{t}\"");
        }
        return sb.ToString();
    }

    internal static string UniquePath(string folder, string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        name = string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > 80) name = name[..80].Trim();
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, $"{name} {stamp}.mp4");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name} {stamp} ({i}).mp4");
        return path;
    }

    public void Dispose()
    {
        // A long recording still running is finished first, so nothing recorded is lost.
        if (_session != null) try { StopSession(Path.GetDirectoryName(_sessionTs)).Wait(TimeSpan.FromMinutes(5)); } catch { }
        _stop.Cancel();
        try
        {
            if (_ffmpeg is { HasExited: false })
            {
                // "q" asks FFmpeg to stop cleanly; kill it if it doesn't.
                try { _ffmpeg.StandardInput.Write('q'); _ffmpeg.StandardInput.Flush(); } catch { }
                if (!_ffmpeg.WaitForExit(2000)) _ffmpeg.Kill(true);
            }
        }
        catch { }
        try { Task.WaitAll(_tasks.ToArray(), 2000); } catch { }
        _game?.Dispose();
        _mic?.Dispose();
        foreach (var (_, c) in _apps) c.Dispose();
        _ffmpeg?.Dispose();
        _ring.Dispose();
    }
}
