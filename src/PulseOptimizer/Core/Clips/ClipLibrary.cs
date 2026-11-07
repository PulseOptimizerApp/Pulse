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
    /// <summary>"3K", "Penta kill"… for clips Pulse saved on its own; null otherwise.</summary>
    public string Title { get; set; }
    public bool Auto => !string.IsNullOrEmpty(Title);
    /// <summary>What the library card shows in bold: the play ("3K") or the game.</summary>
    public string Heading => string.IsNullOrEmpty(Title) ? Game : Title;
    /// <summary>The game part of the name ("VALORANT 2026-11-04 01-30-22" → "VALORANT").</summary>
    public string Game => GameOf(Name);

    /// <summary>
    /// "NBA 2K26 2026-11-04 02-38-47 (trimmed)" → "NBA 2K26": drops the date, and the "(trimmed)" or "(Discord)" a
    /// copy gets, so copies are grouped with their game.
    /// </summary>
    internal static string GameOf(string name)
    {
        var n = Regex.Replace(name, @"(\s\((trimmed|Discord)\)(\s\d+)?)+$", "", RegexOptions.IgnoreCase);
        n = Regex.Replace(n, @"\s(session\s)?\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}( \(\d+\))?$", "").Trim();
        return Regex.Replace(n, @"\s+session$", "", RegexOptions.IgnoreCase).Trim();
    }
}

