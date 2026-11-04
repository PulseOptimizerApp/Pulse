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
    /// <summary>NVIDIA row → chosen value on the Roblox page ("" = don't change).</summary>
    public Dictionary<string, string> NvidiaChoices { get; set; } = new();
    /// <summary>Advanced NVIDIA settings by hex ID → value, like NVIDIA Profile Inspector.</summary>
    public Dictionary<string, string> NvidiaCustom { get; set; } = new();
    /// <summary>An NVIDIA Profile Inspector file (.nip) the user imported for Roblox, if any.</summary>
    public NvImport NvidiaImport { get; set; }
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

    /// <summary>The player's Roblox folder (settings XML, logs). Always under the player's own AppData, not Pulse's.</summary>
    static string RobloxDir => Path.Combine(RobloxUser.LocalAppData, "Roblox");

    /// <summary>Launchers that replace Roblox's own (they keep their own copy of Roblox in LocalAppData\Name\Versions).</summary>
    internal static readonly string[] Strappers = { "Bloxstrap", "Fishstrap", "Voidstrap", "Froststrap", "Lunastrap", "Plexity" };

    /// <summary>
    /// Every folder that can hold Roblox version folders, Roblox's own first. Roblox installs per user into
    /// AppData\Local\Roblox, but an install started from an administrator prompt goes to Program Files (x86), and a
    /// third-party launcher keeps its own copy. The Play button's registered command and Roblox's uninstall entry say
    /// exactly where the player is, so those are checked too.
    /// </summary>
    internal static List<string> VersionRoots()
    {
        var roots = new List<string>
        {
            Path.Combine(RobloxDir, "Versions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox", "Versions"),
        };
        var (hive, prefix) = RobloxUser.UserRoot;
        foreach (var where in new[] { (hive, prefix), (RegistryHive.LocalMachine, "") })
        {
            if (Reg.Get(where.Item1, where.Item2 + @"Software\Microsoft\Windows\CurrentVersion\Uninstall\roblox-player", "InstallLocation") is string loc
                && loc.Length > 0)
                roots.Add(Path.GetDirectoryName(loc.TrimEnd('\\', '/')) ?? loc);
        }
        foreach (var cmd in HandlerCommands().Append(Settings.OriginalHandlers.GetValueOrDefault("roblox-player")))
            if (ExeOf(cmd) is string exe && exe.EndsWith("RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase))
                roots.Add(Path.GetDirectoryName(Path.GetDirectoryName(exe)) ?? "");
        foreach (var name in Strappers) roots.Add(Path.Combine(RobloxUser.LocalAppData, name, "Versions"));
        return roots.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Every installed Roblox player version folder: Roblox's own install first, newest first within each place.</summary>
    public static List<string> PlayerVersions()
    {
        var found = new List<string>();
        foreach (var root in VersionRoots())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                found.AddRange(Directory.GetDirectories(root)
                    .Where(d => File.Exists(Path.Combine(d, "RobloxPlayerBeta.exe")))
                    .OrderByDescending(d => File.GetLastWriteTime(Path.Combine(d, "RobloxPlayerBeta.exe"))));
            }
            catch { /* a folder Pulse can't read: try the next place */ }
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool Installed => PlayerVersions().Count > 0;

    /// <summary>The Microsoft Store version of Roblox is installed (its files are locked by Windows).</summary>
    public static bool StoreVersion()
    {
        var (hive, prefix) = RobloxUser.UserRoot;
        return Reg.SubKeys(hive, prefix + @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages")
            .Any(k => k.StartsWith("ROBLOXCORPORATION.ROBLOX", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The third-party launcher (Bloxstrap and the like) that Roblox's Play button opens right now, if any.</summary>
    public static string ForeignLauncher()
    {
        foreach (var cmd in HandlerCommands())
        {
            if (cmd.Contains("--roblox", StringComparison.Ordinal)) continue;
            var hit = Strappers.FirstOrDefault(n => cmd.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>Why Pulse can't find Roblox, in words the player can act on.</summary>
    public static string WhyNotFound()
    {
        if (StoreVersion())
            return "You have the Microsoft Store version of Roblox. Windows locks its files, so Pulse can't apply settings to it. Install Roblox from roblox.com (it can sit next to the Store version), press Play once, then turn this on.";
        if (RobloxUser.IsDifferentAccount)
            return "Pulse couldn't find Roblox for the Windows account that's logged in. Pulse is running as a different administrator account, so it looked in your account's folders, but Roblox isn't there. Open roblox.com on this account, press Play once so Roblox finishes installing, then try again.";
        return "Pulse couldn't find RobloxPlayerBeta.exe in any Roblox folder for this Windows account. Open roblox.com, press Play once so Roblox finishes installing, then try again.";
    }

    /// <summary>The Play button's current commands: the player's own registration, then the machine-wide one.</summary>
    static IEnumerable<string> HandlerCommands()
    {
        var (hive, prefix) = RobloxUser.UserRoot;
        foreach (var p in Protocols)
        {
            if (Reg.Get(hive, prefix + CommandKey(p), "") is string mine && mine.Length > 0) yield return mine;
            if (Reg.Get(RegistryHive.LocalMachine, CommandKey(p), "") is string machine && machine.Length > 0) yield return machine;
        }
    }

    /// <summary>The program a registered command runs ("C:\...\x.exe" %1 or C:\...\x.exe %1).</summary>
    internal static string ExeOf(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        cmd = cmd.Trim();
        var m = Regex.Match(cmd, "^\"([^\"]+)\"");
        if (m.Success) return m.Groups[1].Value;
        var i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? cmd[..(i + 4)] : null;
    }

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

    // ───────── Settings the Play button reads ─────────

    /// <summary>
    /// A copy of the Roblox settings in the player's own AppData. The Play button runs Pulse as the player, without
    /// admin rights; when the Pulse window runs under a different administrator account its state file isn't the
    /// player's, so the Play button reads this copy instead.
    /// </summary>
    static string LaunchFile => Path.Combine(RobloxUser.LocalAppData, "PulseOptimizer", "roblox-launch.json");

    /// <summary>Saves Pulse's state and the copy the Play button reads. Use this instead of TweakEngine.Save for Roblox changes.</summary>
    public static void Save()
    {
        TweakEngine.Save();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LaunchFile)!);
            File.WriteAllText(LaunchFile, JsonSerializer.Serialize(Settings));
        }
        catch { }
    }

    static RobloxSettings LaunchSettings()
    {
        try
        {
            if (File.Exists(LaunchFile) && JsonSerializer.Deserialize<RobloxSettings>(File.ReadAllText(LaunchFile)) is RobloxSettings s) return s;
        }
        catch { }
        return Settings;
    }

    // ───────── Launch through Pulse ─────────

    static readonly string[] Protocols = { "roblox-player", "roblox" };
    static string CommandKey(string protocol) => $@"Software\Classes\{protocol}\shell\open\command";
    static string PulseCommand => $"\"{Environment.ProcessPath}\" --roblox \"%1\"";

    /// <summary>The player's own registry key (HKCU for them, even when Pulse runs as another administrator).</summary>
    static string UserKey(string protocol) => RobloxUser.UserRoot.Prefix + CommandKey(protocol);

    public static bool HandlerIsPulse() =>
        Reg.Get(RobloxUser.UserRoot.Hive, UserKey("roblox-player"), "") is string cmd && cmd.Contains("--roblox", StringComparison.Ordinal)
        && cmd.Contains(Path.GetFileName(Environment.ProcessPath ?? "PulseOptimizer.exe"), StringComparison.OrdinalIgnoreCase);

    /// <summary>Makes Roblox's Play button go through Pulse (remembering Roblox's own command to put back later).</summary>
    public static void EnableLaunch()
    {
        RegisterKeys(Settings);
        Settings.LaunchThroughPulse = true;
        TweakEngine.AddLog("roblox", "Roblox now launches through Pulse");
    }

    static void RegisterKeys(RobloxSettings settings)
    {
        var (hive, prefix) = RobloxUser.UserRoot;
        foreach (var p in Protocols)
        {
            // What the Play button opens now: the player's own registration, else the machine-wide one.
            var current = Reg.Get(hive, UserKey(p), "") as string;
            if (string.IsNullOrEmpty(current)) current = Reg.Get(RegistryHive.LocalMachine, CommandKey(p), "") as string;
            if (!string.IsNullOrEmpty(current) && !current.Contains("--roblox", StringComparison.Ordinal))
                settings.OriginalHandlers[p] = current;
            var cls = $@"{prefix}Software\Classes\{p}";
            if (Reg.Get(hive, cls, "URL Protocol") == null)
            {
                Reg.Set(hive, cls, "", "URL: Roblox Protocol", RegistryValueKind.String);
                Reg.Set(hive, cls, "URL Protocol", "", RegistryValueKind.String);
            }
            Reg.Set(hive, UserKey(p), "", PulseCommand, RegistryValueKind.String);
        }
    }

    /// <summary>Gives the Play button back to Roblox (or to the launcher that had it before).</summary>
    public static void DisableLaunch()
    {
        var newest = PlayerVersions().FirstOrDefault();
        var hive = RobloxUser.UserRoot.Hive;
        foreach (var p in Protocols)
        {
            var original = Settings.OriginalHandlers.GetValueOrDefault(p);
            if (string.IsNullOrEmpty(original) || !OriginalExeExists(original))
                original = newest != null ? $"\"{Path.Combine(newest, "RobloxPlayerBeta.exe")}\" %1" : null;
            if (original != null) Reg.Set(hive, UserKey(p), "", original, RegistryValueKind.String);
        }
        Settings.LaunchThroughPulse = false;
        TweakEngine.AddLog("roblox", "Roblox launches on its own again");
    }

    static bool OriginalExeExists(string cmd) => ExeOf(cmd) is string exe && File.Exists(exe);

    /// <summary>
    /// Runs when Roblox's Play button is clicked with Launch-through-Pulse on. No window: write the flags into every
    /// version folder, start Roblox exactly as Roblox would, and if Roblox updates itself (new version folder),
    /// write the flags there too before the new client reads them. Then take the Play button back if the update reset it.
    /// If Pulse can't find a Roblox to start, it hands the click to whatever had the Play button before, so pressing
    /// Play never silently does nothing.
    /// </summary>
    public static void HandleLaunch(string uri)
    {
        // This runs as a separate, short-lived process while Pulse itself may be open, so it never saves Pulse's
        // state file (that would overwrite changes made in the open window). It only reads the saved choices.
        var settings = LaunchSettings();
        var options = CreateOptions();
        foreach (var o in options) o.Value = settings.Choices.GetValueOrDefault(o.Key, "");
        var flags = BuildFlags(options, settings.CustomFlags);
        var ordered = PlayerVersions();
        var known = new HashSet<string>(ordered, StringComparer.OrdinalIgnoreCase);
        foreach (var v in ordered) { try { WriteFlags(v, flags); } catch { } }
        try { WriteFpsCap(options.First(o => o.Key == "fps").Value); } catch { }

        var exe = ordered.Select(v => Path.Combine(v, "RobloxPlayerBeta.exe")).FirstOrDefault();
        if (exe == null)
        {
            // Nothing Pulse can start: run the original command (Roblox's, Bloxstrap's...) so Play still works.
            var original = settings.OriginalHandlers.GetValueOrDefault("roblox-player");
            if (ExeOf(original) is string origExe && File.Exists(origExe) && !origExe.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            {
                var cmd = original.Trim();
                var args = cmd[(cmd.StartsWith('"') ? origExe.Length + 2 : origExe.Length)..].Replace("%1", uri).Trim();
                Process.Start(new ProcessStartInfo(origExe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(origExe) });
            }
            return;
        }

        Process.Start(new ProcessStartInfo(exe, $"\"{uri}\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });

        // Watch for a Roblox update for up to two minutes.
        var roots = VersionRoots().Where(Directory.Exists).ToList();
        var until = DateTime.Now.AddMinutes(2);
        while (DateTime.Now < until && roots.Count > 0)
        {
            foreach (var root in roots)
            {
                try
                {
                    foreach (var d in Directory.GetDirectories(root).Where(d => !known.Contains(d)))
                        if (File.Exists(Path.Combine(d, "RobloxPlayerBeta.exe")))
                        {
                            try { WriteFlags(d, flags); known.Add(d); } catch { }
                        }
                }
                catch { }
            }
            Thread.Sleep(400);
        }
        try { if (settings.LaunchThroughPulse && !HandlerIsPulse()) RegisterKeys(settings); } catch { }
    }

    /// <summary>On Pulse start: if Roblox updated and took the Play button back, quietly take it again and re-apply.</summary>
    public static void Maintain()
    {
        if (!Settings.LaunchThroughPulse) return;
        try { if (!HandlerIsPulse()) EnableLaunch(); ApplyAll(); Save(); } catch { }
    }
}
