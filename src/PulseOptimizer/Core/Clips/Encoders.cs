namespace Pulse.Core.Clips;

/// <summary>A video encoder FFmpeg can use on this PC.</summary>
public record Encoder(string Name, string Codec, string Vendor, bool Hardware)
{
    /// <summary>Hardware encoders that take the captured frames straight from the graphics card (no copy to the CPU).</summary>
    public bool TakesGpuFrames => Vendor is "NVIDIA" or "AMD";
}

/// <summary>
/// Finds which encoders work here. Hardware encoders (NVIDIA NVENC, AMD AMF, Intel Quick Sync) run on a separate part of
/// the graphics chip, so recording costs very few frames. The CPU encoder is only a last resort.
/// </summary>
public static class Encoders
{
    static readonly Encoder[] Candidates =
    {
        new("h264_nvenc", "h264", "NVIDIA", true), new("hevc_nvenc", "hevc", "NVIDIA", true), new("av1_nvenc", "av1", "NVIDIA", true),
        new("h264_amf", "h264", "AMD", true), new("hevc_amf", "hevc", "AMD", true), new("av1_amf", "av1", "AMD", true),
        new("h264_qsv", "h264", "Intel", true), new("hevc_qsv", "hevc", "Intel", true), new("av1_qsv", "av1", "Intel", true),
        new("libx264", "h264", "CPU", false),
    };

    static List<Encoder> _found;

    /// <summary>Encoders that actually work on this PC (each is tried with a tiny test encode). Cached.</summary>
    public static async Task<List<Encoder>> Available()
    {
        if (_found != null) return _found;
        var list = new List<Encoder>();
        foreach (var e in Candidates)
        {
            try
            {
                // A 256×256 test pattern for a few frames: fails fast when the driver or chip doesn't support the encoder.
                var (code, _) = await Ffmpeg.Run($"-hide_banner -loglevel error -f lavfi -i color=black:s=256x256:r=30 -frames:v 3 -c:v {e.Name} -f null -", timeoutMs: 20_000);
                if (code == 0) list.Add(e);
            }
            catch { }
        }
        return _found = list;
    }

    public static void Forget() => _found = null;

    /// <summary>The best encoder for a codec: the graphics card's own, else the CPU (H.264 only).</summary>
    public static Encoder Pick(IEnumerable<Encoder> available, string codec)
    {
        var list = available.ToList();
        return list.FirstOrDefault(e => e.Hardware && e.Codec == codec)
               ?? list.FirstOrDefault(e => e.Hardware && e.Codec == "h264")
               ?? list.FirstOrDefault(e => e.Codec == "h264");
    }

    /// <summary>FFmpeg options for an encoder at a bitrate, with a keyframe every second so clips can start anywhere.</summary>
    public static string Options(Encoder e, int mbps, int fps)
    {
        string rate = $"-b:v {mbps}M -maxrate {mbps * 3 / 2}M -bufsize {mbps * 2}M -g {fps} -bf 0";
        return e.Vendor switch
        {
            "NVIDIA" => $"-c:v {e.Name} -preset p4 -tune hq -rc vbr {rate}",
            "AMD" => $"-c:v {e.Name} -usage lowlatency -quality speed -rc vbr_peak {rate}",
            "Intel" => $"-c:v {e.Name} -preset veryfast {rate}",
            _ => $"-c:v {e.Name} -preset veryfast {rate}",
        };
    }
}
