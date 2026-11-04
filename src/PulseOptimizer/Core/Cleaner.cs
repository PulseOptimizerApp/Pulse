using Pulse.Ui;

namespace Pulse.Core;

public record CleanTarget(string Dir, string Pattern = "*", bool Recursive = true);

public class CleanCategory : Observable
{
    public string Id { get; init; }
    public string Name { get; init; }
    public string Glyph { get; init; }
    public string Description { get; init; }
    public string Warning { get; init; } = "";
    /// <summary>Only files older than this are touched, so programs that are running aren't disturbed.</summary>
    public TimeSpan MinAge { get; init; } = TimeSpan.Zero;
    public Func<IEnumerable<CleanTarget>> Targets { get; init; }
    public bool IsRecycleBin { get; init; }
    /// <summary>Bytes found that open apps are using (couldn't be deleted last time). Not counted in <see cref="Size"/>.</summary>
    public long InUseSize { get; set; }

    bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    long _size = -1;
    public long Size { get => _size; set { if (Set(ref _size, value)) Raise(nameof(SizeText)); } }

    int _count;
    public int Count { get => _count; set => Set(ref _count, value); }

    bool _scanning;
    public bool Scanning { get => _scanning; set { if (Set(ref _scanning, value)) Raise(nameof(SizeText)); } }

    public string SizeText => Scanning ? "Scanning…" : Size < 0 ? "—" : Size == 0 ? "Clean" : Format.Bytes(Size);
}

