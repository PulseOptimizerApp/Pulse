using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Pulse.Core.Audio;

namespace Pulse.Core.Mic;

/// <summary>
/// Runs the Mic studio: your real mic → <see cref="MicChain"/> → a virtual cable (VB-CABLE or Voicemeeter) that Discord,
/// games and OBS pick as their mic. Also plays the soundboard into the cable, lets you hear yourself, and does a
/// before/after sound check. Everything runs on the CPU in about 1% of one core; nothing runs while it's off.
/// </summary>
public static class MicStudio
{
    public static MicSettings S => TweakEngine.State.Mic ??= new MicSettings();

    static readonly object Lock = new();
    static WasapiCapture _cap;
    static OutStream _out, _hear;
    static MicChain _chain;
    static Resampler _resampler;
    static readonly List<float> _pending = new(4096), _mono = new(4096);
    static readonly float[] _frame = new float[MicChain.Frame], _mix = new float[MicChain.Frame];
    static readonly List<Voice> _voices = new();
    static Timer _retry;
    static int _starting;

    sealed class Voice { public float[] Data; public int Pos; public float Volume; public bool ToCable, ToHear; public string Tag; }

    public static bool Running => _cap != null;
    /// <summary>What's wrong right now, in plain words; null when everything works.</summary>
    public static string Problem { get; private set; }
    /// <summary>Name of the cable the voice goes to; null when there isn't one.</summary>
    public static string OutputName { get; private set; }
    public static string InputName { get; private set; }
    public static bool Muted { get; private set; }
    public static bool HasNoiseRemoval => _chain?.HasNoiseRemoval ?? Denoiser.Available();
    public static event Action Changed;

    public static float InPeak => _chain?.InPeak ?? 0;
    public static float OutPeak => Muted ? 0 : _chain?.OutPeak ?? 0;
    public static bool GateOpen => _chain?.GateOpen ?? false;
    public static double GateLevelDb => _chain?.GateLevelDb ?? S.GateDb;

    /// <summary>Hear your mic untouched while held (to compare).</summary>
    public static bool Bypass { get => _chain?.Bypass ?? false; set { if (_chain != null) _chain.Bypass = value || ApoMode; } }

    public static void Init()
    {
        ApplyApo();
        if (S.Enabled) Start();
    }

    /// <summary>No-cable mode: hands the mic settings to Equalizer APO (or clears them when off or in cable mode).</summary>
    public static string ApplyApo()
    {
        if (!EqApo.Installed) return null;
        var copy = S.Copy();
        copy.Enabled = S.Enabled && S.Engine == "apo";
        var (id, ch) = EqApo.Resolve(S.CaptureId, capture: true);
        return EqApo.ApplyMic(copy, id, ch == 1);
    }

    /// <summary>No cable: Windows changes the mic itself, Pulse only listens (meters, sound check, hearing yourself).</summary>
    public static bool ApoMode => S.Engine == "apo";

    public static void Shutdown()
    {
        UnregisterHotkeys();
        Stop();
    }

    /// <summary>Settings that need a restart changed (devices, on/off).</summary>
    public static void Refresh()
    {
        ApplyApo();
        if (S.Enabled) { Stop(); Start(); } else Stop();
        RegisterHotkeys();
    }

    // ───────── devices ─────────

    public record Device(string Id, string Name, bool IsDefault);


    /// <summary>A virtual cable (VB-CABLE, including newer "CABLE In 16ch", Hi-Fi Cable, Voicemeeter).</summary>
    public static bool IsCable(string name) => Cables.IsVirtual(name);
    static bool IsCableOut(string name) => Cables.IsVirtual(name);

