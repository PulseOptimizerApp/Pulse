using NAudio.CoreAudioApi;
using NAudio.Wave;
using Pulse.Core.Mic;

namespace Pulse.Core.Audio;

/// <summary>The Headphones page's settings (saved).</summary>
public sealed class GameAudioSettings
{
    public bool Enabled { get; set; }
    /// <summary>The virtual cable's recording side that games play into (e.g. "CABLE-A Output"); empty = found automatically.</summary>
    public string SourceId { get; set; } = "";
    /// <summary>Your headphones; empty = the Windows default (never a cable).</summary>
    public string OutputId { get; set; } = "";
    public string Preset { get; set; } = "footsteps";
    public double[] Eq { get; set; } = (double[])Presets[1].Eq.Clone();
    /// <summary>0–1: lifts quiet sounds (footsteps, reloads) and holds back loud ones (explosions).</summary>
    public double EvenOut { get; set; } = 0.6;
    /// <summary>Virtual surround: 7.1 from the game becomes 3D sound in headphones; stereo gets speakers-in-front placement.</summary>
    public bool Spatial { get; set; }
    public double VolumeDb { get; set; }

    public static readonly (string Key, string Name, string Note, double[] Eq, double EvenOut, bool Spatial)[] Presets =
    {
        ("flat", "Flat", "The game as it is", new double[10], 0, false),
        ("footsteps", "Footsteps", "Any shooter: less rumble, clearer steps", new double[] { -6, -5, -3, -1, 0, 2, 4, 5, 2, 0 }, 0.6, false),
        ("valorant", "VALORANT", "Steps and ability cues", new double[] { -5, -4, -2, -1, 0, 1, 3, 4, 3, 1 }, 0.5, false),
        ("cs2", "Counter-Strike 2", "Steps and reloads forward", new double[] { -6, -5, -3, -1, 0, 2, 4, 5, 2, 0 }, 0.6, false),
        ("cod", "Call of Duty", "Tames explosions, lifts steps", new double[] { -6, -6, -4, -2, 0, 2, 4, 4, 2, 0 }, 0.7, false),
        ("apex", "Apex Legends", "Steps and shields", new double[] { -4, -4, -2, 0, 1, 2, 3, 4, 2, 0 }, 0.5, false),
        ("fortnite", "Fortnite", "Steps and chests", new double[] { -3, -3, -1, 0, 0, 1, 3, 4, 2, 1 }, 0.4, false),
        ("immersive", "Immersive", "Story games and movies", new double[] { 4, 3, 2, 0, -1, 0, 1, 2, 3, 2 }, 0.1, true),
        ("music", "Music", "Warm and full", new double[] { 3, 2, 1, 0, -1, 0, 1, 1, 2, 2 }, 0, false),
    };

    public void ApplyPreset(string key)
    {
        foreach (var p in Presets)
            if (p.Key == key) { Preset = key; Eq = (double[])p.Eq.Clone(); EvenOut = p.EvenOut; Spatial = p.Spatial; return; }
    }
}

/// <summary>
/// Game audio for headphones, like Sonar's game channel: the game plays into a virtual cable, Pulse picks it up,
/// applies the EQ, evens out loud and quiet sounds, optionally places it in 3D, and plays it to your headphones.
/// About 1% of a CPU core. Nothing runs while it's off.
/// </summary>
public static class GameAudio
{
    public static GameAudioSettings S => TweakEngine.State.GameAudio ??= new GameAudioSettings();
    static readonly object Lock = new();
    static WasapiCapture _cap;
    static OutStream _out;
    static GameAudioChain _chain;
    static float[] _stereo = Array.Empty<float>(), _left = Array.Empty<float>(), _right = Array.Empty<float>();
    static Timer _retry;

    public static bool Running => _cap != null;
    public static string Problem { get; private set; }
    public static string SourceName { get; private set; }
    public static string OutputName { get; private set; }
    public static int SourceChannels { get; private set; }
    public static float Peak => _chain?.Peak ?? 0;
    public static bool Bypass { get => _chain?.Bypass ?? false; set { if (_chain != null) _chain.Bypass = value; } }
    public static event Action Changed;

    public record Device(string Id, string Name);