/// <summary>Pictures of the input overlay prepared for one export (deleted afterwards).</summary>
public sealed record InputBurn(string Dir, string ListFile)
{
    public void Clean()
    {
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
    }
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
                Title = s.Titles.TryGetValue(f.Name, out var t) ? t : null,
            })
            .OrderByDescending(c => c.Created).ToList();
    }

    /// <summary>Makes a small picture from 1 second into the clip (hidden .thumbnails folder next to the clips).</summary>
    public static async Task<string> MakeThumbnail(string clip)
    {
        var thumb = ThumbFor(clip);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(thumb)!);
        try { File.SetAttributes(System.IO.Path.GetDirectoryName(thumb)!, FileAttributes.Hidden | FileAttributes.Directory); } catch { }
        var (code, _) = await Ffmpeg.Run($"-hide_banner -loglevel error -y -ss 1 -i \"{clip}\" -frames:v 1 -vf {SquarePixels},scale=480:-2 -q:v 4 \"{thumb}\"", timeoutMs: 30_000);
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
        var (code, _) = await Ffmpeg.Run($"-hide_banner -loglevel error -y -i \"{clip}\" -an -vf fps={rate},{SquarePixels},scale=160:-2,tile={frames}x1 -frames:v 1 -q:v 5 \"{strip}\"", timeoutMs: 60_000);
        return code == 0 && File.Exists(strip) ? strip : null;
    }

    /// <summary>How many audio tracks a clip has: 1 (game + mic mixed) or 3 (mix, game only, mic only).</summary>
    public static async Task<int> AudioTracks(string clip)
    {
        var (_, err) = await Ffmpeg.Run($"-hide_banner -i \"{clip}\"", timeoutMs: 15_000);
        return CountAudio(err);
    }

    internal static int CountAudio(string ffmpegOutput) => Regex.Matches(ffmpegOutput ?? "", @"Stream #\d+:\d+[^:]*: Audio:").Count;

    // ───────── When the clip ends (for kills and deaths on its timeline) ─────────

    static string EndFor(string clip) => System.IO.Path.ChangeExtension(clip, ".end");

    /// <summary>Notes the moment the clip's last frame was recorded (when its key was pressed).</summary>
    public static void SaveEnd(string clip, DateTime end)
    {
        try { File.WriteAllText(EndFor(clip), end.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
    }

    /// <summary>When the clip's last frame was recorded, or null (trimmed copies, older clips).</summary>
    public static DateTime? LoadEnd(string clip)
    {
        try
        {
            return File.Exists(EndFor(clip)) && long.TryParse(File.ReadAllText(EndFor(clip)).Trim(), out var t)
                ? new DateTime(t, DateTimeKind.Utc).ToLocalTime() : null;
        }
        catch { return null; }
    }

    // ───────── Bookmarks (a small file next to the clip) ─────────

    static string MarksFor(string clip) => System.IO.Path.ChangeExtension(clip, ".marks.json");

    public static void SaveMarks(string clip, List<double> marks)
    {
        if (marks == null || marks.Count == 0) return;
        try { File.WriteAllText(MarksFor(clip), System.Text.Json.JsonSerializer.Serialize(marks)); } catch { }
    }

    /// <summary>Bookmarked moments in a clip, in seconds from its start.</summary>
    public static List<double> LoadMarks(string clip)
    {
        try { return File.Exists(MarksFor(clip)) ? System.Text.Json.JsonSerializer.Deserialize<List<double>>(File.ReadAllText(MarksFor(clip))) ?? new() : new(); }
        catch { return new(); }
    }

    /// <summary>
    /// Writes each part track (Game, Discord, Microphone...) of a clip to its own WAV in a temporary folder, so the
    /// player can mute them one by one. Returns the folder and the files in track order.
    /// </summary>
    public static async Task<(string Dir, List<string> Files)> ExtractParts(string clip, int parts)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pulse-parts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var files = Enumerable.Range(1, parts).Select(i => System.IO.Path.Combine(dir, $"{i}.wav")).ToList();
        var outs = string.Join(" ", files.Select((f, i) => $"-map 0:a:{i + 1} -c:a pcm_s16le \"{f}\""));
        var (code, _) = await Ffmpeg.Run($"-hide_banner -loglevel error -y -i \"{clip}\" {outs}", timeoutMs: 60_000);
        if (code != 0 || !files.All(File.Exists)) { CleanParts(dir); return (null, new()); }
        return (dir, files);
    }

    /// <summary>
    /// Every sound track of a clip (0 = everything as recorded, then each source) as raw 16-bit stereo at 48 kHz,
    /// for the editor's mixer. Returns the temp folder and one file per track, or nothing if it didn't work.
    /// </summary>
    public static async Task<(string Dir, List<string> Files)> ExtractTracks(string clip, int tracks)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pulse-parts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var files = Enumerable.Range(0, tracks).Select(i => System.IO.Path.Combine(dir, $"{i}.raw")).ToList();
        var outs = string.Join(" ", files.Select((f, i) => $"-map 0:a:{i} -f s16le -acodec pcm_s16le -ar {ClipMixer.Rate} -ac 2 \"{f}\""));
        var (code, _) = await Ffmpeg.Run($"-hide_banner -loglevel error -y -i \"{clip}\" {outs}", timeoutMs: 120_000);
        if (code != 0 || !files.All(File.Exists)) { CleanParts(dir); return (null, new()); }
        return (dir, files);
    }

    public static void CleanParts(string dir)
    {
        try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    /// <summary>Renames a clip (and its picture and bookmarks). Returns the new path.</summary>
    public static string Rename(string clip, string newName)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) newName = newName.Replace(c, ' ');
        newName = newName.Trim();
        if (newName.Length == 0) throw new InvalidOperationException("Type a name.");
        var dest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(clip)!, newName + System.IO.Path.GetExtension(clip));
        if (dest.Equals(clip, StringComparison.OrdinalIgnoreCase)) return clip;
        if (File.Exists(dest)) throw new InvalidOperationException("A clip with that name already exists.");
        File.Move(clip, dest);
        try { if (File.Exists(ThumbFor(clip))) File.Move(ThumbFor(clip), ThumbFor(dest), true); } catch { }
        try { if (File.Exists(MarksFor(clip))) File.Move(MarksFor(clip), MarksFor(dest), true); } catch { }
        try { if (File.Exists(EndFor(clip))) File.Move(EndFor(clip), EndFor(dest), true); } catch { }
        try { if (File.Exists(StripFor(clip))) File.Move(StripFor(clip), StripFor(dest), true); } catch { }
        try { if (File.Exists(InputTimeline.FileFor(clip))) File.Move(InputTimeline.FileFor(clip), InputTimeline.FileFor(dest), true); } catch { }
        return dest;
    }

    /// <summary>The name of each sound track, in order. Clips from before tracks had names get "All sound", "Game", "Microphone".</summary>
    public static async Task<List<string>> AudioTrackNames(string clip)
    {
        var (_, err) = await Ffmpeg.Run($"-hide_banner -i \"{clip}\"", timeoutMs: 15_000);
        return TrackNames(err);
    }

    internal static List<string> TrackNames(string ffmpegOutput)
    {
        var names = new List<string>();
        var lines = (ffmpegOutput ?? "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!Regex.IsMatch(lines[i], @"Stream #\d+:\d+[^:]*: Audio:")) continue;
            string name = null;
            // The stream's own Metadata block follows: indented "title" or "handler_name" lines.
            for (int j = i + 1; j < lines.Length && !lines[j].TrimStart().StartsWith("Stream #"); j++)
            {
                var m = Regex.Match(lines[j], @"^\s+(title|handler_name)\s*:\s*(.+?)\s*$");
                if (m.Success && (name == null || m.Groups[1].Value == "title")) name = m.Groups[2].Value;
            }
            // FFmpeg's own default handler name isn't a real track name.
            if (name is "SoundHandler" or "Sound Handler" or "") name = null;
            names.Add(name);
        }
        // Clips from before tracks had names: Pulse always wrote all sound, then game, then microphone.
        string[] old = { "All sound", "Game", "Microphone" };
        bool anyNamed = names.Any(n => n != null);
        for (int i = 0; i < names.Count; i++) names[i] ??= !anyNamed && i < old.Length ? old[i] : i == 0 ? "All sound" : $"Track {i + 1}";
        return names;
    }

    /// <summary>Which tracks a copy keeps, from the per-part toggles (track 1 is the mix of all parts).</summary>
    public static List<int> ChooseTracks(int trackCount, IReadOnlyList<bool> on)
    {
        if (trackCount == 0 || on.Count == 0 || on.All(x => !x)) return new List<int>();
        if (trackCount == 1 || on.All(x => x)) return new List<int> { 0 }; // everything: the original mix (includes other apps' sound)
        return on.Select((x, i) => (x, i)).Where(p => p.x).Select(p => p.i + 1).ToList();
    }

    /// <summary>FFmpeg options for the sound to keep: a track number, or -1 for none.</summary>
    internal static string AudioArgs(int track) => track < 0 ? "-an" : $"-map 0:a:{track}? -c:a aac -b:a 256k";

    /// <summary>
    /// FFmpeg options for the sound to keep from several tracks: none, one, or several mixed together
    /// (for example game + microphone, leaving Discord out).
    /// </summary>
    internal static string AudioArgs(IReadOnlyList<int> tracks, int inputIndex = 0)
    {
        if (tracks == null || tracks.Count == 0) return "-an";
        if (tracks.Count == 1) return AudioArgs(tracks[0]);
        var ins = string.Concat(tracks.Select(t => $"[{inputIndex}:a:{t}]"));
        return $"-filter_complex \"{ins}amix=inputs={tracks.Count}:duration=longest:dropout_transition=0:normalize=0,alimiter=limit=0.97:level=0[aout]\" -map \"[aout]\" -c:a aac -b:a 256k";
    }

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
        try { File.Delete(MarksFor(clip)); } catch { }
        try { File.Delete(EndFor(clip)); } catch { }
        try { File.Delete(InputTimeline.FileFor(clip)); } catch { }
    }

    /// <summary>
    /// Saves the part between <paramref name="from"/> and <paramref name="to"/> as a new clip. It's re-encoded with the
    /// graphics card so the cut is exact to the frame (a plain copy could only cut on keyframes).
    /// </summary>
    public static async Task<string> Trim(string clip, double from, double to, Encoder e, int mbps, IReadOnlyList<int> audio = null, InputBurn burn = null, CancellationToken ct = default)
    {
        try
        {
            if (to - from < 0.5) throw new InvalidOperationException("Pick at least half a second to keep.");
            var dest = Sibling(clip, "trimmed");
            var f = from.ToString("0.###", CultureInfo.InvariantCulture);
            var d = (to - from).ToString("0.###", CultureInfo.InvariantCulture);
            var (code, err) = await Ffmpeg.Run($"-hide_banner -loglevel error -y -ss {f} -i \"{clip}\" {OverlayInput(burn)} -t {d} {VideoArgs(burn, 0)} {Encoders.Options(e, mbps, 60)} {AudioArgs(audio ?? new[] { 0 })} -movflags +faststart \"{dest}\"", ct);
            if (code != 0) throw new InvalidOperationException("Trimming didn't work: " + err.Trim());
            return dest;
        }
        finally { burn?.Clean(); }
    }

    /// <summary>The overlay pictures as a second input (FFmpeg's concat list), when an input overlay is burned in.</summary>
    internal static string OverlayInput(InputBurn burn) => burn == null ? "" : $"-f concat -safe 0 -i \"{burn.ListFile}\"";

    /// <summary>
    /// The picture to encode: the clip's video, with the input overlay in the top-right corner when there is one,
    /// scaled to <paramref name="height"/> (0 = unchanged).
    /// </summary>
    internal static string VideoArgs(InputBurn burn, int height)
    {
        string scale = height > 0 ? $"scale=-2:{height}" : "";
        if (burn == null) return $"-map 0:v:0 -vf {SquarePixels}" + (height > 0 ? "," + scale : "");
        var chain = "overlay=x=main_w-overlay_w-main_h*0.025:y=main_h*0.025" + (height > 0 ? "," + scale : "") + ",format=yuv420p";
        return $"-filter_complex \"[0:v:0]{SquarePixels}[base];[base][1:v]{chain}[vout]\" -map \"[vout]\"";
    }

    /// <summary>
    /// Re-encoded copies get square pixels at the clip's display shape: a stretched-resolution clip (4:3 marked to
    /// show 16:9) becomes a real 16:9 picture, so every player and site shows it stretched like in game.
    /// </summary>
    internal const string SquarePixels = "scale=trunc(ih*dar/2)*2:ih,setsar=1";

    /// <summary>
    /// The sample aspect ratio that makes a <paramref name="width"/>×<paramref name="height"/> picture show at the
    /// screen's shape (stretched resolution), as "num/den", or null when it already matches (within 3%).
    /// </summary>
    internal static string StretchSar(int width, int height, double screenAspect)
    {
        if (width <= 0 || height <= 0 || screenAspect <= 0) return null;
        double own = width / (double)height;
        if (Math.Abs(own - screenAspect) <= screenAspect * 0.03) return null;
        // SAR = screen aspect / picture aspect = (screenW × height) / (screenH × width), kept as small whole numbers.
        long num = (long)Math.Round(screenAspect * 1000 * height), den = 1000L * width;
        long g = Gcd(num, den);
        return $"{num / g}/{den / g}";
    }

    static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return Math.Abs(a); }

    /// <summary>FFmpeg options that mark a stream-copied clip to show stretched (no re-encode), or "".</summary>
    internal static string StretchCopyArgs(string codec, string sar)
    {
        if (sar == null) return "";
        return codec switch
        {
            "h264" => $" -bsf:v h264_metadata=sample_aspect_ratio={sar}",
            "hevc" => $" -bsf:v hevc_metadata=sample_aspect_ratio={sar}",
            _ => "", // AV1 has no aspect field to rewrite; its re-encoded copies are still stretched
        };
    }

    /// <summary>Width and height of a clip's video (0, 0 if unknown).</summary>
    public static async Task<(int Width, int Height)> VideoSize(string clip)
    {
        var (_, err) = await Ffmpeg.Run($"-hide_banner -i \"{clip}\"", timeoutMs: 15_000);
        var m = Regex.Match(err ?? "", @"Video:.*?(\d{3,5})x(\d{3,5})");
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (0, 0);
    }

    /// <summary>How wide the clip is meant to be shown (its display aspect, which a stretched-resolution clip sets), or 0.</summary>
    public static async Task<double> DisplayAspect(string clip)
    {
        var (_, err) = await Ffmpeg.Run($"-hide_banner -i \"{clip}\"", timeoutMs: 15_000);
        return ParseDisplayAspect(err);
    }

    internal static double ParseDisplayAspect(string ffmpegOutput)
    {
        var m = Regex.Match(ffmpegOutput ?? "", @"Video:.*?DAR (\d+):(\d+)");
        return m.Success && int.Parse(m.Groups[2].Value) > 0 ? int.Parse(m.Groups[1].Value) / (double)int.Parse(m.Groups[2].Value) : 0;
    }

    /// <summary>The video bitrate (kbit/s) that makes a clip of this length fit Discord's limit with 160 kbit/s audio.</summary>
    internal static int DiscordVideoKbps(double seconds, long targetBytes = DiscordTargetBytes)
    {
        double totalKbps = targetBytes * 8 / 1000.0 / Math.Max(seconds, 1);
        return (int)Math.Max(totalKbps - 160 - 32 /*audio and container*/, 150);
    }

    /// <summary>Re-encodes a copy that fits in a free Discord upload (under 10 MB), lowering the resolution for long clips.</summary>
    public static async Task<string> ForDiscord(string clip, Encoder e, double from = 0, double to = 0, IReadOnlyList<int> audioTracks = null, InputBurn burn = null, CancellationToken ct = default)
    {
        try { return await ForDiscordCore(clip, e, from, to, audioTracks, burn, ct); }
        finally { burn?.Clean(); }
    }

    static async Task<string> ForDiscordCore(string clip, Encoder e, double from, double to, IReadOnlyList<int> audioTracks, InputBurn burn, CancellationToken ct)
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
            var audio = AudioArgs(audioTracks ?? new[] { 0 }).Replace("-b:a 256k", "-b:a 160k");
            var (code, err) = await Ffmpeg.Run($"-hide_banner -loglevel error -y {cut} -i \"{clip}\" {OverlayInput(burn)} {VideoArgs(burn, h)} {codec} {rate} {audio} -movflags +faststart \"{dest}\"", ct);
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
