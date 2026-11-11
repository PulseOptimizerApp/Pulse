using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Pulse.Core.Games.Brawlhalla;

/// <summary>What you changed in Brawlhalla's files: kept in Pulse's state, re-applied after game updates.</summary>
public class BhMods
{
    /// <summary>The notice was read and accepted.</summary>
    public bool Acknowledged { get; set; }
    /// <summary>Skin file → part → picture (a file name in Pulse's own folder).</summary>
    public Dictionary<string, Dictionary<string, string>> Pictures { get; set; } = new();
    /// <summary>Skin ("Gfx_Viking.swf|Bear", see <see cref="BhSkin.Key"/>) → its colors.</summary>
    public Dictionary<string, BhColors> Colors { get; set; } = new();
    /// <summary>Effect key ("dash", "attack-Sword") → the color it's tinted ("RRGGBB"; empty: the game's own).</summary>
    public Dictionary<string, string> Effects { get; set; } = new();
    /// <summary>Background file ("BG_Grove.jpg") → theme key, or "pic:&lt;file&gt;" for your own picture.</summary>
    public Dictionary<string, string> Backgrounds { get; set; } = new();
    /// <summary>Pictures fill the part's space exactly instead of keeping their own shape.</summary>
    public bool Stretch { get; set; }
    /// <summary>Game file (relative) → SHA-256 of what Pulse last wrote, to spot game updates.</summary>
    public Dictionary<string, string> Written { get; set; } = new();
    /// <summary>Mods you installed (downloaded from GameBanana and the like), oldest first; later ones win.</summary>
    public List<BhInstalled> Installed { get; set; } = new();
    /// <summary>Changes picked since the last Apply (they aren't in the game yet).</summary>
    public bool Pending { get; set; }
    public bool FastGpu { get; set; }
    public bool HighPriority { get; set; }

    public bool Any => Installed.Count > 0 || Pictures.Values.Any(p => p.Count > 0) || Colors.Values.Any(c => !c.IsOriginal) || Effects.Values.Any(e => e.Length > 0) || Backgrounds.Count > 0;
}

/// <summary>A mod you installed: its name and the game files it replaces (relative path → its copy in Pulse's folder).</summary>
public class BhInstalled
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = new();
    public DateTime Added { get; set; } = DateTime.UtcNow;
}

public class BhColors
{
    /// <summary>"RRGGBB" → "RRGGBB": colors you picked one by one.</summary>
    public Dictionary<string, string> Map { get; set; } = new();
    /// <summary>Turn every other color around the color wheel, in degrees.</summary>
    public int Hue { get; set; }
    /// <summary>-100 to 100.</summary>
    public int Saturation { get; set; }
    /// <summary>-100 to 100.</summary>
    public int Brightness { get; set; }
    public bool IsOriginal => Map.Count == 0 && Hue == 0 && Saturation == 0 && Brightness == 0;
}

/// <summary>A skin found in the game files: the file it lives in, its name, and its parts.</summary>
public sealed record BhSkin(string File, string Name, string Legend, List<string> Parts)
{
    public string Key => File + "|" + Name;
}

/// <summary>An effect Pulse can recolor: which file it's in and which parts are it.</summary>
public sealed record BhEffect(string Key, string Name, string Detail, string[] Files, Func<string, bool> Takes)
{
    public BhEffect(string key, string name, string detail, string file, Func<string, bool> takes) : this(key, name, detail, new[] { file }, takes) { }
}

/// <summary>A one-click look: one color for a set of effects.</summary>
public sealed record BhEffectPack(string Name, int Rgb);

/// <summary>A map background theme.</summary>
public sealed record BhTheme(string Key, string Name, string Detail);

/// <summary>
/// Customizes Brawlhalla on this PC by changing its own files (only while the game is closed): your pictures on skin
/// parts, your colors, recolored effects and map backgrounds. Before the first change to a file Pulse keeps the
/// original in its own folder; every change is rebuilt from that original, so changes never pile up, and Restore puts
/// it back byte for byte. Game updates replace the files: Pulse notices and applies your changes again.
/// Also two Windows settings for smoother play (the faster graphics card, high CPU priority).
/// </summary>
public static class BrawlhallaMods
{
    static BhMods M => (TweakEngine.State.Brawlhalla ??= new BrawlhallaSettings()).Mods ??= new BhMods();

    static string Root => Path.Combine(TweakEngine.DataDir, "brawlhalla", "mods");
    static string Originals => Path.Combine(Root, "originals");
    public static string PictureDir => Path.Combine(Root, "pictures");
    static string IndexFile => Path.Combine(Root, "skins.json");

    // ───────── where the game is ─────────

    static string _gameDir;

    /// <summary>Brawlhalla's install folder (Steam), or null when it isn't installed.</summary>
    public static string GameDir
    {
        get
        {
            if (_gameDir != null && Directory.Exists(_gameDir)) return _gameDir;
            var dir = Live.GsiServer.SteamGameDir(291550);
            if (dir == null)
            {
                var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Steam\steamapps\common\Brawlhalla");
                if (Directory.Exists(fallback)) dir = fallback;
            }
            return _gameDir = dir != null && File.Exists(Path.Combine(dir, "Brawlhalla.exe")) ? dir : null;
        }
    }