    public static bool IsCableOut(string n) => Cables.IsVirtual(n);
    /// <summary>The name to pick as the game's sound output for a cable's recording side ("CABLE Output" → "CABLE In 16ch").</summary>
    public static string PlaybackNameFor(string sourceName) => sourceName == null ? "" : Cables.OtherEnd(sourceName, wantInput: false);

    /// <summary>Cable recording sides a game can play into (the Mic studio's cable is left out).</summary>
    public static List<Device> Sources()
    {
        var list = Cables.Devices().Where(d => d.Input && Cables.IsVirtual(d.Name)).Select(d => new Device(d.Id, d.Name)).ToList();
        // The Mic studio sends your voice through its cable; using the same one here would mix the game into your mic.
        var micCable = MicStudio.S.Enabled ? MicStudio.OutputName ?? MicStudio.FindCable()?.Name : null;
        if (micCable != null) list.RemoveAll(d => Cables.SameCable(d.Name, micCable));
        return list.OrderBy(d => d.Name.Contains("Hi-Fi") ? 0 : 1).ThenBy(d => d.Name).ToList();
    }

    public static List<Device> Headphones()
    {
        return Cables.Devices().Where(d => !d.Input && !Cables.IsVirtual(d.Name)).Select(d => new Device(d.Id, d.Name)).ToList();
    }

    public static void Init() { if (S.Enabled) Start(); }
    public static void Shutdown() => Stop();
    public static void Refresh() { Stop(); if (S.Enabled) Start(); }

    public static void Start()
    {
        Stop(true);
        Problem = null;
        try
        {
            using var en = new MMDeviceEnumerator();
            MMDevice src = null;
            if (!string.IsNullOrEmpty(S.SourceId)) try { src = en.GetDevice(S.SourceId); if (src.State != DeviceState.Active) src = null; } catch { }
            if (src == null) { var first = Sources().FirstOrDefault(); if (first != null) src = en.GetDevice(first.Id); }
            if (src == null) { Problem = "NoCable"; Changed?.Invoke(); return; }

            MMDevice dst = null;
            if (!string.IsNullOrEmpty(S.OutputId)) try { dst = en.GetDevice(S.OutputId); if (dst.State != DeviceState.Active) dst = null; } catch { }
            dst ??= en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (dst == null || IsLoop(dst.FriendlyName, src.FriendlyName))
            {
                // Windows' default output is the cable itself (so games play into it): pick real headphones instead.
                var hp = Headphones().FirstOrDefault();
                dst = hp != null ? en.GetDevice(hp.Id) : null;
            }
            if (dst == null) { Fail("No headphones or speakers found."); return; }

            lock (Lock)
            {
                SourceName = src.FriendlyName;
                OutputName = dst.FriendlyName;
                _cap = new WasapiCapture(src, true, 10);
                var wf = _cap.WaveFormat;
                SourceChannels = wf.Channels;
                _chain = new GameAudioChain(wf.SampleRate);
                _out = new OutStream(dst, 20);
                _cap.DataAvailable += (_, e) => OnData(e.Buffer, e.BytesRecorded, wf);
                _cap.RecordingStopped += (_, e) => { if (e.Exception != null && S.Enabled) ThreadPool.QueueUserWorkItem(_ => Fail("The game audio stopped (a device was unplugged). Trying again…")); };
                _cap.StartRecording();
            }
        }
        catch (Exception ex) { Fail("Game audio couldn't start: " + ex.Message); }
        Changed?.Invoke();
    }

    static bool IsLoop(string output, string source) => Cables.IsVirtual(output);

    static void Fail(string message)
    {
        Problem = message;
        Stop(true);
        _retry ??= new Timer(_ => { if (S.Enabled && !Running) Start(); else if (Running) { _retry?.Dispose(); _retry = null; } }, null, 4000, 4000);
        Changed?.Invoke();
    }

    public static void Stop() => Stop(false);

    static void Stop(bool keepRetry)
    {
        if (!keepRetry) { _retry?.Dispose(); _retry = null; Problem = null; }
        WasapiCapture cap; OutStream o;
        lock (Lock) { cap = _cap; o = _out; _cap = null; _out = null; _chain = null; }
        try { cap?.StopRecording(); cap?.Dispose(); } catch { }
        o?.Dispose();
        if (!keepRetry) Changed?.Invoke();
    }

