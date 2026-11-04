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
        // Browsers and chat
        "chrome", "msedge", "msedgewebview2", "firefox", "opera", "brave", "vivaldi", "Discord", "Telegram", "WhatsApp",
        "Signal", "Teams", "ms-teams", "Zoom", "Slack", "Claude",
        // Launchers (the games they start are detected on their own)
        "steam", "steamwebhelper", "EpicGamesLauncher", "EADesktop", "Origin", "Battle.net", "RiotClientServices",
        "RiotClientUx", "GalaxyClient", "UbisoftConnect", "upc", "XboxPcApp", "GameBar", "Overwolf", "Playnite.DesktopApp",
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

    static bool IsFullscreen(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var r)) return false;
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
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);

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
    /// Cheap enough to call every few seconds (no icons, one process lookup).
    /// </summary>
    public static string RunningGame()
    {
        try
        {
            if (_lastGamePid != 0)
            {
                try
                {
                    using var last = Process.GetProcessById(_lastGamePid);
                    if (!last.HasExited) return _lastGameName;
                }
                catch { }
                _lastGamePid = 0; _lastGameName = null; _lastGameWindow = IntPtr.Zero;
            }
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == Environment.ProcessId) return null;
            using var p = Process.GetProcessById((int)pid);
            if (NotGames.Contains(p.ProcessName)) return null;
            string path = null;
            try { path = p.MainModule?.FileName; } catch { }
            // Games Pulse knows by name count even in a window (Roblox lives in AppData, not a game library).
            var known = Pulse.Core.Games.GameCatalog.All.FirstOrDefault(g =>
                g.Exes.Any(e => Path.GetFileNameWithoutExtension(e).Equals(p.ProcessName, StringComparison.OrdinalIgnoreCase)));
            bool isGame = known != null
                || (path != null && LibraryMarkers.Any(m => path.Contains(m, StringComparison.OrdinalIgnoreCase)))
                || IsFullscreen(hwnd);
            if (!isGame) return null;
            string name = known?.Name;
            if (name == null) try { name = p.MainModule?.FileVersionInfo.ProductName; } catch { }
            if (string.IsNullOrWhiteSpace(name)) name = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.MainWindowTitle;
            _lastGamePid = p.Id;
            _lastGameName = name.Trim();
            _lastGameWindow = hwnd;
            return _lastGameName;
        }
        catch { return null; }
    }

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
                try { path = p.MainModule?.FileName; } catch { /* protected by anti-cheat: rely on fullscreen check */ }

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
