using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pulse.Core.Clips;

/// <summary>Which apps get their own sound track in a clip: the game, Discord and Spotify (when they're running).</summary>
public static class AppAudio
{
    /// <summary>Apps with their own track, by process name (without .exe) → track name.</summary>
    static readonly (string Process, string Title)[] Known =
    {
        ("Discord", "Discord"), ("DiscordPTB", "Discord"), ("DiscordCanary", "Discord"),
        ("Spotify", "Spotify"),
    };

    /// <summary>The track name and process for each source. The game comes first.</summary>
    public static List<(string Title, int Pid)> Sources(int gamePid)
    {
        var list = new List<(string, int)>();
        if (gamePid > 0) list.Add(("Game", gamePid));
        var seen = new HashSet<string>();
        foreach (var (name, title) in Known)
        {
            if (seen.Contains(title)) continue;
            int root = RootProcess(name);
            if (root > 0) { list.Add((title, root)); seen.Add(title); }
        }
        return list;
    }

    /// <summary>
    /// The top process of an app made of many (Discord and Spotify run a dozen). Capturing it includes all the
    /// ones it started, which is where the sound actually plays.
    /// </summary>
    static int RootProcess(string name)
    {
        Process[] ps = Array.Empty<Process>();
        try
        {
            ps = Process.GetProcessesByName(name);
            if (ps.Length == 0) return 0;
            var ids = new HashSet<int>(ps.Select(p => p.Id));
            foreach (var p in ps)
                if (!ids.Contains(ParentOf(p.Id))) return p.Id;
            return ps[0].Id;
        }
        catch { return 0; }
        finally { foreach (var p in ps) p.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public int dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public int th32ModuleID, cntThreads, th32ParentProcessID, pcPriClassBase, dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(int flags, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    /// <summary>
    /// The process that started this one (0 if unknown), from Windows' list of running processes. Opens no process
    /// and reads nothing from inside one.
    /// </summary>
    static int ParentOf(int pid)
    {
        var snap = CreateToolhelp32Snapshot(0x2 /*TH32CS_SNAPPROCESS*/, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return 0;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = Marshal.SizeOf<PROCESSENTRY32>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                if (e.th32ProcessID == pid) return e.th32ParentProcessID;
            return 0;
        }
        finally { CloseHandle(snap); }
    }
}
