using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Pulse.Ui;

namespace Pulse.Core.Roblox;

/// <summary>Everything the user picked on the Roblox page. Saved in Pulse's state file.</summary>
public class RobloxSettings
{
    public bool LaunchThroughPulse { get; set; }
    public string Preset { get; set; } = "balanced";
    /// <summary>Option key → chosen value ("" = leave it to Roblox).</summary>
    public Dictionary<string, string> Choices { get; set; } = new();
    /// <summary>Extra allowlisted flags typed into the advanced editor.</summary>
    public Dictionary<string, string> CustomFlags { get; set; } = new();
    /// <summary>The roblox / roblox-player launch commands from before Pulse took them over.</summary>
    public Dictionary<string, string> OriginalHandlers { get; set; } = new();
    public DateTime? LastLaunch { get; set; }
    /// <summary>NVIDIA driver settings Pulse set for Roblox, with what was there before (for Undo).</summary>
    public List<NvBackup> NvidiaBackups { get; set; } = new();
}

/// <summary>One setting on the Roblox page, shown as a row of choices.</summary>
public class RobloxOption : Observable
{
    public string Key { get; init; }
    public string Title { get; init; }
    public string Detail { get; init; }
    public List<RobloxChoice> Choices { get; init; } = new();
    /// <summary>Fast Flags this option writes, per choice value. A null value removes the flag (Roblox decides).</summary>
    public Func<string, Dictionary<string, string>> Flags { get; init; } = _ => new();

    string _value = "";
    public string Value
    {
        get => _value;
        set { if (Set(ref _value, value ?? "")) foreach (var c in Choices) c.Raise(nameof(RobloxChoice.IsSelected)); }
    }
}

public class RobloxChoice : Observable
{
    public RobloxOption Owner { get; init; }
    public string Label { get; init; }
    public string Value { get; init; }
    public bool IsSelected
    {
        get => Owner.Value == Value;
        set { if (value) Owner.Value = Value; }
    }
}

/// <summary>
/// Pulse's Roblox launcher. Since September 2025 Roblox only honors Fast Flags on its official allowlist, read from
/// ClientSettings\ClientAppSettings.json in the Roblox version folder. Pulse writes only those flags into that file,
/// the same mechanism Roblox documents; it never touches the running game. With "Launch through Pulse" on, Pulse
/// becomes the handler for Roblox's Play button so the flags are written before every launch, including right after
/// a Roblox update (which creates a fresh version folder without them).
/// </summary>
public static class RobloxBoost
{
    /// <summary>Roblox's Fast Flag allowlist (DevForum announcement, 29 Sep 2025). Anything else is ignored by Roblox.</summary>
    public static readonly HashSet<string> Allowlist = new(StringComparer.Ordinal)
    {
        "DFIntCSGLevelOfDetailSwitchingDistance", "DFIntCSGLevelOfDetailSwitchingDistanceL12",
        "DFIntCSGLevelOfDetailSwitchingDistanceL23", "DFIntCSGLevelOfDetailSwitchingDistanceL34",
        "FFlagHandleAltEnterFullscreenManually", "DFFlagTextureQualityOverrideEnabled", "DFIntTextureQualityOverride",
        "FIntDebugForceMSAASamples", "DFFlagDisableDPIScale", "FFlagDebugGraphicsPreferD3D11", "FFlagDebugSkyGray",
        "DFFlagDebugPauseVoxelizer", "DFIntDebugFRMQualityLevelOverride", "FIntFRMMaxGrassDistance", "FIntFRMMinGrassDistance",
        "FFlagDebugGraphicsPreferVulkan", "FFlagDebugGraphicsPreferOpenGL", "FIntGrassMovementReducedMotionFactor",
    };

    static Dictionary<string, string> F(params (string k, string v)[] kv) => kv.ToDictionary(x => x.k, x => x.v);
    static RobloxOption Opt(string key, string title, string detail, (string label, string value)[] choices, Func<string, Dictionary<string, string>> flags)
    {
        var o = new RobloxOption { Key = key, Title = title, Detail = detail, Flags = flags };
        foreach (var (l, v) in choices) o.Choices.Add(new RobloxChoice { Owner = o, Label = l, Value = v });
        return o;
    }

