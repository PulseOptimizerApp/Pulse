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

    public static ImageSource Legend(string name) => string.IsNullOrEmpty(name) ? null : Load($"legend-{Key(name)}.png");

    public static ImageSource Weapon(string weapon)
    {
        var k = Key(weapon);
        // The API's names → the pictures' names.
        k = k switch { "fists" or "gauntlets" => "gauntlets", "pistol" or "blasters" => "blasters", "katar" => "katars", "boots" or "battleboots" => "battleboots", "hammer" => "grapplehammer", _ => k };
        return Load($"weapon-{k}.png");
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
