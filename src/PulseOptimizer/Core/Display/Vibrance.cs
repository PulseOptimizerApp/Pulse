using System.Runtime.InteropServices;

namespace Pulse.Core.Display;

/// <summary>Settings for making games more vibrant.</summary>
public class VibranceSettings
{
    public bool Enabled { get; set; }
    /// <summary>How vibrant VALORANT looks: 0–100, where 50 is normal and 100 is the most the graphics card allows.</summary>
    public int Level { get; set; } = 75;
}

/// <summary>
/// Colour vibrance through the graphics card's own driver setting: NVIDIA's Digital Vibrance and AMD's Saturation,
/// the same slider their control panels have. Nothing in the game is touched. Levels are 0–100 with 50 as normal.
/// </summary>
public abstract class Vibrance
{
    public abstract string Card { get; }
    /// <summary>The level now (0–100, 50 normal), or -1 when it can't be read.</summary>
    public abstract int Get();
    /// <summary>Sets every screen on the card. False when the driver refused.</summary>
    public abstract bool Set(int level);

    /// <summary>The card's vibrance control, or null when this PC's graphics card has none Pulse can use.</summary>
    public static Vibrance Find()
    {
        try { var n = new NvidiaVibrance(); if (n.Ready) return n; } catch { }
        try { var a = new AmdVibrance(); if (a.Ready) return a; } catch { }
        return null;
    }

    /// <summary>Maps 0–100 (50 = normal) onto a driver range whose normal value is <paramref name="normal"/>.</summary>
    internal static int ToDriver(int level, int min, int normal, int max)
    {
        level = Math.Clamp(level, 0, 100);
        return level >= 50
            ? normal + (int)Math.Round((max - normal) * (level - 50) / 50.0)
            : min + (int)Math.Round((normal - min) * level / 50.0);
    }

    internal static int FromDriver(int value, int min, int normal, int max)
    {
        if (value >= normal) return max == normal ? 50 : 50 + (int)Math.Round(50.0 * (value - normal) / (max - normal));
        return normal == min ? 50 : (int)Math.Round(50.0 * (value - min) / (normal - min));
    }
}

/// <summary>NVIDIA Digital Vibrance through NVAPI (0 is normal, up to 63).</summary>
sealed class NvidiaVibrance : Vibrance
{
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumFn(int index, out IntPtr display);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetFn(IntPtr display, int output, ref DvcInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetFn(IntPtr display, int output, int level);

    [StructLayout(LayoutKind.Sequential)]
    struct DvcInfo { public uint Version; public int Current, Min, Max; }

    readonly EnumFn _enum;
    readonly GetFn _get;
    readonly SetFn _set;
    public bool Ready { get; }
    public override string Card => "NVIDIA";

    static T Fn<T>(uint id) where T : Delegate
    {
        var p = QueryInterface(id);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    public NvidiaVibrance()
    {
        var init = Fn<InitFn>(0x0150E828);
        _enum = Fn<EnumFn>(0x9ABDD40D);
        _get = Fn<GetFn>(0x4085DE45);
        _set = Fn<SetFn>(0x172409B4);
        Ready = init != null && _enum != null && _get != null && _set != null && init() == 0 && Displays().Any();
    }

    IEnumerable<IntPtr> Displays()
    {
        for (int i = 0; i < 16; i++)
        {
            if (_enum(i, out var h) != 0) yield break;
            yield return h;
        }
    }

    bool Info(IntPtr h, out DvcInfo info)
    {
        info = new DvcInfo { Version = (uint)Marshal.SizeOf<DvcInfo>() | (1u << 16) };
        return _get(h, 0, ref info) == 0;
    }

    public override int Get()
    {
        foreach (var h in Displays())
            if (Info(h, out var i)) return FromDriver(i.Current, i.Min, i.Min, i.Max);
        return -1;
    }

    public override bool Set(int level)
    {
        bool any = false;
        foreach (var h in Displays())
            if (Info(h, out var i)) any |= _set(h, 0, ToDriver(level, i.Min, i.Min, i.Max)) == 0;
        return any;
    }
}

/// <summary>AMD Saturation through ADL (100 is normal, 0–200).</summary>
sealed class AmdVibrance : Vibrance
{
    const string Dll = "atiadlxx.dll";
    const int Saturation = 4; // ADL_DISPLAY_COLOR_SATURATION

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr Malloc(int size);
    static readonly Malloc Alloc = size => Marshal.AllocHGlobal(size);

    [DllImport(Dll)] static extern int ADL_Main_Control_Create(Malloc callback, int connectedOnly);
    [DllImport(Dll)] static extern int ADL_Adapter_NumberOfAdapters_Get(ref int count);
    [DllImport(Dll)] static extern int ADL_Display_DisplayInfo_Get(int adapter, ref int count, out IntPtr info, int forceDetect);
    [DllImport(Dll)] static extern int ADL_Display_Color_Get(int adapter, int display, int type, out int current, out int def, out int min, out int max, out int step);
    [DllImport(Dll)] static extern int ADL_Display_Color_Set(int adapter, int display, int type, int current);

    const int DisplayInfoSize = 16 + 4 + 256 + 256 + 4 * 5;
    readonly List<(int Adapter, int Display)> _displays = new();
    public bool Ready { get; }
    public override string Card => "AMD";

    public AmdVibrance()
    {
        if (ADL_Main_Control_Create(Alloc, 1) != 0) return;
        int n = 0;
        if (ADL_Adapter_NumberOfAdapters_Get(ref n) != 0) return;
        var seen = new HashSet<(int, int)>();
        for (int a = 0; a < n; a++)
        {
            int count = 0;
            if (ADL_Display_DisplayInfo_Get(a, ref count, out var arr, 0) != 0 || arr == IntPtr.Zero) continue;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var p = arr + i * DisplayInfoSize;
                    int logical = Marshal.ReadInt32(p, 0), adapter = Marshal.ReadInt32(p, 8);
                    int value = Marshal.ReadInt32(p, DisplayInfoSize - 4);
                    // Connected (1) and mapped to the desktop (2).
                    if ((value & 3) != 3 || !seen.Add((adapter, logical))) continue;
                    if (ADL_Display_Color_Get(adapter, logical, Saturation, out _, out _, out _, out _, out _) == 0) _displays.Add((adapter, logical));
                }
            }
            finally { Marshal.FreeHGlobal(arr); }
        }
        Ready = _displays.Count > 0;
    }

    public override int Get()
    {
        foreach (var (a, d) in _displays)
            if (ADL_Display_Color_Get(a, d, Saturation, out var cur, out var def, out var min, out var max, out _) == 0) return FromDriver(cur, min, def, max);
        return -1;
    }

    public override bool Set(int level)
    {
        bool any = false;
        foreach (var (a, d) in _displays)
            if (ADL_Display_Color_Get(a, d, Saturation, out _, out var def, out var min, out var max, out _) == 0)
                any |= ADL_Display_Color_Set(a, d, Saturation, ToDriver(level, min, def, max)) == 0;
        return any;
    }
}