    /// <summary>The options on the Roblox page. "" always means "leave it to Roblox".</summary>
    public static List<RobloxOption> CreateOptions() => new()
    {
        Opt("fps", "Frame rate cap", "Roblox's own Maximum Frame Rate (60 until you raise it). Uncapped sets it to 9999 and locks Roblox's settings file so Roblox can't put it back to 240, the same way Froststrap does it; it never touches the game itself. While uncapped, changes you make in Roblox's own settings menu (volume, sensitivity, graphics) won't save. Pick any other option to unlock it.",
            new[] { ("Roblox default", ""), ("60", "60"), ("120", "120"), ("144", "144"), ("240", "240"), ("Uncapped", UncappedValue) }, _ => new()),
        Opt("api", "Graphics API", "Which graphics system Roblox uses. DirectX 11 is the most stable on most PCs; Vulkan can give more FPS on some AMD and NVIDIA cards. Try both with the delay check.",
            new[] { ("Auto", ""), ("DirectX 11", "d3d11"), ("Vulkan", "vulkan"), ("OpenGL", "opengl") },
            v => v switch
            {
                "d3d11" => F(("FFlagDebugGraphicsPreferD3D11", "True")),
                "vulkan" => F(("FFlagDebugGraphicsPreferVulkan", "True")),
                "opengl" => F(("FFlagDebugGraphicsPreferOpenGL", "True")),
                _ => new(),
            }),
        Opt("quality", "Lock graphics quality", "Stops Roblox from changing the graphics level on its own mid-game. Lowest gives the most FPS in busy experiences.",
            new[] { ("Roblox decides", ""), ("Lowest", "1"), ("Low", "4"), ("Medium", "8"), ("High", "15") },
            v => v.Length == 0 ? new() : F(("DFIntDebugFRMQualityLevelOverride", v))),
        Opt("textures", "Texture quality", "Lower texture quality frees graphics memory. It helps most on cards with 4 GB or less; it doesn't raise FPS much otherwise.",
            new[] { ("Roblox decides", ""), ("Lowest", "0"), ("Low", "1"), ("Medium", "2"), ("High", "3") },
            v => v.Length == 0 ? new() : F(("DFFlagTextureQualityOverrideEnabled", "True"), ("DFIntTextureQualityOverride", v))),
        Opt("msaa", "Anti-aliasing (MSAA)", "Smooths jagged edges at an FPS cost. Off gives the most FPS.",
            new[] { ("Roblox decides", ""), ("Off", "1"), ("2x", "2"), ("4x", "4") },
            v => v.Length == 0 ? new() : F(("FIntDebugForceMSAASamples", v))),
        Opt("grass", "Grass", "Removing grass gives a few frames in outdoor maps and makes players lying in grass easier to see.",
            new[] { ("Normal", ""), ("Off", "off") },
            v => v == "off" ? F(("FIntFRMMinGrassDistance", "0"), ("FIntFRMMaxGrassDistance", "0")) : new()),
        Opt("voxel", "Pause lighting updates", "Stops Roblox recalculating its lighting grid. Saves CPU time in big maps; lighting can look slightly off when things move or break.",
            new[] { ("Off", ""), ("On", "on") },
            v => v == "on" ? F(("DFFlagDebugPauseVoxelizer", "True")) : new()),
        Opt("sky", "Plain gray sky", "Replaces the skybox with flat gray. A tiny FPS gain; mostly for very weak PCs.",
            new[] { ("Off", ""), ("On", "on") },
            v => v == "on" ? F(("FFlagDebugSkyGray", "True")) : new()),
        Opt("fullscreen", "Alt+Enter exclusive fullscreen", "Lets Alt+Enter switch Roblox to exclusive fullscreen (normal fullscreen is borderless). Can lower delay a little; some PCs get odd resolutions, so it's off by default.",
            new[] { ("Off", ""), ("On", "on") },
            v => v == "on" ? F(("FFlagHandleAltEnterFullscreenManually", "False")) : new()),
        Opt("dpi", "Keep full resolution", "With Windows display scaling above 100%, Roblox renders at a lower resolution. On keeps it sharp but costs FPS.",
            new[] { ("Off", ""), ("On", "on") },
            v => v == "on" ? F(("DFFlagDisableDPIScale", "True")) : new()),
    };