    /// <summary>Real microphones (virtual cable outputs are left out so Pulse can't feed itself).</summary>
    public static List<Device> Mics() => List(DataFlow.Capture).Where(d => !IsCableOut(d.Name)).ToList();
    public static List<Device> Outputs() => List(DataFlow.Render);
    /// <summary>The cable for your voice: VB-CABLE first (any version), then Voicemeeter. Hi-Fi Cable is left for Headphones.</summary>
    public static Device FindCable()
    {
        var outs = Outputs().Where(d => IsCable(d.Name)).ToList();
        return outs.FirstOrDefault(d => d.Name.StartsWith("CABLE", StringComparison.OrdinalIgnoreCase) && Cables.Family(d.Name).Equals("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase))
            ?? outs.FirstOrDefault(d => d.Name.StartsWith("CABLE", StringComparison.OrdinalIgnoreCase))
            ?? outs.FirstOrDefault(d => d.Name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase))
            ?? outs.FirstOrDefault(d => !d.Name.Contains("Hi-Fi", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What apps pick as their mic for this cable ("CABLE In 16ch (...)" → "CABLE Output (...)").</summary>
    public static string MicNameFor(string cable) => Cables.OtherEnd(cable, wantInput: true);

    static List<Device> List(DataFlow flow) =>
        Cables.Devices().Where(d => d.Input == (flow == DataFlow.Capture)).Select(d => new Device(d.Id, d.Name, false)).OrderBy(d => d.Name).ToList();

    static MMDevice Get(MMDeviceEnumerator en, string id, DataFlow flow, Role role)
    {
        if (!string.IsNullOrEmpty(id)) try { var d = en.GetDevice(id); if (d.State == DeviceState.Active) return d; } catch { }
        try { return en.GetDefaultAudioEndpoint(flow, role); } catch { return null; }
    }

    // ───────── start / stop ─────────

    public static void Start()
    {
        if (Interlocked.Exchange(ref _starting, 1) == 1) return;
        try
        {
            Stop(keepRetry: true);
            Problem = null;
            using var en = new MMDeviceEnumerator();
            var mic = Get(en, S.CaptureId, DataFlow.Capture, Role.Communications);
            // Windows' default mic is often the cable itself once VB-CABLE is set up for Discord: use a real mic then.
            if (mic != null && IsCableOut(mic.FriendlyName) && string.IsNullOrEmpty(S.CaptureId))
            {
                var real = Mics().FirstOrDefault();
                mic = real != null ? en.GetDevice(real.Id) : null;
            }
            if (mic == null) { Fail("No microphone found. Plug one in and Pulse will pick it up."); return; }
            if (IsCableOut(mic.FriendlyName)) { Fail("That's the virtual cable, not a microphone. Pick your real mic."); return; }
            InputName = mic.FriendlyName;

            // The voice goes to the chosen cable, or to the first one Pulse finds.
            MMDevice outDev = null;
            if (!string.IsNullOrEmpty(S.OutputId)) outDev = Get(en, S.OutputId, DataFlow.Render, Role.Multimedia);
            if (outDev == null) { var c = FindCable(); if (c != null) outDev = Get(en, c.Id, DataFlow.Render, Role.Multimedia); }
            if (outDev != null && !IsCable(outDev.FriendlyName) && string.IsNullOrEmpty(S.OutputId)) outDev = null;
            if (ApoMode) outDev = null;

            lock (Lock)
            {
                _chain = new MicChain();
                _pending.Clear();
                if (outDev != null)
                {
                    OutputName = outDev.FriendlyName;
                    _out = new OutStream(outDev, 20);
                }
                else { OutputName = null; Problem = ApoMode ? null : "NoCable"; }
                _chain.Bypass = ApoMode;

                _cap = new WasapiCapture(mic, true, 10);
                var wf = _cap.WaveFormat;
                _resampler = wf.SampleRate == MicChain.Rate ? null : new Resampler(wf.SampleRate, MicChain.Rate);
                _cap.DataAvailable += (_, e) => OnData(e.Buffer, e.BytesRecorded, wf);
                _cap.RecordingStopped += (_, e) =>
                {
                    if (e.Exception != null && S.Enabled) ThreadPool.QueueUserWorkItem(_ => Fail("The microphone stopped (unplugged or taken by another app). Trying again…"));
                };
                _cap.StartRecording();
            }
            if (S.Monitor) EnsureHear();
            RegisterHotkeys();
        }
        catch (Exception ex)
        {
            Fail(ex.HResult == unchecked((int)0x8889000A) ? "Another app is using the mic in exclusive mode. Turn off \"Allow apps to take exclusive control\" for it in Windows sound settings." : "The Mic studio couldn't start: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _starting, 0);
            Changed?.Invoke();
        }
    }

    static void Fail(string message)
    {
        Problem = message;
        Stop(keepRetry: true);
        // Devices come and go (headset unplugged, cable just installed): try again every few seconds.
        _retry ??= new Timer(_ => { if (S.Enabled && !Running) Start(); else if (Running) { _retry?.Dispose(); _retry = null; } }, null, 4000, 4000);
        Changed?.Invoke();
    }

    public static void Stop() => Stop(false);

    static void Stop(bool keepRetry)
    {
        if (!keepRetry) { _retry?.Dispose(); _retry = null; Problem = null; }
        WasapiCapture cap; OutStream o, h; MicChain chain;
        // Take everything out under the lock, then dispose outside it: disposing waits for the audio thread, which may be
        // waiting for the lock.
        lock (Lock)
        {
            cap = _cap; o = _out; h = _hear; chain = _chain;
            _cap = null; _out = null; _hear = null; _chain = null;
            lock (_voices) _voices.Clear();
            lock (_clipCache) _clipCache.Clear();
            _checkRec = null;
        }
        try { cap?.StopRecording(); } catch { }
        try { cap?.Dispose(); } catch { }
        o?.Dispose();
        h?.Dispose();
        chain?.Dispose();
        if (!keepRetry) Changed?.Invoke();
    }

    static void EnsureHear()
    {
        lock (Lock)
        {
            if (_hear != null || _cap == null) return;
            try
            {
                using var en = new MMDeviceEnumerator();
                var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (IsCable(dev.FriendlyName)) return; // never echo into the cable
                _hear = new OutStream(dev, 30);
            }
            catch { _hear = null; }
        }
    }

    public static void SetMonitor(bool on)
    {
        S.Monitor = on;
        if (on) EnsureHear();
        Changed?.Invoke();
    }

    public static void ToggleMute()
    {
        Muted = !Muted;
        Blip(!Muted);
        Changed?.Invoke();
    }

    // ───────── the audio thread ─────────

    static void OnData(byte[] buf, int count, WaveFormat wf)
    {
        // This runs on the audio thread: an exception here would close Pulse, so it's caught and the studio restarts.
        try { OnDataCore(buf, count, wf); }
        catch (Exception ex) { ThreadPool.QueueUserWorkItem(_ => Fail("The Mic studio hit a problem and restarted: " + ex.Message)); }
    }

    static void OnDataCore(byte[] buf, int count, WaveFormat wf)
    {
        lock (Lock)
        {
            if (_chain == null) return;
            int ch = Math.Max(1, wf.Channels), bps = wf.BitsPerSample / 8, n = count / (bps * ch);
            _mono.Clear();
            for (int i = 0; i < n; i++)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++)
                {
                    int o = (i * ch + c) * bps;
                    sum += bps switch
                    {
                        4 => BitConverter.ToSingle(buf, o),
                        2 => BitConverter.ToInt16(buf, o) / 32768f,
                        3 => ((buf[o] << 8 | buf[o + 1] << 16 | buf[o + 2] << 24) >> 8) / 8388608f,
                        _ => 0,
                    };
                }
                // Many mics only fill one channel of a stereo stream; take the louder side rather than halving it.
                _mono.Add(ch == 1 ? sum : sum / ch * 1.4142f);
            }
            if (_resampler != null) _resampler.Process(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_mono), _pending);
            else _pending.AddRange(_mono);

            int at = 0;
            while (_pending.Count - at >= MicChain.Frame)
            {
                _pending.CopyTo(at, _frame, 0, MicChain.Frame);
                at += MicChain.Frame;
                ProcessFrame();
            }
            if (at > 0) _pending.RemoveRange(0, at);
        }
    }

    static void ProcessFrame()
    {
        if (_checkRec != null && _checkPos < _checkRec.Length)
        {
            int k = Math.Min(MicChain.Frame, _checkRec.Length - _checkPos);
            Array.Copy(_frame, 0, _checkRec, _checkPos, k);
            _checkPos += k;
            if (_checkPos >= _checkRec.Length) { _checkDone = _checkRec; _checkRec = null; ThreadPool.QueueUserWorkItem(_ => { CheckFinished?.Invoke(); Changed?.Invoke(); }); }
        }

        _chain.Process(_frame, S);
        if (Muted) Array.Clear(_frame);

        // To the cable: voice + soundboard.
        if (_out != null)
        {
            Array.Copy(_frame, _mix, MicChain.Frame);
            MixVoices(_mix, cable: true);
            // Clocks drift a little between the mic and the cable; never let the delay build up.
            if (_out.BufferedMs < 80) _out.Write(_mix, _mix, MicChain.Rate);
        }

        // To your headphones: your voice (when listening to yourself) + sounds you hear yourself.
        if (_hear != null)
        {
            bool checkPlaying;
            lock (_voices) checkPlaying = _voices.Any(v => v.Tag == "check");
            if (S.Monitor && !checkPlaying) Array.Copy(_frame, _mix, MicChain.Frame); else Array.Clear(_mix);
            MixVoices(_mix, cable: false);
            if (_hear.BufferedMs < 90) _hear.Write(_mix, _mix, MicChain.Rate);
        }
        else
        {
            lock (_voices) _voices.RemoveAll(v => !v.ToCable && v.Pos >= v.Data.Length);
        }
    }

    static void MixVoices(float[] mix, bool cable)
    {
        lock (_voices)
        {
            if (_voices.Count == 0) return;
            foreach (var v in _voices)
            {
                if (cable ? !v.ToCable : !v.ToHear) continue;
                int pos = v.Pos;
                for (int i = 0; i < MicChain.Frame && pos < v.Data.Length; i++, pos++)
                {
                    float x = mix[i] + v.Data[pos] * v.Volume;
                    mix[i] = Math.Abs(x) > 0.95f ? Math.Sign(x) * 0.95f : x;
                }
            }
            // Positions move on once per frame: after the cable pass when it exists, otherwise after the headphones pass.
            if (!cable || _out == null || _hear == null)
            {
                foreach (var v in _voices) v.Pos += MicChain.Frame;
                _voices.RemoveAll(v => v.Pos >= v.Data.Length);
            }
        }
    }


    // ───────── soundboard ─────────

    static readonly Dictionary<string, float[]> _clipCache = new();
    const int MaxClipSeconds = 60;

    /// <summary>Plays a sound into the cable (and to you, when "hear sounds" is on).</summary>
    public static async Task<string> Play(SoundItem item)
    {
        if (!Running) return "Turn the Mic studio on to use the soundboard.";
        float[] data;
        try { data = await Task.Run(() => Decode(item.File)); }
        catch { return "Couldn't play that file."; }
        if (data == null) return "Couldn't play that file.";
        if (S.HearSounds) EnsureHear();
        float vol = (float)(Math.Clamp(item.Volume, 0, 1.5) * Math.Clamp(S.SoundVolume, 0, 1));
        lock (_voices)
        {
            _voices.RemoveAll(v => v.Tag == item.File); // pressing again restarts it
            _voices.Add(new Voice { Data = data, Volume = vol, ToCable = _out != null, ToHear = S.HearSounds, Tag = item.File });
        }
        return null;
    }

    public static void StopSounds()
    {
        lock (_voices) _voices.RemoveAll(v => v.Tag != "check" && v.Tag != "blip");
    }

    public static bool IsPlaying(SoundItem item) { lock (_voices) return _voices.Any(v => v.Tag == item.File); }

    static float[] Decode(string file)
    {
        lock (_clipCache) if (_clipCache.TryGetValue(file, out var hit)) return hit;
        using var reader = new MediaFoundationReader(file);
        var sp = reader.ToSampleProvider();
        int ch = sp.WaveFormat.Channels, rate = sp.WaveFormat.SampleRate;
        var mono = new List<float>(rate * 4);
        var buf = new float[4096 * ch];
        int read;
        while ((read = sp.Read(buf, 0, buf.Length)) > 0 && mono.Count < rate * MaxClipSeconds)
            for (int i = 0; i + ch <= read; i += ch)
            {
                float s = 0;
                for (int c = 0; c < ch; c++) s += buf[i + c];
                mono.Add(s / ch);
            }
        float[] data;
        if (rate == MicChain.Rate) data = mono.ToArray();
        else
        {
            var outList = new List<float>((int)(mono.Count * (double)MicChain.Rate / rate) + 16);
            new Resampler(rate, MicChain.Rate).Process(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mono), outList);
            data = outList.ToArray();
        }
        lock (_clipCache)
        {
            if (_clipCache.Count >= 6) _clipCache.Remove(_clipCache.Keys.First());
            _clipCache[file] = data;
        }
        return data;
    }

    /// <summary>A short two-note sound in your headphones when you mute (falling) or unmute (rising).</summary>
    static void Blip(bool up)
    {
        if (!Running) return;
        EnsureHear();
        int n = MicChain.Rate / 7;
        var d = new float[n * 2];
        for (int i = 0; i < d.Length; i++)
        {
            int part = i / n, j = i % n;
            double f = (up ? new[] { 660.0, 990.0 } : new[] { 880.0, 520.0 })[part];
            double env = Math.Sin(Math.PI * j / n);
            d[i] = (float)(Math.Sin(2 * Math.PI * f * i / MicChain.Rate) * env * 0.18);
        }
        lock (_voices) _voices.Add(new Voice { Data = d, Volume = 1, ToHear = true, Tag = "blip" });
    }

    // ───────── sound check ─────────

    static float[] _checkRec, _checkDone;
    static int _checkPos;
    public static event Action CheckFinished;
    public const int CheckSeconds = 6;
    public static bool Checking => _checkRec != null;
    public static bool HasCheck => _checkDone != null;

    /// <summary>Records a few seconds of your raw mic, to play back before and after.</summary>
    public static bool StartCheck()
    {
        if (!Running) return false;
        lock (Lock) { _checkPos = 0; _checkRec = new float[MicChain.Rate * CheckSeconds]; }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Plays the recording as it came from the mic, or through the current settings.</summary>
    public static void PlayCheck(bool processed)
    {
        var raw = _checkDone;
        if (raw == null || !Running) return;
        EnsureHear();
        float[] data = (float[])raw.Clone();
        if (processed)
        {
            using var chain = new MicChain();
            var s = S.Copy();
            var f = new float[MicChain.Frame];
            for (int at = 0; at + MicChain.Frame <= data.Length; at += MicChain.Frame)
            {
                Array.Copy(data, at, f, 0, MicChain.Frame);
                chain.Process(f, s);
                Array.Copy(f, 0, data, at, MicChain.Frame);
            }
        }
        lock (_voices)
        {
            _voices.RemoveAll(v => v.Tag == "check");
            _voices.Add(new Voice { Data = data, Volume = 1, ToHear = true, Tag = "check" });
        }
    }

    public static void StopCheck() { lock (_voices) _voices.RemoveAll(v => v.Tag == "check"); }

    // ───────── hotkeys: Ctrl+Shift+M mute, Ctrl+Alt+1–9 sounds, Ctrl+Alt+0 stop ─────────

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    const int MuteId = 0x5D00, SoundId = 0x5D10;
    static HwndSource _hotkeys;
    static readonly List<int> _registered = new();
    /// <summary>False when another app already owns Ctrl+Shift+M.</summary>
    public static bool MuteHotkeyWorks { get; private set; } = true;

    static void RegisterHotkeys()
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(RegisterHotkeys); return; }
        UnregisterHotkeys();
        if (!S.Enabled || !(S.MuteHotkey || S.SoundHotkeys)) return;
        _hotkeys = new HwndSource(new HwndSourceParameters("Pulse mic hotkeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) });
        _hotkeys.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg != 0x0312) return IntPtr.Zero;
            int id = w.ToInt32();
            if (id == MuteId) ToggleMute();
            else if (id == SoundId) StopSounds();
            else if (id > SoundId && id <= SoundId + 9 && id - SoundId - 1 < S.Sounds.Count) _ = Play(S.Sounds[id - SoundId - 1]);
            handled = true;
            return IntPtr.Zero;
        });
        if (S.MuteHotkey)
        {
            MuteHotkeyWorks = RegisterHotKey(_hotkeys.Handle, MuteId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x4D);
            if (MuteHotkeyWorks) _registered.Add(MuteId);
        }
        if (S.SoundHotkeys)
            for (int i = 0; i <= 9; i++)
                if (RegisterHotKey(_hotkeys.Handle, SoundId + i, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)(0x30 + i))) _registered.Add(SoundId + i);
    }

    static void UnregisterHotkeys()
    {
        if (_hotkeys == null) return;
        foreach (var id in _registered) UnregisterHotKey(_hotkeys.Handle, id);
        _registered.Clear();
        _hotkeys.Dispose();
        _hotkeys = null;
    }

    /// <summary>Hotkeys follow the settings (called after a hotkey switch or the sound list changed).</summary>
    public static void HotkeysChanged() => RegisterHotkeys();
}
