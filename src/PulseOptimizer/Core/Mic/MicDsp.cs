using System.Reflection;
using System.Runtime.InteropServices;

namespace Pulse.Core.Mic;

/// <summary>A sound on the soundboard.</summary>
public sealed class SoundItem
{
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    /// <summary>0–1.5.</summary>
    public double Volume { get; set; } = 0.8;
}

/// <summary>The Mic studio's settings (saved).</summary>
public sealed class MicSettings
{
    public bool Enabled { get; set; }
    /// <summary>
    /// "apo": changed inside Windows by Equalizer APO, no cable (Discord keeps your real mic).
    /// "cable": Pulse processes it and sends it through VB-CABLE (needed for voice effects and the soundboard).
    /// </summary>
    public string Engine { get; set; } = "cable";
    /// <summary>Windows id of the real microphone; empty = the default mic.</summary>
    public string CaptureId { get; set; } = "";
    /// <summary>Windows id of the output (a virtual cable such as VB-CABLE); empty = found automatically.</summary>
    public string OutputId { get; set; } = "";
    public string Preset { get; set; } = "natural";

    public bool NoiseRemoval { get; set; } = true;
    /// <summary>0–1: how much of the noise-free signal replaces the original (Light 0.6, Balanced 0.85, Max 1).</summary>
    public double Strength { get; set; } = 0.85;

    public bool Gate { get; set; } = true;
    /// <summary>Sets the gate's level from the room's noise by itself.</summary>
    public bool GateAuto { get; set; } = true;
    /// <summary>Manual gate level (dBFS): quieter than this goes silent.</summary>
    public double GateDb { get; set; } = -50;

    public double BassDb { get; set; } = 2;
    public double ClarityDb { get; set; } = 3;
    public double AirDb { get; set; } = 2;
    /// <summary>10-band EQ in dB at 31, 62, 125, 250, 500, 1k, 2k, 4k, 8k, 16k Hz, on top of bass / clarity / air.</summary>
    public double[] Eq { get; set; } = new double[10];

    /// <summary>0–1: tames harsh "s" and "sh" sounds.</summary>
    public double DeEss { get; set; } = 0.4;
    /// <summary>Keeps your volume the same whether you lean in or sit back.</summary>
    public bool AutoVolume { get; set; } = true;
    /// <summary>0–1: compressor amount (evens out loud and quiet speech).</summary>
    public double Leveling { get; set; } = 0.5;
    /// <summary>0–1: room reverb.</summary>
    public double Reverb { get; set; }
    public double OutputDb { get; set; }
    /// <summary>"none", "radio", "megaphone", "robot", "chipmunk", "giant", "demon", "cave", "stadium".</summary>
    public string Effect { get; set; } = "none";

    /// <summary>Also play your processed voice in your own headphones.</summary>
    public bool Monitor { get; set; }
    /// <summary>Ctrl+Shift+M mutes and unmutes the mic.</summary>
    public bool MuteHotkey { get; set; } = true;

    public List<SoundItem> Sounds { get; set; } = new();
    /// <summary>0–1: soundboard volume for everyone.</summary>
    public double SoundVolume { get; set; } = 0.7;
    /// <summary>Hear soundboard sounds yourself, too.</summary>
    public bool HearSounds { get; set; } = true;
    /// <summary>Ctrl+Alt+1–9 play the first nine sounds, Ctrl+Alt+0 stops them.</summary>
    public bool SoundHotkeys { get; set; } = true;

