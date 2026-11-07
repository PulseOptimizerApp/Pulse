using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Pulse.Core.Devices;

public enum DeviceKind { Mouse, Keyboard }

/// <summary>A connected mouse or keyboard (several Windows entries for one device are merged).</summary>
public sealed class GearDevice
{
    public DeviceKind Kind { get; set; }
    public string Name { get; set; }
    public string Maker { get; set; }
    public string Vid { get; init; }
    public string Pid { get; init; }
    /// <summary>Raw input handles that belong to this device (a mouse can show up as several).</summary>
    public List<IntPtr> Handles { get; } = new();
    /// <summary>What the device said about itself over its maker's protocol (Logitech HID++), when it answered.</summary>
    public LogiInfo Logi { get; set; }
    public int? Battery => Logi?.Battery;
    public bool Charging => Logi?.Charging == true;
    public string Key => $"{Kind}-{Vid}-{Pid}-{Name}";
    public bool Wireless => Logi != null ? Logi.Index != 0xFF : Name.Contains("receiver", StringComparison.OrdinalIgnoreCase) || Name.Contains("wireless", StringComparison.OrdinalIgnoreCase)
                            || Name.Contains("lightspeed", StringComparison.OrdinalIgnoreCase) || Name.Contains("dongle", StringComparison.OrdinalIgnoreCase);
    /// <summary>The maker's own app, for settings stored on the device itself (onboard DPI, RGB, button remaps).</summary>
    public string MakerApp => Makers.App(Vid);
}

/// <summary>Lists mice and keyboards with Windows raw input and asks each one for its product name (HID).</summary>
public static class InputDevices
{
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] list, ref uint count, uint size);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, StringBuilder data, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(Microsoft.Win32.SafeHandles.SafeFileHandle h, StringBuilder buf, int len);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetManufacturerString(Microsoft.Win32.SafeHandles.SafeFileHandle h, StringBuilder buf, int len);
    const uint RIDI_DEVICENAME = 0x20000007, RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1;

    public static List<GearDevice> List()
    {
        var found = new Dictionary<string, GearDevice>();
        uint count = 0, size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(null, ref count, size);
        if (count == 0) return new();
        var list = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(list, ref count, size) == uint.MaxValue) return new();
        foreach (var d in list.Take((int)count))
        {
            if (d.dwType != RIM_TYPEMOUSE && d.dwType != RIM_TYPEKEYBOARD) continue;
            uint len = 0;
            GetRawInputDeviceInfo(d.hDevice, RIDI_DEVICENAME, null, ref len);
            if (len == 0) continue;
            var sb = new StringBuilder((int)len + 1);
            if (GetRawInputDeviceInfo(d.hDevice, RIDI_DEVICENAME, sb, ref len) == uint.MaxValue) continue;
            var path = sb.ToString();
            var m = System.Text.RegularExpressions.Regex.Match(path, @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) continue; // remote desktop and other virtual devices
            var kind = d.dwType == RIM_TYPEMOUSE ? DeviceKind.Mouse : DeviceKind.Keyboard;
            string vid = m.Groups[1].Value.ToUpperInvariant(), pid = m.Groups[2].Value.ToUpperInvariant();
            var key = $"{kind}:{vid}:{pid}";
            if (!found.TryGetValue(key, out var dev))
            {
                var (product, maker) = HidStrings(path);
                dev = new GearDevice
                {
                    Kind = kind, Vid = vid, Pid = pid,
                    Maker = Makers.Name(vid) ?? (string.IsNullOrWhiteSpace(maker) ? null : maker.Trim()),
                    Name = string.IsNullOrWhiteSpace(product) ? (kind == DeviceKind.Mouse ? "Mouse" : "Keyboard") : product.Trim(),
                };
                found[key] = dev;
            }
            dev.Handles.Add(d.hDevice);
        }
        // A receiver or keyboard can also report as a mouse (and the other way round): if a device is both, keep the
        // entry that matches its name.
        return found.Values.OrderBy(d => d.Kind).ThenBy(d => d.Name).ToList();
    }

    /// <summary>
    /// Adds what Logitech devices said over HID++ (real model name, battery...). A wireless receiver shows up in
    /// Windows as both a mouse and a keyboard; the entry that doesn't match the paired device is dropped.
    /// </summary>
    public static List<GearDevice> Merge(List<GearDevice> devices, List<LogiInfo> logi)
    {
        var claimed = new HashSet<string>();
        foreach (var l in logi)
        {
            var kind = l.IsKeyboard ? DeviceKind.Keyboard : DeviceKind.Mouse;
            var same = devices.Where(d => d.Vid == l.Vid && d.Pid == l.Pid && d.Logi == null).ToList();
            var target = same.FirstOrDefault(d => d.Kind == kind) ?? same.FirstOrDefault();
            if (target == null)
            {
                target = new GearDevice { Kind = kind, Vid = l.Vid, Pid = l.Pid };
                devices.Add(target);
            }
            target.Logi = l;
            target.Name = l.Name;
            target.Maker = "Logitech";
            claimed.Add(l.Vid + l.Pid);
        }
        devices.RemoveAll(d => d.Logi == null && claimed.Contains(d.Vid + d.Pid));
        return devices.OrderBy(d => d.Kind).ThenBy(d => d.Name).ToList();
    }

    static (string Product, string Maker) HidStrings(string path)
    {
        try
        {
            // Access 0: only asks the device for its description, never reads its input.
            using var h = CreateFile(path, 0, 3 /* share read/write */, IntPtr.Zero, 3 /* open existing */, 0, IntPtr.Zero);
            if (h.IsInvalid) return (null, null);
            var p = new StringBuilder(256);
            var mk = new StringBuilder(256);
            return (HidD_GetProductString(h, p, p.Capacity * 2) ? p.ToString() : null, HidD_GetManufacturerString(h, mk, mk.Capacity * 2) ? mk.ToString() : null);
        }
        catch { return (null, null); }
    }
}

