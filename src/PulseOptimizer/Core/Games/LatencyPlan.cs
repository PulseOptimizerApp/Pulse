using System.Windows.Media;
using System.Diagnostics;
using Microsoft.Win32;
using Pulse.Ui;

namespace Pulse.Core.Games;

/// <summary>A game found on this PC, with its low-latency checklist.</summary>
public class GameItem : Observable
{
    public KnownGame Game { get; init; }
    public string Name => Game.Name;
    public bool Installed { get; init; }
    public bool HasReflex => Game.Reflex;
    public bool HasAntiLag2 => Game.AntiLag2;

    bool _running;
    public bool Running { get => _running; set { if (Set(ref _running, value)) Raise(nameof(StatusText)); } }

    int _done, _total;
    public int Done { get => _done; set { if (Set(ref _done, value)) Raise(nameof(ProgressText)); } }
    public int Total { get => _total; set { if (Set(ref _total, value)) Raise(nameof(ProgressText)); } }
    public string ProgressText => Total == 0 || !Installed ? "" : Done >= Total ? "✓ Done" : $"{Done}/{Total}";

    public string StatusText => Game.IsAddTile ? "Pick one from your PC" : Game.Id == "other" ? "Steps for any game" : Running ? "Running now" : !Installed ? (Game.IsCustom ? "Can't find its .exe" : "Not installed") : Game.IsCustom ? "Added by you" : "Installed";
    public bool Dim => !Installed && !Game.IsAddTile && Game.Id != "other";
    public CustomGame Custom { get; init; }
    public string Short => Game.Short;
    public System.Windows.Media.Brush Tint => GameArt.Tint(Game);
    public System.Windows.Media.Brush Glow => GameArt.Glow(Game);
    public System.Windows.Media.ImageSource Icon { get; set; }
    public System.Windows.Media.ImageSource CoverImage { get; set; }
    public bool HasIcon => Icon != null;
    public bool HasCover => CoverImage != null;
    /// <summary>No cover and no icon: show the short name on the colored tile.</summary>
    public bool ShowLetters => Icon == null && CoverImage == null;
}

public class PlanStep : Observable
{
    public string Id { get; init; }
    public string Title { get; init; }
    public string Detail { get; init; }
    /// <summary>Pulse confirmed this from the PC itself; the box can't be unticked.</summary>
    public bool Automatic { get; init; }
    public string ActionKey { get; init; } = "";
    public string ActionText { get; init; } = "";
    public bool HasAction => !string.IsNullOrEmpty(ActionKey);
    public bool CanTick => !Automatic;

    bool _done;
    public bool Done { get => _done; set => Set(ref _done, value); }
}