    public static readonly string[] EqLabels = { "31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" };
    public static readonly double[] EqFreqs = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public static readonly (string Key, string Name, string Note, Action<MicSettings> Apply)[] Presets =
    {
        ("natural", "Natural", "Clean and true to your voice", s => s.Set(bass: 2, clarity: 3, air: 2, level: 0.5, deEss: 0.4)),
        ("broadcast", "Broadcast", "Full, warm radio voice", s => s.Set(bass: 5, clarity: 4, air: 3, level: 0.8, deEss: 0.5, eq: new double[] { 0, 1, 2, 0, -2, 0, 1, 1, 0, 0 })),
        ("deep", "Deep", "More low end", s => s.Set(bass: 7, clarity: 1, air: 0, level: 0.6, deEss: 0.3, eq: new double[] { 0, 2, 2, 1, -1, 0, 0, 0, 0, 0 })),
        ("bright", "Bright", "Cuts through game sound", s => s.Set(bass: 0, clarity: 6, air: 5, level: 0.5, deEss: 0.6, eq: new double[] { -2, -1, 0, -1, -1, 0, 1, 1, 0, 0 })),
        ("streamer", "Streamer", "Loud, punchy, even", s => s.Set(bass: 4, clarity: 5, air: 4, level: 0.9, deEss: 0.6, eq: new double[] { 0, 1, 1, -1, -2, 0, 1, 2, 1, 0 })),
        ("headset", "Headset fix", "Makes thin headset mics fuller", s => s.Set(bass: 6, clarity: 2, air: 3, level: 0.7, deEss: 0.3, eq: new double[] { 0, 0, 2, 2, 0, -1, -1, 0, 1, 1 })),
        ("raw", "Just clean", "Only removes noise", s => s.Set(bass: 0, clarity: 0, air: 0, level: 0, deEss: 0)),
    };

    public static readonly (string Key, string Name)[] Effects =
    {
        ("none", "None"), ("radio", "Radio"), ("megaphone", "Megaphone"), ("robot", "Robot"), ("chipmunk", "Chipmunk"),
        ("giant", "Giant"), ("demon", "Demon"), ("cave", "Cave"), ("stadium", "Stadium"),
    };

    void Set(double bass, double clarity, double air, double level, double deEss, double[] eq = null)
    {
        BassDb = bass; ClarityDb = clarity; AirDb = air; Leveling = level; DeEss = deEss; Effect = "none"; Reverb = 0;
        Eq = eq != null ? (double[])eq.Clone() : new double[10];
    }

    public void ApplyPreset(string key)
    {
        foreach (var p in Presets) if (p.Key == key) { p.Apply(this); Preset = key; return; }
    }

    public MicSettings Copy()
    {
        var c = (MicSettings)MemberwiseClone();
        c.Eq = (double[])(Eq ?? new double[10]).Clone();
        return c;
    }
}

/// <summary>A second-order filter (RBJ audio EQ cookbook).</summary>
public sealed class Biquad
{
    double b0, b1, b2, a1, a2, z1, z2;

    public static Biquad HighPass(double fs, double f, double q = 0.707) => Make(fs, f, q, 0, "hp");
    public static Biquad LowPass(double fs, double f, double q = 0.707) => Make(fs, f, q, 0, "lp");
    public static Biquad Peak(double fs, double f, double q, double db) => Make(fs, f, q, db, "peak");
    public static Biquad LowShelf(double fs, double f, double db) => Make(fs, f, 0.707, db, "ls");
    public static Biquad HighShelf(double fs, double f, double db) => Make(fs, f, 0.707, db, "hs");

    static Biquad Make(double fs, double f, double q, double db, string type)
    {
        f = Math.Min(f, fs * 0.45);
        double A = Math.Pow(10, db / 40), w = 2 * Math.PI * f / fs, cs = Math.Cos(w), sn = Math.Sin(w), alpha = sn / (2 * q);
        double sq = 2 * Math.Sqrt(A) * alpha;
        double b0, b1, b2, a0, a1, a2;
        switch (type)
        {
            case "hp": b0 = (1 + cs) / 2; b1 = -(1 + cs); b2 = (1 + cs) / 2; a0 = 1 + alpha; a1 = -2 * cs; a2 = 1 - alpha; break;
            case "lp": b0 = (1 - cs) / 2; b1 = 1 - cs; b2 = (1 - cs) / 2; a0 = 1 + alpha; a1 = -2 * cs; a2 = 1 - alpha; break;
            case "peak": b0 = 1 + alpha * A; b1 = -2 * cs; b2 = 1 - alpha * A; a0 = 1 + alpha / A; a1 = -2 * cs; a2 = 1 - alpha / A; break;
            case "ls":
                b0 = A * ((A + 1) - (A - 1) * cs + sq); b1 = 2 * A * ((A - 1) - (A + 1) * cs); b2 = A * ((A + 1) - (A - 1) * cs - sq);
                a0 = (A + 1) + (A - 1) * cs + sq; a1 = -2 * ((A - 1) + (A + 1) * cs); a2 = (A + 1) + (A - 1) * cs - sq; break;
            default:
                b0 = A * ((A + 1) + (A - 1) * cs + sq); b1 = -2 * A * ((A - 1) + (A + 1) * cs); b2 = A * ((A + 1) + (A - 1) * cs - sq);
                a0 = (A + 1) - (A - 1) * cs + sq; a1 = 2 * ((A - 1) - (A + 1) * cs); a2 = (A + 1) - (A - 1) * cs - sq; break;
        }
        return new Biquad { b0 = b0 / a0, b1 = b1 / a0, b2 = b2 / a0, a1 = a1 / a0, a2 = a2 / a0 };
    }

