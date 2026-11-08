using NAudio.CoreAudioApi;
using NAudio.Wave;
using Pulse.Core.Mic;

namespace Pulse.Core.Audio;

/// <summary>
/// Plays to one Windows device in exactly the format Windows mixes it at (its channel count and rate), so it works
/// with any device: "CABLE In 16ch", a 7.1 headset, a 44.1 kHz DAC. Stereo goes to the first two channels.
/// </summary>
public sealed class OutStream : IDisposable
{
    readonly WasapiOut _out;
    readonly BufferedWaveProvider _buf;
    readonly int _ch, _rate;
    Resampler _rl, _rr;
    int _srcRate;
    readonly List<float> _l = new(4096), _r = new(4096);
    byte[] _bytes = Array.Empty<byte>();
    public string Name { get; }

    public OutStream(MMDevice dev, int latencyMs = 20)
    {
        Name = dev.FriendlyName;
        var mix = dev.AudioClient.MixFormat; // shared mode always mixes as 32-bit float
        _ch = Math.Max(1, mix.Channels);
        _rate = mix.SampleRate;
        _buf = new BufferedWaveProvider(mix) { BufferDuration = TimeSpan.FromSeconds(1), DiscardOnBufferOverflow = true, ReadFully = true };
        _out = new WasapiOut(dev, AudioClientShareMode.Shared, true, latencyMs);
        _out.Init(_buf);
        _out.Play();
    }

    public double BufferedMs => _buf.BufferedDuration.TotalMilliseconds;

    /// <summary>Adds sound (left/right, any rate). Mono: pass the same span twice.</summary>
    public void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right, int srcRate)
    {
        int n;
        ReadOnlySpan<float> L = left, R = right;
        if (srcRate != _rate)
        {
            if (_srcRate != srcRate) { _srcRate = srcRate; _rl = new Resampler(srcRate, _rate); _rr = new Resampler(srcRate, _rate); }
            _l.Clear(); _r.Clear();
            _rl.Process(left, _l); _rr.Process(right, _r);
            n = Math.Min(_l.Count, _r.Count);
            L = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_l)[..n];
            R = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_r)[..n];
        }
        else n = Math.Min(left.Length, right.Length);
        int need = n * _ch * 4;
        if (_bytes.Length < need) _bytes = new byte[need];
        else Array.Clear(_bytes, 0, need);
        var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(_bytes.AsSpan(0, need));
        for (int i = 0; i < n; i++)
        {
            if (_ch == 1) f[i] = (L[i] + R[i]) * 0.5f;
            else { f[i * _ch] = L[i]; f[i * _ch + 1] = R[i]; }
        }
        _buf.AddSamples(_bytes, 0, need);
    }

    public void Dispose()
    {
        try { _out.Stop(); } catch { }
        try { _out.Dispose(); } catch { }
    }
}

/// <summary>Recognises virtual audio cables (VB-CABLE incl. "CABLE In 16ch", Hi-Fi Cable, CABLE-A/B, Voicemeeter).</summary>
public static class Cables
{
    public static bool IsVirtual(string name) => name != null &&
        (name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CABLE", StringComparison.OrdinalIgnoreCase)
         || name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) || name.Contains("Hi-Fi Cable", StringComparison.OrdinalIgnoreCase));

    /// <summary>"CABLE Output (VB-Audio Virtual Cable)" → "VB-Audio Virtual Cable": the part both sides of one cable share.</summary>
    public static string Family(string name)
    {
        if (name == null) return "";
        int a = name.LastIndexOf('('), b = name.LastIndexOf(')');
        var f = a >= 0 && b > a ? name[(a + 1)..b] : name;
        // Without the driver part, tell cables apart by their first word ("CABLE-A", "Hi-Fi").
        return a >= 0 ? f : name.Split(' ')[0];
    }

    public static bool IsVoicemeeter(string name) => name?.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Whether a playback and a recording device are the two ends of the same cable. Voicemeeter has many numbered
    /// ins and outs that only connect through its mixer, so "In 3" only pairs with "Out 3" (and "AUX" with "AUX").
    /// </summary>
    public static bool SameCable(string a, string b)
    {
        if (a == null || b == null || !Family(a).Equals(Family(b), StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsVoicemeeter(a)) return true;
        return a.Contains("AUX", StringComparison.OrdinalIgnoreCase) == b.Contains("AUX", StringComparison.OrdinalIgnoreCase) && VmIndex(a) == VmIndex(b);
    }

    /// <summary>"Voicemeeter In 3 (...)" → 3, "Voicemeeter Out B2 (...)" → 2, "Voicemeeter Input (...)" → 1.</summary>
    internal static int VmIndex(string name)
    {
        int cut = name.IndexOf('(');
        var label = cut > 0 ? name[..cut] : name;
        var m = System.Text.RegularExpressions.Regex.Match(label, @"(\d+)\s*$");
        return m.Success ? int.Parse(m.Groups[1].Value) : 1;
    }

    static (DateTime At, List<(string Id, string Name, bool Input)> List) _cache;

    /// <summary>Active devices (cached for 5 s: asking Windows is slow enough to make a page stutter).</summary>
    public static List<(string Id, string Name, bool Input)> Devices(bool fresh = false)
    {
        if (!fresh && _cache.List != null && (DateTime.UtcNow - _cache.At).TotalSeconds < 5) return _cache.List;
        var list = new List<(string, string, bool)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
                foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    try { list.Add((d.ID, d.FriendlyName, flow == DataFlow.Capture)); } catch { }
                    d.Dispose();
                }
        }
        catch { }
        _cache = (DateTime.UtcNow, list);
        return list;
    }

    /// <summary>The other end's name of a cable ("CABLE In 16ch (...)" → "CABLE Output (...)").</summary>
    public static string OtherEnd(string name, bool wantInput)
    {
        var hit = Devices().FirstOrDefault(d => d.Input == wantInput && IsVirtual(d.Name) && SameCable(d.Name, name));
        return hit.Name ?? (wantInput ? name.Replace("Input", "Output").Replace(" In ", " Output ") : name.Replace("Output", "Input"));
    }
}