/// <summary>USB vendor ids of gaming brands, and the app each one uses for on-device settings.</summary>
public static class Makers
{
    static readonly Dictionary<string, (string Name, string App)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["046D"] = ("Logitech", "Logitech G HUB"), ["1532"] = ("Razer", "Razer Synapse"), ["1038"] = ("SteelSeries", "SteelSeries GG"),
        ["1B1C"] = ("Corsair", "Corsair iCUE"), ["0951"] = ("HyperX", "HyperX NGENUITY"), ["03F0"] = ("HP / HyperX", "HyperX NGENUITY"),
        ["31E3"] = ("Wooting", "Wootility"), ["3434"] = ("Keychron", "Keychron Launcher"), ["0B05"] = ("ASUS", "Armoury Crate"),
        ["1E7D"] = ("ROCCAT / Turtle Beach", "Turtle Beach Swarm II"), ["2516"] = ("Cooler Master", "MasterPlus+"), ["045E"] = ("Microsoft", null),
        ["05AC"] = ("Apple", null), ["04D9"] = ("Ducky / Holtek", null), ["3367"] = ("Endgame Gear", "Endgame Gear software"),
        ["3554"] = ("Pulsar / Lamzu / VXE", "the maker's web driver"), ["361D"] = ("Finalmouse", "Finalmouse web app"), ["258A"] = ("Glorious / SinoWealth", "Glorious CORE"),
        ["320F"] = ("Glorious", "Glorious CORE"), ["1915"] = ("Nordic (wireless)", null), ["24AE"] = ("Rapoo", null), ["3151"] = ("Akko / MonsGeek", "the maker's web driver"),
        ["093A"] = ("PixArt sensor (generic)", null), ["0D8C"] = ("C-Media (USB audio)", null), ["17EF"] = ("Lenovo", "Lenovo Legion Space"),
        ["0738"] = ("Mad Catz", null), ["1EA7"] = ("SHARKOON / generic", null), ["2EA8"] = ("Zowie (BenQ)", null),
    };

    public static string Name(string vid) => vid != null && Known.TryGetValue(vid, out var k) ? k.Name : null;
    public static string App(string vid) => vid != null && Known.TryGetValue(vid, out var k) ? k.App : null;
}

