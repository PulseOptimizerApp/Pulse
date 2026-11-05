using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pulse.Core.Roblox;

/// <summary>
/// Changes the font Roblox's menus, chat and game text use, the way Bloxstrap does: the font is copied into each
/// Roblox version's content\fonts folder, and every font family in content\fonts\families is pointed at it.
/// The original family files are kept next to them (families.pulse-original) and put back by <see cref="Reset"/>.
/// Emoji fonts are left alone so emojis still show.
/// </summary>
public static class RobloxFont
{
    public const string FileStem = "PulseCustomFont";
    const string BackupDir = "families.pulse-original";

    /// <summary>Where Pulse keeps its copy of the chosen font (so it can re-apply after Roblox updates).</summary>
    public static string StoreDir => Path.Combine(RobloxUser.LocalAppData, "PulseOptimizer", "roblox-font");

    /// <summary>Is this a font Roblox can load (TrueType or OpenType, not a collection)?</summary>
    public static bool IsFontFile(string path, out string why)
    {
        why = null;
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".ttf" or ".otf")) { why = "Pick a .ttf or .otf font file."; return false; }
            using var fs = File.OpenRead(path);
            if (fs.Length < 1024 || fs.Length > 50L << 20) { why = "That doesn't look like a font file."; return false; }
            var head = new byte[4];
            fs.ReadExactly(head);
            bool ok = (head[0] == 0 && head[1] == 1 && head[2] == 0 && head[3] == 0) // TrueType
                      || (head[0] == 'O' && head[1] == 'T' && head[2] == 'T' && head[3] == 'O') // OpenType (CFF)
                      || (head[0] == 't' && head[1] == 'r' && head[2] == 'u' && head[3] == 'e');
            if (!ok) why = "That doesn't look like a font file.";
            return ok;
        }
        catch (Exception ex) { why = ex.Message; return false; }
    }

    /// <summary>Copies the font into Pulse's folder and returns the stored path.</summary>
    public static string Store(string source)
    {
        Directory.CreateDirectory(StoreDir);
        var dest = Path.Combine(StoreDir, FileStem + Path.GetExtension(source).ToLowerInvariant());
        File.Copy(source, dest, overwrite: true);
        return dest;
    }

    /// <summary>Points one Roblox version at the font. Returns how many font families were changed.</summary>
    public static int Apply(string versionDir, string fontPath)
    {
        var fonts = Path.Combine(versionDir, "content", "fonts");
        var families = Path.Combine(fonts, "families");
        if (!Directory.Exists(families) || !File.Exists(fontPath)) return 0;
        var fileName = FileStem + Path.GetExtension(fontPath).ToLowerInvariant();
        File.Copy(fontPath, Path.Combine(fonts, fileName), overwrite: true);

        // Keep Roblox's own family files the first time, so Reset can put them back exactly.
        var backup = Path.Combine(fonts, BackupDir);
        if (!Directory.Exists(backup))
        {
            Directory.CreateDirectory(backup);
            foreach (var f in Directory.GetFiles(families, "*.json")) File.Copy(f, Path.Combine(backup, Path.GetFileName(f)), overwrite: false);
        }

        int n = 0;
        foreach (var file in Directory.GetFiles(backup, "*.json"))
        {
            try
            {
                var text = Rewrite(File.ReadAllText(file), "rbxasset://fonts/" + fileName);
                if (text == null) continue;
                File.WriteAllText(Path.Combine(families, Path.GetFileName(file)), text);
                n++;
            }
            catch { }
        }
        return n;
    }

    /// <summary>One family file with every face pointed at the font, or null to leave it (emoji fonts).</summary>
    public static string Rewrite(string json, string assetId)
    {
        var node = JsonNode.Parse(json);
        if (node is not JsonObject root) return null;
        var name = root["name"]?.GetValue<string>() ?? "";
        if (name.Contains("emoji", StringComparison.OrdinalIgnoreCase)) return null;
        if (root["faces"] is not JsonArray faces) return null;
        foreach (var face in faces.OfType<JsonObject>()) face["assetId"] = assetId;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Puts Roblox's own fonts back in one version.</summary>
    public static void Reset(string versionDir)
    {
        var fonts = Path.Combine(versionDir, "content", "fonts");
        var backup = Path.Combine(fonts, BackupDir);
        var families = Path.Combine(fonts, "families");
        if (!Directory.Exists(backup) || !Directory.Exists(families)) return;
        foreach (var f in Directory.GetFiles(backup, "*.json"))
            try { File.Copy(f, Path.Combine(families, Path.GetFileName(f)), overwrite: true); } catch { }
    }

    /// <summary>Applies (or resets) the saved choice in every installed Roblox version.</summary>
    public static int ApplyAll()
    {
        var path = RobloxBoost.Settings.CustomFont;
        int n = 0;
        foreach (var v in RobloxBoost.PlayerVersions())
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) { if (Apply(v, path) > 0) n++; }
                else Reset(v);
            }
            catch { }
        }
        return n;
    }

    /// <summary>Fonts installed in Windows that Roblox can use: (name, file).</summary>
    public static List<(string Name, string File)> WindowsFonts()
    {
        var list = new List<(string, string)>();
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        try
        {
            foreach (var f in Directory.GetFiles(dir).Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)))
            {
                string name;
                try
                {
                    var gt = new System.Windows.Media.GlyphTypeface(new Uri(f));
                    var fam = gt.Win32FamilyNames.TryGetValue(System.Globalization.CultureInfo.GetCultureInfo("en-US"), out var en) ? en : gt.Win32FamilyNames.Values.FirstOrDefault();
                    var face = gt.Win32FaceNames.TryGetValue(System.Globalization.CultureInfo.GetCultureInfo("en-US"), out var fe) ? fe : "";
                    name = string.IsNullOrEmpty(face) || face == "Regular" ? fam : fam + " " + face;
                }
                catch { continue; }
                if (!string.IsNullOrWhiteSpace(name)) list.Add((name, f));
            }
        }
        catch { }
        return list.GroupBy(x => x.Item1).Select(g => g.First()).OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