    public float Process(float x)
    {
        double y = b0 * x + z1;
        z1 = b1 * x - a1 * y + z2;
        z2 = b2 * x - a2 * y;
        return (float)y;
    }

    /// <summary>Gain (dB) of this filter at frequency f, for drawing the EQ curve.</summary>
    public double ResponseDb(double fs, double f)
    {
        double w = 2 * Math.PI * f / fs;
        var z1c = System.Numerics.Complex.FromPolarCoordinates(1, -w);
        var z2c = z1c * z1c;
        var h = (b0 + b1 * z1c + b2 * z2c) / (1 + a1 * z1c + a2 * z2c);
        return 20 * Math.Log10(Math.Max(h.Magnitude, 1e-9));
    }
}

/// <summary>RNNoise (Xiph, BSD-3-Clause): removes background noise from speech, 480 samples (10 ms at 48 kHz) at a time.</summary>
public sealed class Denoiser : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr CreateFn(IntPtr model);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] unsafe delegate float ProcessFn(IntPtr st, float* output, float* input);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DestroyFn(IntPtr st);

    static IntPtr _lib;
    static bool _tried;
    static CreateFn _create;
    static ProcessFn _process;
    static DestroyFn _destroy;
    static readonly object Gate = new();
    IntPtr _st;
    readonly float[] _in = new float[480], _out = new float[480];

    /// <summary>Loads rnnoise.dll (carried inside Pulse). False when this build doesn't include it.</summary>
    public static bool Available()
    {
        lock (Gate)
        {
            if (_lib != IntPtr.Zero) return true;
            if (_tried) return false;
            _tried = true;
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("rnnoise.dll");
                if (s == null) return false;
                var dir = Path.Combine(TweakEngine.DataDir, "native");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "rnnoise-" + s.Length + ".dll");
                if (!File.Exists(file) || new FileInfo(file).Length != s.Length) { using var f = File.Create(file); s.CopyTo(f); }
                var lib = NativeLibrary.Load(file);
                _create = Marshal.GetDelegateForFunctionPointer<CreateFn>(NativeLibrary.GetExport(lib, "rnnoise_create"));
                _process = Marshal.GetDelegateForFunctionPointer<ProcessFn>(NativeLibrary.GetExport(lib, "rnnoise_process_frame"));
                _destroy = Marshal.GetDelegateForFunctionPointer<DestroyFn>(NativeLibrary.GetExport(lib, "rnnoise_destroy"));
                _lib = lib;
                return true;
            }
            catch { _lib = IntPtr.Zero; return false; }
        }
    }

    public Denoiser()
    {
        if (!Available()) throw new InvalidOperationException("Noise removal isn't included in this build.");
        _st = _create(IntPtr.Zero);
    }

    /// <summary>Cleans one 480-sample frame in place (samples in −1…1). Returns the chance (0–1) that it's speech.</summary>
    public unsafe float Process(float[] frame)
    {
        for (int i = 0; i < 480; i++) _in[i] = frame[i] * 32768f;
        float vad;
        fixed (float* o = _out, n = _in) vad = _process(_st, o, n);
        for (int i = 0; i < 480; i++) frame[i] = _out[i] / 32768f;
        return vad;
    }

    public void Dispose()
    {
        if (_st != IntPtr.Zero) { _destroy(_st); _st = IntPtr.Zero; }
    }
}