/// <summary>
/// Listens to mouse movement (raw input, the same way games read the mouse) and reports, per device, how often it
/// reports (polling rate) and how many counts it moved (for the DPI check). Use on the UI thread; costs nothing
/// once disposed. Shares raw input with Macros the same way the Measure page's polling test does.
/// </summary>
public sealed class MouseMeter : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr handle, uint command, byte[] data, ref uint size, uint headerSize);
    const int WM_INPUT = 0x00FF;
    const uint RIDEV_INPUTSINK = 0x100, RIDEV_REMOVE = 0x1, RID_INPUT = 0x10000003;

    public sealed class Stats
    {
        public long Reports;
        public long CountX, CountY;     // signed sum (DPI check moves one way)
        public readonly List<double> Stamps = new();
        public double LastAt;
    }

    readonly HwndSource _source;
    readonly Stopwatch _sw = Stopwatch.StartNew();
    readonly byte[] _buf = new byte[64];
    public Dictionary<IntPtr, Stats> Devices { get; } = new();

    public MouseMeter()
    {
        _source = new HwndSource(new HwndSourceParameters("PulseDeviceMeter") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(Hook);
        var dev = new[] { new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = _source.Handle } };
        if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            throw new InvalidOperationException("Couldn't listen to the mouse (error " + Marshal.GetLastWin32Error() + ").");
        Pulse.Core.Input.PhysicalInput.OtherStarted();
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT) return IntPtr.Zero;
        Pulse.Core.Input.PhysicalInput.FromOtherWindow(lParam);
        int header = IntPtr.Size == 8 ? 24 : 16;
        uint size = (uint)_buf.Length;
        if (GetRawInputData(lParam, RID_INPUT, _buf, ref size, (uint)header) == uint.MaxValue || size < header + 20) return IntPtr.Zero;
        if (BitConverter.ToUInt32(_buf, 0) != 0) return IntPtr.Zero; // not a mouse
        var dev = IntPtr.Size == 8 ? (IntPtr)BitConverter.ToInt64(_buf, 8) : (IntPtr)BitConverter.ToInt32(_buf, 8);
        ushort flags = BitConverter.ToUInt16(_buf, header);
        int dx = BitConverter.ToInt32(_buf, header + 12), dy = BitConverter.ToInt32(_buf, header + 16);
        if ((flags & 1) != 0 || (dx == 0 && dy == 0)) return IntPtr.Zero; // absolute (tablets, remote) or button only
        if (!Devices.TryGetValue(dev, out var s)) Devices[dev] = s = new Stats();
        double now = _sw.Elapsed.TotalMilliseconds;
        s.Reports++; s.CountX += dx; s.CountY += dy; s.LastAt = now;
        s.Stamps.Add(now);
        if (s.Stamps.Count > 4000) s.Stamps.RemoveRange(0, 2000);
        return IntPtr.Zero;
    }

    public double Now => _sw.Elapsed.TotalMilliseconds;

    /// <summary>Reports per second over the last moment of steady movement (pauses ignored), or 0 if it hasn't moved enough.</summary>
    public static double Rate(Stats s, double now)
    {
        var recent = s.Stamps.Where(t => now - t < 1000).ToList();
        if (recent.Count < 40) return 0;
        var gaps = recent.Zip(recent.Skip(1), (a, b) => b - a).Where(g => g < 20).OrderBy(g => g).ToList();
        if (gaps.Count < 30) return 0;
        double median = gaps[gaps.Count / 2];
        return median > 0 ? 1000 / median : 0;
    }

    public void Reset() { foreach (var s in Devices.Values) { s.CountX = s.CountY = 0; s.Reports = 0; } }

    public void Dispose()
    {
        try
        {
            var dev = new[] { new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero } };
            RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }
        catch { }
        Pulse.Core.Input.PhysicalInput.OtherStopped();
        _source.RemoveHook(Hook);
        _source.Dispose();
    }

    /// <summary>The usual polling rates; a measured rate is shown as the nearest one.</summary>
    public static int Nearest(double hz)
    {
        int[] rates = { 125, 250, 500, 1000, 2000, 4000, 8000 };
        return rates.OrderBy(r => Math.Abs(Math.Log(hz / r))).First();
    }
}

/// <summary>A speaker, headset or microphone Windows knows about (read from Windows' own audio settings).</summary>
public sealed class AudioDevice
{
    public bool Input { get; init; }
    public string Name { get; init; }
    public string Adapter { get; init; }
    public bool IsDefault { get; set; }
    public int SampleRate { get; init; }
    public int Bits { get; init; }
    public int Channels { get; init; }
    /// <summary>A Bluetooth headset in call mode: mono, low-quality sound while its mic is used.</summary>
    public bool HandsFree => (Name + " " + Adapter).Contains("Hands-Free", StringComparison.OrdinalIgnoreCase);
    public string Format => SampleRate <= 0 ? "" : $"{SampleRate / 1000.0:0.#} kHz · {Bits}-bit · " + (Channels switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", _ => Channels + " ch" });
}

public static class AudioDevices
{
    const string Root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";
    const string PKeyDesc = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";          // "Headphones"
    const string PKeyAdapter = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";       // "Arctis Nova Pro Wireless"
    const string PKeyFormat = "{f19f064d-082c-4e27-bc73-6882a1bb8e4c},0";        // the format Windows mixes at