public static class LatencyPlan
{
    /// <summary>Known games installed on this PC (Steam, Epic, and Windows' installed apps list), plus "Any other game".</summary>
    public static List<GameItem> FindGames()
    {
        var names = new List<string>();
        names.AddRange(PcDiagnostics.LauncherGames().Select(g => g.Name));
        foreach (var (hive, path) in new[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            foreach (var sub in Reg.SubKeys(hive, path))
                if (Reg.Get(hive, $@"{path}\{sub}", "DisplayName") is string dn) names.Add(dn);
        }

        var running = RunningExes();
        var list = new List<GameItem>();
        foreach (var g in GameCatalog.All)
        {
            bool installed = names.Any(g.IsNamed);
            bool isRunning = g.Exes.Any(running.Contains);
            var (icon, cover) = GameArt.Find(g, installed || isRunning);
            list.Add(new GameItem { Game = g, Installed = installed || isRunning, Running = isRunning, Icon = icon, CoverImage = cover });
        }
        // Running first, then installed, then the rest, keeping the catalog's order within each group.
        // Games the user added themselves.
        foreach (var c in TweakEngine.State.CustomGames.ToList())
        {
            var g = GameCatalog.Custom(c);
            bool exists = !string.IsNullOrEmpty(c.Exe) && File.Exists(c.Exe);
            ImageSource icon = null;
            try { icon = exists ? Native.LargeIcon(c.Exe) : null; } catch { }
            list.Add(new GameItem { Game = g, Custom = c, Installed = exists, Running = g.Exes.Any(running.Contains), Icon = icon, CoverImage = GameArt.Bundled(c.Name) });
        }
        list = list.OrderByDescending(i => i.Running).ThenByDescending(i => i.Installed).ToList();
        list.Add(new GameItem { Game = GameCatalog.AddTile });
        return list;
    }

    static Task<List<GameItem>> _games;

    /// <summary>The game list, found once and shared by Home and Games.</summary>
    public static Task<List<GameItem>> GamesAsync(bool refresh = false)
    {
        if (_games == null || refresh || _games.IsFaulted) _games = Task.Run(FindGames);
        return _games;
    }

    public static HashSet<string> RunningExes()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { set.Add(p.ProcessName + ".exe"); } catch { }
            finally { p.Dispose(); }
        }
        return set;
    }

    /// <summary>Builds the checklist for one game, tailored to this PC's graphics card and monitor.</summary>
    public static List<PlanStep> Build(KnownGame g, HardwareFacts hw, MonitorInfo monitor)
    {
        var steps = new List<PlanStep>();
        var ticked = TweakEngine.State.GameChecklist.TryGetValue(g.Id, out var t) ? t : new List<string>();
        bool T(string id) => ticked.Contains(id);

        // 1. Refresh rate (checked automatically from the PC).
        if (monitor != null)
        {
            int max = monitor.MaxHzAtCurrent;
            bool atMax = monitor.Current.Hz >= max - 1;
            steps.Add(new PlanStep
            {
                Id = "refresh", Automatic = atMax, Done = atMax,
                Title = atMax ? $"Monitor at its highest refresh rate ({monitor.Current.Hz} Hz)" : $"Set your monitor to {max} Hz (it's at {monitor.Current.Hz} Hz)",
                Detail = atMax ? "Pulse checked this for you." : "The biggest free improvement: more frames reach your eyes sooner.",
                ActionKey = atMax ? "" : $"refresh:{monitor.DeviceName}|{monitor.Current.Width}|{monitor.Current.Height}|{max}",
                ActionText = atMax ? "" : $"Switch to {max} Hz",
            });
        }

        // 2. The game's own latency technology, or the driver's.
        if (hw.Gpu == GpuVendor.Nvidia && g.Reflex)
            steps.Add(new PlanStep
            {
                Id = "reflex", Done = T("reflex"),
                Title = "Turn on NVIDIA Reflex: On + Boost",
                Detail = $"{g.LatencySetting}. NVIDIA's own advice is to always turn Reflex on; it lines up your CPU and graphics card so frames don't wait in a queue.",
            });
        else if (hw.Gpu == GpuVendor.Amd && g.AntiLag2)
            steps.Add(new PlanStep
            {
                Id = "antilag2", Done = T("antilag2"),
                Title = "Turn on AMD Anti-Lag 2",
                Detail = $"{g.LatencySetting}. Anti-Lag 2 is built into this game, so it cuts more delay than the driver version.",
            });
        else if (hw.Gpu == GpuVendor.Nvidia)
            steps.Add(new PlanStep
            {
                Id = "ullm", Done = T("ullm"),
                Title = "Set Low Latency Mode to Ultra for this game",
                Detail = g.IsGeneric
                    ? "If the game has NVIDIA Reflex, use that instead. Otherwise: NVIDIA Control Panel → Manage 3D settings → Program Settings → add the game → Low Latency Mode → Ultra → Apply. (Doesn't work in Vulkan games.)"
                    : $"{g.Name} doesn't have NVIDIA Reflex, so this is NVIDIA's next-best option: NVIDIA Control Panel → Manage 3D settings → Program Settings → add {g.Name} → Low Latency Mode → Ultra → Apply.",
            });
        else if (hw.Gpu == GpuVendor.Amd)
            steps.Add(new PlanStep
            {
                Id = "antilag", Done = T("antilag"),
                Title = "Turn on Radeon Anti-Lag for this game",
                Detail = "AMD Software → Gaming → Games → pick the game → turn Radeon Anti-Lag on. (If the game itself offers AMD Anti-Lag 2, use that instead.)",
            });
        else
            steps.Add(new PlanStep
            {
                Id = "lowlatency", Done = T("lowlatency"),
                Title = "Turn on the game's low-latency option",
                Detail = "Look in the game's Video or Graphics settings for a Low latency, Reflex or Anti-Lag option.",
            });

        // 3. Fullscreen.
        if (g.FullscreenSetting.Length > 0 || g.IsGeneric)
            steps.Add(new PlanStep
            {
                Id = "fullscreen", Done = T("fullscreen"),
                Title = "Play in fullscreen",
                Detail = (g.FullscreenSetting.Length > 0 ? g.FullscreenSetting + ". " : "Use the game's Fullscreen (or Exclusive Fullscreen) display mode. ") +
                         "NVIDIA recommends exclusive fullscreen whenever a game offers it.",
            });

        // 4. Sync and frame cap, worked out for this monitor.
        int hz = monitor?.Current.Hz ?? 60;
        if (hz >= 100)
        {
            int cap = hz - 3;
            bool reflexHandlesCap = hw.Gpu == GpuVendor.Nvidia && g.Reflex;
            steps.Add(new PlanStep
            {
                Id = "sync", Done = T("sync"),
                Title = reflexHandlesCap ? "G-SYNC on, V-Sync on in the driver, off in the game" : $"G-SYNC/FreeSync on and cap at {cap} FPS",
                Detail = reflexHandlesCap
                    ? $"Turn on G-SYNC (see Diagnose for the steps), set Vertical sync to On in NVIDIA Control Panel, and turn V-Sync off inside {g.Name}. With Reflex on, the driver then caps your frame rate just under {hz} Hz automatically."
                    : $"Turn on G-SYNC or FreeSync, turn V-Sync off in the game, and cap the frame rate at {cap} FPS (3 below your {hz} Hz) with the game's own limiter if it has one. That keeps you inside the adaptive-sync range without added delay.",
            });
        }
        else
        {
            steps.Add(new PlanStep
            {
                Id = "vsync", Done = T("vsync"),
                Title = "Turn V-Sync off in the game",
                Detail = "On a 60 Hz screen without G-SYNC or FreeSync, V-Sync adds noticeable delay. Turning it off gives the lowest delay; you may see some tearing.",
            });
        }

        // 5. Mouse.
        steps.Add(new PlanStep
        {
            Id = "mouse", Done = T("mouse"),
            Title = "Set up your mouse for the lowest delay",
            Detail = string.Join("\n", new[]
            {
                "•  Polling rate: 1000 Hz or higher in your mouse's software. At 125 Hz, NVIDIA measured up to 3 ms extra delay. 4000–8000 Hz only helps on a fast CPU and a high-refresh monitor; on slower CPUs it can cost FPS in CS2, VALORANT and Fortnite.",
                "•  Connection: a cable or the mouse's own 2.4 GHz receiver, never Bluetooth. Plug it into a port on the back of the PC, not a hub or the monitor.",
                "•  DPI: 800–1600 is a good range. A higher DPI reports small movements a little sooner; if you raise it, lower your in-game sensitivity so it feels the same.",
                "•  In the game: raw input on (where offered), mouse acceleration and smoothing off.",
                "•  Windows' \"Enhance pointer precision\" adds acceleration. Turn it off in Tweaks so your desktop and game aim match.",
            }),
            ActionKey = "page:measure", ActionText = "Test my mouse",
        });

        // 6. The settings pros use, then the ones that cost the most frames.
        var pro = g.ProSettings ?? Array.Empty<string>();
        if (pro.Length > 0 || g.FpsSettings.Length > 0)
        {
            var parts = new List<string>();
            if (pro.Length > 0) parts.Add(string.Join("\n", pro.Select(f => "•  " + f)));
            if (g.FpsSettings.Length > 0)
                parts.Add((pro.Length > 0 ? "Still short on FPS?\n" : "") + string.Join("\n", g.FpsSettings.Select(f => "•  " + f)));
            if (g.ProSource.Length > 0) parts.Add("Based on " + g.ProSource + ". Sensitivity is personal: copy the setup, then adjust.");
            steps.Add(new PlanStep
            {
                Id = "fps", Done = T("fps"),
                Title = pro.Length > 0 ? $"Use the settings pros use in {g.Name}" : g.IsGeneric ? "Lower the settings that cost the most FPS" : $"Get more FPS in {g.Name}",
                Detail = string.Join("\n\n", parts),
            });
        }

        if (g.Note.Length > 0 && !g.IsGeneric)
            steps.Add(new PlanStep { Id = "note", Done = T("note"), Title = $"{g.Name} tip", Detail = g.Note });

        return steps;
    }

    public static void SaveTicks(string gameId, IEnumerable<PlanStep> steps)
    {
        TweakEngine.State.GameChecklist[gameId] = steps.Where(s => s.Done && !s.Automatic).Select(s => s.Id).ToList();
        TweakEngine.Save();
    }
}
