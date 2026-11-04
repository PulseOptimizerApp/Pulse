using System.Globalization;
using System.Text.RegularExpressions;

namespace Pulse.Core.Clips;

/// <summary>One saved clip.</summary>
public class ClipFile
{
    public string Path { get; init; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public DateTime Created { get; init; }
    public long Size { get; init; }
    public double Seconds { get; set; }
    public string Thumbnail { get; set; }
    public bool Favorite { get; set; }
    /// <summary>The game part of the name ("VALORANT 2026-11-04 01-30-22" → "VALORANT").</summary>
    public string Game => Regex.Replace(Name, @"\s\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}( \(\d+\))?$", "").Trim();
}

/// <summary>The clips folder: listing, thumbnails, the storage limit, trimming and the Discord-sized export.</summary>
public static class ClipLibrary
{
    /// <summary>Discord's upload limit without Nitro is 10 MB; aim a little under so the file always fits.</summary>
    public const long DiscordLimitBytes = 10L * 1024 * 1024;
    const long DiscordTargetBytes = DiscordLimitBytes * 92 / 100;

    static string ThumbDir(string folder) => System.IO.Path.Combine(folder, ".thumbnails");
    public static string ThumbFor(string clip) => System.IO.Path.Combine(ThumbDir(System.IO.Path.GetDirectoryName(clip)!), System.IO.Path.GetFileNameWithoutExtension(clip) + ".jpg");

    public static List<ClipFile> List(ClipSettings s)
    {
        var folder = s.ClipsFolder;
        if (!Directory.Exists(folder)) return new();
        return new DirectoryInfo(folder).GetFiles("*.mp4")
            .Select(f => new ClipFile
            {
                Path = f.FullName, Created = f.CreationTime, Size = f.Length,
                Thumbnail = File.Exists(ThumbFor(f.FullName)) ? ThumbFor(f.FullName) : null,
                Favorite = s.Favorites.Contains(f.Name, StringComparer.OrdinalIgnoreCase),
            })
            .OrderByDescending(c => c.Created).ToList();
    }

    /// <summary>Makes a small picture from 1 second into the clip (hidden .thumbnails folder next to the clips).</summary>
    public static async Task<string> MakeThumbnail(string clip)
    {
        var thumb = ThumbFor(clip);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(thumb)!);
        try { File.SetAttributes(System.IO.Path.GetDirectoryName(thumb)!, FileAttributes.Hidden | FileAttributes.Directory); } catch { }
        var (code, _) = await Ffmpeg.Run($"-hide_banner -loglevel error -y -ss 1 -i \"{clip}\" -frames:v 1 -vf scale=480:-2 -q:v 4 \"{thumb}\"", timeoutMs: 30_000);
        return code == 0 && File.Exists(thumb) ? thumb : null;
    }

    public static string StripFor(string clip) => System.IO.Path.Combine(ThumbDir(System.IO.Path.GetDirectoryName(clip)!), System.IO.Path.GetFileNameWithoutExtension(clip) + ".strip.jpg");