    /// <summary>Active playback and recording devices; the default ones are marked.</summary>
    public static List<AudioDevice> List()
    {
        var all = new List<AudioDevice>();
        foreach (var (kind, input) in new[] { ("Render", false), ("Capture", true) })
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{Root}\{kind}");
            if (key == null) continue;
            var list = new List<(AudioDevice Dev, long Chosen)>();
            foreach (var id in key.GetSubKeyNames())
            {
                try
                {
                    using var dk = key.OpenSubKey(id);
                    if (dk == null || Convert.ToInt32(dk.GetValue("DeviceState", 0)) != 1) continue; // 1 = active
                    using var props = dk.OpenSubKey("Properties");
                    string desc = props?.GetValue(PKeyDesc) as string, adapter = props?.GetValue(PKeyAdapter) as string;
                    int rate = 0, bits = 0, ch = 0;
                    if (props?.GetValue(PKeyFormat) is byte[] f && f.Length >= 24)
                    {
                        // a PROPVARIANT-wrapped WAVEFORMATEX: the format starts after an 8-byte header
                        int o = 8;
                        ch = BitConverter.ToUInt16(f, o + 2);
                        rate = (int)BitConverter.ToUInt32(f, o + 4);
                        bits = BitConverter.ToUInt16(f, o + 14);
                        if (f.Length >= o + 20 && BitConverter.ToUInt16(f, o + 16) >= 22) bits = BitConverter.ToUInt16(f, o + 18) is var vb && vb > 0 ? vb : bits;
                    }
                    // Windows stamps the time a device was made the default (Role:0 = console, Role:2 = communications).
                    long chosen = 0;
                    foreach (var role in new[] { "Role:0", "Role:1" })
                        if (dk.GetValue(role) is byte[] t && t.Length >= 16) chosen = Math.Max(chosen, Stamp(t));
                    list.Add((new AudioDevice { Input = input, Name = desc ?? "Audio device", Adapter = adapter ?? "", SampleRate = rate, Bits = bits, Channels = ch }, chosen));
                }
                catch { }
            }
            if (list.Count > 0) list.OrderByDescending(x => x.Chosen).First().Dev.IsDefault = true;
            all.AddRange(list.Select(x => x.Dev).OrderByDescending(d => d.IsDefault).ThenBy(d => d.Adapter));
        }
        return all;
    }

    static long Stamp(byte[] st)
    {
        // SYSTEMTIME: year, month, day-of-week, day, hour, minute, second, ms (all 16-bit)
        try
        {
            var d = new DateTime(BitConverter.ToUInt16(st, 0), BitConverter.ToUInt16(st, 2), BitConverter.ToUInt16(st, 6),
                BitConverter.ToUInt16(st, 8), BitConverter.ToUInt16(st, 10), BitConverter.ToUInt16(st, 12), BitConverter.ToUInt16(st, 14));
            return d.Ticks;
        }
        catch { return 0; }
    }
}

/// <summary>
/// Remaps keyboard keys for every app and game with Windows' own "Scancode Map" (the same thing Microsoft
/// PowerToys' remapper can't do for games, because it works in software). Takes effect after signing out or
/// restarting. The value before Pulse's first change is backed up and can be put back.
/// </summary>
public static class KeyRemap
{
    public const string KeyPath = @"SYSTEM\CurrentControlSet\Control\Keyboard Layout";
    public const string ValueName = "Scancode Map";

    /// <summary>One remap: pressing <see cref="From"/> acts like <see cref="To"/> (0 = the key does nothing). Codes include 0xE000 for extended keys.</summary>
    public readonly record struct Map(ushort From, ushort To);

