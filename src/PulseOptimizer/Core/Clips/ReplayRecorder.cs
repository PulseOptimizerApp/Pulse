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
    readonly CancellationTokenSource _stop = new();
    readonly List<Task> _tasks = new();
    Process _ffmpeg;
    AudioCapture _game, _mic;
    readonly StringWriter _log = new();

    public ReplayRecorder(ClipSettings settings, Encoder encoder)
    {
        _s = settings;
        _encoder = encoder;
        _ring.KeepSeconds = settings.ReplaySeconds + 2;
    }

    public bool Running => _ffmpeg is { HasExited: false };
    public Encoder Encoder => _encoder;
    public long BufferBytes => _ring.Bytes;
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

        var args = Arguments(_s, _encoder, gamePipe != null ? $@"\\.\pipe\{id}-game" : null, micPipe != null ? $@"\\.\pipe\{id}-mic" : null);
        _ffmpeg = Process.Start(Ffmpeg.Start(args, redirectOut: true))!;
        try { _ffmpeg.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } // the game always comes first

        if (gamePipe != null) _tasks.Add(Pump(gamePipe, _game));
        if (micPipe != null) _tasks.Add(Pump(micPipe, _mic));
        _tasks.Add(Task.Run(ReadVideo));
        _tasks.Add(Task.Run(ReadLog));
    }

    /// <summary>The FFmpeg command line. Exposed for tests.</summary>
    internal static string Arguments(ClipSettings s, Encoder e, string gamePipe, string micPipe)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats" };
        int fps = Math.Clamp(s.Fps, 24, 240);

        // Input 0: the screen, grabbed on the graphics card.
        a.Add($"-f lavfi -i ddagrab=output_idx={Math.Max(0, s.Monitor)}:framerate={fps}:draw_mouse={(s.RecordCursor ? 1 : 0)}");
        int next = 1;
        int gameIn = -1, micIn = -1;
        string Raw(string pipe) => $"-thread_queue_size 4096 -f f32le -ar {AudioCapture.Rate} -ac 2 -i {pipe}";
        if (gamePipe != null) { a.Add(Raw(gamePipe)); gameIn = next++; }
        if (micPipe != null) { a.Add(Raw(micPipe)); micIn = next++; }

        // Video: stays on the graphics card for NVIDIA/AMD at native size; otherwise downloaded once and scaled.
        int h = s.Resolution switch { "1440" => 1440, "1080" => 1080, "720" => 720, _ => 0 };
        string v;
        if (h == 0 && e.TakesGpuFrames) v = "[0:v]null[v]";
        else if (h == 0 && e.Vendor == "Intel") v = "[0:v]hwmap=derive_device=qsv,format=qsv[v]";
        else if (h == 0) v = "[0:v]hwdownload,format=bgra,format=nv12[v]";
        else v = $"[0:v]hwdownload,format=bgra,scale=-2:{h}:flags=bilinear,format=nv12[v]";

        var filters = new List<string> { v };
        var maps = new List<string> { "-map", "[v]" };
        if (gameIn >= 0 && micIn >= 0)
        {
            if (s.SeparateMicTrack)
            {
                filters.Add($"[{micIn}:a]asplit=2[m1][m2]");
                filters.Add($"[{gameIn}:a][m1]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[mix]");
                maps.AddRange(new[] { "-map", "[mix]", "-map", "[m2]" });
            }
            else
            {
                filters.Add($"[{gameIn}:a][{micIn}:a]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0[mix]");
                maps.AddRange(new[] { "-map", "[mix]" });
            }
        }
        else if (gameIn >= 0) maps.AddRange(new[] { "-map", $"{gameIn}:a" });
        else if (micIn >= 0) maps.AddRange(new[] { "-map", $"{micIn}:a" });

        a.Add("-filter_complex \"" + string.Join(";", filters) + "\"");
        a.AddRange(maps);
        a.Add(Encoders.Options(e, Math.Clamp(s.BitrateMbps, 2, 150), fps));
        if (gameIn >= 0 || micIn >= 0) a.Add("-c:a aac -b:a 160k");
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
            await using (var f = File.Create(ts)) n = _ring.SaveTo(f, seconds, Now);
            if (n == 0) throw new InvalidOperationException("There's nothing in the replay yet. It fills up a few seconds after a game starts.");
            var mp4 = UniquePath(folder, name);
            var (code, err) = await Ffmpeg.Run($"-hide_banner -loglevel error -y -i \"{ts}\" -map 0 -c copy -movflags +faststart \"{mp4}\"", ct, 120_000);
            if (code != 0 || !File.Exists(mp4)) throw new InvalidOperationException("FFmpeg couldn't save the clip: " + err.Trim());
            return mp4;
        }
        finally { try { File.Delete(ts); } catch { } }
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
        _ffmpeg?.Dispose();
        _ring.Clear();
    }
}
