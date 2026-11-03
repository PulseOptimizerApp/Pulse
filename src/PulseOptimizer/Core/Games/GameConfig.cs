using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Pulse.Core.Games;

public enum ConfigFormat { ValveKv, Ini, RobloxXml }

/// <summary>One performance setting Pulse may change in a game's own settings file.</summary>
public record ConfigEdit(string Key, string Value, string Label, Func<string, string> Describe = null)
{
    public string Show(string v) => Describe?.Invoke(v) ?? v;
}

/// <summary>A planned change, shown to the user before anything is written.</summary>
public record ConfigChange(string Label, string From, string To, string Key, string NewValue);

/// <summary>
/// Writes the pro video/performance settings straight into a game's own settings file, the same file the game's
/// menu writes. Rules: the game must be closed; only keys on the list below are touched, and only if they already
/// exist in the file (nothing is added or guessed); never sensitivity, keybinds, crosshair, audio or FOV;
/// the original file is backed up first and Undo puts it back exactly.
/// </summary>
public class GameConfigProfile
{
    public string GameId { get; init; }
    public ConfigFormat Format { get; init; }
    public Func<string> Locate { get; init; }
    public List<ConfigEdit> Edits { get; init; } = new();
    public string Note { get; init; } = "";

    // ───────── Reading and planning ─────────

    public string FilePath
    {
        get { try { return Locate?.Invoke(); } catch { return null; } }
    }

    /// <summary>What would change. Settings already at the pro value, or missing from the file, are left out.</summary>
    public List<ConfigChange> Plan(string text)
    {
        var list = new List<ConfigChange>();
        foreach (var e in Edits)
        {
            var current = Read(text, e.Key);
            if (current == null) continue;
            if (string.Equals(current.Trim(), e.Value, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new ConfigChange(e.Label, e.Show(current.Trim()), e.Show(e.Value), e.Key, e.Value));
        }
        return list;
    }

    public string Apply(string text, IEnumerable<ConfigChange> changes)
    {
        foreach (var c in changes) text = Write(text, c.Key, c.NewValue);
        return text;
    }

    string Read(string text, string key)
    {
        var m = Pattern(key).Match(text);
        return m.Success ? m.Groups["v"].Value : null;
    }

    string Write(string text, string key, string value) =>
        Pattern(key).Replace(text, m => m.Value[..(m.Groups["v"].Index - m.Index)] + value + m.Value[(m.Groups["v"].Index - m.Index + m.Groups["v"].Length)..], 1);

    Regex Pattern(string key) => Format switch
    {
        // "setting.csm_enabled"		"0"
        ConfigFormat.ValveKv => new Regex("\"" + Regex.Escape(key) + "\"[ \\t]+\"(?<v>[^\"\\r\\n]*)\"", RegexOptions.IgnoreCase),
        // sg.ShadowQuality=2   (any section; Unreal Engine keys are unique in GameUserSettings.ini)
        ConfigFormat.Ini => new Regex("^[ \\t]*" + Regex.Escape(key) + "[ \\t]*=[ \\t]*(?<v>[^\\r\\n]*?)[ \\t]*(?=\\r?$)", RegexOptions.Multiline | RegexOptions.IgnoreCase),
        // <int name="FramerateCap">60</int>
        _ => new Regex("<(?:int|bool|token|float|string) name=\"" + Regex.Escape(key) + "\">(?<v>[^<]*)</", RegexOptions.IgnoreCase),
    };
}

public static class GameConfigs
{
    /// <summary>Words that must never appear in a key Pulse changes: those settings are personal.</summary>
    public static readonly string[] Forbidden = { "sens", "bind", "key", "crosshair", "fov", "fieldofview", "mastervolume", "musicvolume", "voice", "sound", "audio", "mouse", "invert", "aim", "zoom", "reticle" };

    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static string Newest(IEnumerable<string> paths) =>
        paths.Where(File.Exists).OrderByDescending(File.GetLastWriteTime).FirstOrDefault();