    public static List<Map> Read()
    {
        var list = new List<Map>();
        if (Reg.Get(RegistryHive.LocalMachine, KeyPath, ValueName) is not byte[] b || b.Length < 16) return list;
        int n = (int)BitConverter.ToUInt32(b, 8) - 1;
        for (int i = 0; i < n && 12 + i * 4 + 4 <= b.Length; i++)
        {
            ushort to = BitConverter.ToUInt16(b, 12 + i * 4), from = BitConverter.ToUInt16(b, 14 + i * 4);
            if (from != 0) list.Add(new Map(from, to));
        }
        return list;
    }

    public static byte[] Encode(IReadOnlyList<Map> maps)
    {
        var b = new byte[16 + maps.Count * 4];
        BitConverter.GetBytes((uint)(maps.Count + 1)).CopyTo(b, 8);
        for (int i = 0; i < maps.Count; i++)
        {
            BitConverter.GetBytes(maps[i].To).CopyTo(b, 12 + i * 4);
            BitConverter.GetBytes(maps[i].From).CopyTo(b, 14 + i * 4);
        }
        return b; // ends with four zero bytes
    }

    /// <summary>Writes the remaps (backing up the original value the first time). An empty list removes the value.</summary>
    public static void Write(IReadOnlyList<Map> maps, DeviceSettings s)
    {
        s.ScancodeBackup ??= Reg.Snapshot(RegistryHive.LocalMachine, KeyPath, ValueName);
        if (maps.Count == 0) Reg.Delete(RegistryHive.LocalMachine, KeyPath, ValueName);
        else Reg.Set(RegistryHive.LocalMachine, KeyPath, ValueName, Encode(maps), RegistryValueKind.Binary);
        s.RemapPending = true;
    }

    /// <summary>Puts back what was there before Pulse's first remap.</summary>
    public static void Restore(DeviceSettings s)
    {
        if (s.ScancodeBackup != null) Reg.Restore(s.ScancodeBackup);
        else Reg.Delete(RegistryHive.LocalMachine, KeyPath, ValueName);
        s.ScancodeBackup = null;
        s.RemapPending = true;
    }

    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetKeyNameText(int lParam, StringBuilder name, int size);

    /// <summary>The scan code (with 0xE000 for extended keys) of a virtual key.</summary>
    public static ushort ScanOf(int vk)
    {
        uint sc = MapVirtualKey((uint)vk, 4 /* MAPVK_VK_TO_VSC_EX */);
        if (sc == 0) return 0;
        if ((sc & 0xFF00) == 0xE100) return 0; // Pause: can't be remapped this way
        return (ushort)sc;
    }

    public static string NameOf(ushort scan)
    {
        if (scan == 0) return Pulse.Ui.Loc.T("Nothing (turned off)");
        int lParam = (scan & 0xFF) << 16 | ((scan & 0xE000) == 0xE000 ? 1 << 24 : 0);
        var sb = new StringBuilder(64);
        return GetKeyNameText(lParam, sb, sb.Capacity) > 0 ? sb.ToString() : "0x" + scan.ToString("X4", CultureInfo.InvariantCulture);
    }

    /// <summary>Common one-click remaps.</summary>
    public static readonly (string Name, Map[] Maps)[] Presets =
    {
        ("Turn off the Windows key", new[] { new Map(0xE05B, 0), new Map(0xE05C, 0) }),
        ("Caps Lock works as Ctrl", new[] { new Map(0x003A, 0x001D) }),
        ("Turn off Caps Lock", new[] { new Map(0x003A, 0) }),
        ("Turn off the menu key", new[] { new Map(0xE05D, 0) }),
    };
}

/// <summary>Key repeat: how long a held key waits before repeating (0–3) and how fast it repeats (0–31). Instant, no restart.</summary>
public static class KeyRepeat
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);
    const uint SPI_GETKEYBOARDDELAY = 0x16, SPI_SETKEYBOARDDELAY = 0x17, SPI_GETKEYBOARDSPEED = 0x0A, SPI_SETKEYBOARDSPEED = 0x0B, SPIF_SAVE = 0x3;

    public static int Delay { get { int v = 1; SystemParametersInfo(SPI_GETKEYBOARDDELAY, 0, ref v, 0); return v; } }
    public static int Speed { get { int v = 31; SystemParametersInfo(SPI_GETKEYBOARDSPEED, 0, ref v, 0); return v; } }

    public static void Set(int delay, int speed, DeviceSettings s)
    {
        s.OriginalKeyDelay ??= Delay;
        s.OriginalKeySpeed ??= Speed;
        SystemParametersInfo(SPI_SETKEYBOARDDELAY, (uint)Math.Clamp(delay, 0, 3), IntPtr.Zero, SPIF_SAVE);
        SystemParametersInfo(SPI_SETKEYBOARDSPEED, (uint)Math.Clamp(speed, 0, 31), IntPtr.Zero, SPIF_SAVE);
    }

    /// <summary>Delay steps are about 250 ms each (0 = 250 ms … 3 = 1 s).</summary>
    public static int DelayMs(int step) => (step + 1) * 250;
    /// <summary>Speed 0–31 is about 2.5 to 30 repeats a second.</summary>
    public static double RepeatsPerSecond(int speed) => 2.5 + speed * (30 - 2.5) / 31;
}