public static class Cleaner
{
    static string Local(string rel) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), rel);
    static string Roaming(string rel) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), rel);
    static string LocalLow(string rel) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", rel);
    static string Win(string rel) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), rel);
    static string ProgramData(string rel) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), rel);

    static IEnumerable<string> SubDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    static IEnumerable<CleanTarget> ChromiumCaches(string userData)
    {
        foreach (var profile in SubDirs(userData))
        {
            var name = Path.GetFileName(profile);
            if (name != "Default" && !name.StartsWith("Profile ") && name != "Guest Profile") continue;
            foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage" })
                yield return new CleanTarget(Path.Combine(profile, sub));
        }
    }

    public static readonly List<CleanCategory> Categories = new()
    {
        new CleanCategory
        {
            Id = "user-temp", Name = "Your temporary files", Glyph = "", Selected = true,
            Description = "Files apps create while they work and forget to remove — installers, unpacked updates, logs.",
            MinAge = TimeSpan.FromHours(2),
            Targets = () => new[] { new CleanTarget(Path.GetTempPath()) }
        },
        new CleanCategory
        {
            Id = "win-temp", Name = "Windows temporary files", Glyph = "", Selected = true,
            Description = "Temporary files Windows and system services leave in the Windows Temp folder.",
            MinAge = TimeSpan.FromHours(2),
            Targets = () => new[] { new CleanTarget(Win("Temp")) }
        },
        new CleanCategory
        {
            Id = "wu", Name = "Windows Update leftovers", Glyph = "", Selected = true,
            Description = "Update installers Windows already used. Windows downloads again anything it still needs.",
            Targets = () => new[] { new CleanTarget(Win(@"SoftwareDistribution\Download")) }
        },
        new CleanCategory
        {
            Id = "do", Name = "Shared update cache", Glyph = "", Selected = true,
            Description = "Pieces of updates kept so they can be shared with other PCs (Delivery Optimization).",
            Targets = () => new[] { new CleanTarget(Win(@"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")) }
        },
        new CleanCategory
        {
            Id = "crash", Name = "Crash dumps and error reports", Glyph = "", Selected = true,
            Description = "Memory snapshots and reports saved when an app or Windows crashed. Only useful for developers debugging a crash.",
            Targets = () => new[]
            {
                new CleanTarget(ProgramData(@"Microsoft\Windows\WER\ReportArchive")),
                new CleanTarget(ProgramData(@"Microsoft\Windows\WER\ReportQueue")),
                new CleanTarget(Local("CrashDumps")),
                new CleanTarget(Win("Minidump")),
                new CleanTarget(Win(""), "MEMORY.DMP", false),
            }
        },
        new CleanCategory
        {
            Id = "browser", Name = "Browser cache", Glyph = "", Selected = false,
            Description = "Saved copies of websites from Chrome, Edge, Brave, Opera and Firefox. You stay logged in; passwords, history and bookmarks are not touched.",
            Warning = "Close your browsers first for the best result. Sites load a bit slower the first time afterwards.",
            Targets = () => ChromiumCaches(Local(@"Google\Chrome\User Data"))
                .Concat(ChromiumCaches(Local(@"Microsoft\Edge\User Data")))
                .Concat(ChromiumCaches(Local(@"BraveSoftware\Brave-Browser\User Data")))
                .Concat(new[]
                {
                    new CleanTarget(Local(@"Opera Software\Opera Stable\Cache")),
                    new CleanTarget(Local(@"Opera Software\Opera GX Stable\Cache")),
                })
                .Concat(SubDirs(Local(@"Mozilla\Firefox\Profiles")).Select(p => new CleanTarget(Path.Combine(p, "cache2"))))
        },
        new CleanCategory
        {
            Id = "apps", Name = "Game launcher and Discord caches", Glyph = "", Selected = false,
            Description = "Web page caches inside Steam, Epic Games, Discord and similar apps. Your games and downloads are not touched.",
            Warning = "Close these apps first or some files will be skipped.",
            Targets = () => new[]
            {
                new CleanTarget(Local(@"Steam\htmlcache")),
                new CleanTarget(Local(@"EpicGamesLauncher\Saved\webcache")),
                new CleanTarget(Local(@"EpicGamesLauncher\Saved\webcache_4147")),
                new CleanTarget(Local(@"EpicGamesLauncher\Saved\webcache_4430")),
                new CleanTarget(Roaming(@"discord\Cache")),
                new CleanTarget(Roaming(@"discord\Code Cache")),
                new CleanTarget(Roaming(@"discord\GPUCache")),
                new CleanTarget(Roaming(@"Spotify\Browser\Cache")),
                new CleanTarget(Local(@"Spotify\Data")),
            }
        },
        new CleanCategory
        {
            Id = "roblox", Name = "Roblox cache", Glyph = "\uE7FC", Selected = false,
            Description = "Downloaded assets and logs Roblox keeps in its temp folder. Roblox downloads what it needs again.",
            Warning = "Close Roblox first. Experiences load a little slower the first time afterwards.",
            MinAge = TimeSpan.FromHours(1),
            Targets = () => new[] { new CleanTarget(Path.Combine(Path.GetTempPath(), "Roblox")), new CleanTarget(Local(@"Roblox\logs")) }
        },
        new CleanCategory
        {
            Id = "shaders", Name = "Graphics shader cache", Glyph = "", Selected = false,
            Description = "Pre-compiled shaders saved by your graphics driver so games load faster.",
            Warning = "Games will rebuild these and may stutter for the first few minutes. Only worth clearing after a driver update or if you see graphics glitches.",
            Targets = () => new[]
            {
                new CleanTarget(Local("D3DSCache")),
                new CleanTarget(Local(@"NVIDIA\DXCache")),
                new CleanTarget(Local(@"NVIDIA\GLCache")),
                new CleanTarget(LocalLow(@"NVIDIA\PerDriverVersion\DXCache")),
                new CleanTarget(Local(@"AMD\DxCache")),
                new CleanTarget(Local(@"AMD\DxcCache")),
                new CleanTarget(Local(@"AMD\VkCache")),
            }
        },
        new CleanCategory
        {
            Id = "thumbs", Name = "Thumbnail cache", Glyph = "", Selected = false,
            Description = "Small preview images File Explorer saves for your photos and videos. Explorer rebuilds them when needed.",
            Warning = "File Explorer keeps some of these open, so not all may be removed.",
            Targets = () => new[]
            {
                new CleanTarget(Local(@"Microsoft\Windows\Explorer"), "thumbcache_*.db", false),
            }
        },
        new CleanCategory
        {
            Id = "recycle", Name = "Recycle Bin", Glyph = "", Selected = false, IsRecycleBin = true,
            Description = "Files you already deleted. Emptying the bin removes them permanently.",
            Warning = "Check the Recycle Bin first if you might need something back.",
            Targets = () => Array.Empty<CleanTarget>()
        },
    };

    static readonly EnumerationOptions Opts = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    static IEnumerable<FileInfo> Files(CleanCategory c)
    {
        var cutoff = DateTime.Now - c.MinAge;
        foreach (var t in c.Targets())
        {
            if (string.IsNullOrEmpty(t.Dir) || !Directory.Exists(t.Dir)) continue;
            IEnumerable<FileInfo> list;
            try
            {
                var o = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    RecurseSubdirectories = t.Recursive,
                };
                list = new DirectoryInfo(t.Dir).EnumerateFiles(t.Pattern, o);
            }
            catch { continue; }
            foreach (var f in list)
            {
                bool ok;
                try { ok = c.MinAge == TimeSpan.Zero || f.LastWriteTime < cutoff; } catch { ok = false; }
                if (ok) yield return f;
            }
        }
    }

    public static void Scan(CleanCategory c)
    {
        if (c.IsRecycleBin)
        {
            var (size, items) = Native.RecycleBinInfo();
            c.Size = size; c.Count = (int)items;
            return;
        }
        long total = 0, inUse = 0; int n = 0;
        try
        {
            foreach (var f in Files(c))
            {
                try
                {
                    // Files that couldn't be deleted a moment ago are still in use: counting them as "ready to free"
                    // made Free look like it did nothing (the same size came straight back after cleaning).
                    if (StillInUse(f.FullName)) { inUse += f.Length; continue; }
                    total += f.Length; n++;
                }
                catch { }
            }
        }
        catch { }
        c.Size = total; c.Count = n; c.InUseSize = inUse;
    }

    /// <summary>Deletes the files in a category. Returns bytes freed. Files in use are skipped.</summary>
    // Files an app had open when Pulse tried to delete them, and when. Forgotten after 30 minutes (apps close).
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> InUse = new(StringComparer.OrdinalIgnoreCase);

    internal static bool StillInUse(string path) =>
        InUse.TryGetValue(path, out var at) && (DateTime.UtcNow - at < TimeSpan.FromMinutes(30) || !InUse.TryRemove(path, out _));

    /// <param name="progress">Called now and then with the bytes freed so far (for the progress screen).</param>
    public static long Clean(CleanCategory c, Action<long> progress) => CleanCore(c, progress);

    public static long Clean(CleanCategory c) => CleanCore(c, null);

    static long CleanCore(CleanCategory c, Action<long> progress)
    {
        if (c.IsRecycleBin)
        {
            var (size, _) = Native.RecycleBinInfo();
            return Native.EmptyRecycleBin() ? size : 0;
        }
        long freed = 0;
        var tick = System.Diagnostics.Stopwatch.StartNew();
        foreach (var f in Files(c).ToList())
        {
            try
            {
                var len = f.Length;
                if (f.IsReadOnly) f.IsReadOnly = false;
                f.Delete();
                freed += len;
                InUse.TryRemove(f.FullName, out _);
            }
            catch { InUse[f.FullName] = DateTime.UtcNow; } // in use or protected: skipped, and not counted again for a while
            if (progress != null && tick.ElapsedMilliseconds > 150) { progress(freed); tick.Restart(); }
        }
        progress?.Invoke(freed);
        // Remove folders that are now empty (never the target folder itself).
        foreach (var t in c.Targets().Where(t => t.Recursive && Directory.Exists(t.Dir)))
        {
            try
            {
                foreach (var d in Directory.EnumerateDirectories(t.Dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                                           .OrderByDescending(d => d.Length).ToList())
                {
                    try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
                }
            }
            catch { }
        }
        return freed;
    }
}
