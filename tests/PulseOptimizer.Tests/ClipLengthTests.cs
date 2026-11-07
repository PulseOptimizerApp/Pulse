using Pulse.Core.Clips;
using Xunit;

namespace PulseOptimizer.Tests;

public class ClipLengthTests
{
    static byte[] Box(string type, params byte[][] body)
    {
        var content = body.SelectMany(b => b).ToArray();
        int size = 8 + content.Length;
        return new byte[] { (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size }
            .Concat(System.Text.Encoding.ASCII.GetBytes(type)).Concat(content).ToArray();
    }

    static byte[] U32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    [Fact]
    public void Reads_the_length_from_the_mp4_header_even_after_the_video_data()
    {
        // ftyp, a big mdat (the video), then moov with mvhd v0: timescale 1000, duration 30500 → 30.5 s
        var mvhd = Box("mvhd", new byte[] { 0, 0, 0, 0 }, U32(0), U32(0), U32(1000), U32(30500), new byte[80]);
        var file = Box("ftyp", System.Text.Encoding.ASCII.GetBytes("isom"), U32(512))
            .Concat(Box("mdat", new byte[100_000]))
            .Concat(Box("moov", mvhd)).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"pulse-len-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, file);
        try { Assert.Equal(30.5, ClipLibrary.Mp4Seconds(new FileInfo(path)), 3); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_broken_file_has_no_length()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pulse-len-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        try { Assert.Equal(0, ClipLibrary.Mp4Seconds(new FileInfo(path))); }
        finally { File.Delete(path); }
    }
}