    public static readonly Dictionary<string, Dictionary<string, string>> Presets = new()
    {
        ["maxfps"] = new() { ["fps"] = UncappedValue, ["api"] = "d3d11", ["quality"] = "1", ["textures"] = "1", ["msaa"] = "1", ["grass"] = "off", ["voxel"] = "on", ["sky"] = "", ["fullscreen"] = "", ["dpi"] = "" },
        ["balanced"] = new() { ["fps"] = "144", ["api"] = "d3d11", ["quality"] = "", ["textures"] = "", ["msaa"] = "1", ["grass"] = "off", ["voxel"] = "", ["sky"] = "", ["fullscreen"] = "", ["dpi"] = "" },
        ["default"] = new() { ["fps"] = "", ["api"] = "", ["quality"] = "", ["textures"] = "", ["msaa"] = "", ["grass"] = "", ["voxel"] = "", ["sky"] = "", ["fullscreen"] = "", ["dpi"] = "" },
    };

    public static RobloxSettings Settings => TweakEngine.State.Roblox ??= new RobloxSettings();

    // ───────── Flags ─────────

    /// <summary>The Fast Flags for these choices, plus allowlisted custom flags. Never contains a flag Roblox doesn't allow.</summary>
    public static Dictionary<string, string> BuildFlags(IEnumerable<RobloxOption> options, IReadOnlyDictionary<string, string> custom)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var o in options)
            foreach (var kv in o.Flags(o.Value)) flags[kv.Key] = kv.Value;
        foreach (var kv in custom ?? new Dictionary<string, string>())
            if (IsValidCustom(kv.Key, kv.Value, out _)) flags[kv.Key] = kv.Value.Trim();
        foreach (var k in flags.Keys.Where(k => !Allowlist.Contains(k)).ToList()) flags.Remove(k);
        return flags;
    }

    /// <summary>Checks a flag typed into the advanced editor.</summary>
    public static bool IsValidCustom(string name, string value, out string why)
    {
        name = name?.Trim() ?? ""; value = value?.Trim() ?? "";
        if (!Allowlist.Contains(name)) { why = "Roblox ignores this flag: it isn't on Roblox's allowlist."; return false; }
        bool isBool = name.StartsWith("FFlag") || name.StartsWith("DFFlag");
        if (isBool && value is not ("True" or "False")) { why = "This flag takes True or False."; return false; }
        if (!isBool && !Regex.IsMatch(value, @"^-?\d{1,9}$")) { why = "This flag takes a whole number."; return false; }
        why = "";
        return true;
    }

    // ───────── Roblox install ─────────

    static string RobloxDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox");

    /// <summary>Every installed Roblox player version folder, newest first.</summary>
    public static List<string> PlayerVersions()
    {
        var versions = Path.Combine(RobloxDir, "Versions");
        if (!Directory.Exists(versions)) return new();
        return Directory.GetDirectories(versions)
            .Where(d => File.Exists(Path.Combine(d, "RobloxPlayerBeta.exe")))
            .OrderByDescending(d => File.GetLastWriteTime(Path.Combine(d, "RobloxPlayerBeta.exe"))).ToList();
    }

    public static bool Installed => PlayerVersions().Count > 0;

    /// <summary>Writes the flags file into a version folder. An empty set removes Pulse's file so Roblox runs untouched.</summary>
    public static void WriteFlags(string versionDir, Dictionary<string, string> flags)
    {
        var dir = Path.Combine(versionDir, "ClientSettings");
        var file = Path.Combine(dir, "ClientAppSettings.json");
        if (flags.Count == 0)
        {
            if (File.Exists(file)) File.WriteAllText(file, "{}");
            return;
        }
        Directory.CreateDirectory(dir);
        File.WriteAllText(file, JsonSerializer.Serialize(flags, new JsonSerializerOptions { WriteIndented = true }));
    }

    public const string UncappedValue = "9999";

    static string SettingsXml =>
        Directory.Exists(RobloxDir) ? Directory.GetFiles(RobloxDir, "GlobalBasicSettings_*.xml").OrderByDescending(File.GetLastWriteTime).FirstOrDefault() : null;

    /// <summary>True while Pulse has Roblox's settings file locked for an uncapped frame rate.</summary>
    public static bool SettingsLocked => SettingsXml is string f && File.GetAttributes(f).HasFlag(FileAttributes.ReadOnly);

    /// <summary>
    /// Roblox's Maximum Frame Rate, in GlobalBasicSettings_*.xml. Uncapped (9999) also marks the file read-only, because
    /// Roblox otherwise resets anything above 240 when it saves. Any other choice unlocks the file again.
    /// "" leaves Roblox's value alone but still unlocks the file if Pulse had locked it.
    /// </summary>
    public static bool WriteFpsCap(string value)
    {
        var file = SettingsXml;
        if (file == null) return false;
        var attrs = File.GetAttributes(file);
        if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        if (string.IsNullOrEmpty(value)) return true;
        var xml = File.ReadAllText(file);
        var updated = SetFramerateCap(xml, value);
        if (updated != xml) File.WriteAllText(file, updated);
        if (value == UncappedValue) File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        return true;
    }

    /// <summary>Sets FramerateCap in Roblox's settings XML, adding it to the user settings if Roblox hasn't written it yet.</summary>
    internal static string SetFramerateCap(string xml, string value)
    {
        var rx = new Regex("(<int name=\"FramerateCap\">)(-?\\d+)(</int>)");
        if (rx.IsMatch(xml)) return rx.Replace(xml, m => m.Groups[1].Value + value + m.Groups[3].Value, 1);
        var props = new Regex("(<Item class=\"UserGameSettings\"[^>]*>\\s*<Properties>)");
        return props.IsMatch(xml) ? props.Replace(xml, m => m.Groups[1].Value + $"\n\t\t\t<int name=\"FramerateCap\">{value}</int>", 1) : xml;
    }

    /// <summary>Applies the saved choices to every installed version (call with Roblox closed for the FPS cap to stick).</summary>
    public static int ApplyAll()
    {
        var options = CreateOptions();
        foreach (var o in options) o.Value = Settings.Choices.GetValueOrDefault(o.Key, "");
        var flags = BuildFlags(options, Settings.CustomFlags);
        int n = 0;
        foreach (var v in PlayerVersions()) { try { WriteFlags(v, flags); n++; } catch { } }
        try { WriteFpsCap(options.First(o => o.Key == "fps").Value); } catch { }
        return n;
    }

    // ───────── Launch through Pulse ─────────

    static readonly string[] Protocols = { "roblox-player", "roblox" };
    static string CommandKey(string protocol) => $@"Software\Classes\{protocol}\shell\open\command";
    static string PulseCommand => $"\"{Environment.ProcessPath}\" --roblox \"%1\"";

    public static bool HandlerIsPulse() =>
        Reg.Get(RegistryHive.CurrentUser, CommandKey("roblox-player"), "") is string cmd && cmd.Contains("--roblox", StringComparison.Ordinal)
        && cmd.Contains(Path.GetFileName(Environment.ProcessPath ?? "PulseOptimizer.exe"), StringComparison.OrdinalIgnoreCase);

    /// <summary>Makes Roblox's Play button go through Pulse (remembering Roblox's own command to put back later).</summary>
    public static void EnableLaunch()
    {
        RegisterKeys();
        Settings.LaunchThroughPulse = true;
        TweakEngine.AddLog("roblox", "Roblox now launches through Pulse");
    }

    static void RegisterKeys()
    {
        foreach (var p in Protocols)
        {
            var current = Reg.Get(RegistryHive.CurrentUser, CommandKey(p), "") as string;
            if (!string.IsNullOrEmpty(current) && !current.Contains("--roblox", StringComparison.Ordinal))
                Settings.OriginalHandlers[p] = current;
            Reg.Set(RegistryHive.CurrentUser, CommandKey(p), "", PulseCommand, RegistryValueKind.String);
            if (Reg.Get(RegistryHive.CurrentUser, $@"Software\Classes\{p}", "URL Protocol") == null)
            {
                Reg.Set(RegistryHive.CurrentUser, $@"Software\Classes\{p}", "", "URL: Roblox Protocol", RegistryValueKind.String);
                Reg.Set(RegistryHive.CurrentUser, $@"Software\Classes\{p}", "URL Protocol", "", RegistryValueKind.String);
            }
        }
    }

    /// <summary>Gives the Play button back to Roblox.</summary>
    public static void DisableLaunch()
    {
        var newest = PlayerVersions().FirstOrDefault();
        foreach (var p in Protocols)
        {
            var original = Settings.OriginalHandlers.GetValueOrDefault(p);
            if (string.IsNullOrEmpty(original) || !OriginalExeExists(original))
                original = newest != null ? $"\"{Path.Combine(newest, "RobloxPlayerBeta.exe")}\" %1" : null;
            if (original != null) Reg.Set(RegistryHive.CurrentUser, CommandKey(p), "", original, RegistryValueKind.String);
        }
        Settings.LaunchThroughPulse = false;
        TweakEngine.AddLog("roblox", "Roblox launches on its own again");
    }

    static bool OriginalExeExists(string cmd)
    {
        var m = Regex.Match(cmd, "^\"([^\"]+)\"");
        return m.Success && File.Exists(m.Groups[1].Value);
    }

    /// <summary>
    /// Runs when Roblox's Play button is clicked with Launch-through-Pulse on. No window: write the flags into every
    /// version folder, start Roblox exactly as Roblox would, and if Roblox updates itself (new version folder),
    /// write the flags there too before the new client reads them. Then take the Play button back if the update reset it.
    /// </summary>
    public static void HandleLaunch(string uri)
    {
        // This runs as a separate, short-lived process while Pulse itself may be open, so it never saves Pulse's
        // state file (that would overwrite changes made in the open window). It only reads the saved choices.
        var options = CreateOptions();
        foreach (var o in options) o.Value = Settings.Choices.GetValueOrDefault(o.Key, "");
        var flags = BuildFlags(options, Settings.CustomFlags);
        var known = new HashSet<string>(PlayerVersions(), StringComparer.OrdinalIgnoreCase);
        foreach (var v in known) { try { WriteFlags(v, flags); } catch { } }
        try { WriteFpsCap(options.First(o => o.Key == "fps").Value); } catch { }

        var exe = known.Select(v => Path.Combine(v, "RobloxPlayerBeta.exe")).FirstOrDefault();
        var original = Settings.OriginalHandlers.GetValueOrDefault("roblox-player");
        if (exe == null && original != null && OriginalExeExists(original))
            exe = Regex.Match(original, "^\"([^\"]+)\"").Groups[1].Value;
        if (exe == null) return;

        Process.Start(new ProcessStartInfo(exe, $"\"{uri}\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });

        // Watch for a Roblox update for up to two minutes.
        var versions = Path.Combine(RobloxDir, "Versions");
        var until = DateTime.Now.AddMinutes(2);
        while (DateTime.Now < until && Directory.Exists(versions))
        {
            foreach (var d in Directory.GetDirectories(versions).Where(d => !known.Contains(d)))
            {
                if (File.Exists(Path.Combine(d, "RobloxPlayerBeta.exe")))
                {
                    try { WriteFlags(d, flags); known.Add(d); } catch { }
                }
            }
            Thread.Sleep(400);
        }
        try { if (Settings.LaunchThroughPulse && !HandlerIsPulse()) RegisterKeys(); } catch { }
    }

    /// <summary>On Pulse start: if Roblox updated and took the Play button back, quietly take it again and re-apply.</summary>
    public static void Maintain()
    {
        if (!Settings.LaunchThroughPulse) return;
        try { if (!HandlerIsPulse()) EnableLaunch(); ApplyAll(); TweakEngine.Save(); } catch { }
    }
}
