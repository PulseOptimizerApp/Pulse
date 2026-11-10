using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pulse.Core.Games.Brawlhalla;

/// <summary>
/// Legend portraits and weapon icons, built into Pulse (small PNGs, decoded once and kept). Missing ones are null:
/// callers show the legend's initial instead.
/// </summary>
public static class BrawlhallaArt
{
    static readonly Dictionary<string, ImageSource> Cache = new();

    /// <summary>"Sir Roland" → "sirroland", "Bödvar" → "bodvar".</summary>
    public static string Key(string name)
    {
        var plain = new StringBuilder();
        foreach (var c in (name ?? "").Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsAsciiLetterOrDigit(c)) plain.Append(char.ToLowerInvariant(c));
        return plain.ToString();
    }

    /// <summary>Raised (on a worker thread) when a portrait Pulse didn't have has been downloaded.</summary>
    public static event Action Arrived;
    static readonly HashSet<string> Asked = new();

    /// <summary>
    /// A legend's portrait: built in; or, for a legend newer than this Pulse, downloaded once from the Brawlhalla Wiki
    /// and kept on this PC (null until it arrives, then <see cref="Arrived"/>).
    /// </summary>
    public static ImageSource Legend(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var key = Key(name);
        if (key.Length == 0 || key == "random") return null;
        var img = Load($"legend-{key}.png");
        if (img != null) return img;
        lock (Cache)
        {
            if (Cache.TryGetValue("dl-" + key, out var got) && got != null) return got;
            var file = Brawlhalla.PortraitFile(key);
            if (File.Exists(file) && FromDisk(file) is { } disk) { Cache["dl-" + key] = disk; return disk; }
            if (!Asked.Add(key)) return null;
        }
        _ = Task.Run(async () => { if (await Brawlhalla.DownloadPortrait(name, key)) Arrived?.Invoke(); });
        return null;
    }

    /// <summary>A downloaded portrait: the wiki's has a frame round it, cut off like the built-in ones.</summary>
    static ImageSource FromDisk(string file)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(file);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            bmp.Freeze();
            int m = (int)Math.Round(19.0 * bmp.PixelWidth / 240);
            if (bmp.PixelWidth <= 2 * m || bmp.PixelHeight <= 2 * m) return bmp;
            var cut = new CroppedBitmap(bmp, new Int32Rect(m, m, bmp.PixelWidth - 2 * m, bmp.PixelHeight - 2 * m));
            cut.Freeze();
            return cut;
        }
        catch { return null; }
    }

    public static ImageSource Weapon(string weapon)
    {
        var k = Key(weapon);
        // The API's names → the pictures' names.
        k = k switch { "fists" or "gauntlets" => "gauntlets", "pistol" or "blasters" => "blasters", "katar" => "katars", "boots" or "battleboots" => "battleboots", "hammer" => "grapplehammer", _ => k };
        return Load($"weapon-{k}.png");
    }

    /// <summary>The rank's banner from the game ("Gold 3" → the Gold banner); null when unranked.</summary>
    public static ImageSource Rank(string tier)
    {
        var first = (tier ?? "").Trim().Split(' ')[0].ToLowerInvariant();
        if (first == "fallen") first = "valhallan";
        return first is "valhallan" or "diamond" or "platinum" or "gold" or "silver" or "bronze" or "tin" ? Load($"rank-{first}.png") : null;
    }

    static ImageSource Load(string file)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(file, out var img)) return img;
            try
            {
                var s = Application.GetResourceStream(new Uri($"pack://application:,,,/Pulse;component/Assets/Brawlhalla/{file}", UriKind.Absolute))?.Stream;
                if (s != null)
                    using (s)
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.StreamSource = s;
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        img = bmp;
                    }
            }
            catch { img = null; }
            Cache[file] = img;
            return img;
        }
    }
}
