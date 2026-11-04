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

    /// <summary>Most separate app tracks per clip (plus the full mix). Enough for a game, voice chat, music and a browser.</summary>
    public const int MaxApps = 6;

    /// <summary>
    /// The track name and process for each source: the game first, then every app in Windows' volume mixer (Discord,
    /// Spotify, browsers, Steam...). Falls back to Discord and Spotify if the mixer can't be read.
    /// </summary>
    public static List<(string Title, int Pid)> Sources(int gamePid)
    {
        var list = new List<(string Title, int Pid)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (gamePid > 0) { list.Add(("Game", gamePid)); names.Add("Game"); }
        var mixer = MixerApps();
        foreach (var (pid, name) in mixer)
        {
            if (list.Count >= MaxApps + (gamePid > 0 ? 1 : 0)) break;
            if (pid == gamePid || pid == Environment.ProcessId || !names.Add(name)) continue;
            if (gamePid > 0 && IsInTree(pid, gamePid)) continue; // the game's own helper processes are in its track already
            list.Add((name, pid));
        }
        if (mixer.Count == 0)
            foreach (var (proc, title) in Known)
            {
                if (names.Contains(title)) continue;
                int root = RootProcess(proc);
                if (root > 0) { list.Add((title, root)); names.Add(title); }
            }
        return list;
    }

    /// <summary>Apps with a sound session on the default output (what the volume mixer lists), with a friendly name.</summary>
    public static List<(int Pid, string Name)> MixerApps()
    {
        var found = new List<(int, string)>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0, 0, out var device) < 0) return found;
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out var mgrObj) < 0) return found;
            var mgr = (IAudioSessionManager2)mgrObj;
            if (mgr.GetSessionEnumerator(out var sessions) < 0) return found;
            sessions.GetCount(out int count);
            var seen = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var ctl) < 0 || ctl is not IAudioSessionControl2 c2) continue;
                if (c2.GetState(out int state) < 0 || state == 2 /*expired*/) continue;
                if (c2.IsSystemSoundsSession() == 0) continue; // S_OK means it is the system sounds session
                if (c2.GetProcessId(out uint pid) < 0 || pid == 0 || !seen.Add((int)pid)) continue;
                var name = FriendlyName((int)pid);
                if (name != null) found.Add(((int)pid, name));
            }
        }
        catch { }
        return found;
    }

    /// <summary>"Discord", "Spotify", "Google Chrome": the program's own description, else its process name.</summary>
    static string FriendlyName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            string desc = null;
            try { desc = p.MainModule?.FileVersionInfo.FileDescription; } catch { }
            var name = string.IsNullOrWhiteSpace(desc) ? p.ProcessName : desc.Trim();
            if (name.Equals("audiodg", StringComparison.OrdinalIgnoreCase)) return null;
            return name.Length > 28 ? name[..28].Trim() : name;
        }
        catch { return null; }
    }

    /// <summary>True when <paramref name="pid"/> is <paramref name="root"/> or was started by it (at any depth).</summary>
    static bool IsInTree(int pid, int root)
    {
        for (int i = 0, p = pid; i < 16 && p > 0; i++, p = ParentOf(p)) if (p == root) return true;
        return false;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr groupingParam, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr groupingParam, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr ctx);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr ctx);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, IntPtr ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
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
