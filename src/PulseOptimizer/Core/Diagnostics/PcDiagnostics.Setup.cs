using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Pulse.Core;

/// <summary>
/// Setup-mistake checks (research doc, round 2). All read-only: Pulse points to the fix,
/// it never changes these settings itself, because several are stored in places Microsoft doesn't document.
/// </summary>
public static partial class PcDiagnostics
{
    static readonly Regex Discrete = new(@"GeForce|RTX|GTX|Quadro|Radeon\s+(RX|Pro)|Arc\s*\(TM\)\s*[AB]\d|Arc\s+[AB]\d", RegexOptions.IgnoreCase);
    static readonly Regex Integrated = new(@"UHD|Iris|HD Graphics|Radeon\s*\(TM\)\s*Graphics|Radeon Graphics|Vega \d+ Graphics|Arc\s*\(TM\)\s*Graphics", RegexOptions.IgnoreCase);

    internal static bool IsDiscrete(string gpu) => Discrete.IsMatch(gpu) && !Integrated.IsMatch(gpu);
    internal static bool IsIntegrated(string gpu) => Integrated.IsMatch(gpu) && !Discrete.IsMatch(gpu);

    // ───────── 1. Monitor plugged into the motherboard ─────────

    static IEnumerable<Finding> MonitorOnMotherboard()
    {
        // Laptops route the built-in screen through the integrated GPU by design, so this only applies to desktops.
        if (SystemInfo.HasBattery) yield break;
        var discrete = SystemInfo.Gpus.Select(g => g.Name).Where(IsDiscrete).ToList();
        if (discrete.Count == 0) yield break;

        var driving = new List<string>();
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0) driving.Add(dd.DeviceString);
        }
        if (driving.Count == 0) yield break;

        if (driving.All(d => !IsDiscrete(d)))
            yield return new Finding
            {
                Id = "monitor-port", Rank = -1, Severity = Severity.Problem,
                Title = "Your monitor is plugged into the motherboard, not the graphics card",
                Detail = $"Your screen is driven by {string.Join(", ", driving.Distinct())}, but this PC has {string.Join(", ", discrete)}.",
                Fix = "Move the monitor cable to a port on the graphics card (the lower, horizontal ports on the back of the PC, not the ones near the USB ports). Games can run several times faster.",
            };
        else
            yield return new Finding
            {
                Id = "monitor-port", Rank = -1, Severity = Severity.Ok, Title = "Monitor connection",
                Detail = $"Your screen is driven by {string.Join(", ", driving.Where(IsDiscrete).Distinct())}.",
            };
    }

    // ───────── 2. Single-channel memory ─────────

    static readonly Regex ChannelRx = new(@"CHANNEL\s*([A-H])\b|DIMM[_\s-]?([A-H])\d|^([A-H])\d|_([A-H])\d", RegexOptions.IgnoreCase);

    /// <summary>Memory channel letter from WMI's DeviceLocator/BankLabel (e.g. "DIMM_A1", "P0 CHANNEL A"), or null.</summary>
    internal static string ChannelOf(string deviceLocator, string bankLabel)
    {
        foreach (var text in new[] { deviceLocator ?? "", bankLabel ?? "" })
        {
            var m = ChannelRx.Match(text.Trim());
            if (m.Success) return m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value.ToUpperInvariant();
        }
        return null;
    }

    static IEnumerable<Finding> MemoryChannels()
    {
        var doc = Ps("ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_PhysicalMemory | Select-Object DeviceLocator,BankLabel,Capacity)");
        var mods = doc.RootElement.EnumerateArray().ToList();
        if (mods.Count == 0) yield break;
        bool laptop = SystemInfo.HasBattery;

        if (mods.Count == 1)
        {
            yield return new Finding
            {
                Id = "ram-channels", Rank = 0, Severity = laptop ? Severity.Check : Severity.Problem,
                Title = laptop ? "Memory may be running in single-channel mode" : "Memory is running in single-channel mode",
                Detail = $"Windows sees one memory module ({Format.Bytes(Long(mods[0], "Capacity"), 0)})." +
                         (laptop ? " Some laptops with memory soldered to the board still run dual-channel, so check your model's specs." : ""),
                Fix = "A second matching stick doubles memory bandwidth, which noticeably raises FPS in CPU-heavy games and anything using integrated graphics. Buy the same speed and size as the one you have.",
            };
            yield break;
        }

        var channels = mods.Select(m => ChannelOf(Str(m, "DeviceLocator"), Str(m, "BankLabel"))).ToList();

        if (channels.All(c => c != null) && channels.Distinct().Count() == 1)
            yield return new Finding
            {
                Id = "ram-channels", Rank = 0, Severity = Severity.Check,
                Title = "Your memory sticks may share one channel",
                Detail = $"All {mods.Count} modules report channel {channels[0]} ({string.Join(", ", mods.Select(m => Str(m, "DeviceLocator")))}).",
                Fix = "Most boards need two sticks in specific slots (often A2 and B2, the 2nd and 4th from the CPU) to run dual-channel. Check your motherboard manual's memory section.",
            };
        else
            yield return new Finding
            {
                Id = "ram-channels", Rank = 0, Severity = Severity.Ok, Title = "Memory channels",
                Detail = $"{mods.Count} modules installed" + (channels.All(c => c != null) ? $" across channels {string.Join(", ", channels.Distinct().OrderBy(c => c))}." : "."),
            };
    }

    // ───────── 3. Games on a hard drive ─────────

    record GameInstall(string Name, string Path);

    static IEnumerable<GameInstall> SteamGames()
    {
        if (Reg.Get(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath") is not string steam) yield break;
        var vdf = Path.Combine(steam.Replace('/', '\\'), "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        var libs = Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\"));
        foreach (var lib in libs)
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string text;
                try { text = File.ReadAllText(acf); } catch { continue; }
                var name = Regex.Match(text, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
                var dir = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"").Groups[1].Value;
                if (name.Length > 0 && dir.Length > 0 && !name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase) && !name.Contains("Redistributable"))
                    yield return new GameInstall(name, Path.Combine(apps, "common", dir));
            }
        }
    }

    static IEnumerable<GameInstall> EpicGames()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            GameInstall g = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                g = new GameInstall(Str(doc.RootElement, "DisplayName"), Str(doc.RootElement, "InstallLocation"));
            }
            catch { }
            if (g != null && g.Name.Length > 0 && g.Path.Length > 0) yield return g;
        }
    }

    /// <summary>Name and folder of every Steam and Epic game found.</summary>
    internal static List<(string Name, string Path)> LauncherGames()
    {
        try { return SteamGames().Concat(EpicGames()).Select(g => (g.Name, g.Path)).ToList(); }
        catch { return new(); }
    }

    /// <summary>Install folders of every Steam and Epic game found (used to recognize a running game).</summary>
    internal static List<string> GameInstallFolders()
    {
        try { return SteamGames().Concat(EpicGames()).Select(g => g.Path).Where(p => p.Length > 3).ToList(); }
        catch { return new List<string>(); }
    }

    static IEnumerable<Finding> GamesOnHardDrive()
    {
        var games = SteamGames().Concat(EpicGames()).Where(g => g.Path.Length > 1 && g.Path[1] == ':').ToList();
        if (games.Count == 0) yield break;

        var map = Ps(@"$m = @{}
Get-Partition | Where-Object DriveLetter | ForEach-Object {
  $n = $_.DiskNumber
  $d = Get-PhysicalDisk | Where-Object DeviceId -eq ""$n"" | Select-Object -First 1
  $m[[string]$_.DriveLetter] = [string]$d.MediaType
}
ConvertTo-Json -Compress -InputObject $m");
        var media = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (map.RootElement.ValueKind == JsonValueKind.Object)
            foreach (var p in map.RootElement.EnumerateObject()) media[p.Name] = p.Value.GetString() ?? "";

        var onHdd = games.Where(g => media.TryGetValue(g.Path[..1], out var t) && t.Equals("HDD", StringComparison.OrdinalIgnoreCase)).ToList();
        if (onHdd.Count > 0)
        {
            var names = string.Join(", ", onHdd.Select(g => g.Name).Distinct().Take(6)) + (onHdd.Count > 6 ? $" and {onHdd.Count - 6} more" : "");
            yield return new Finding
            {
                Id = "games-hdd", Rank = 1, Severity = Severity.Problem,
                Title = $"{onHdd.Count} game{(onHdd.Count == 1 ? " is" : "s are")} installed on a hard drive",
                Detail = $"{names}. Drive{(onHdd.Select(g => g.Path[..1]).Distinct().Count() == 1 ? "" : "s")} {string.Join(", ", onHdd.Select(g => g.Path[..2]).Distinct())}.",
                Fix = "Hard drives cause long loading screens and stutter when games stream in new areas. Move the games you play most to an SSD: Steam (game Properties → Installed Files → Move install folder) and Epic both can move installs without re-downloading.",
            };
        }
        else
            yield return new Finding
            {
                Id = "games-hdd", Rank = 1, Severity = Severity.Ok, Title = "Game storage",
                Detail = $"All {games.Count} Steam and Epic games found are on SSDs or drives of unknown type.",
            };
    }

    // ───────── 4. Wi-Fi vs Ethernet ─────────

    static IEnumerable<Finding> NetworkConnection()
    {
        var doc = Ps(@"$r = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric | Select-Object -First 1
$a = if ($r) { Get-NetAdapter -InterfaceIndex $r.ifIndex -ErrorAction SilentlyContinue } else { $null }
ConvertTo-Json -Compress -InputObject ([pscustomobject]@{ Name = [string]$a.Name; Desc = [string]$a.InterfaceDescription; Media = [string]$a.PhysicalMediaType; Speed = [string]$a.LinkSpeed })");
        var e = doc.RootElement;
        if (e.ValueKind != JsonValueKind.Object || Str(e, "Name").Length == 0)
        {
            yield return Unknown("network", "Network connection", "No active internet connection found.");
            yield break;
        }
        var desc = Str(e, "Desc");
        bool wifi = Str(e, "Media").Contains("802.11") || Regex.IsMatch(desc, "Wi-?Fi|Wireless|WLAN", RegexOptions.IgnoreCase);
        var speed = Str(e, "Speed");

        if (!wifi)
        {
            bool slow = Regex.IsMatch(speed, @"^\s*(10|100)\s*Mbps", RegexOptions.IgnoreCase);
            yield return new Finding
            {
                Id = "network", Rank = 3, Severity = slow ? Severity.Check : Severity.Ok,
                Title = slow ? "Your Ethernet link is running slowly" : "Wired connection",
                Detail = $"{desc} at {speed}.",
                Fix = slow ? "The cable or port is only negotiating 100 Mbps. Try a different cable (Cat5e or better) or router port; a damaged cable is the usual cause." : "",
            };
            yield break;
        }

        // netsh output is localized; only parse the numbers we need.
        var w = Shell.Cmd("netsh wlan show interfaces").Output;
        var channel = int.TryParse(Regex.Match(w, @"(?m)^\s*Channel\s*:\s*(\d+)").Groups[1].Value, out var ch) ? ch : 0;
        var signal = Regex.Match(w, @"(\d+)\s*%").Groups[1].Value;
        var bandText = Regex.Match(w, @"(?m)^\s*Band\s*:\s*(.+)$").Groups[1].Value.Trim();
        bool twoFour = bandText.StartsWith("2.4") || (bandText.Length == 0 && channel is > 0 and <= 14);
        var band = bandText.Length > 0 ? bandText : channel == 0 ? "unknown band" : channel <= 14 ? "2.4 GHz" : "5 GHz or higher";

        yield return new Finding
        {
            Id = "network", Rank = 3, Severity = Severity.Check,
            Title = twoFour ? "You're on 2.4 GHz Wi-Fi" : "You're on Wi-Fi",
            Detail = $"{desc}, {band}" + (signal.Length > 0 ? $", signal {signal}%." : "."),
            Fix = "Wi-Fi is the most common cause of ping spikes and lag. An Ethernet cable to the router is the single best fix." +
                  (twoFour ? " If you can't run a cable, connect to your router's 5 GHz (or 6 GHz) network instead: it's faster and less crowded." : ""),
        };
    }

    // ───────── 5. Laptop power and graphics ─────────

    static IEnumerable<Finding> LaptopPowerAndGpu()
    {
        if (!SystemInfo.HasBattery) yield break;
        if (!SystemInfo.IsPluggedIn)
            yield return new Finding
            {
                Id = "laptop-power", Rank = -1, Severity = Severity.Problem,
                Title = "Your laptop is running on battery",
                Detail = "On battery, laptops cut CPU and GPU power to save charge, often to a fraction of full speed.",
                Fix = "Plug in while gaming, and set the power mode to Best performance (Settings → System → Power & battery).",
                ActionKey = "settings:ms-settings:powersleep", ActionText = "Open power settings",
            };
        else
            yield return new Finding
            {
                Id = "laptop-power", Rank = -1, Severity = Severity.Ok, Title = "Laptop power",
                Detail = "Plugged in. For games, set the power mode to Best performance in Settings → System → Power & battery.",
                ActionKey = "settings:ms-settings:powersleep", ActionText = "Open power settings",
            };

        var names = SystemInfo.Gpus.Select(g => g.Name).ToList();
        var dGpu = names.FirstOrDefault(IsDiscrete);
        if (dGpu != null && names.Any(IsIntegrated))
            yield return new Finding
            {
                Id = "laptop-gpu", Rank = 0, Severity = Severity.Check,
                Title = "Make sure games use your graphics card",
                Detail = $"This laptop has two graphics chips: {string.Join(" and ", names.Distinct())}. Windows sometimes runs games on the weaker built-in one.",
                Fix = $"In Settings → System → Display → Graphics, set each game to \"High performance\" ({dGpu}). If your laptop's own app has a MUX or \"dGPU only\" mode, that's even faster.",
                ActionKey = "settings:ms-settings:display-advancedgraphics", ActionText = "Open graphics settings",
            };
    }

    // ───────── 6. Resizable BAR ─────────

    static readonly Regex RebarCapable = new(@"RTX\s*(30|40|50)\d{2}|Radeon\s+RX\s*(6|7|9)\d{3}|Arc\s*(\(TM\))?\s*[AB]\d{3}", RegexOptions.IgnoreCase);

    static IEnumerable<Finding> ResizableBar()
    {
        var gpu = SystemInfo.Gpus.Select(g => g.Name).FirstOrDefault(n => RebarCapable.IsMatch(n));
        if (gpu == null) yield break;
        bool nvidia = gpu.Contains("RTX", StringComparison.OrdinalIgnoreCase);
        bool intel = gpu.Contains("Arc", StringComparison.OrdinalIgnoreCase);
        // Windows doesn't expose Resizable BAR state through a documented user-mode API, so Pulse asks you to check.
        yield return new Finding
        {
            Id = "rebar", Rank = 6, Severity = Severity.Check,
            Title = "Check that Resizable BAR is on",
            Detail = $"Your {gpu} supports Resizable BAR (AMD calls it Smart Access Memory). NVIDIA reports gains from a few percent up to 12% in some games, and its drivers only use it in games where it helps.",
            Fix = (nvidia ? "To check: NVIDIA Control Panel → System Information → \"Resizable BAR: Yes\". "
                  : intel ? "Intel Arc cards rely on it heavily. "
                  : "To check: AMD Software → Performance → Tuning, look for Smart Access Memory. ") +
                  "If it's off, update your motherboard BIOS and turn on \"Above 4G Decoding\" and \"Re-Size BAR\" in the BIOS. Pulse never changes BIOS settings.",
            ActionKey = nvidia ? "url:https://www.nvidia.com/en-gb/geforce/news/geforce-rtx-30-series-resizable-bar-support" : "",
            ActionText = "NVIDIA's guide",
        };
    }

    // ───────── Windows settings that Pulse points to rather than changes ─────────

    static IEnumerable<Finding> WindowedGameOptimizations()
    {
        if (!SystemInfo.IsWindows11) yield break;
        // Read-only: the storage format isn't documented, so Pulse only reads it and links to the real setting.
        var v = Reg.Get(RegistryHive.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings") as string ?? "";
        bool on = v.Contains("SwapEffectUpgradeEnable=1", StringComparison.OrdinalIgnoreCase);
        yield return new Finding
        {
            Id = "windowed-opt", Rank = 5, Severity = on ? Severity.Ok : Severity.Check,
            Title = on ? "Optimizations for windowed games" : "Optimizations for windowed games may be off",
            Detail = "Microsoft's setting for DirectX 10 and 11 games in windowed or borderless mode: it switches them to a faster presentation method with lower latency, and enables variable refresh rate and Auto HDR.",
            Fix = on ? "" : "Turn it on in Settings → System → Display → Graphics → Change default graphics settings.",
            ActionKey = on ? "" : "settings:ms-settings:display-advancedgraphics", ActionText = "Open graphics settings",
        };
    }

    static IEnumerable<Finding> MemoryIntegrity()
    {
        var r = Shell.PowerShell("(Get-CimInstance -Namespace root\\Microsoft\\Windows\\DeviceGuard -ClassName Win32_DeviceGuard).SecurityServicesRunning -contains 2", 30_000);
        var t = r.Output.Trim();
        if (!t.EndsWith("True", StringComparison.OrdinalIgnoreCase) && !t.EndsWith("False", StringComparison.OrdinalIgnoreCase)) yield break;
        bool on = t.EndsWith("True", StringComparison.OrdinalIgnoreCase);
        yield return new Finding
        {
            Id = "hvci", Rank = 7, Severity = Severity.Ok,
            Title = on ? "Memory Integrity is on" : "Memory Integrity is off",
            Detail = on
                ? "This Windows security feature blocks malicious drivers. Microsoft's gaming guidance says gamers can turn it off while playing for better performance, but the PC is more exposed to threats while it's off. Pulse won't change it for you."
                : "This Windows security feature is off. If you turned it off for gaming, consider turning it back on when you're done.",
            ActionKey = "settings:windowsdefender://coreisolation", ActionText = "Open Core isolation",
        };
    }
}
