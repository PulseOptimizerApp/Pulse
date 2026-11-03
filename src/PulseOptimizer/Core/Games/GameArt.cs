using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Pulse.Core.Games;

/// <summary>
/// Art for game tiles, read from the PC itself: the game's own app icon and, for Steam games, the cover art Steam
/// already downloaded. Nothing is fetched from the internet and no publisher art ships with Pulse.
/// Games that aren't installed get a tile in their own colors.
/// </summary>
public static class GameArt
{
    static readonly Dictionary<string, int> SteamIds = new()
    {
        ["cs2"] = 730, ["r6"] = 359550, ["rivals"] = 2767030, ["overwatch"] = 2357570,
    };

    public static Brush Tint(KnownGame g)
    {
        var b = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(g.Color1), (Color)ColorConverter.ConvertFromString(g.Color2), new Point(0, 0), new Point(0.7, 1));
        b.Freeze();
        return b;
    }

    /// <summary>A soft radial glow in the game's main color, for the top of its tile.</summary>
    public static Brush Glow(KnownGame g)
    {
        var c = (Color)ColorConverter.ConvertFromString(g.Color1);
        var b = new RadialGradientBrush(Color.FromArgb(0x66, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    /// <summary>
    /// Finds the icon and cover for one game. Cover: Steam's own cache on this PC, else the cover embedded at build time.
    /// Icon: from the installed game. Slow-ish (disk), so call off the UI thread.
    /// </summary>
    public static (ImageSource icon, ImageSource cover) Find(KnownGame g, bool installed)
    {
        if (g.IsGeneric || g.IsAddTile) return (null, null);
        ImageSource cover = null, icon = null;
        if (installed)
        {
            try { cover = SteamCover(g); } catch { }
            try { icon = Native.LargeIcon(ExePath(g)) ?? Native.LargeIcon(UninstallIcon(g)); } catch { }
        }
        cover ??= Bundled(g.Name);
        return (icon, cover);
    }

    static readonly Dictionary<string, ImageSource> BundledCache = new();

    /// <summary>Cover art embedded by tools/get-game-art.py (keyed by the game's name, letters and digits only).</summary>
    public static ImageSource Bundled(string name)
    {
        var key = new string((name ?? "").ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        lock (BundledCache)
        {
            if (BundledCache.TryGetValue(key, out var img)) return img;
            try
            {
                Stream stream = null;
                foreach (var ext in new[] { ".jpg", ".png" })
                {
                    try { stream = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/Games/{key}{ext}", UriKind.Absolute))?.Stream; }
                    catch { stream = null; }
                    if (stream != null) break;
                }
                using var _ = stream;
                if (stream != null)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = stream;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 300;
                    bmp.EndInit();
                    bmp.Freeze();
                    img = bmp;
                }
            }
            catch { img = null; }
            return BundledCache[key] = img;
        }
    }

    static ImageSource SteamCover(KnownGame g)
    {
        if (!SteamIds.TryGetValue(g.Id, out var id)) return null;
        if (Reg.Get(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath") is not string steam) return null;
        var cache = Path.Combine(steam.Replace('/', '\\'), "appcache", "librarycache");
        string file = null;
        var flat = Path.Combine(cache, $"{id}_library_600x900.jpg");
        if (File.Exists(flat)) file = flat;
        else
        {
            // Newer Steam clients keep each game's art in its own folder.
            var dir = Path.Combine(cache, id.ToString());
            if (Directory.Exists(dir))
                file = Directory.EnumerateFiles(dir, "library_600x900*.jpg", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }).FirstOrDefault();
        }
        return file == null ? null : Load(file, 360);
    }

    static ImageSource Load(string file, int width)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri(file);
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.DecodePixelWidth = width;
        img.EndInit();
        img.Freeze();
        return img;
    }

    static string ExePath(KnownGame g)
    {
        // 1. In its Steam or Epic folder.
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var (name, folder) in PcDiagnostics.LauncherGames())
        {
            if (!g.IsNamed(name) || !Directory.Exists(folder)) continue;
            foreach (var exe in g.Exes)
            {
                var hit = Directory.EnumerateFiles(folder, exe, opts).FirstOrDefault();
                if (hit != null) return hit;
            }
        }
        // 2. Where its own installer says it is (Riot, Battle.net, Ubisoft).
        foreach (var folder in UninstallValues(g, "InstallLocation"))
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var exe in g.Exes.Append(g.Name.Replace(" ", "") + ".exe"))
            {
                var hit = Directory.EnumerateFiles(folder, exe, opts).FirstOrDefault();
                if (hit != null) return hit;
            }
        }
        // 3. Roblox keeps itself in AppData.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (g.Id == "roblox")
        {
            var versions = Path.Combine(local, "Roblox", "Versions");
            if (Directory.Exists(versions))
                return Directory.EnumerateFiles(versions, "RobloxPlayerBeta.exe", opts).OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        }
        return null;
    }

    static string UninstallIcon(KnownGame g) => UninstallValues(g, "DisplayIcon").FirstOrDefault();

    static IEnumerable<string> UninstallValues(KnownGame g, string value)
    {
        foreach (var (hive, path) in new[]
        {
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            foreach (var sub in Reg.SubKeys(hive, path))
            {
                var k = $@"{path}\{sub}";
                if (Reg.Get(hive, k, "DisplayName") is not string dn || !g.IsNamed(dn)) continue;
                if (Reg.Get(hive, k, value) is string v && v.Length > 0) yield return Environment.ExpandEnvironmentVariables(v);
            }
        }
    }
}
