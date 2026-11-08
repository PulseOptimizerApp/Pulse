using Pulse.Core.Audio;
using Xunit;

namespace PulseOptimizer.Tests;

public class GameAudioTests
{
    static byte[] Interleaved(int ch, int frames, Func<int, int, float> sample)
    {
        var b = new byte[frames * ch * 4];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < ch; c++) BitConverter.TryWriteBytes(new Span<byte>(b, (i * ch + c) * 4, 4), sample(i, c));
        return b;
    }

    static (double L, double R) Rms(float[] lr, int from)
    {
        double l = 0, r = 0; int n = 0;
        for (int i = from; i < lr.Length / 2; i++) { l += lr[i * 2] * lr[i * 2]; r += lr[i * 2 + 1] * lr[i * 2 + 1]; n++; }
        return (Math.Sqrt(l / n), Math.Sqrt(r / n));
    }

    [Fact]
    public void Flat_settings_pass_stereo_through()
    {
        var s = new GameAudioSettings(); s.ApplyPreset("flat");
        var chain = new GameAudioChain(48000);
        var input = Interleaved(2, 48000, (i, c) => (float)(0.3 * Math.Sin(2 * Math.PI * 1000 * i / 48000)));
        var outLR = new float[96000];
        chain.Process(input, 48000, 2, 4, outLR, s);
        var (l, r) = Rms(outLR, 4800);
        Assert.InRange(l / (0.3 / Math.Sqrt(2)), 0.95, 1.05);
        Assert.InRange(r, l * 0.99, l * 1.01);
    }

    [Fact]
    public void A_left_side_speaker_is_louder_in_the_left_ear()
    {
        var s = new GameAudioSettings(); s.ApplyPreset("flat"); s.Spatial = true;
        var chain = new GameAudioChain(48000);
        // 7.1 with sound only on the left side speaker (channel 6).
        var input = Interleaved(8, 48000, (i, c) => c == 6 ? (float)(0.4 * Math.Sin(2 * Math.PI * 2000 * i / 48000)) : 0);
        var outLR = new float[96000];
        chain.Process(input, 48000, 8, 4, outLR, s);
        var (l, r) = Rms(outLR, 4800);
        Assert.True(l > r * 1.5, $"left {l} right {r}");
        Assert.All(outLR, v => Assert.True(float.IsFinite(v) && Math.Abs(v) < 1));
    }

    [Fact]
    public void Every_preset_has_ten_bands()
    {
        Assert.All(GameAudioSettings.Presets, p => Assert.Equal(10, p.Eq.Length));
        Assert.Equal("CABLE Input", GameAudio.PlaybackNameFor("CABLE Output"));
        Assert.Equal("Hi-Fi Cable Input (VB-Audio Hi-Fi Cable)", GameAudio.PlaybackNameFor("Hi-Fi Cable Output (VB-Audio Hi-Fi Cable)"));
    }
}