/// <summary>Shifts pitch without changing speed: two crossfaded read heads on a short delay line.</summary>
public sealed class PitchShifter
{
    const int W = 1920; // 40 ms window at 48 kHz
    readonly float[] _buf = new float[W * 2 + 8];
    int _w;
    double _d;

    public float Process(float x, double ratio)
    {
        int n = _buf.Length;
        _buf[_w] = x;
        _d += 1 - ratio;
        if (_d < 0) _d += W; else if (_d >= W) _d -= W;
        double d2 = _d + W / 2.0;
        if (d2 >= W) d2 -= W;
        double g1 = Math.Sin(Math.PI * _d / W), g2 = Math.Sin(Math.PI * d2 / W);
        float y = (float)(g1 * g1 * Read(_d + 2) + g2 * g2 * Read(d2 + 2));
        _w = (_w + 1) % n;
        return y;

        double Read(double delay)
        {
            double pos = _w - delay;
            while (pos < 0) pos += n;
            int i = (int)pos;
            double f = pos - i;
            return _buf[i % n] * (1 - f) + _buf[(i + 1) % n] * f;
        }
    }
}

/// <summary>A small Freeverb-style room: four damped combs into two all-passes.</summary>
public sealed class Reverb
{
    readonly float[][] _comb, _ap;
    readonly int[] _ci = new int[4], _ai = new int[2];
    readonly float[] _store = new float[4];

    public Reverb()
    {
        int[] c = { 1557, 1617, 1491, 1422 }, a = { 556, 441 };
        _comb = c.Select(d => new float[(int)(d * 48000.0 / 44100)]).ToArray();
        _ap = a.Select(d => new float[(int)(d * 48000.0 / 44100)]).ToArray();
    }

    public void Clear()
    {
        foreach (var b in _comb) Array.Clear(b);
        foreach (var b in _ap) Array.Clear(b);
        Array.Clear(_store);
    }

    /// <summary>Returns the wet signal only.</summary>
    public float Process(float x, float size, float damp = 0.25f)
    {
        float input = x * 0.12f, sum = 0;
        for (int k = 0; k < 4; k++)
        {
            var b = _comb[k];
            float o = b[_ci[k]];
            _store[k] = o * (1 - damp) + _store[k] * damp;
            b[_ci[k]] = input + _store[k] * size;
            _ci[k] = (_ci[k] + 1) % b.Length;
            sum += o;
        }
        for (int k = 0; k < 2; k++)
        {
            var b = _ap[k];
            float o = b[_ai[k]];
            b[_ai[k]] = sum + o * 0.5f;
            _ai[k] = (_ai[k] + 1) % b.Length;
            sum = o - sum;
        }
        return sum;
    }
}

/// <summary>
/// The Mic studio's sound chain, for 48 kHz mono audio in 480-sample frames:
/// rumble filter → AI noise removal → noise gate (auto level) → auto volume → EQ (bass / clarity / air + 10 bands) →
/// de-esser → leveling (compressor) → voice effect → room reverb → output volume → limiter. Settings can change while it runs.
/// </summary>
public sealed class MicChain : IDisposable
{
    public const int Rate = 48000, Frame = 480;
    readonly Denoiser _denoiser;
    readonly Biquad _hp = Biquad.HighPass(Rate, 80);
    readonly Biquad _essDetect = Biquad.HighPass(Rate, 5500, 0.8), _essBand = Biquad.HighPass(Rate, 5500, 0.8);
    Biquad _bass, _clarity, _air, _fxA, _fxB;
    readonly Biquad[] _eq = new Biquad[10];
    readonly double[] _eqDb = Enumerable.Repeat(double.NaN, 10).ToArray();
    double _bassDb = double.NaN, _clarityDb = double.NaN, _airDb = double.NaN;
    string _fx;
    double _gateGain = 1, _env, _compGain = 1, _phase, _essEnv, _essGain = 1;
    double _floorDb = -60, _speechDb = -24, _autoGain = 1;
    int _hold;
    readonly float[] _dry = new float[Frame];
    readonly float[] _echo = new float[Rate * 3 / 10];
    int _echoPos;
    readonly PitchShifter _pitch = new();
    readonly Reverb _room = new(), _fxRoom = new();

