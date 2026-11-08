using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Pulse.Core.Clips;

/// <summary>A clip found in another app's folder, ready to bring into Pulse.</summary>
public sealed record ImportCandidate(string Path, string Game, DateTime Created, long Size);

/// <summary>Another clipping app's folder that has clips in it.</summary>
public sealed record ImportSource(string App, string Folder, int Count);

/// <summary>
/// Brings clips from Medal (and NVIDIA, Xbox Game Bar, Outplayed, SteelSeries Moments, or any folder) into Pulse's
/// library. On the same drive each clip is added as a hard link: it shows in Pulse without taking any extra space, and
/// deleting it in Pulse leaves the other app's copy alone. On another drive it's copied. The originals are never
/// changed, moved or deleted.
/// </summary>
public static class ClipImport
{
    static readonly string[] VideoExt = { ".mp4" };
    static readonly string[] SkipFolders = { "Thumbnails", ".thumbnails", "Editor", "Temp", "temp", "cache", "Cache", "Screenshots", "Uploads" };

    /// <summary>Folders of the clipping apps found on this PC, with how many clips each has that aren't in Pulse yet.</summary>
    public static List<ImportSource> FindSources(ClipSettings s)
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var found = new List<(string App, string Folder)>();
        foreach (var f in MedalFolders()) found.Add(("Medal", f));
        found.Add(("Xbox Game Bar", System.IO.Path.Combine(videos, "Captures")));
        found.Add(("Outplayed", System.IO.Path.Combine(videos, "Overwolf", "Outplayed")));
        found.Add(("SteelSeries Moments", System.IO.Path.Combine(videos, "SteelSeries Moments")));
        found.Add(("AMD", System.IO.Path.Combine(videos, "Radeon ReLive")));
        // NVIDIA's instant replay saves into Videos\<game>\ (or Videos\NVIDIA\<game>\).
        found.Add(("NVIDIA", System.IO.Path.Combine(videos, "NVIDIA")));
        var list = new List<ImportSource>();
        foreach (var (app, folder) in found.DistinctBy(x => x.Folder.ToLowerInvariant()))
        {
            if (!Directory.Exists(folder)) continue;
            int n = Scan(folder, app, s).Count;
            if (n > 0) list.Add(new ImportSource(app, folder, n));
        }
        return list;
    }

    /// <summary>
    /// Medal's capture folder: C:\Medal by default, or Videos\Medal, or wherever its settings point (found by looking
    /// for folder paths in Medal's own settings files that exist on this PC).
    /// </summary>
    public static List<string> MedalFolders()
    {
        var c = new List<string>
        {
            @"C:\Medal", @"D:\Medal", @"E:\Medal",
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Medal"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MedalTV"),
        };
        try
        {
            var store = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Medal", "store");
            if (Directory.Exists(store))
                foreach (var file in Directory.GetFiles(store, "*.json").Where(f => new FileInfo(f).Length < 2_000_000 && !f.EndsWith("clips.json", StringComparison.OrdinalIgnoreCase)))
                    foreach (Match m in Regex.Matches(File.ReadAllText(file), @"""([A-Za-z]:\\\\(?:[^""\\]|\\\\)+)"""))
                    {
                        var p = m.Groups[1].Value.Replace(@"\\", @"\");
                        if (Directory.Exists(p) && (Directory.Exists(System.IO.Path.Combine(p, "Clips")) || p.Contains("Medal", StringComparison.OrdinalIgnoreCase))) c.Insert(0, p);
                    }
        }
        catch { }
        return c.Where(Directory.Exists).Select(p => Directory.Exists(System.IO.Path.Combine(p, "Clips")) ? System.IO.Path.Combine(p, "Clips") : p)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Clips under <paramref name="folder"/> that aren't in Pulse yet. The game is the folder the clip sits in.</summary>
    public static List<ImportCandidate> Scan(string folder, string app, ClipSettings s)
    {
        var list = new List<ImportCandidate>();
        var done = new HashSet<string>(s.ImportedSources ?? new(), StringComparer.OrdinalIgnoreCase);
        var own = System.IO.Path.GetFullPath(s.ClipsFolder).TrimEnd('\\');
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
            foreach (var path in Directory.EnumerateFiles(folder, "*.*", opts))
            {
                if (!VideoExt.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                var dir = System.IO.Path.GetDirectoryName(path)!;
                if (dir.StartsWith(own, StringComparison.OrdinalIgnoreCase)) continue; // Pulse's own clips
                if (dir.Split('\\').Any(p => SkipFolders.Contains(p))) continue;
                var fi = new FileInfo(path);
                if (fi.Length < 100_000) continue;
                if (done.Contains(Key(fi))) continue;
                list.Add(new ImportCandidate(path, GameFor(fi, folder, app), fi.CreationTime < fi.LastWriteTime ? fi.CreationTime : fi.LastWriteTime, fi.Length));
            }
        }
        catch { }
        return list.OrderByDescending(c => c.Created).ToList();
    }

    static string Key(FileInfo f) => f.FullName + "|" + f.Length;

    /// <summary>The game: the clip's folder name under the app's folder ("Clips\VALORANT\x.mp4" → VALORANT), or the app's name.</summary>
    internal static string GameFor(FileInfo f, string root, string app)
    {
        var parent = f.Directory?.Name ?? "";
        bool atRoot = string.Equals(f.DirectoryName?.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        string game = parent;
        if (atRoot || parent.Equals("Clips", StringComparison.OrdinalIgnoreCase))
        {
            // Xbox Game Bar and others name the file after the game: "VALORANT 2026-11-04 20-11-03.mp4".
            var fromName = Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(f.Name), @"[\s_-]*\d{4}[-._]\d{2}[-._]\d{2}.*$", "").Trim();
            game = fromName.Length > 1 && !Regex.IsMatch(fromName, @"^\d+$") && !fromName.StartsWith("MedalTV", StringComparison.OrdinalIgnoreCase) ? fromName : app;
        }
        game = Regex.Replace(game, @"[\\/:*?""<>|]", "").Trim();
        return game.Length == 0 ? app : game;
    }

    /// <summary>The name a clip gets in Pulse's folder: "VALORANT 2026-11-04 01-30-22.mp4", like Pulse's own clips.</summary>
    internal static string TargetName(string folder, ImportCandidate c)
    {
        var baseName = $"{c.Game} {c.Created:yyyy-MM-dd HH-mm-ss}";
        var ext = System.IO.Path.GetExtension(c.Path).ToLowerInvariant();
        var path = System.IO.Path.Combine(folder, baseName + ext);
        for (int i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(folder, $"{baseName} ({i}){ext}");
        return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

    /// <summary>Brings the clips in. Returns (added, linked without using space, bytes copied).</summary>
    public static (int Added, int Linked, long Copied) Import(IReadOnlyList<ImportCandidate> clips, ClipSettings s, IProgress<double> progress = null, CancellationToken ct = default)
    {
        var folder = s.ClipsFolder;
        Directory.CreateDirectory(folder);
        s.ImportedSources ??= new();
        s.ImportedNames ??= new();
        int added = 0, linked = 0;
        long copied = 0;
        for (int i = 0; i < clips.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = clips[i];
            try
            {
                var dest = TargetName(folder, c);
                if (CreateHardLink(dest, c.Path, IntPtr.Zero)) linked++;
                else { File.Copy(c.Path, dest); copied += c.Size; }
                try { File.SetCreationTime(dest, c.Created); } catch { }
                s.ImportedSources.Add(c.Path + "|" + c.Size);
                s.ImportedNames.Add(System.IO.Path.GetFileName(dest));
                added++;
            }
            catch { }
            progress?.Report((i + 1) / (double)clips.Count);
        }
        return (added, linked, copied);
    }
}