    static void OnData(byte[] buf, int count, WaveFormat wf)
    {
        // The audio thread: an exception here would close Pulse, so it's caught and game audio restarts.
        try
        {
            lock (Lock)
            {
                if (_chain == null || _out == null) return;
                int ch = Math.Max(1, wf.Channels), bps = wf.BitsPerSample / 8, n = count / (bps * ch);
                if (_stereo.Length < n * 2) { _stereo = new float[n * 2]; _left = new float[n]; _right = new float[n]; }
                _chain.Process(buf, n, ch, bps, _stereo, S);
                // The cable and the headphones run on different clocks: never let the delay build up.
                if (_out.BufferedMs > 60) return;
                for (int i = 0; i < n; i++) { _left[i] = _stereo[i * 2]; _right[i] = _stereo[i * 2 + 1]; }
                _out.Write(_left.AsSpan(0, n), _right.AsSpan(0, n), wf.SampleRate);
            }
        }
        catch (Exception ex) { ThreadPool.QueueUserWorkItem(_ => Fail("Game audio hit a problem and restarted: " + ex.Message)); }
    }
}

/// <summary>
/// The game audio chain, per sample frame: down to stereo (or 3D when spatial is on) → 10-band EQ → even out loud and
/// quiet (linked compressor with make-up gain) → volume → limiter.
/// </summary>
public sealed class GameAudioChain
{
    readonly int _rate;
    readonly Biquad[,] _eq = new Biquad[2, 10];
    readonly double[] _eqDb = Enumerable.Repeat(double.NaN, 10).ToArray();
    readonly Spatializer _spatial;
    double _env, _gain = 1;
    public float Peak;
    public bool Bypass;

    public GameAudioChain(int rate) { _rate = rate; _spatial = new Spatializer(rate); }

    static float Read(byte[] b, int o, int bps) => bps switch
    {
        4 => BitConverter.ToSingle(b, o),
        2 => BitConverter.ToInt16(b, o) / 32768f,
        3 => ((b[o] << 8 | b[o + 1] << 16 | b[o + 2] << 24) >> 8) / 8388608f,
        _ => 0,
    };

    /// <summary>Raw interleaved input (any channel count) → processed interleaved stereo in <paramref name="outLR"/>.</summary>
    public void Process(byte[] input, int frames, int ch, int bps, float[] outLR, GameAudioSettings s)
    {
        var eq = s.Eq ?? new double[10];
        for (int b = 0; b < 10; b++)
        {
            double v = b < eq.Length ? Math.Clamp(eq[b], -12, 12) : 0;
            if (v != _eqDb[b])
            {
                _eqDb[b] = v;
                for (int c = 0; c < 2; c++) _eq[c, b] = Math.Abs(v) < 0.05 ? null : b switch
                {
                    0 => Biquad.LowShelf(_rate, 40, v),
                    9 => Biquad.HighShelf(_rate, 12000, v),
                    _ => Biquad.Peak(_rate, MicSettings.EqFreqs[b], 1.4, v),
                };
            }
        }
        double amount = Math.Clamp(s.EvenOut, 0, 1);
        double threshold = -16 - 18 * amount, ratio = 1 + 5 * amount, makeup = Math.Pow(10, -threshold * (1 - 1 / ratio) * 0.55 / 20);
        float vol = (float)Math.Pow(10, s.VolumeDb / 20);
        var frame = new float[Math.Max(ch, 2)];
        float peak = 0;
        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < ch; c++) frame[c] = Read(input, (i * ch + c) * bps, bps);
            float l, r;
            if (Bypass)
            {
                (l, r) = ch == 1 ? (frame[0], frame[0]) : Spatializer.Downmix(frame, ch);
            }
            else
            {
                (l, r) = s.Spatial ? _spatial.Process(frame, ch) : ch == 1 ? (frame[0], frame[0]) : Spatializer.Downmix(frame, ch);
                for (int b = 0; b < 10; b++) if (_eq[0, b] != null) { l = _eq[0, b].Process(l); r = _eq[1, b].Process(r); }
                if (amount > 0.01)
                {
                    double x = Math.Max(Math.Abs(l), Math.Abs(r));
                    _env += (x - _env) * (x > _env ? 0.003 : 0.00008);
                    double envDb = 20 * Math.Log10(_env + 1e-9), over = envDb - threshold;
                    double gainDb = over <= 0 ? 0 : (1 / ratio - 1) * over;
                    _gain += (Math.Pow(10, gainDb / 20) - _gain) * 0.01;
                    float g = (float)(_gain * makeup);
                    l *= g; r *= g;
                }
                l *= vol; r *= vol;
                l = Limit(l); r = Limit(r);
            }
            outLR[i * 2] = l; outLR[i * 2 + 1] = r;
            peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
        }
        Peak = peak;
    }

    static float Limit(float x) => Math.Abs(x) <= 0.85f ? x : Math.Sign(x) * (0.85f + 0.14f * (float)Math.Tanh((Math.Abs(x) - 0.85f) / 0.14f));
}

