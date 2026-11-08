using System.Runtime.InteropServices;

namespace Pulse.Core.Measurement;

/// <summary>
/// GPU temperature and video memory the way Task Manager reads them: from Windows' own graphics kernel (D3DKMT) and
/// performance counters. Works for NVIDIA, AMD and Intel on Windows 10 2004 or newer. No driver is installed and no
/// other process is touched (tools like Afterburner load their own kernel driver for this; anti-cheats dislike that).
/// </summary>
public sealed class GpuSensors : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct ADAPTERINFO { public uint hAdapter; public LUID Luid; public uint NumOfSources; public int bPrecisePresentRegionsPreferred; }
    [StructLayout(LayoutKind.Sequential)] struct ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }
    [StructLayout(LayoutKind.Sequential)] struct QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pPrivateDriverData; public uint PrivateDriverDataSize; }
    [StructLayout(LayoutKind.Sequential)]
    struct ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PCIEBandwidth;
        public uint FanRPM, Power;
        public ulong Temperature; // tenths of a degree Celsius
        public byte PowerStateOverride;
    }
    [StructLayout(LayoutKind.Sequential)] struct CLOSEADAPTER { public uint hAdapter; }

    [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref ENUMADAPTERS2 e);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QUERYADAPTERINFO q);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CLOSEADAPTER c);
    const int KMTQAITYPE_ADAPTERPERFDATA = 62;

    readonly List<uint> _adapters = new();
    readonly PdhSum _vram = new(@"\GPU Adapter Memory(*)\Dedicated Usage");

    public GpuSensors()
    {
        try
        {
            var e = new ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return;
            int size = Marshal.SizeOf<ADAPTERINFO>();
            e.pAdapters = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
            try
            {
                if (D3DKMTEnumAdapters2(ref e) != 0) return;
                for (int i = 0; i < e.NumAdapters; i++) _adapters.Add(Marshal.PtrToStructure<ADAPTERINFO>(e.pAdapters + i * size).hAdapter);
            }
            finally { Marshal.FreeHGlobal(e.pAdapters); }
        }
        catch { }
    }

    /// <summary>The hottest graphics adapter in °C (the one you game on), or null when Windows doesn't report it.</summary>
    public double? TemperatureC()
    {
        double best = 0;
        foreach (var h in _adapters)
        {
            try
            {
                var data = new ADAPTER_PERFDATA();
                var buf = Marshal.AllocHGlobal(Marshal.SizeOf<ADAPTER_PERFDATA>());
                try
                {
                    Marshal.StructureToPtr(data, buf, false);
                    var q = new QUERYADAPTERINFO { hAdapter = h, Type = KMTQAITYPE_ADAPTERPERFDATA, pPrivateDriverData = buf, PrivateDriverDataSize = (uint)Marshal.SizeOf<ADAPTER_PERFDATA>() };
                    if (D3DKMTQueryAdapterInfo(ref q) != 0) continue;
                    data = Marshal.PtrToStructure<ADAPTER_PERFDATA>(buf);
                }
                finally { Marshal.FreeHGlobal(buf); }
                double c = data.Temperature / 10.0;
                if (c > 5 && c < 130) best = Math.Max(best, c);
            }
            catch { }
        }
        return best > 0 ? best : null;
    }

    /// <summary>Dedicated video memory in use, in bytes (all adapters), or null.</summary>
    public double? VramBytes() => _vram.Sample(max: true);

    public void Dispose()
    {
        foreach (var h in _adapters) try { var c = new CLOSEADAPTER { hAdapter = h }; D3DKMTCloseAdapter(ref c); } catch { }
        _adapters.Clear();
        _vram.Dispose();
    }
}

/// <summary>One wildcard performance counter, summed (or the largest instance) each time it's sampled.</summary>
public sealed class PdhSum : IDisposable
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

    public PdhSum(string path)
    {
        try
        {
            _ok = PdhOpenQuery(null, IntPtr.Zero, out _query) == 0 && PdhAddEnglishCounter(_query, path, IntPtr.Zero, out _counter) == 0;
            if (_ok) PdhCollectQueryData(_query);
        }
        catch { _ok = false; }
    }

    public double? Sample(bool max = false)
    {
        if (!_ok) return null;
        try
        {
            if (PdhCollectQueryData(_query) != 0) return null;
            uint bytes = 0, count = 0;
            if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bytes, ref count, IntPtr.Zero) != PDH_MORE_DATA) return null;
            var buf = Marshal.AllocHGlobal((int)bytes);
            try
            {
                if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref bytes, ref count, buf) != 0) return null;
                int size = IntPtr.Size + 16;
                double total = 0, top = 0;
                for (int i = 0; i < count; i++)
                {
                    var item = buf + i * size;
                    if (Marshal.ReadInt32(item + IntPtr.Size) != 0) continue;
                    double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + IntPtr.Size + 8));
                    total += v; top = Math.Max(top, v);
                }
                return max ? top : total;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }
}
