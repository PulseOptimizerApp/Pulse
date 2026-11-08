using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pulse.Core;

/// <summary>
/// Moves Pulse's settings to another PC: crosshairs (and their pictures), headphone EQ, macros, clips settings, overlay, Roblox, reaction history and app options go into one .pulsebackup file.
/// What belongs to this PC stays behind: tweak backups and undo history, restore points, key remaps, game file
/// backups and health snapshots (restoring those on another PC would undo things that were never done there).
/// </summary>
public static class SettingsBackup
{
    public const string Extension = ".pulsebackup";
    const string SettingsEntry = "pulse-settings.json", InfoEntry = "pulse-backup.json";
    static readonly string[] Folders = { "crosshairs" };

    /// <summary>The parts of the saved state that travel to another PC.</summary>
    internal static readonly string[] Portable =
    {
        "Settings", "GameChecklist", "CustomGames", "Roblox", "Clips", "Reaction", "Macros", "MacroList", "MacroOptions",
        "GameSession", "Crosshair", "GameAudio",
    };

    public static void Export(string path)
    {
        var all = JsonSerializer.SerializeToNode(TweakEngine.State)!.AsObject();
        var keep = new JsonObject();
        foreach (var k in Portable) if (all[k] is JsonNode n) keep[k] = n.DeepClone();
        var tmp = path + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            Write(zip, InfoEntry, new JsonObject { ["app"] = "Pulse", ["version"] = Updater.CurrentVersion, ["created"] = DateTime.Now.ToString("o"), ["dataDir"] = TweakEngine.DataDir }.ToJsonString());
            Write(zip, SettingsEntry, keep.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            foreach (var f in Folders)
            {
                var dir = Path.Combine(TweakEngine.DataDir, f);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir)) zip.CreateEntryFromFile(file, f + "/" + Path.GetFileName(file), CompressionLevel.Optimal);
            }
        }
        File.Move(tmp, path, true);
    }

    static void Write(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open());
        w.Write(text);
    }

    /// <summary>What a backup holds, for the confirm message. Null when it isn't a Pulse backup.</summary>
    public static (string Version, DateTime Created, int Crosshairs, int Macros)? Peek(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var info = JsonNode.Parse(Read(zip, InfoEntry) ?? "{}");
            var set = JsonNode.Parse(Read(zip, SettingsEntry) ?? "");
            if ((string)info?["app"] != "Pulse" || set == null) return null;
            return ((string)info["version"] ?? "?", DateTime.TryParse((string)info["created"], out var d) ? d : DateTime.MinValue,
                (set["Crosshair"]?["Saved"] as JsonArray)?.Count ?? 0, (set["MacroList"] as JsonArray)?.Count ?? 0);
        }
        catch { return null; }
    }

    static string Read(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        if (e == null) return null;
        using var r = new StreamReader(e.Open());
        return r.ReadToEnd();
    }

    /// <summary>Restores a backup over this PC's settings (this PC's own history is kept). Returns null or a problem.</summary>
    public static string Import(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var info = JsonNode.Parse(Read(zip, InfoEntry) ?? "{}");
            var text = Read(zip, SettingsEntry);
            if ((string)info?["app"] != "Pulse" || text == null) return "That file isn't a Pulse settings backup.";
            // Pictures and sounds were saved under the old PC's data folder: point them at this one.
            var oldDir = (string)info["dataDir"];
            if (!string.IsNullOrEmpty(oldDir)) text = text.Replace(JsonEncodedText.Encode(oldDir).ToString(), JsonEncodedText.Encode(TweakEngine.DataDir).ToString());
            var incoming = JsonNode.Parse(text)!.AsObject();

            foreach (var e in zip.Entries)
            {
                var parts = e.FullName.Split('/');
                if (parts.Length != 2 || !Folders.Contains(parts[0]) || string.IsNullOrEmpty(parts[1]) || parts[1] != Path.GetFileName(parts[1])) continue;
                var dir = Path.Combine(TweakEngine.DataDir, parts[0]);
                Directory.CreateDirectory(dir);
                e.ExtractToFile(Path.Combine(dir, parts[1]), true);
            }

            var all = JsonSerializer.SerializeToNode(TweakEngine.State)!.AsObject();
            foreach (var k in Portable) if (incoming[k] is JsonNode n) all[k] = n.DeepClone();
            // This PC keeps its own clips folder.
            if (all["Clips"] is JsonObject clips && TweakEngine.State.Clips != null) clips["Folder"] = TweakEngine.State.Clips.Folder;
            var state = all.Deserialize<AppState>();
            if (state == null) return "The backup couldn't be read.";
            TweakEngine.ReplaceState(state);
            TweakEngine.AddLog("app", "Settings restored from a backup");
            return null;
        }
        catch (Exception ex) { return "The backup couldn't be restored: " + ex.Message; }
    }
}