    static string Ue(string v) => v switch { "0" => "Low", "1" => "Medium", "2" => "High", "3" => "Epic", "4" => "Cinematic", _ => v };
    static string OnOff(string v) => v is "1" or "True" or "true" ? "On" : v is "0" or "False" or "false" ? "Off" : v;

    public static readonly List<GameConfigProfile> All = new()
    {
        // Counter-Strike 2: Steam\userdata\<account>\730\local\cfg\cs2_video.txt (Valve key-value format).
        new GameConfigProfile
        {
            GameId = "cs2", Format = ConfigFormat.ValveKv,
            Locate = () =>
            {
                if (Reg.Get(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath") is not string steam) return null;
                var userdata = Path.Combine(steam.Replace('/', '\\'), "userdata");
                if (!Directory.Exists(userdata)) return null;
                return Newest(Directory.GetDirectories(userdata).Select(d => Path.Combine(d, "730", "local", "cfg", "cs2_video.txt")));
            },
            Edits = new()
            {
                new("setting.fullscreen", "1", "Display mode", v => v == "1" ? "Fullscreen" : "Windowed/borderless"),
                new("setting.mat_vsync", "0", "Wait for vertical sync", OnOff),
                new("setting.shaderquality", "0", "Shader detail", v => v == "0" ? "Low" : "High"),
                new("setting.videocfg_shadow_quality", "0", "Global shadow quality", v => v switch { "0" => "Low", "1" => "Medium", "2" => "High", "3" => "Very high", _ => v }),
                new("setting.videocfg_particle_detail", "0", "Particle detail", v => v switch { "0" => "Low", "1" => "Medium", "2" => "High", "3" => "Very high", _ => v }),
                new("setting.videocfg_ao_detail", "0", "Ambient occlusion", v => v == "0" ? "Disabled" : "On"),
                new("setting.videocfg_fsr_detail", "0", "FidelityFX Super Resolution", v => v == "0" ? "Disabled" : "On"),
            },
            Note = "Boost Player Contrast, anti-aliasing and NVIDIA Reflex stay as manual steps: set them in Settings → Video.",
        },

        // Apex Legends: Saved Games\Respawn\Apex\local\videoconfig.txt (same key-value format).
        new GameConfigProfile
        {
            GameId = "apex", Format = ConfigFormat.ValveKv,
            Locate = () => Newest(new[] { Path.Combine(Home, "Saved Games", "Respawn", "Apex", "local", "videoconfig.txt") }),
            Edits = new()
            {
                new("setting.mat_vsync_mode", "0", "V-Sync", v => v == "0" ? "Disabled" : "On"),
                new("setting.dvs_enable", "0", "Adaptive resolution", OnOff),
                new("setting.ssao_enabled", "0", "Ambient occlusion", OnOff),
                new("setting.volumetric_lighting", "0", "Volumetric lighting", OnOff),
                new("setting.mat_depthfeather_enable", "0", "Depth of field when aiming", OnOff),
                new("setting.cl_ragdoll_maxcount", "0", "Ragdolls", v => v == "0" ? "Off" : v),
                new("setting.mat_antialias_mode", "0", "Anti-aliasing", v => v == "0" ? "None" : "TSAA"),
            },
            Note = "Shadows, texture budget and NVIDIA Reflex are easiest to set in Settings → Video.",
        },

        // Fortnite: LocalAppData\FortniteGame\Saved\Config\WindowsClient\GameUserSettings.ini (Unreal Engine).
        new GameConfigProfile
        {
            GameId = "fortnite", Format = ConfigFormat.Ini,
            Locate = () => Newest(new[] { Path.Combine(Local, "FortniteGame", "Saved", "Config", "WindowsClient", "GameUserSettings.ini") }),
            Edits = new()
            {
                new("PreferredRHI", "dx11", "Rendering mode (graphics API)", v => v.Equals("dx11", StringComparison.OrdinalIgnoreCase) ? "DirectX 11 / Performance" : v),
                new("PreferredFeatureLevel", "es31", "Rendering mode", v => v.Equals("es31", StringComparison.OrdinalIgnoreCase) ? "Performance" : "DirectX 11/12"),
                new("bUseVSync", "False", "V-Sync", OnOff),
                new("sg.ResolutionQuality", "100", "3D resolution", v => v + "%"),
                new("sg.ViewDistanceQuality", "0", "View distance", v => v == "0" ? "Near" : Ue(v)),
                new("sg.TextureQuality", "0", "Textures", Ue),
            },
            Note = "Restart Fortnite after this: Performance mode only takes effect on launch.",
        },

        // Marvel Rivals: LocalAppData\Marvel\Saved\Config\Windows\GameUserSettings.ini (Unreal Engine).
        new GameConfigProfile
        {
            GameId = "rivals", Format = ConfigFormat.Ini,
            Locate = () => Newest(new[] { Path.Combine(Local, "Marvel", "Saved", "Config", "Windows", "GameUserSettings.ini") }),
            Edits = new()
            {
                new("bUseVSync", "False", "V-Sync", OnOff),
                new("sg.ReflectionQuality", "0", "Reflections", Ue),
                new("sg.PostProcessQuality", "0", "Post-processing", Ue),
                new("sg.TextureQuality", "0", "Textures", Ue),
                new("sg.EffectsQuality", "0", "Effects", Ue),
                new("sg.FoliageQuality", "0", "Foliage", Ue),
                new("sg.ShadowQuality", "1", "Shadows", Ue),
            },
            Note = "Global illumination, upscaling and Low Latency Mode stay manual: set them in Settings → Display and Graphics.",
        },

    };

    public static GameConfigProfile For(string gameId) => All.FirstOrDefault(p => p.GameId == gameId);

    static string BackupDir(string gameId) => Path.Combine(TweakEngine.DataDir, "game-settings", gameId);

    /// <summary>Applies the planned changes after backing the file up. Throws if the file changed since it was read.</summary>
    public static void Apply(GameConfigProfile p, string path, string original, List<ConfigChange> changes)
    {
        if (File.ReadAllText(path) != original) throw new IOException("The settings file changed while you were reading. Open the page again and retry.");
        var dir = BackupDir(p.GameId);
        Directory.CreateDirectory(dir);
        var backup = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(path)}");
        File.Copy(path, backup, overwrite: true);
        var readOnly = File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);
        if (readOnly) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        File.WriteAllText(path, p.Apply(original, changes));
        if (readOnly) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        TweakEngine.State.GameConfigBackups[p.GameId] = new GameConfigBackup { File = path, Backup = backup, Date = DateTime.Now, Count = changes.Count };
        TweakEngine.AddLog("games", $"Applied {changes.Count} pro settings to {GameCatalog.All.FirstOrDefault(g => g.Id == p.GameId)?.Name ?? p.GameId}");
    }

    public static bool CanUndo(string gameId) =>
        TweakEngine.State.GameConfigBackups.TryGetValue(gameId, out var b) && File.Exists(b.Backup);

    /// <summary>Puts the game's settings file back exactly as it was before Pulse changed it.</summary>
    public static void Undo(string gameId)
    {
        if (!TweakEngine.State.GameConfigBackups.TryGetValue(gameId, out var b) || !File.Exists(b.Backup))
            throw new FileNotFoundException("Pulse's backup of this game's settings is missing.");
        var readOnly = File.Exists(b.File) && File.GetAttributes(b.File).HasFlag(FileAttributes.ReadOnly);
        if (readOnly) File.SetAttributes(b.File, File.GetAttributes(b.File) & ~FileAttributes.ReadOnly);
        File.Copy(b.Backup, b.File, overwrite: true);
        TweakEngine.State.GameConfigBackups.Remove(gameId);
        TweakEngine.AddLog("undo", $"Restored {GameCatalog.All.FirstOrDefault(g => g.Id == gameId)?.Name ?? gameId}'s original settings");
    }
}

public class GameConfigBackup
{
    public string File { get; set; }
    public string Backup { get; set; }
    public DateTime Date { get; set; }
    public int Count { get; set; }
}
