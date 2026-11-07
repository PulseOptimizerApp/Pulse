using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace Pulse.Core.Measurement;

public record GameEntry(string Title, string Exe, ImageSource Icon, string Why);

/// <summary>
/// Finds running games. A window counts as a game if its program lives in a game library folder
/// (Steam, Epic, Riot, Xbox, EA, Ubisoft, GOG, Battle.net games) or it fills the whole screen.
/// Overlays, launchers, RGB/audio tools and archivers are always ignored.
/// </summary>
public static class GameDetector
{
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows and Pulse itself
        "explorer", "ApplicationFrameHost", "TextInputHost", "SystemSettings", "Taskmgr", "ShellExperienceHost",
        "StartMenuExperienceHost", "SearchHost", "LockApp", "PulseOptimizer", "WindowsTerminal", "cmd", "powershell", "pwsh",
        "ShellHost", "Widgets", "WidgetBoard", "PhoneExperienceHost", "LogonUI", "consent", "dwm", "SecurityHealthSystray",
        // Screenshot tools: their full-screen overlay (Win+Shift+S) isn't a game, and recording it breaks the snip
        "ScreenClippingHost", "SnippingTool", "ScreenSketch", "ShareX", "Lightshot", "Greenshot", "Snagit32", "SnagitEditor",
        "Flameshot", "PowerToys", "PowerToys.ScreenRuler", "PowerToys.MeasureToolUI", "Microsoft.CmdPal.UI",
        // Browsers and chat
        "chrome", "msedge", "msedgewebview2", "firefox", "opera", "brave", "vivaldi", "Discord", "Telegram", "WhatsApp",
        "Signal", "Teams", "ms-teams", "Zoom", "Slack", "Claude",
        // Launchers (the games they start are detected on their own)
        "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "EADesktop", "EABackgroundService", "Origin", "Battle.net",
        "Agent", "RiotClientServices", "RiotClientUx", "RiotClientUxRender", "Riot Client", "LeagueClient", "LeagueClientUx",
        "LeagueClientUxRender", "vgtray", "GalaxyClient", "UplayWebCore", "RockstarService", "Launcher", "RobloxStudioBeta", "UbisoftConnect", "upc", "XboxPcApp", "GameBar", "Overwolf", "Playnite.DesktopApp",
        // Overlays, capture, hardware and audio tools
        "NVIDIA Overlay", "NVIDIA Share", "NVIDIA app", "nvcontainer", "RadeonSoftware", "AMDRSServ", "NZXT CAM", "CAM",
        "obs64", "obs32", "Medal", "MedalEncoder", "SteelSeriesGG", "SteelSeriesEngine", "lghub", "iCUE", "RazerAppEngine",
        "Razer Synapse 3", "ArmouryCrate", "MSI Center", "voicemeeter", "voicemeeterpro", "voicemeeter8", "voicemeeter8x64",
        "Spotify", "HWiNFO64", "MSIAfterburner", "RTSS", "CPUID HWMonitor", "LatencyMon",
        // Everyday apps
        "WinRAR", "7zFM", "notepad", "notepad++", "Code", "devenv", "Photoshop", "AcroRd32", "Acrobat", "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK",
    };

    static readonly string[] LibraryMarkers =
    {
        @"\steamapps\common\", @"\Epic Games\", @"\Riot Games\", @"\XboxGames\", @"\ModifiableWindowsApps\",
        @"\EA Games\", @"\Electronic Arts\", @"\Ubisoft Game Launcher\games\", @"\GOG Galaxy\Games\", @"\GOG Games\",
        @"\Blizzard Entertainment\", @"\Rockstar Games\", @"\Games\",
    };

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>True for programs that are never a game (Windows parts, screenshot tools, launchers, chat...).</summary>
    internal static bool IsIgnored(string processName) => NotGames.Contains(processName ?? "");

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    static bool IsFullscreen(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var r)) return false;
        // Overlays (a screenshot tool's dimmed screen, tool palettes) cover the screen but aren't games.
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2 /*nearest*/), ref mi)) return false;
        var m = mi.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    static int _lastGamePid;
    static string _lastGameName;
    static IntPtr _lastGameWindow;
    static bool _lastKnown;

    // Clips, Game mode and match recaps all ask every few seconds. They share one answer per second, and the process
    // list is read at most once per 1.5 s, so Pulse costs almost nothing while it waits for a game.
    static readonly object Gate = new();
    static string _answer;
    static long _answerAt = long.MinValue;
    static (string Name, int Pid)[] _procs = Array.Empty<(string, int)>();
    static long _procsAt = long.MinValue;
    // The window in front that was last found not to be a game: not looked at again until it changes (or 20 s pass,
    // in case it went fullscreen).
    static IntPtr _notGameHwnd;
    static long _notGameAt;

    static HashSet<string> _exes;
    static HashSet<string> Exes => _exes ??= new HashSet<string>(
        Pulse.Core.Games.GameCatalog.All.SelectMany(g => g.Exes).Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);

    static long Now => Environment.TickCount64;

    /// <summary>Names and ids of running programs, read at most every 1.5 s and shared by everything in Pulse that asks.</summary>
    public static (string Name, int Pid)[] Processes()
    {
        lock (Gate)
        {
            if (Now - _procsAt < 1500) return _procs;
            var all = Process.GetProcesses();
            var list = new (string, int)[all.Length];
            for (int i = 0; i < all.Length; i++)
            {
                try { list[i] = (all[i].ProcessName, all[i].Id); } catch { list[i] = ("", 0); }
                all[i].Dispose();
            }
            _procs = list;
            _procsAt = Now;
            return list;
        }
    }

    /// <summary>True if a program with this name (no .exe) is running. Uses the shared process list.</summary>
    public static bool IsRunning(string name)
    {
        foreach (var p in Processes()) if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool Alive(int pid)
    {
        foreach (var p in Processes()) if (p.Pid == pid) return true;
        return false;
    }

    /// <summary>A running game from Pulse's catalog (by exe name), or null. Uses the shared process list.</summary>
    static Process KnownRunning()
    {
        var exes = Exes;
        foreach (var p in Processes())
            if (exes.Contains(p.Name))
                try { return Process.GetProcessById(p.Pid); } catch { }
        return null;
    }

    static bool NoKnownRunning()
    {
        var exes = Exes;
        foreach (var p in Processes()) if (exes.Contains(p.Name)) return false;
        return true;
    }

    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    /// <summary>A program's full path, or null (anti-cheat protected games refuse; the fullscreen check covers them).</summary>
    static string ImagePath(Process p)
    {
        try { return p.MainModule?.FileName; } catch { return null; }
    }

    /// <summary>The process of the game <see cref="RunningGame"/> last found (0 if none).</summary>
    public static int GamePid => _lastGamePid;

    /// <summary>The main window of the game <see cref="RunningGame"/> last found (Zero if none). Used by Clips to record just the game.</summary>
    public static IntPtr GameWindow
    {
        get
        {
            if (_lastGamePid == 0) return IntPtr.Zero;
            if (_lastGameWindow != IntPtr.Zero && IsWindow(_lastGameWindow)) return _lastGameWindow;
            // The game made a new window (some do when a match loads): use its current main window.
            try { using var p = Process.GetProcessById(_lastGamePid); _lastGameWindow = p.MainWindowHandle; } catch { _lastGameWindow = IntPtr.Zero; }
            return _lastGameWindow;
        }
    }

    /// <summary>
    /// The game being played right now, or null. A game counts once it's in the foreground (from a game library folder,
    /// or fullscreen); after that it stays "running" while its process lives, so alt-tabbing out doesn't stop clipping.
    /// Callers share one answer per second.
    /// </summary>
    public static string RunningGame()
    {
        lock (Gate)
        {
            if (Now - _answerAt < 1000) return _answer;
            _answer = Detect();
            _answerAt = Now;
            return _answer;
        }
    }

    static string Detect()
    {
        try
        {
            if (_lastGamePid != 0)
            {
                // A game Pulse knows by name wins over a guess (for example a launcher that was in front first).
                if (Alive(_lastGamePid) && (_lastKnown || NoKnownRunning())) return _lastGameName;
                _lastGamePid = 0; _lastGameName = null; _lastGameWindow = IntPtr.Zero;
            }
            // A game from Pulse's catalog that's running counts even when it isn't in front (alt-tabbed, or a launcher
            // like Riot Client was in front when recording started).
            using (var kp = KnownRunning())
            {
                if (kp != null && kp.MainWindowHandle != IntPtr.Zero)
                {
                    var g = Pulse.Core.Games.GameCatalog.All.First(x => x.Exes.Any(e => Path.GetFileNameWithoutExtension(e).Equals(kp.ProcessName, StringComparison.OrdinalIgnoreCase)));
                    _lastGamePid = kp.Id; _lastGameName = g.Name; _lastGameWindow = kp.MainWindowHandle; _lastKnown = true;
                    return _lastGameName;
                }
            }
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            if (hwnd == _notGameHwnd && Now - _notGameAt < 20_000) return null;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == Environment.ProcessId) return null;
            using var p = Process.GetProcessById((int)pid);
            if (NotGames.Contains(p.ProcessName)) { NotAGame(hwnd); return null; }
            string path = ImagePath(p);
            // Games Pulse knows by name count even in a window (Roblox lives in AppData, not a game library).
            var known = Pulse.Core.Games.GameCatalog.All.FirstOrDefault(g =>
                g.Exes.Any(e => Path.GetFileNameWithoutExtension(e).Equals(p.ProcessName, StringComparison.OrdinalIgnoreCase)));
            bool isGame = known != null
                || (path != null && LibraryMarkers.Any(m => path.Contains(m, StringComparison.OrdinalIgnoreCase)))
                || IsFullscreen(hwnd);
            if (!isGame) { NotAGame(hwnd); return null; }
            string name = known?.Name;
            if (name == null) try { name = p.MainModule?.FileVersionInfo.ProductName; } catch { }
            if (string.IsNullOrWhiteSpace(name)) name = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.MainWindowTitle;
            _lastGamePid = p.Id;
            _lastGameName = name.Trim();
            _lastGameWindow = hwnd;
            _lastKnown = known != null;
            _notGameHwnd = IntPtr.Zero;
            return _lastGameName;
        }
        catch { return null; }
    }

    static void NotAGame(IntPtr hwnd) { _notGameHwnd = hwnd; _notGameAt = Now; }

    /// <summary>Settings or the game list changed: answer fresh next time.</summary>
    public static void Forget() { lock (Gate) { _answerAt = long.MinValue; _notGameHwnd = IntPtr.Zero; } }

    public static List<GameEntry> Find(IReadOnlyList<string> installFolders)
    {
        var found = new List<GameEntry>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero || p.Id == Environment.ProcessId) continue;
                if (NotGames.Contains(p.ProcessName)) continue;

                string path = null;
                path = ImagePath(p);

                string why = null;
                if (path != null && installFolders.Any(f => path.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
                    why = "Installed game";
                else if (path != null && LibraryMarkers.Any(m => path.Contains(m, StringComparison.OrdinalIgnoreCase)))
                    why = "Game folder";
                else if (IsFullscreen(p.MainWindowHandle))
                    why = "Running fullscreen";
                if (why == null) continue;

                var title = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.MainWindowTitle;
                found.Add(new GameEntry(title, p.ProcessName + ".exe", Native.FileIcon(path), why));
            }
            catch { }
            finally { p.Dispose(); }
        }
        return found.GroupBy(g => g.Exe, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(g => g.Title).ToList();
    }
}