    /// <summary>Latest peaks (0–1), gate state and the level the gate opens at, for the page's meters.</summary>
    public float InPeak, OutPeak;
    public bool GateOpen;
    public double GateLevelDb => _gateThreshold;
    double _gateThreshold = -50;
    public bool HasNoiseRemoval => _denoiser != null;
    /// <summary>Pass the mic through untouched (to compare).</summary>
    public bool Bypass;

    public MicChain(bool withDenoiser = true)
    {
        if (withDenoiser && Denoiser.Available()) try { _denoiser = new Denoiser(); } catch { }
    }

    public void Process(float[] f, MicSettings s)
    {
        float inPeak = 0;
        for (int i = 0; i < Frame; i++) inPeak = Math.Max(inPeak, Math.Abs(f[i]));
        InPeak = inPeak;
        if (Bypass) { OutPeak = inPeak; GateOpen = true; return; }

        for (int i = 0; i < Frame; i++) f[i] = _hp.Process(f[i]);

        // AI noise removal, blended by strength; RNNoise also says how likely this frame is speech.
        float vad = 1;
        bool ai = _denoiser != null && s.NoiseRemoval;
        if (ai)
        {
            Array.Copy(f, _dry, Frame);
            vad = _denoiser.Process(f);
            float k = (float)Math.Clamp(s.Strength, 0, 1);
            if (k < 1) for (int i = 0; i < Frame; i++) f[i] = f[i] * k + _dry[i] * (1 - k);
        }

        // Noise gate. The auto level follows the room's noise floor (the quietest the mic gets), sitting just above it.
        double db = RmsDb(f);
        if (db < _floorDb) _floorDb += (db - _floorDb) * 0.2;
        else _floorDb += Math.Min(db - _floorDb, 0.02); // creeps up slowly so speech doesn't drag it along
        _gateThreshold = s.GateAuto ? Math.Clamp(_floorDb + 10, -72, -30) : s.GateDb;
        bool open = !s.Gate || db > _gateThreshold && (!ai || vad > 0.3);
        if (open) _hold = 20; else if (_hold > 0) _hold--;
        GateOpen = open || _hold > 0;
        double target = GateOpen ? 1 : 0.01;
        for (int i = 0; i < Frame; i++)
        {
            _gateGain += (target - _gateGain) * (target > _gateGain ? 0.03 : 0.0012);
            f[i] *= (float)_gateGain;
        }

        // Auto volume: learns how loud you talk and brings it to a steady level (only while you're talking).
        if (s.AutoVolume)
        {
            double sdb = RmsDb(f);
            if (GateOpen && sdb > _gateThreshold + 6) _speechDb += (sdb - _speechDb) * 0.01;
            double want = Math.Clamp(-20 - _speechDb, -10, 14);
            double g = Math.Pow(10, want / 20);
            for (int i = 0; i < Frame; i++) { _autoGain += (g - _autoGain) * 0.0005; f[i] *= (float)_autoGain; }
        }

        // EQ (filters rebuilt only when a slider moved).
        if (s.BassDb != _bassDb) { _bassDb = s.BassDb; _bass = Biquad.LowShelf(Rate, 160, s.BassDb); }
        if (s.ClarityDb != _clarityDb) { _clarityDb = s.ClarityDb; _clarity = Biquad.Peak(Rate, 3200, 0.9, s.ClarityDb); }
        if (s.AirDb != _airDb) { _airDb = s.AirDb; _air = Biquad.HighShelf(Rate, 9000, s.AirDb); }
        var eq = s.Eq ?? Array.Empty<double>();
        for (int b = 0; b < 10; b++)
        {
            double v = b < eq.Length ? Math.Clamp(eq[b], -12, 12) : 0;
            if (v != _eqDb[b]) { _eqDb[b] = v; _eq[b] = Math.Abs(v) < 0.05 ? null : EqBand(b, v); }
        }
        for (int i = 0; i < Frame; i++)
        {
            float x = _air.Process(_clarity.Process(_bass.Process(f[i])));
            for (int b = 0; b < 10; b++) if (_eq[b] != null) x = _eq[b].Process(x);
            f[i] = x;
        }

        // De-esser: when the "s" band gets loud compared to the voice, turn just that band down.
        double ess = Math.Clamp(s.DeEss, 0, 1);
        if (ess > 0.01)
        {
            double limit = 0.35 - 0.25 * ess;
            for (int i = 0; i < Frame; i++)
            {
                float hfd = _essDetect.Process(f[i]), hf = _essBand.Process(f[i]);
                double a = Math.Abs(hfd);
                _essEnv += (a - _essEnv) * (a > _essEnv ? 0.05 : 0.002);
                double level = Math.Abs(f[i]) * 0.5 + _env * 0.5 + 1e-6;
                double g = _essEnv > limit * level * 2 ? Math.Max(0.25, limit * level * 2 / _essEnv) : 1;
                _essGain += (g - _essGain) * 0.05;
                f[i] -= (float)(hf * (1 - _essGain));
            }
        }

        // Leveling: a soft-knee compressor with make-up gain.
        double amount = Math.Clamp(s.Leveling, 0, 1);
        if (amount > 0.01)
        {
            double threshold = -12 - 16 * amount, ratio = 1 + 5 * amount, makeup = Math.Pow(10, -threshold * (1 - 1 / ratio) * 0.5 / 20);
            for (int i = 0; i < Frame; i++)
            {
                double x = Math.Abs(f[i]);
                _env += (x - _env) * (x > _env ? 0.01 : 0.0004);
                double envDb = 20 * Math.Log10(_env + 1e-9), over = envDb - threshold, gainDb;
                if (over <= -3) gainDb = 0;
                else if (over < 3) gainDb = (1 / ratio - 1) * (over + 3) * (over + 3) / 12; // 6 dB soft knee
                else gainDb = (1 / ratio - 1) * over;
                _compGain += (Math.Pow(10, gainDb / 20) - _compGain) * 0.05;
                f[i] = (float)(f[i] * _compGain * makeup);
            }
        }
        else for (int i = 0; i < Frame; i++) { double x = Math.Abs(f[i]); _env += (x - _env) * (x > _env ? 0.01 : 0.0004); }

        Effect(f, s.Effect ?? "none");

        double rv = Math.Clamp(s.Reverb, 0, 1);
        if (rv > 0.01) for (int i = 0; i < Frame; i++) f[i] += _room.Process(f[i], (float)(0.7 + 0.15 * rv)) * (float)(rv * 0.9);

        // Output volume and a limiter that never lets the voice clip.
        float gain = (float)Math.Pow(10, s.OutputDb / 20);
        float outPeak = 0;
        for (int i = 0; i < Frame; i++)
        {
            float x = f[i] * gain;
            if (Math.Abs(x) > 0.8f) x = Math.Sign(x) * (0.8f + 0.19f * (float)Math.Tanh((Math.Abs(x) - 0.8f) / 0.19f));
            f[i] = x;
            outPeak = Math.Max(outPeak, Math.Abs(x));
        }
        OutPeak = outPeak;
    }

