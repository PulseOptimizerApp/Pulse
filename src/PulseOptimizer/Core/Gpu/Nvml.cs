using System.Runtime.InteropServices;
using System.Text;

namespace Pulse.Core.Gpu;

/// <summary>
/// NVIDIA's own management library (NVML, nvml.dll), which ships with every NVIDIA driver. It's the documented,
/// supported way to read a GeForce card's clocks, power and temperature and to set its power limit and clock offsets.
/// It's the same interface nvidia-smi uses. No kernel driver of Pulse's own, nothing injected into games. Setting
/// values needs admin rights, which Pulse already has. Everything is reset by the driver on restart.
/// </summary>
public static class Nvml
{
    const string Dll = "nvml.dll";
    public const int Success = 0, NotSupported = 3, NoPermission = 4;

    [DllImport(Dll, EntryPoint = "nvmlInit_v2")] static extern int Init();
    [DllImport(Dll, EntryPoint = "nvmlShutdown")] static extern int ShutdownNative();
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCount_v2")] static extern int DeviceCount(out uint count);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] static extern int Handle(uint index, out IntPtr device);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetName")] static extern int Name(IntPtr d, byte[] name, uint length);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetTemperature")] internal static extern int Temperature(IntPtr d, int sensor, out uint c);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetClockInfo")] internal static extern int Clock(IntPtr d, int type, out uint mhz);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMaxClockInfo")] internal static extern int MaxClock(IntPtr d, int type, out uint mhz);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerUsage")] internal static extern int PowerUsage(IntPtr d, out uint mw);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")] internal static extern int EnforcedLimit(IntPtr d, out uint mw);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints")] internal static extern int LimitRange(IntPtr d, out uint minMw, out uint maxMw);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementDefaultLimit")] internal static extern int DefaultLimit(IntPtr d, out uint mw);
    [DllImport(Dll, EntryPoint = "nvmlDeviceSetPowerManagementLimit")] internal static extern int SetLimit(IntPtr d, uint mw);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetUtilizationRates")] internal static extern int Utilization(IntPtr d, out Util u);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetFanSpeed")] internal static extern int FanSpeed(IntPtr d, out uint percent);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMemoryInfo")] internal static extern int Memory(IntPtr d, out MemInfo m);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetGpcClkVfOffset")] internal static extern int GetCoreOffset(IntPtr d, out int mhz);
    [DllImport(Dll, EntryPoint = "nvmlDeviceSetGpcClkVfOffset")] internal static extern int SetCoreOffset(IntPtr d, int mhz);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetGpcClkMinMaxVfOffset")] internal static extern int CoreOffsetRange(IntPtr d, out int min, out int max);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMemClkVfOffset")] internal static extern int GetMemOffset(IntPtr d, out int mhz);
    [DllImport(Dll, EntryPoint = "nvmlDeviceSetMemClkVfOffset")] internal static extern int SetMemOffset(IntPtr d, int mhz);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMemClkMinMaxVfOffset")] internal static extern int MemOffsetRange(IntPtr d, out int min, out int max);
    [DllImport(Dll, EntryPoint = "nvmlDeviceSetGpuLockedClocks")] internal static extern int LockClocks(IntPtr d, uint minMhz, uint maxMhz);
    [DllImport(Dll, EntryPoint = "nvmlDeviceResetGpuLockedClocks")] internal static extern int ResetLockedClocks(IntPtr d);
    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")] internal static extern int ThrottleReasons(IntPtr d, out ulong reasons);

    [StructLayout(LayoutKind.Sequential)] internal struct Util { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] internal struct MemInfo { public ulong Total, Free, Used; }

    public const int ClockGraphics = 0, ClockMemory = 2;

    static readonly object Gate = new();
    static bool _tried, _ok;
    static IntPtr _device;
    static string _name;

    /// <summary>The first NVIDIA graphics card, or null when there isn't one (or its driver is too old for NVML).</summary>
    public static IntPtr? Device
    {
        get
        {
            lock (Gate)
            {
                if (_tried) return _ok ? _device : null;
                _tried = true;
                try
                {
                    if (!SystemInfo.HasNvidia) return null;
                    if (Init() != Success) return null;
                    if (DeviceCount(out var n) != Success || n == 0) return null;
                    if (Handle(0, out _device) != Success) return null;
                    var buf = new byte[96];
                    if (Name(_device, buf, (uint)buf.Length) == Success) _name = Encoding.ASCII.GetString(buf).TrimEnd('\0');
                    _ok = true;
                    return _device;
                }
                catch { return null; }   // nvml.dll missing (no NVIDIA driver)
            }
        }
    }

    public static bool Available => Device != null;
    public static string CardName => Device != null ? _name : null;

    public static void Shutdown()
    {
        lock (Gate) { if (_ok) try { ShutdownNative(); } catch { } _ok = false; _tried = false; }
    }

    /// <summary>A plain-words reason for a failed call.</summary>
    public static string Explain(int code) => code switch
    {
        Success => "",
        NotSupported => "This card or driver doesn't allow it.",
        NoPermission => "Windows didn't give Pulse permission (it needs to run as administrator).",
        2 => "That value is outside what the card allows.",
        _ => "The driver said no (code " + code + ").",
    };
}
