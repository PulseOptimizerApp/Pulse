using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Pulse.Core;

public static class Native
{
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    /// <summary>
    /// Pulse's own memory back to Windows: a full, compacting collection, then the working set trimmed (pages Pulse
    /// isn't touching go back to the system; they come back by themselves if needed). Only Pulse's own process.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage,
            PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }
    [DllImport("psapi.dll", EntryPoint = "GetProcessMemoryInfo")] static extern bool GetProcessMemoryInfo(IntPtr process, ref PROCESS_MEMORY_COUNTERS_EX2 c, uint size);

    /// <summary>The memory Task Manager shows for a process (its private working set), or 0.</summary>
    public static long TaskManagerMemory(System.Diagnostics.Process p)
    {
        try
        {
            if (p == null || p.HasExited) return 0;
            var c = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
            if (GetProcessMemoryInfo(p.Handle, ref c, c.cb) && c.PrivateWorkingSetSize != UIntPtr.Zero) return (long)c.PrivateWorkingSetSize;
            p.Refresh();
            return p.WorkingSet64;
        }
        catch { return 0; }
    }

    [DllImport("gdi32.dll")] static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);
    const int D3DKMT_PRIORITY_HIGH = 4;

    /// <summary>
    /// Lets a process (Pulse's recorder) go first on the graphics card, like OBS does when it runs as administrator.
    /// When a game holds the GPU near 100% (common on integrated graphics), the recorder otherwise waits behind it and
    /// clips come out choppy. The recorder's work is tiny, so the game barely notices. Needs administrator rights.
    /// </summary>
    public static bool RaiseGpuPriority(System.Diagnostics.Process p)
    {
        try { return p != null && !p.HasExited && D3DKMTSetProcessSchedulingPriorityClass(p.Handle, D3DKMT_PRIORITY_HIGH) == 0; }
        catch { return false; }
    }

    public static void GiveBackMemory()
    {
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            SetProcessWorkingSetSize(me.Handle, (IntPtr)(-1), (IntPtr)(-1));
        }
        catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    public static MEMORYSTATUSEX Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref m);
        return m;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, int[] pv, uint winIni);

    [StructLayout(LayoutKind.Sequential)]
    public struct STICKYKEYS { public uint cbSize; public uint dwFlags; }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    static extern bool SystemParametersInfoSticky(uint action, uint param, ref STICKYKEYS pv, uint winIni);

    const uint SPI_GETMOUSE = 0x0003, SPI_SETMOUSE = 0x0004;
    const uint SPI_GETSTICKYKEYS = 0x003A, SPI_SETSTICKYKEYS = 0x003B;
    const uint SPIF_UPDATEINIFILE = 0x01, SPIF_SENDCHANGE = 0x02;
    public const uint SKF_HOTKEYACTIVE = 0x00000004;

    /// <summary>Mouse thresholds and acceleration: [threshold1, threshold2, acceleration]. Windows default is 6, 10, 1.</summary>
    public static int[] GetMouse()
    {
        var a = new int[3];
        SystemParametersInfo(SPI_GETMOUSE, 0, a, 0);
        return a;
    }

    public static void SetMouse(int[] values)
    {
        if (!SystemParametersInfo(SPI_SETMOUSE, 0, values, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
            throw new InvalidOperationException("Windows refused the mouse setting (error " + Marshal.GetLastWin32Error() + ").");
    }

    public static uint GetStickyKeysFlags()
    {
        var sk = new STICKYKEYS { cbSize = (uint)Marshal.SizeOf<STICKYKEYS>() };
        SystemParametersInfoSticky(SPI_GETSTICKYKEYS, sk.cbSize, ref sk, 0);
        return sk.dwFlags;
    }

    public static void SetStickyKeysFlags(uint flags)
    {
        var sk = new STICKYKEYS { cbSize = (uint)Marshal.SizeOf<STICKYKEYS>(), dwFlags = flags };
        if (!SystemParametersInfoSticky(SPI_SETSTICKYKEYS, sk.cbSize, ref sk, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
            throw new InvalidOperationException("Windows refused the Sticky Keys setting (error " + Marshal.GetLastWin32Error() + ").");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

    public static (long size, long items) RecycleBinInfo()
    {
        var i = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref i) == 0 ? (i.i64Size, i.i64NumItems) : (0, 0);
    }

    public static bool EmptyRecycleBin() => SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4) == 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint cb, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr h);

    /// <summary>Gets a file's icon as a frozen WPF image (safe to create off the UI thread).</summary>
    public static ImageSource FileIcon(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var info = new SHFILEINFO();
            SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 /*ICON*/ | 0x0 /*LARGE*/);
            if (info.hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DestroyIcon(info.hIcon); }
        }
        catch { return null; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);

    /// <summary>A file's icon at up to 256 px (games ship big icons; the shell's default is 32 px). Frozen, so any thread.</summary>
    public static ImageSource LargeIcon(string path, int size = 256)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            int index = 0;
            var comma = path.LastIndexOf(',');
            if (comma > 2 && int.TryParse(path[(comma + 1)..], out var i)) { index = i; path = path[..comma]; }
            path = path.Trim('"');
            if (!File.Exists(path)) return null;
            var icons = new IntPtr[1];
            var ids = new uint[1];
            if (PrivateExtractIcons(path, index, size, size, icons, ids, 1, 0) == 0 || icons[0] == IntPtr.Zero) return FileIcon(path);
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DestroyIcon(icons[0]); }
        }
        catch { return null; }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Asks Windows 11 to give the window rounded corners and a dark frame. No-op on Windows 10.</summary>
    public static void StyleWindow(IntPtr hwnd)
    {
        try
        {
            int round = 2; DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
            int dark = 1; DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
        catch { }
    }
}