    /// <summary>A row of small frames across the whole clip, for the trimmer's timeline (cached next to the thumbnails).</summary>
    public static async Task<string> Filmstrip(string clip, double seconds, int frames = 16)
    {
        var strip = StripFor(clip);
        if (File.Exists(strip) && File.GetLastWriteTimeUtc(strip) >= File.GetLastWriteTimeUtc(clip)) return strip;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(strip)!);
        // Slightly fewer frames per second than needed so the tile always fills before the clip ends.
        var rate = (frames / Math.Max(seconds, 1) * 0.98).ToString("0.#####", CultureInfo.InvariantCulture);
        var (code, _) = await Ffmpeg.Run($"-hide_banner -loglevel error -y -i \"{clip}\" -an -vf fps={rate},scale=160:-2,tile={frames}x1 -frames:v 1 -q:v 5 \"{strip}\"", timeoutMs: 60_000);
        return code == 0 && File.Exists(strip) ? strip : null;
    }

    /// <summary>How many audio tracks a clip has: 1 (game + mic mixed) or 3 (mix, game only, mic only).</summary>
    public static async Task<int> AudioTracks(string clip)
    {
        var (_, err) = await Ffmpeg.Run($"-hide_banner -i \"{clip}\"", timeoutMs: 15_000);
        return CountAudio(err);
    }

    internal static int CountAudio(string ffmpegOutput) => Regex.Matches(ffmpegOutput ?? "", @"Stream #\d+:\d+[^:]*: Audio:").Count;

    /// <summary>FFmpeg options for the sound to keep: a track number, or -1 for none.</summary>
    internal static string AudioArgs(int track) => track < 0 ? "-an" : $"-map 0:a:{track}? -c:a aac -b:a 160k";

    /// <summary>Clip length in seconds, read from FFmpeg's own report.</summary>
    public static async Task<double> Duration(string clip)
    {
        var (_, err) = await Ffmpeg.Run($"-hide_banner -i \"{clip}\"", timeoutMs: 15_000);
        return ParseDuration(err);
    }

    internal static double ParseDuration(string ffmpegOutput)
    {
        var m = Regex.Match(ffmpegOutput ?? "", @"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)");
        if (!m.Success) return 0;
        return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Deletes the oldest clips that aren't favorites until the folder is under the storage limit.</summary>
    public static int EnforceLimit(ClipSettings s)
    {
        if (s.StorageLimitGb <= 0) return 0;
        long limit = s.StorageLimitGb * 1024L * 1024 * 1024;
        var clips = List(s);
        long total = clips.Sum(c => c.Size);
        int removed = 0;
        foreach (var c in clips.Where(c => !c.Favorite).OrderBy(c => c.Created))
        {
            if (total <= limit) break;
            try { Delete(c.Path); total -= c.Size; removed++; } catch { }
        }
        return removed;
    }

    public static void Delete(string clip)
    {
        File.Delete(clip);
        try { File.Delete(ThumbFor(clip)); } catch { }
        try { File.Delete(StripFor(clip)); } catch { }
    }

    /// <summary>
    /// Saves the part between <paramref name="from"/> and <paramref name="to"/> as a new clip. It's re-encoded with the
    /// graphics card so the cut is exact to the frame (a plain copy could only cut on keyframes).
    /// </summary>
    public static async Task<string> Trim(string clip, double from, double to, Encoder e, int mbps, int audioTrack = 0, CancellationToken ct = default)
    {
        if (to - from < 0.5) throw new InvalidOperationException("Pick at least half a second to keep.");
        var dest = Sibling(clip, "trimmed");
        var f = from.ToString("0.###", CultureInfo.InvariantCulture);
        var d = (to - from).ToString("0.###", CultureInfo.InvariantCulture);
        var (code, err) = await Ffmpeg.Run($"-hide_banner -loglevel error -y -ss {f} -i \"{clip}\" -t {d} -map 0:v:0 {Encoders.Options(e, mbps, 60)} {AudioArgs(audioTrack)} -movflags +faststart \"{dest}\"", ct);
        if (code != 0) throw new InvalidOperationException("Trimming didn't work: " + err.Trim());
        return dest;
    }

    /// <summary>The video bitrate (kbit/s) that makes a clip of this length fit Discord's limit with 128 kbit/s audio.</summary>
    internal static int DiscordVideoKbps(double seconds, long targetBytes = DiscordTargetBytes)
    {
        double totalKbps = targetBytes * 8 / 1000.0 / Math.Max(seconds, 1);
        return (int)Math.Max(totalKbps - 128 - 32 /*container*/, 150);
    }

    /// <summary>Re-encodes a copy that fits in a free Discord upload (under 10 MB), lowering the resolution for long clips.</summary>
    public static async Task<string> ForDiscord(string clip, Encoder e, double from = 0, double to = 0, int audioTrack = 0, CancellationToken ct = default)
    {
        double total = await Duration(clip);
        if (total <= 0) throw new InvalidOperationException("Couldn't read the clip's length.");
        if (to <= from) { from = 0; to = total; }
        double secs = to - from;
        var dest = Sibling(clip, "Discord");
        int kbps = DiscordVideoKbps(secs);
        var cut = $"-ss {from.ToString("0.###", CultureInfo.InvariantCulture)} -t {secs.ToString("0.###", CultureInfo.InvariantCulture)}";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int h = kbps >= 4500 ? 1080 : kbps >= 2000 ? 720 : 480;
            string rate = $"-b:v {kbps}k -maxrate {kbps}k -bufsize {kbps * 2}k";
            // H.264 plays everywhere, including Discord's own player: use the graphics card's H.264 encoder.
            var codec = e.Hardware ? $"-c:v {H264Of(e)}" : "-c:v libx264 -preset veryfast";
            var audio = audioTrack < 0 ? "-an" : $"-map 0:a:{audioTrack}? -c:a aac -b:a 128k";
            var (code, err) = await Ffmpeg.Run($"-hide_banner -loglevel error -y {cut} -i \"{clip}\" -map 0:v:0 -vf scale=-2:{h} {codec} {rate} {audio} -movflags +faststart \"{dest}\"", ct);
            if (code != 0) throw new InvalidOperationException("Making the Discord copy didn't work: " + err.Trim());
            if (new FileInfo(dest).Length <= DiscordLimitBytes) return dest;
            kbps = kbps * 80 / 100; // the encoder overshot: try again a bit smaller
        }
        return dest;
    }

    internal static string H264Of(Encoder e) => e.Codec == "h264" ? e.Name : "h264_" + e.Name[(e.Name.IndexOf('_') + 1)..];

    static string Sibling(string clip, string tag)
    {
        var dir = System.IO.Path.GetDirectoryName(clip)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(clip) + $" ({tag})";
        var path = System.IO.Path.Combine(dir, name + ".mp4");
        for (int i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(dir, $"{name} {i}.mp4");
        return path;
    }
}