/// <summary>
/// The default microphone's level, for a live meter. Uses Windows' basic recording API with small buffers;
/// nothing is saved or sent. Poll <see cref="Read"/> about 20 times a second.
/// </summary>
public sealed class MicMeter : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }
    [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, uint device, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, uint size);
    [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);
    const uint WAVE_MAPPER = uint.MaxValue, WHDR_DONE = 1;
    const int Buffers = 4, BufBytes = 1600; // 50 ms of 16 kHz mono 16-bit each

    IntPtr _h;
    readonly IntPtr[] _hdrs = new IntPtr[Buffers];
    readonly int _hdrSize = Marshal.SizeOf<WAVEHDR>();
    public bool Ok { get; }

    public MicMeter()
    {
        var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = 16000, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = 32000 };
        if (waveInOpen(out _h, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) return;
        for (int i = 0; i < Buffers; i++)
        {
            var hdr = new WAVEHDR { lpData = Marshal.AllocHGlobal(BufBytes), dwBufferLength = BufBytes };
            _hdrs[i] = Marshal.AllocHGlobal(_hdrSize);
            Marshal.StructureToPtr(hdr, _hdrs[i], false);
            waveInPrepareHeader(_h, _hdrs[i], (uint)_hdrSize);
            waveInAddBuffer(_h, _hdrs[i], (uint)_hdrSize);
        }
        Ok = waveInStart(_h) == 0;
    }

    /// <summary>The loudest sample since the last call, 0–1 (0 when nothing new arrived).</summary>
    public double Read()
    {
        if (_h == IntPtr.Zero) return 0;
        double peak = 0;
        foreach (var p in _hdrs)
        {
            var hdr = Marshal.PtrToStructure<WAVEHDR>(p);
            if ((hdr.dwFlags & WHDR_DONE) == 0) continue;
            int n = (int)hdr.dwBytesRecorded / 2;
            for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(Marshal.ReadInt16(hdr.lpData, i * 2) / 32768.0));
            waveInUnprepareHeader(_h, p, (uint)_hdrSize);
            hdr.dwFlags = 0; hdr.dwBytesRecorded = 0;
            Marshal.StructureToPtr(hdr, p, false);
            waveInPrepareHeader(_h, p, (uint)_hdrSize);
            waveInAddBuffer(_h, p, (uint)_hdrSize);
        }
        return Math.Min(1, peak);
    }

    public void Dispose()
    {
        if (_h == IntPtr.Zero) return;
        waveInReset(_h);
        foreach (var p in _hdrs)
        {
            if (p == IntPtr.Zero) continue;
            waveInUnprepareHeader(_h, p, (uint)_hdrSize);
            var hdr = Marshal.PtrToStructure<WAVEHDR>(p);
            Marshal.FreeHGlobal(hdr.lpData);
            Marshal.FreeHGlobal(p);
        }
        waveInClose(_h);
        _h = IntPtr.Zero;
    }
}

/// <summary>The Devices page's saved state: backups of what it changed.</summary>
public sealed class DeviceSettings
{
    /// <summary>The "Scancode Map" value before Pulse's first key remap.</summary>
    public Pulse.Core.ActionBackup ScancodeBackup { get; set; }
    /// <summary>A remap changed since the last restart (Windows applies them at sign-in).</summary>
    public bool RemapPending { get; set; }
    public int? OriginalKeyDelay { get; set; }
    public int? OriginalKeySpeed { get; set; }
    /// <summary>Device → the photo the user picked for it (a file in Pulse's data folder).</summary>
    public Dictionary<string, string> Photos { get; set; } = new();
}
