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