    static Biquad EqBand(int b, double db) => b switch
    {
        0 => Biquad.LowShelf(Rate, 40, db),
        9 => Biquad.HighShelf(Rate, 12000, db),
        _ => Biquad.Peak(Rate, MicSettings.EqFreqs[b], 1.4, db),
    };

    /// <summary>The whole EQ's gain (dB) at f, for drawing its curve.</summary>
    public static double EqCurveDb(MicSettings s, double f)
    {
        double sum = Biquad.LowShelf(Rate, 160, s.BassDb).ResponseDb(Rate, f) + Biquad.Peak(Rate, 3200, 0.9, s.ClarityDb).ResponseDb(Rate, f)
                     + Biquad.HighShelf(Rate, 9000, s.AirDb).ResponseDb(Rate, f);
        var eq = s.Eq ?? Array.Empty<double>();
        for (int b = 0; b < Math.Min(10, eq.Length); b++) if (Math.Abs(eq[b]) >= 0.05) sum += EqBand(b, eq[b]).ResponseDb(Rate, f);
        return sum;
    }

    static double RmsDb(float[] f)
    {
        double rms = 0;
        for (int i = 0; i < f.Length; i++) rms += f[i] * f[i];
        return 10 * Math.Log10(rms / f.Length + 1e-12);
    }