/// <summary>
/// Places speaker channels around your head for headphones (virtual surround): each speaker reaches the far ear a
/// little later (up to 0.65 ms) and duller (the head's shadow), rear speakers lose a little top end like sounds behind
/// you do. A stereo game gets two speakers in front at ±30°, which also makes long sessions less tiring.
/// </summary>
public sealed class Spatializer
{
    // 7.1 order (Windows): FL FR FC LFE BL BR SL SR. Azimuth in degrees, negative = left.
    static readonly double[] Angles71 = { -30, 30, 0, 0, -135, 135, -90, 90 };
    readonly int _rate;
    readonly float[][] _delay;
    readonly int[] _farDelay;
    readonly float[] _farGain, _lp, _lpState, _rearState;
    readonly bool[] _right;
    readonly bool[] _rear;
    int _pos;
    const int Max = 64;

    public Spatializer(int rate)
    {
        _rate = rate;
        int n = 8;
        _delay = Enumerable.Range(0, n).Select(_ => new float[Max]).ToArray();
        _farDelay = new int[n]; _farGain = new float[n]; _lp = new float[n]; _lpState = new float[n]; _rearState = new float[n];
        _right = new bool[n]; _rear = new bool[n];
        for (int i = 0; i < n; i++)
        {
            double az = Angles71[i] * Math.PI / 180, a = Math.Abs(Math.Sin(az));
            double itd = 0.0875 / 343 * (Math.Abs(az > Math.PI / 2 ? Math.PI - az : az) + a); // Woodworth
            _farDelay[i] = Math.Clamp((int)Math.Round(itd * rate), 0, Max - 1);
            _farGain[i] = (float)(1 - 0.45 * a);
            double fc = 9000 - 7200 * a; // the head's shadow: duller on the far side
            _lp[i] = (float)(1 - Math.Exp(-2 * Math.PI * fc / rate));
            _right[i] = Angles71[i] > 0;
            _rear[i] = Math.Abs(Angles71[i]) > 100;
        }
    }

    public static (float L, float R) Downmix(float[] f, int ch)
    {
        if (ch == 2) return (f[0], f[1]);
        // ITU downmix: center and surrounds at −3 dB, LFE left out.
        float l = f[0], r = f[1];
        if (ch > 2) { l += 0.707f * f[2]; r += 0.707f * f[2]; }
        for (int c = 4; c < ch; c++) { if ((c & 1) == 0) l += 0.707f * f[c]; else r += 0.707f * f[c]; }
        return (l * 0.6f, r * 0.6f);
    }

    public (float L, float R) Process(float[] f, int ch)
    {
        float l = 0, r = 0;
        int speakers = ch >= 8 ? 8 : ch >= 6 ? 6 : 2;
        for (int s = 0; s < speakers && s < ch; s++)
        {
            if (s == 3 && speakers > 2) { l += f[3] * 0.5f; r += f[3] * 0.5f; continue; } // LFE: both ears
            int idx = s; // 5.1's surrounds land on the rear slots (±135°), 7.1 adds the sides (±90°)
            float x = f[s];
            if (_rear[idx]) { _rearState[idx] += (x - _rearState[idx]) * 0.55f; x = _rearState[idx]; } // a little darker behind you
            var d = _delay[idx];
            d[_pos] = x;
            float far = d[(_pos - _farDelay[idx] + Max) % Max];
            _lpState[idx] += (far - _lpState[idx]) * _lp[idx];
            float farOut = _lpState[idx] * _farGain[idx];
            if (Angles71[idx] == 0) { l += x * 0.8f; r += x * 0.8f; }
            else if (_right[idx]) { r += x; l += farOut; }
            else { l += x; r += farOut; }
        }
        _pos = (_pos + 1) % Max;
        float k = speakers == 2 ? 0.75f : 0.5f;
        return (l * k, r * k);
    }
}