    public static bool GameRunning()
    {
        var ps = Process.GetProcessesByName("Brawlhalla");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    // ───────── skins ─────────

    // Game files with art that isn't a fighter's skin.
    static readonly Regex NotSkins = new(@"^Gfx_(Emojis|GameUI|LoadingFrames|ClientThemeLogos|PlayerThemes|SplashArt|Cutscene|MapArtAnims|Gamemodes|Pets_.*)\.swf$", RegexOptions.IgnoreCase);

    // The game's internal names for some legends, so searching "Mordex" finds "Werewolf".
    static readonly Dictionary<string, string> LegendNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Werewolf"] = "Mordex", ["Viking"] = "Bödvar", ["Cowgirl"] = "Cassidy", ["Ninetails"] = "Yumiko", ["Reaper"] = "Nix",
        ["Valkyrie"] = "Brynn", ["Dwarf"] = "Ulgrim", ["Minotaur"] = "Teros", ["Mikasa"] = "Mikasa", ["Eren"] = "Eren", ["Levi"] = "Levi",
    };

    sealed record IndexEntry(string File, long Size, long Ticks, List<string> Symbols);

    /// <summary>
    /// Every skin in the game files, by the name the game gives it (parts end in "_Name"; parts without one are the
    /// file's own). Reading 300-odd files takes a few seconds the first time, then comes from a saved index.
    /// </summary>
    public static List<BhSkin> Skins(IProgress<double> progress = null)
    {
        var dir = GameDir;
        if (dir == null) return new();
        Dictionary<string, IndexEntry> old = new(StringComparer.OrdinalIgnoreCase);
        try { if (File.Exists(IndexFile)) old = JsonSerializer.Deserialize<List<IndexEntry>>(File.ReadAllText(IndexFile))!.ToDictionary(e => e.File, StringComparer.OrdinalIgnoreCase); } catch { }
        var files = Directory.GetFiles(dir, "Gfx_*.swf").Select(Path.GetFileName).Where(f => !NotSkins.IsMatch(f!)).OrderBy(f => f).ToList();
        var index = new List<IndexEntry>();
        bool changed = false;
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i]!;
            // Index the original, not Pulse's changed copy.
            var path = OriginalOrGame(f);
            try
            {
                var info = new FileInfo(path);
                if (old.TryGetValue(f, out var e) && e.Size == info.Length && e.Ticks == info.LastWriteTimeUtc.Ticks) { index.Add(e); }
                else
                {
                    var swf = SwfFile.Load(File.ReadAllBytes(path));
                    index.Add(new IndexEntry(f, info.Length, info.LastWriteTimeUtc.Ticks, swf.Symbols.Where(s => s.StartsWith("a_", StringComparison.Ordinal)).ToList()));
                    changed = true;
                }
            }
            catch { }
            progress?.Report((i + 1) / (double)files.Count);
        }
        if (changed) try { Directory.CreateDirectory(Root); File.WriteAllText(IndexFile, JsonSerializer.Serialize(index)); } catch { }

        var skins = new List<BhSkin>();
        foreach (var e in index)
        {
            var fileName = Path.GetFileNameWithoutExtension(e.File)[4..];
            foreach (var g in e.Symbols.GroupBy(s => SkinOf(s, fileName)))
            {
                var legend = LegendOf(fileName, g.Key);
                skins.Add(new BhSkin(e.File, g.Key, legend, g.OrderBy(x => x).ToList()));
            }
        }
        return skins.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"a_Hair_Mikasa" → "Mikasa"; a part without a skin name belongs to the file ("Viking").</summary>
    static string SkinOf(string symbol, string fileName)
    {
        var rest = symbol[2..];
        int us = rest.IndexOf('_');
        if (us < 0) return fileName;
        var name = rest[(us + 1)..];
        int swap = name.IndexOf("_Swap", StringComparison.Ordinal);
        if (swap > 0) name = name[..swap];
        return name.Length > 0 ? name : fileName;
    }

    static string LegendOf(string fileName, string skin)
    {
        foreach (var (code, legend) in LegendNames)
            if (fileName.StartsWith(code, StringComparison.OrdinalIgnoreCase) || skin.Contains(code, StringComparison.OrdinalIgnoreCase)) return legend;
        return "";
    }

    /// <summary>A part's short name for people: "a_WeaponSpearBend1_Mikasa" → "Weapon Spear Bend 1".</summary>
    public static string PartLabel(string part)
    {
        var s = part.StartsWith("a_", StringComparison.Ordinal) ? part[2..] : part;
        int us = s.IndexOf('_');
        if (us > 0) s = s[..us];
        return Regex.Replace(Regex.Replace(s, "([a-z])([A-Z0-9])", "$1 $2"), "([0-9])([A-Za-z])", "$1 $2");
    }

    static readonly string[] WeaponWords = { "Spear", "Katar", "Sword", "GreatSword", "Greatsword", "Hammer", "Axe", "Bow", "Gauntlet", "Blaster", "Pistol", "Lance", "RocketLance", "Cannon", "Orb", "Scythe", "Boots", "Chakram", "Fists" };
    static readonly string[] HeadWords = { "Hair", "Eyes", "Eye", "Mouth", "Head", "Accent", "Helmet", "Jaw", "Nose", "Ear", "Face", "Hat", "Beard", "Brow", "Mask" };

    /// <summary>
    /// What a part is, for grouping: "Spear" for every spear piece, "Eyes" for every eye pose. One picture for a
    /// whole group is the easy way: the game swaps between these pieces as you move.
    /// </summary>
    public static string PartGroup(string part)
    {
        var s = part.StartsWith("a_", StringComparison.Ordinal) ? part[2..] : part;
        int us = s.IndexOf('_');
        if (us > 0) s = s[..us];
        if (s.StartsWith("Weapon", StringComparison.Ordinal) && s.Length > 6) s = s[6..];
        foreach (var w in WeaponWords) if (s.StartsWith(w, StringComparison.OrdinalIgnoreCase)) return w switch { "GreatSword" or "Greatsword" => "Greatsword", "RocketLance" or "Lance" => "Rocket Lance", "Pistol" or "Blaster" => "Blasters", "Gauntlet" or "Fists" => "Gauntlets", _ => w };
        var m = Regex.Match(s, "^[A-Z][a-z]+");
        return m.Success ? m.Value : s;
    }

    /// <summary>"Head", "Weapons" or "Body", for ordering groups.</summary>
    public static string GroupKind(string group) =>
        WeaponWords.Any(w => group.StartsWith(w, StringComparison.OrdinalIgnoreCase)) || group is "Rocket Lance" or "Blasters" or "Gauntlets" ? "Weapons"
        : HeadWords.Contains(group, StringComparer.OrdinalIgnoreCase) ? "Head" : "Body";

    /// <summary>A skin file's parts and their sizes (from the original file).</summary>
    public static List<SwfPart> PartsOf(string file)
    {
        try { return SwfFile.Load(File.ReadAllBytes(OriginalOrGame(file))).Parts(); }
        catch { return new(); }
    }

    /// <summary>The colors a skin uses, most used first.</summary>
    public static List<(int Rgb, int Count)> PaletteOf(string file, IEnumerable<string> parts)
    {
        try { return SwfFile.Load(File.ReadAllBytes(OriginalOrGame(file))).Palette(parts); }
        catch { return new(); }
    }

    /// <summary>Copies a picture you picked into Pulse's folder (so moving or deleting yours doesn't break the skin).</summary>
    public static string AddPicture(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif")) throw new InvalidDataException("Pick a PNG, JPG, WebP, BMP or GIF.");
        if (new FileInfo(path).Length > 25_000_000) throw new InvalidDataException("That picture is over 25 MB.");
        LoadPicture(path, 64); // throws when it isn't a picture Windows can read
        Directory.CreateDirectory(PictureDir);
        var name = Guid.NewGuid().ToString("N") + ext;
        File.Copy(path, Path.Combine(PictureDir, name));
        return name;
    }

    /// <summary>Reads a picture as RGBA, scaled down so its longer side is at most <paramref name="maxSide"/>.</summary>
    public static SwfImage LoadPicture(string path, int maxSide = 1024)
    {
        var dec = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource src = dec.Frames[0];
        double k = Math.Min(1, maxSide / (double)Math.Max(src.PixelWidth, src.PixelHeight));
        if (k < 1) src = new TransformedBitmap(src, new ScaleTransform(k, k));
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
        return new SwfImage(w, h, px);
    }

    // ───────── installing mods ─────────

    static string InstalledDir => Path.Combine(Root, "installed");
    static readonly string[] ModTypes = { ".swf", ".jpg", ".jpeg", ".png" };

    /// <summary>Every art file in the game, by name, so a mod's files can be matched to where they go.</summary>
    static Dictionary<string, string> GameFiles()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (GameDir == null) return map;
        foreach (var f in Directory.EnumerateFiles(GameDir, "*", SearchOption.AllDirectories))
        {
            if (!ModTypes.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(GameDir, f);
            if (rel.StartsWith("Adobe AIR", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("META-INF", StringComparison.OrdinalIgnoreCase)) continue;
            map.TryAdd(Path.GetFileName(f), rel);
        }
        return map;
    }

    /// <summary>
    /// Installs a downloaded mod: a .zip, or a single .swf or picture. Each file is matched to the game file with the
    /// same name and kept in Pulse's folder; Apply puts it in the game (with the original kept, as always). Returns the
    /// mod and the files in it that don't match anything in Brawlhalla. Throws with a message for people when nothing fits.
    /// </summary>
    public static (BhInstalled Mod, List<string> Skipped) InstallMod(string path)
    {
        if (GameDir == null) throw new InvalidDataException(Ui.Loc.T("Brawlhalla isn't installed through Steam on this PC."));
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".rar" or ".7z") throw new InvalidDataException(Ui.Loc.T("Unzip this mod first (it's a .rar or .7z), then drop in the files inside, or zip them as a .zip."));
        var files = new List<(string Name, byte[] Bytes)>();
        if (ext == ".zip")
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            foreach (var e in zip.Entries)
            {
                if (e.Length == 0 || e.Length > 200_000_000 || !ModTypes.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase)) continue;
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                files.Add((e.Name, ms.ToArray()));
            }
        }
        else if (ModTypes.Contains(ext)) files.Add((Path.GetFileName(path), File.ReadAllBytes(path)));
        else throw new InvalidDataException(Ui.Loc.T("Pulse installs mods as .zip, .swf, .jpg or .png files."));

        var game = GameFiles();
        var mod = new BhInstalled { Name = Path.GetFileNameWithoutExtension(path) };
        var skipped = new List<string>();
        foreach (var (name, bytes) in files)
        {
            if (!game.TryGetValue(name, out var rel)) { skipped.Add(name); continue; }
            // A .swf has to be one Pulse (and the game) can read.
            if (name.EndsWith(".swf", StringComparison.OrdinalIgnoreCase)) { try { SwfFile.Load(bytes); } catch { skipped.Add(name); continue; } }
            var store = Path.Combine(InstalledDir, mod.Id);
            Directory.CreateDirectory(store);
            File.WriteAllBytes(Path.Combine(store, name), bytes);
            mod.Files[rel] = name;
        }
        if (mod.Files.Count == 0)
            throw new InvalidDataException(Ui.Loc.T("None of this mod's files match a Brawlhalla file. It may be for an older version of the game, or need its own installer."));
        M.Installed.Add(mod);
        M.Pending = true;
        TweakEngine.Save();
        return (mod, skipped);
    }

    /// <summary>Takes an installed mod out (Apply puts the game's own files back).</summary>
    public static void RemoveMod(string id)
    {
        var mod = M.Installed.FirstOrDefault(m => m.Id == id);
        if (mod == null) return;
        M.Installed.Remove(mod);
        M.Pending = true;
        try { Directory.Delete(Path.Combine(InstalledDir, mod.Id), true); } catch { }
        TweakEngine.Save();
    }

    /// <summary>The installed mod file that replaces a game file (the latest one wins), or null.</summary>
    static byte[] Replacement(string rel)
    {
        for (int i = M.Installed.Count - 1; i >= 0; i--)
        {
            var m = M.Installed[i];
            if (m.Files.TryGetValue(rel, out var name))
            {
                var p = Path.Combine(InstalledDir, m.Id, name);
                if (File.Exists(p)) return File.ReadAllBytes(p);
            }
        }
        return null;
    }

    // ───────── effects ─────────

    // Effects files that aren't one legend's or skin's own: shared, weapons, menus, maps.
    static readonly Regex NotLegendFx = new(@"^SFX_(1|KO|KO2|HitReacts|Emote|GameModes|Level|MapArtAnims|.*_MapArtAnims|BP\d+|Sword|Spear|Hammer|Axe|Katar|Bow|Pistols|Lance|Scythe|Cannon|Orb|GreatSword|Fists|Chakram|SWBlasters|Sabers)\.swf$", RegexOptions.IgnoreCase);

    static readonly Regex BodyPart = new(@"^a_(Hand|Arm|Forearm|Shoulder|Torso|Waist|Head|Leg|Foot|Thigh|Shin|Jaw|Eye|Mouth|Hair|Pelvis|Neck|Calf|Belt|Accent|Helmet|Nose|Weapon)", RegexOptions.IgnoreCase);

    static bool Starts(string s, params string[] names) => names.Any(n => s.Equals(n, StringComparison.Ordinal) || s.StartsWith(n, StringComparison.Ordinal));

    /// <summary>The effects Pulse can recolor, and where they are.</summary>
    public static List<BhEffect> EffectList()
    {
        var list = new List<BhEffect>
        {
            new("dash", "Dashes", "The streak and dust when you dash forward or back.", "SFX_1.swf", s => Starts(s, "a_DashForward", "a_DashBack", "a_DashDust", "a_DustDirectional")),
            new("dodge", "Dodges", "The flash when you dodge, and the dodge cooldown.", "SFX_1.swf", s => Starts(s, "a_DodgeFX", "a_DodgeCooldown")),
            new("gc", "Gravity cancels", "The ring when you gravity cancel.", "SFX_1.swf", s => Starts(s, "a_GravityCancelFX")),
            new("jump", "Jumps and walls", "Jump dust, wall slides and wall jumps.", "SFX_1.swf", s => Starts(s, "a_JumpDust", "a_WallSlip", "a_DustWallTouch")),
            new("trail", "Knockback trails", "The smoke and fire behind a fighter sent flying.", new[] { "SFX_1.swf", "SFX_KO.swf" }, s => Starts(s, "a_SmokeTrailREDUX", "a_SmokeTrailFire", "a_SmokeTrailSuperUltraFast", "a_SmokeTrail03", "a_SmokeTrail04")),
            new("hit", "Hit sparks", "The flash when an attack lands, for every weapon.", "SFX_HitReacts.swf", s => Starts(s, "a_HitReact", "a_SteamHit", "a_Apoc04Hit")),
            new("boom", "Explosions", "Bombs, mines, snowballs and other item blasts.", "SFX_1.swf", s => Starts(s, "a_Explosion", "a_FlashBangExplosion", "a_Snowball_Impact")),
            new("ko", "KO effects", "The burst when someone is knocked out, including the KO effect you have on.", new[] { "SFX_KO.swf", "SFX_KO2.swf" }, s => Starts(s, "a_SmokeTrailEnd", "a_KFPKOglow", "a_MegamanKO", "SkelaughKO", "StreetFighterKO", "a_OffScreenBubble")),
        };
        var dir = GameDir;
        // Each legend's and crossover's own effects file: signatures, special trails and sparks.
        if (dir != null)
        {
            var own = Directory.GetFiles(dir, "SFX_*.swf").Select(Path.GetFileName).Where(f => !NotLegendFx.IsMatch(f!)).OrderBy(f => f).ToArray();
            if (own.Length > 0)
                list.Add(new BhEffect("legend", "Legend and skin effects", "Signature attacks and special effects that belong to a legend or crossover skin.", own!, s => !BodyPart.IsMatch(s)));
        }
        foreach (var (file, weapon) in new[] { ("SFX_Sword.swf", "Sword"), ("SFX_Spear.swf", "Spear"), ("SFX_Hammer.swf", "Hammer"), ("SFX_Axe.swf", "Axe"), ("SFX_Katar.swf", "Katars"), ("SFX_Bow.swf", "Bow"),
                     ("SFX_Pistols.swf", "Blasters"), ("SFX_Lance.swf", "Rocket Lance"), ("SFX_Scythe.swf", "Scythe"), ("SFX_Cannon.swf", "Cannon"), ("SFX_Orb.swf", "Orb"), ("SFX_GreatSword.swf", "Greatsword"),
                     ("SFX_Fists.swf", "Gauntlets"), ("SFX_Chakram.swf", "Chakram") })
        {
            if (dir != null && !File.Exists(Path.Combine(dir, file))) continue;
            list.Add(new BhEffect("attack-" + weapon, weapon + " attacks", "Slashes, sparks and smoke on " + weapon.ToLowerInvariant() + " attacks and signatures.", file, s => !BodyPart.IsMatch(s)));
        }
        return list;
    }

    /// <summary>One-click looks: one color for every effect.</summary>
    public static readonly List<BhEffectPack> Packs = new()
    {
        new("Royal Gold", 0xFFC93C), new("Ice", 0x5CE1FF), new("Inferno", 0xFF4A1F),
        new("Shadow", 0x8A4DFF), new("Toxic", 0x8BFF3B), new("Sakura", 0xFF7CC8),
    };

    /// <summary>Which group an effect sits in on the Effects tab: Movement, Hits, Weapons or Legends.</summary>
    public static string EffectGroup(string key) => key switch
    {
        "dash" or "dodge" or "gc" or "jump" or "trail" => "Movement",
        "hit" or "boom" or "ko" => "Hits and KOs",
        "legend" => "Legends and crossovers",
        _ => "Weapons",
    };

    // ───────── map backgrounds ─────────

    public static readonly List<BhTheme> Themes = new()
    {
        new("clean", "Clean & dark", "Dimmed and softened, so fighters stand out."),
        new("black", "Pitch black", "No background at all: the clearest view of the fight."),
        new("night", "Night", "Cool, dark blue."),
        new("mono", "Black & white", "No color, so the fighters' colors pop."),
        new("sunset", "Sunset", "Warm orange and purple."),
        new("vivid", "Vivid", "Richer, brighter colors."),
    };

    /// <summary>The background pictures in the game ("BG_Grove.jpg"), in mapArt\Backgrounds.</summary>
    public static List<string> BackgroundFiles()
    {
        var dir = GameDir == null ? null : Path.Combine(GameDir, "mapArt", "Backgrounds");
        return dir != null && Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jpg").Select(Path.GetFileName).OrderBy(f => f).ToList()! : new();
    }

    /// <summary>A background's name for people: "BG_MiamiDome_Anim.jpg" → "Miami Dome (animated)".</summary>
    public static string BackgroundLabel(string file)
    {
        var s = Path.GetFileNameWithoutExtension(file);
        if (s.StartsWith("BG_", StringComparison.Ordinal)) s = s[3..];
        bool anim = s.EndsWith("_Anim", StringComparison.Ordinal);
        if (anim) s = s[..^5];
        s = Regex.Replace(s.Replace('_', ' ').Replace('-', ' '), "([a-z])([A-Z0-9])", "$1 $2");
        return anim ? s + " (animated)" : s;
    }

    public static string BackgroundPath(string file) => Path.Combine("mapArt", "Backgrounds", file);

    /// <summary>The original background, for previews.</summary>
    public static string OriginalBackground(string file) => OriginalOrGame(BackgroundPath(file));

    /// <summary>A background with a theme (or your picture, "pic:name") applied, as JPEG bytes the same size as the original.</summary>
    public static byte[] ThemedBackground(byte[] original, string theme)
    {
        var src = BitmapDecoder.Create(new MemoryStream(original), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        var enc = new JpegBitmapEncoder { QualityLevel = 90 };
        enc.Frames.Add(BitmapFrame.Create(ApplyTheme(src, theme)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>A theme applied to a picture (any size, so the page can preview it on a thumbnail).</summary>
    public static BitmapSource ApplyTheme(BitmapSource src, string theme)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        byte[] px;
        if (theme.StartsWith("pic:", StringComparison.Ordinal))
        {
            // Your picture, cropped to fill the background (centred).
            var pic = BitmapDecoder.Create(new Uri(Path.Combine(PictureDir, theme[4..])), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
            double k = Math.Max(w / (double)pic.PixelWidth, h / (double)pic.PixelHeight);
            var scaled = new TransformedBitmap(pic, new ScaleTransform(k, k));
            int cw = Math.Min(w, scaled.PixelWidth), ch = Math.Min(h, scaled.PixelHeight);
            int cx = Math.Max(0, (scaled.PixelWidth - cw) / 2), cy = Math.Max(0, (scaled.PixelHeight - ch) / 2);
            px = Pixels(new CroppedBitmap(scaled, new Int32Rect(cx, cy, cw, ch)), out w, out h);
        }
        else
        {
            BitmapSource basis = src;
            if (theme == "clean" && w > 16)
            {
                // Soft focus: shrink a lot, then back up.
                double k = Math.Max(0.08, 8.0 / w);
                var small = new TransformedBitmap(src, new ScaleTransform(k, k));
                basis = new TransformedBitmap(small, new ScaleTransform(w / (double)small.PixelWidth, h / (double)small.PixelHeight));
            }
            px = Pixels(basis, out w, out h);
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i], g = px[i + 1], r = px[i + 2];
                double y = 0.299 * r + 0.587 * g + 0.114 * b;
                double fy = (i / 4 / w) / (double)h;
                (r, g, b) = theme switch
                {
                    "clean" => (r * 0.42, g * 0.42, b * 0.45),
                    "black" => (14, 15, 18),
                    "night" => (y * 0.32 + r * 0.08, y * 0.40 + g * 0.08, y * 0.62 + b * 0.12),
                    "mono" => (y * 0.85, y * 0.85, y * 0.85),
                    "sunset" => (r * 0.55 + (1 - fy) * 120 + 20, g * 0.45 + 30 * fy, b * 0.5 + fy * 90 + 40),
                    "vivid" => (y + (r - y) * 1.45, y + (g - y) * 1.45, y + (b - y) * 1.45),
                    _ => (r, g, b),
                };
                px[i] = Clamp(b); px[i + 1] = Clamp(g); px[i + 2] = Clamp(r); px[i + 3] = 255;
            }
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    static byte[] Pixels(BitmapSource s, out int w, out int h)
    {
        var bgra = new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);
        w = bgra.PixelWidth; h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        return px;
    }

    // ───────── colors ─────────

    /// <summary>What a skin's colors become: your picks first, then the hue/saturation/brightness for the rest.</summary>
    public static Func<int, int> ColorMap(BhColors c)
    {
        var picks = c.Map.Where(kv => TryHex(kv.Key, out _) && TryHex(kv.Value, out _)).ToDictionary(kv => Hex(kv.Key), kv => Hex(kv.Value));
        return rgb =>
        {
            if (picks.TryGetValue(rgb, out var to)) return to;
            if (c.Hue == 0 && c.Saturation == 0 && c.Brightness == 0) return rgb;
            // Outlines stay outlines.
            if (rgb == 0) return 0;
            var (hh, ss, ll) = ToHsl(rgb);
            hh = (hh + c.Hue / 360.0 + 1) % 1;
            ss = Math.Clamp(ss * (1 + c.Saturation / 100.0), 0, 1);
            ll = c.Brightness >= 0 ? ll + (1 - ll) * c.Brightness / 100.0 * 0.6 : ll * (1 + c.Brightness / 100.0 * 0.6);
            return FromHsl(hh, ss, ll);
        };
    }

    /// <summary>Tints a color toward <paramref name="target"/>'s hue and saturation, keeping how light it is.</summary>
    public static int Tint(int rgb, int target)
    {
        var (_, _, l) = ToHsl(rgb);
        var (th, ts, tl) = ToHsl(target);
        // Keep white highlights a little white, so effects still glow.
        double light = l > 0.92 ? l : Math.Clamp(l * 0.75 + tl * 0.25, 0, 1);
        return FromHsl(th, ts, light);
    }

    public static bool TryHex(string s, out int rgb)
    {
        s = (s ?? "").Trim().TrimStart('#');
        rgb = 0;
        return s.Length == 6 && int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out rgb);
    }

    static int Hex(string s) { TryHex(s, out var v); return v; }
    public static string ToHex(int rgb) => (rgb & 0xFFFFFF).ToString("X6");

    public static (double H, double S, double L) ToHsl(int rgb)
    {
        double r = ((rgb >> 16) & 255) / 255.0, g = ((rgb >> 8) & 255) / 255.0, b = (rgb & 255) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d < 1e-9) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    public static int FromHsl(double h, double s, double l)
    {
        double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1 / 6.0) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
            return p;
        }
        double r, g, b;
        if (s < 1e-9) r = g = b = l;
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            r = Hue(p, q, h + 1 / 3.0); g = Hue(p, q, h); b = Hue(p, q, h - 1 / 3.0);
        }
        return (Clamp(r * 255) << 16) | (Clamp(g * 255) << 8) | Clamp(b * 255);
    }

    // ───────── applying ─────────

    static string OriginalOrGame(string rel)
    {
        var orig = Path.Combine(Originals, rel);
        return File.Exists(orig) ? orig : Path.Combine(GameDir ?? "", rel);
    }

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    /// <summary>Every game file Pulse has changes for, or has changed before.</summary>
    static HashSet<string> TouchedFiles()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (f, p) in M.Pictures) if (p.Count > 0) set.Add(f);
        foreach (var (k, c) in M.Colors) if (!c.IsOriginal) set.Add(k.Split('|')[0]);
        var effects = EffectList();
        foreach (var (k, v) in M.Effects) if (v.Length > 0 && effects.FirstOrDefault(e => e.Key == k) is { } e) set.UnionWith(e.Files);
        foreach (var f in M.Backgrounds.Keys) set.Add(BackgroundPath(f));
        foreach (var m in M.Installed) set.UnionWith(m.Files.Keys);
        foreach (var f in M.Written.Keys) set.Add(f);
        if (Directory.Exists(Originals))
            foreach (var f in Directory.EnumerateFiles(Originals, "*", SearchOption.AllDirectories)) set.Add(Path.GetRelativePath(Originals, f));
        return set;
    }

    /// <summary>
    /// Writes every change you've made into the game's files (and puts back files you no longer change). Brawlhalla has
    /// to be closed. Returns null when all went well, otherwise what went wrong, for the person.
    /// </summary>
    public static string ApplyAll()
    {
        if (GameDir == null) return Ui.Loc.T("Brawlhalla isn't installed through Steam on this PC.");
        if (GameRunning()) return Ui.Loc.T("Close Brawlhalla first: its files can't change while it's running.");
        var problems = new List<string>();
        foreach (var rel in TouchedFiles())
        {
            try { ApplyFile(rel); }
            catch (UnauthorizedAccessException) { return Ui.Loc.T("Windows didn't let Pulse change Brawlhalla's files. Run Pulse as administrator and try again."); }
            catch (Exception ex) { problems.Add($"{rel}: {ex.Message}"); }
        }
        TweakEngine.Save();
        if (problems.Count > 0)
        {
            TweakEngine.AddLog("brawlhalla", "Brawlhalla mods: " + string.Join("; ", problems));
            return Ui.Loc.F("{0} file(s) couldn't be changed; the rest are done.", problems.Count);
        }
        M.Pending = false;
        TweakEngine.Save();
        TweakEngine.AddLog("brawlhalla", "Brawlhalla mods applied");
        return null;
    }

    static void ApplyFile(string rel)
    {
        var game = Path.Combine(GameDir!, rel);
        if (!File.Exists(game)) { M.Written.Remove(rel); return; }
        var current = File.ReadAllBytes(game);
        var origPath = Path.Combine(Originals, rel);
        if (!File.Exists(origPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(origPath)!);
            File.WriteAllBytes(origPath, current);
        }
        else if (M.Written.TryGetValue(rel, out var mine) && mine != Sha(current))
        {
            // The game replaced the file (an update): that's the new original.
            File.WriteAllBytes(origPath, current);
        }
        var original = File.ReadAllBytes(origPath);
        // An installed mod's file takes the original's place; your colors and effects still go on top.
        var basis = Replacement(rel);
        var result = Build(rel, basis ?? original) ?? basis;
        if (result == null)
        {
            // Nothing to change any more: the original goes back and Pulse forgets the file.
            if (Sha(current) != Sha(original)) WriteGameFile(game, original);
            File.Delete(origPath);
            M.Written.Remove(rel);
            return;
        }
        WriteGameFile(game, result);
        M.Written[rel] = Sha(result);
    }

    static void WriteGameFile(string path, byte[] bytes)
    {
        var tmp = path + ".pulse-tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, true);
    }

    /// <summary>The changed file, built from the original; null when nothing changes it.</summary>
    static byte[] Build(string rel, byte[] original)
    {
        var name = Path.GetFileName(rel);
        if (rel.StartsWith(Path.Combine("mapArt", "Backgrounds"), StringComparison.OrdinalIgnoreCase))
            return M.Backgrounds.TryGetValue(name, out var theme) && theme.Length > 0 ? ThemedBackground(original, theme) : null;

        bool any = false;
        var swf = SwfFile.Load(original);
        var fileName = Path.GetFileNameWithoutExtension(name).Length > 4 ? Path.GetFileNameWithoutExtension(name)[4..] : name;
        foreach (var (key, colors) in M.Colors)
        {
            var bits = key.Split('|');
            if (bits.Length != 2 || !bits[0].Equals(name, StringComparison.OrdinalIgnoreCase) || colors.IsOriginal) continue;
            // Only this skin's parts: one file can hold several skins.
            var parts = swf.Symbols.Where(x => x.StartsWith("a_", StringComparison.Ordinal) && SkinOf(x, fileName) == bits[1]).ToList();
            if (parts.Count > 0 && swf.Recolor(ColorMap(colors), parts) > 0) any = true;
        }
        foreach (var e in EffectList().Where(e => e.Files.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            if (!M.Effects.TryGetValue(e.Key, out var hex) || !TryHex(hex, out var target)) continue;
            var parts = swf.Symbols.Where(e.Takes).ToList();
            if (parts.Count == 0) continue;
            if (swf.Recolor(rgb => Tint(rgb, target), parts) > 0) any = true;
        }
        if (M.Pictures.TryGetValue(name, out var pics) && pics.Count > 0)
        {
            var images = new Dictionary<string, SwfImage>();
            var cache = new Dictionary<string, SwfImage>();
            foreach (var (part, pic) in pics)
            {
                var path = Path.Combine(PictureDir, pic);
                if (!File.Exists(path)) continue;
                if (!cache.TryGetValue(pic, out var img)) cache[pic] = img = LoadPicture(path);
                images[part] = img;
            }
            if (swf.ReplaceParts(images, M.Stretch) > 0) any = true;
        }
        return any ? swf.Save() : null;
    }

    /// <summary>Puts every file Pulse changed back to the original and forgets all changes.</summary>
    public static string RestoreAll()
    {
        if (GameRunning()) return Ui.Loc.T("Close Brawlhalla first: its files can't change while it's running.");
        var m = M;
        m.Pictures.Clear(); m.Colors.Clear(); m.Effects.Clear(); m.Backgrounds.Clear();
        foreach (var mod in m.Installed.ToList()) RemoveMod(mod.Id);
        var result = ApplyAll();
        return result;
    }

    /// <summary>True when a game update replaced files Pulse had changed (so your changes need applying again).</summary>
    public static bool NeedsReapply()
    {
        if (GameDir == null || M.Written.Count == 0) return false;
        foreach (var (rel, sha) in M.Written)
        {
            try
            {
                var path = Path.Combine(GameDir, rel);
                if (File.Exists(path) && Sha(File.ReadAllBytes(path)) != sha) return true;
            }
            catch { }
        }
        return false;
    }

    // ───────── smoother ─────────

    const string GpuKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    const string HighPerformance = "GpuPreference=2;";
    const string IfeoKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\Brawlhalla.exe\PerfOptions";

    static string Exe => GameDir == null ? null : Path.Combine(GameDir, "Brawlhalla.exe");

    /// <summary>Windows runs Brawlhalla on the faster graphics card (Settings → System → Display → Graphics).</summary>
    public static bool FastGpuOn => Exe != null && Reg.Get(RegistryHive.CurrentUser, GpuKey, Exe) as string == HighPerformance;

    public static void SetFastGpu(bool on)
    {
        if (Exe == null) return;
        if (on) Reg.Set(RegistryHive.CurrentUser, GpuKey, Exe, HighPerformance, RegistryValueKind.String);
        else if (FastGpuOn) Reg.Delete(RegistryHive.CurrentUser, GpuKey, Exe);
        M.FastGpu = on;
        TweakEngine.Save();
    }

    /// <summary>Windows gives Brawlhalla high CPU priority every time it starts (so background apps wait their turn).</summary>
    public static bool HighPriorityOn => Reg.Get(RegistryHive.LocalMachine, IfeoKey, "CpuPriorityClass") is int v && v == 3;

    public static void SetHighPriority(bool on)
    {
        if (on) Reg.Set(RegistryHive.LocalMachine, IfeoKey, "CpuPriorityClass", 3, RegistryValueKind.DWord);
        else if (HighPriorityOn) Reg.Delete(RegistryHive.LocalMachine, IfeoKey, "CpuPriorityClass");
        M.HighPriority = on;
        TweakEngine.Save();
    }
}
