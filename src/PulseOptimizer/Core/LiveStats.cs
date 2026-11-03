using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Pulse.Core;

/// <summary>
/// GPU load from Windows' own performance counters (the same "GPU Engine" counters Task Manager uses):
/// the 3D engine utilization of every process, added up.
/// </summary>
public sealed class GpuMeter : IDisposable
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, ref uint count, IntPtr buffer);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);

    const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
    const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    IntPtr _query, _counter;
    readonly bool _ok;

    public GpuMeter()
    {
        try
        {
            _ok = PdhOpenQuery(null, IntPtr.Zero, out _query) == 0
                  && PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _counter) == 0;
            if (_ok) PdhCollectQueryData(_query);
        }
        catch { _ok = false; }
    }

    /// <summary>0..1, or null when this PC doesn't expose GPU counters.</summary>
    public double? Sample()
    {
        if (!_ok) return null;
        try
        {
            if (PdhCollectQueryData(_query) != 0) return null;
            uint bytes = 0, count = 0;
            if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bytes, ref count, IntPtr.Zero) != PDH_MORE_DATA) return 0;
            var buf = Marshal.AllocHGlobal((int)bytes);
            try
            {
                if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bytes, ref count, buf) != 0) return null;
                // PDH_FMT_COUNTERVALUE_ITEM: name pointer, then { DWORD status; (padding); double value }.
                int size = IntPtr.Size + 16;
                double sum = 0;
                for (int i = 0; i < count; i++)
                {
                    var item = buf + i * size;
                    if (Marshal.ReadInt32(item + IntPtr.Size) != 0) continue;
                    sum += BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + IntPtr.Size + 8));
                }
                return Math.Clamp(sum / 100, 0, 1);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
    }

    public void Dispose() { if (_query != IntPtr.Zero) PdhCloseQuery(_query); _query = IntPtr.Zero; }
}

/// <summary>One shared once-a-second sample of CPU, GPU and memory, with a short history for sparklines.</summary>
public static class LiveStats
{
    public const int HistoryLength = 40;
    public static readonly List<double> Cpu = new(), Gpu = new(), Ram = new();
    public static double CpuNow, RamNow, RamUsedBytes, RamTotalBytes;
    public static double? GpuNow;
    public static event Action Updated;

    static DispatcherTimer _timer;
    static readonly CpuMeter _cpu = new();
    static GpuMeter _gpu;
    static bool _sampling;

    public static void Start()
    {
        if (_timer != null) return;
        _cpu.Sample();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) =>
        {
            if (_sampling) return;
            _sampling = true;
            try
            {
                var (cpu, gpu, used, total) = await Task.Run(() =>
                {
                    _gpu ??= new GpuMeter();
                    var m = Native.Memory();
                    return (_cpu.Sample(), _gpu.Sample(), (double)(m.ullTotalPhys - m.ullAvailPhys), (double)m.ullTotalPhys);
                });
                CpuNow = cpu; GpuNow = gpu; RamUsedBytes = used; RamTotalBytes = total; RamNow = total > 0 ? used / total : 0;
                Push(Cpu, cpu); Push(Ram, RamNow); Push(Gpu, gpu ?? 0);
                Updated?.Invoke();
            }
            finally { _sampling = false; }
        };
        _timer.Start();
    }

    static void Push(List<double> list, double v)
    {
        list.Add(v);
        if (list.Count > HistoryLength) list.RemoveAt(0);
    }
}
