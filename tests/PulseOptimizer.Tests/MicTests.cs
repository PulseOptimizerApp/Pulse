using Pulse.Core.Mic;
using Xunit;

namespace PulseOptimizer.Tests;

public class MicTests
{
    static MicSettings Flat() => new()
    {
        NoiseRemoval = false, Gate = false, BassDb = 0, ClarityDb = 0, AirDb = 0, DeEss = 0, AutoVolume = false, Leveling = 0, OutputDb = 0, Effect = "none",
    };

    static float[] Sine(double hz, double amp, int frames, int start = 0) =>
        Enumerable.Range(start * MicChain.Frame, frames * MicChain.Frame).Select(i => (float)(amp * Math.Sin(2 * Math.PI * hz * i / MicChain.Rate))).ToArray();

    static float[] Run(MicChain chain, MicSettings s, float[] input)
    {
        var output = new float[input.Length];
        var f = new float[MicChain.Frame];
        for (int at = 0; at + MicChain.Frame <= input.Length; at += MicChain.Frame)
        {
            Array.Copy(input, at, f, 0, MicChain.Frame);
            chain.Process(f, s);
            Array.Copy(f, 0, output, at, MicChain.Frame);
        }
        return output;
    }

    static double Rms(float[] x, int from) => Math.Sqrt(x.Skip(from).Select(v => (double)v * v).Average());

    [Fact]
    public void A_flat_chain_leaves_the_voice_alone()
    {
        using var chain = new MicChain(withDenoiser: false);
        var input = Sine(1000, 0.3, 100);
        var output = Run(chain, Flat(), input);
        Assert.InRange(Rms(output, 4800) / Rms(input, 4800), 0.9, 1.1);
    }

    [Fact]
    public void The_gate_closes_on_silence_and_opens_on_speech()
    {
        using var chain = new MicChain(withDenoiser: false);
        var s = Flat(); s.Gate = true;
        var rnd = new Random(1);
        var hiss = Enumerable.Range(0, 300 * MicChain.Frame).Select(_ => (float)((rnd.NextDouble() - 0.5) * 0.002)).ToArray();
        var quiet = Run(chain, s, hiss);
        Assert.False(chain.GateOpen);
        Assert.True(Rms(quiet, quiet.Length - 4800) < Rms(hiss, 0) * 0.2);
        Run(chain, s, Sine(400, 0.3, 30));
        Assert.True(chain.GateOpen);
    }

    [Fact]
    public void The_limiter_never_lets_it_clip()
    {
        using var chain = new MicChain(withDenoiser: false);
        var s = Flat(); s.OutputDb = 12; s.BassDb = 12; s.Leveling = 1;
        var output = Run(chain, s, Sine(150, 0.99, 100));
        Assert.True(output.Max(Math.Abs) < 1f);
    }

    [Fact]
    public void Chipmunk_raises_the_pitch_and_giant_lowers_it()
    {
        static int Crossings(float[] x, int from) { int n = 0; for (int i = from + 1; i < x.Length; i++) if (x[i - 1] < 0 && x[i] >= 0) n++; return n; }
        var input = Sine(300, 0.3, 200);
        int baseCount = Crossings(input, 9600);
        foreach (var (fx, lo, hi) in new[] { ("chipmunk", 1.3, 1.9), ("giant", 0.55, 0.9) })
        {
            using var chain = new MicChain(withDenoiser: false);
            var s = Flat(); s.Effect = fx;
            var output = Run(chain, s, input);
            Assert.InRange(Crossings(output, 9600) / (double)baseCount, lo, hi);
        }
    }

    [Fact]
    public void Every_effect_runs_and_stays_in_range()
    {
        foreach (var (key, _) in MicSettings.Effects)
        {
            using var chain = new MicChain(withDenoiser: false);
            var s = Flat(); s.Effect = key; s.Reverb = 0.5;
            var output = Run(chain, s, Sine(220, 0.5, 60));
            Assert.All(output, v => Assert.True(float.IsFinite(v) && Math.Abs(v) < 1f, key));
        }
    }

    [Fact]
    public void The_eq_curve_matches_its_sliders()
    {
        var s = Flat();
        Assert.InRange(MicChain.EqCurveDb(s, 1000), -0.1, 0.1);
        s.BassDb = 6;
        Assert.InRange(MicChain.EqCurveDb(s, 40), 5, 6.5);
        s = Flat(); s.Eq[5] = 6; // 1 kHz
        Assert.InRange(MicChain.EqCurveDb(s, 1000), 5.5, 6.5);
        Assert.InRange(MicChain.EqCurveDb(s, 100), -0.5, 0.5);
    }

    [Fact]
    public void Presets_set_the_sound_and_copies_are_separate()
    {
        var s = new MicSettings();
        s.ApplyPreset("broadcast");
        Assert.Equal("broadcast", s.Preset);
        Assert.Equal(5, s.BassDb);
        var c = s.Copy();
        c.Eq[0] = 9;
        Assert.NotEqual(9, s.Eq[0]);
        Assert.All(MicSettings.Presets, p => Assert.False(string.IsNullOrEmpty(p.Note)));
    }

    [Fact]
    public void Resampling_to_48k_keeps_the_length_right()
    {
        var r = new Resampler(44100, 48000);
        var output = new List<float>();
        r.Process(new float[44100], output);
        Assert.InRange(output.Count, 47990, 48010);
    }

    [Fact]
    public void Noise_removal_quiets_noise_when_the_library_is_there()
    {
        if (!Denoiser.Available()) return; // built in CI; not on every dev machine
        using var chain = new MicChain();
        var s = Flat(); s.NoiseRemoval = true; s.Strength = 1;
        var rnd = new Random(2);
        var noise = Enumerable.Range(0, 300 * MicChain.Frame).Select(_ => (float)((rnd.NextDouble() - 0.5) * 0.1)).ToArray();
        var output = Run(chain, s, noise);
        Assert.True(Rms(output, 48000) < Rms(noise, 48000) * 0.5);
    }

    [Fact]
    public void Cable_names_are_recognised()
    {
        Assert.True(MicStudio.IsCable("CABLE Input (VB-Audio Virtual Cable)"));
        Assert.True(MicStudio.IsCable("Voicemeeter Input (VB-Audio Voicemeeter VAIO)"));
        Assert.False(MicStudio.IsCable("Speakers (Realtek(R) Audio)"));
    }
}