    /// <summary>Fun voices, like Voicemod's.</summary>
    void Effect(float[] f, string fx)
    {
        if (fx != _fx)
        {
            _fx = fx;
            (_fxA, _fxB) = fx switch
            {
                "radio" => (Biquad.HighPass(Rate, 400, 0.9), Biquad.LowPass(Rate, 3200, 0.9)),
                "megaphone" => (Biquad.HighPass(Rate, 700, 1.2), Biquad.LowPass(Rate, 2600, 1.2)),
                "demon" => (Biquad.LowShelf(Rate, 200, 6), Biquad.LowPass(Rate, 5000, 0.7)),
                "giant" => (Biquad.LowShelf(Rate, 180, 4), (Biquad)null),
                "chipmunk" => (Biquad.HighPass(Rate, 200, 0.7), (Biquad)null),
                _ => ((Biquad)null, (Biquad)null),
            };
            Array.Clear(_echo);
            _fxRoom.Clear();
        }
        switch (fx)
        {
            case "radio":
                for (int i = 0; i < Frame; i++) f[i] = (float)Math.Tanh(_fxB.Process(_fxA.Process(f[i])) * 2.2) * 0.7f;
                break;
            case "megaphone":
                for (int i = 0; i < Frame; i++) f[i] = (float)Math.Tanh(_fxB.Process(_fxA.Process(f[i])) * 6) * 0.6f;
                break;
            case "robot":
                for (int i = 0; i < Frame; i++)
                {
                    _phase += 2 * Math.PI * 55 / Rate;
                    if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
                    f[i] = (float)(f[i] * Math.Sin(_phase) * 1.6);
                }
                break;
            case "chipmunk":
                for (int i = 0; i < Frame; i++) f[i] = _fxA.Process(_pitch.Process(f[i], 1.6));
                break;
            case "giant":
                for (int i = 0; i < Frame; i++) f[i] = _fxA.Process(_pitch.Process(f[i], 0.72)) * 1.1f;
                break;
            case "demon":
                for (int i = 0; i < Frame; i++)
                {
                    float y = _fxB.Process(_fxA.Process(_pitch.Process(f[i], 0.62)));
                    y = (float)Math.Tanh(y * 2.5) * 0.6f;
                    f[i] = y + _fxRoom.Process(y, 0.8f) * 0.5f;
                }
                break;
            case "cave":
                for (int i = 0; i < Frame; i++) f[i] = f[i] * 0.7f + _fxRoom.Process(f[i], 0.9f, 0.15f) * 1.1f;
                break;
            case "stadium":
                for (int i = 0; i < Frame; i++)
                {
                    float delayed = _echo[_echoPos];
                    float y = f[i] + delayed * 0.4f;
                    _echo[_echoPos] = y * 0.55f;
                    _echoPos = (_echoPos + 1) % _echo.Length;
                    f[i] = y * 0.75f + _fxRoom.Process(y, 0.86f) * 0.5f;
                }
                break;
        }
    }

    public void Dispose() => _denoiser?.Dispose();
}

/// <summary>Converts any rate to 48 kHz (or back) with 4-point Hermite interpolation, streaming.</summary>
public sealed class Resampler
{
    readonly double _step;
    double _pos;
    float _x0, _x1, _x2, _x3;
    public Resampler(int fromRate, int toRate) { _step = (double)fromRate / toRate; }

    /// <summary>Feeds input samples; output samples are appended to <paramref name="output"/>.</summary>
    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        foreach (var s in input)
        {
            _x0 = _x1; _x1 = _x2; _x2 = _x3; _x3 = s;
            while (_pos < 1)
            {
                double t = _pos;
                double c0 = _x1, c1 = 0.5 * (_x2 - _x0), c2 = _x0 - 2.5 * _x1 + 2 * _x2 - 0.5 * _x3, c3 = 0.5 * (_x3 - _x0) + 1.5 * (_x1 - _x2);
                output.Add((float)(((c3 * t + c2) * t + c1) * t + c0));
                _pos += _step;
            }
            _pos -= 1;
        }
    }
}
