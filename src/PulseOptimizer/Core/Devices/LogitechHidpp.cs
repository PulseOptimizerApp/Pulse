using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pulse.Core.Devices;

/// <summary>What a Logitech device told Pulse about itself over HID++.</summary>
public sealed class LogiInfo
{
    public string Name { get; set; }
    /// <summary>HID++ device type: 0 keyboard, 1 remote, 2 numpad, 3 mouse, 4 touchpad, 5 trackball, 6 presenter, 8 headset.</summary>
    public int Type { get; set; } = -1;
    public bool IsMouse => Type is 3 or 4 or 5;
    public bool IsKeyboard => Type is 0 or 2;
    public int? Battery { get; set; }
    public bool Charging { get; set; }
    public int? Dpi { get; set; }
    public List<int> DpiList { get; set; } = new();
    public int? RateHz { get; set; }
    public List<int> Rates { get; set; } = new();
    public string Vid { get; init; }
    public string Pid { get; init; }
    /// <summary>The HID path of the long-report collection and the device's slot on it (1–6 on a receiver, 0xFF wired).</summary>
    internal string Path { get; init; }
    internal byte Index { get; init; }
    internal Dictionary<ushort, byte> Features { get; } = new();
}

/// <summary>
/// Talks to Logitech mice and keyboards with HID++ 2.0, Logitech's own protocol (the one G HUB and the open-source
/// Solaar use): reads the real model name, battery, DPI and report rate, and can set DPI and report rate. Uses the
/// device's vendor channel only (usage page 0xFF00); it never reads what you type or how you move.
/// Every request waits at most a moment, so an asleep or out-of-range device just shows less.
/// </summary>
public static class LogitechHidpp
{
    const ushort Root = 0x0000, DeviceName = 0x0005, BatteryStatus = 0x1000, UnifiedBattery = 0x1004,
        AdjustableDpi = 0x2201, ReportRate = 0x8060, ExtendedReportRate = 0x8061;
    const byte SwId = 0x0A, LongReport = 0x11;

    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] list, ref uint count, uint size);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, StringBuilder data, ref uint size);
    [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, byte[] data, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    const uint RIDI_DEVICENAME = 0x20000007, RIDI_DEVICEINFO = 0x2000000b, RIM_TYPEHID = 2;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_FLAG_OVERLAPPED = 0x40000000;

    /// <summary>Logitech HID++ channels (vendor page 0xFF00, long reports) currently connected.</summary>
    static List<(string Path, string Vid, string Pid)> Channels()
    {
        var found = new List<(string, string, string)>();
        uint count = 0, size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(null, ref count, size);
        if (count == 0) return found;
        var list = new RAWINPUTDEVICELIST[count];
        if (GetRawInputDeviceList(list, ref count, size) == uint.MaxValue) return found;
        foreach (var d in list.Take((int)count))
        {
            if (d.dwType != RIM_TYPEHID) continue;
            var info = new byte[32];
            uint isz = (uint)info.Length;
            BitConverter.GetBytes(isz).CopyTo(info, 0);
            if (GetRawInputDeviceInfo(d.hDevice, RIDI_DEVICEINFO, info, ref isz) == uint.MaxValue) continue;
            // RID_DEVICE_INFO: cbSize, dwType, then for HID: vendor, product, version, usage page, usage
            uint vendor = BitConverter.ToUInt32(info, 8), product = BitConverter.ToUInt32(info, 12);
            ushort page = BitConverter.ToUInt16(info, 20), usage = BitConverter.ToUInt16(info, 22);
            if (vendor != 0x046D || page != 0xFF00 || usage != 0x0002) continue;
            uint len = 0;
            GetRawInputDeviceInfo(d.hDevice, RIDI_DEVICENAME, (StringBuilder)null, ref len);
            if (len == 0) continue;
            var sb = new StringBuilder((int)len + 1);
            if (GetRawInputDeviceInfo(d.hDevice, RIDI_DEVICENAME, sb, ref len) == uint.MaxValue) continue;
            found.Add((sb.ToString(), "046D", product.ToString("X4")));
        }
        return found;
    }

    /// <summary>Every Logitech HID++ 2.0 device that answers (receivers are asked about each paired slot).</summary>
    public static async Task<List<LogiInfo>> Scan()
    {
        var all = new List<LogiInfo>();
        foreach (var (path, vid, pid) in Channels())
        {
            try
            {
                using var ch = Open(path);
                if (ch == null) continue;
                foreach (byte index in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 })
                {
                    var ping = await ch.Request(index, 0, 0x10, 0, 0, 0x5A);
                    if (ping == null || ping[4] < 2) continue; // no device there, or an old HID++ 1.0 one
                    var info = new LogiInfo { Path = path, Index = index, Vid = vid, Pid = pid };
                    await Read(ch, info);
                    if (info.Name != null && !all.Any(a => a.Name == info.Name && a.Pid == info.Pid)) all.Add(info);
                    if (index == 0xFF) break; // wired / Bluetooth: one device on this channel
                }
            }
            catch { }
        }
        return all;
    }

    static async Task<byte> Feature(Channel ch, LogiInfo info, ushort id)
    {
        if (info.Features.TryGetValue(id, out var i)) return i;
        var r = await ch.Request(info.Index, 0, 0x00, (byte)(id >> 8), (byte)id);
        i = r == null ? (byte)0 : r[4];
        info.Features[id] = i;
        return i;
    }

    static async Task Read(Channel ch, LogiInfo info)
    {
        // Name: length, then 16 characters at a time.
        var f = await Feature(ch, info, DeviceName);
        if (f != 0 && await ch.Request(info.Index, f, 0x00) is { } lenReply)
        {
            int len = lenReply[4];
            var name = new List<byte>();
            while (name.Count < len)
            {
                var part = await ch.Request(info.Index, f, 0x10, (byte)name.Count);
                if (part == null) break;
                name.AddRange(part.Skip(4).Take(Math.Min(16, len - name.Count)));
            }
            var s = Encoding.UTF8.GetString(name.ToArray()).TrimEnd('\0').Trim();
            if (s.Length > 0) info.Name = s;
            if (await ch.Request(info.Index, f, 0x20) is { } type) info.Type = type[4];
        }

        // Battery: the newer "unified" report, else the older one.
        if ((f = await Feature(ch, info, UnifiedBattery)) != 0 && await ch.Request(info.Index, f, 0x10) is { } ub)
        {
            info.Battery = ub[4] > 0 ? ub[4] : ub[5] switch { 8 => 100, 4 => 60, 2 => 20, 1 => 5, _ => null };
            info.Charging = ub[6] is 1 or 2;
        }
        else if ((f = await Feature(ch, info, BatteryStatus)) != 0 && await ch.Request(info.Index, f, 0x00) is { } bs)
        {
            if (bs[4] > 0) info.Battery = bs[4];
            info.Charging = bs[6] is 1 or 2;
        }

        // DPI: the current value and what the sensor allows.
        if ((f = await Feature(ch, info, AdjustableDpi)) != 0)
        {
            if (await ch.Request(info.Index, f, 0x20, 0) is { } d)
            {
                int dpi = d[5] << 8 | d[6];
                if (dpi == 0) dpi = d[7] << 8 | d[8];
                if (dpi > 0) info.Dpi = dpi;
            }
            var bytes = new List<byte>();
            for (byte part = 0; part < 8; part++)
            {
                var l = await ch.Request(info.Index, f, 0x10, 0, 0, part);
                if (l == null) break;
                bytes.AddRange(l.Skip(5));
                if (bytes.Count >= 2 && bytes[^1] == 0 && bytes[^2] == 0) break;
            }
            info.DpiList = DpiList(bytes);
        }

        // Report rate: 1–8 ms on most mice; the extended feature adds 2, 4 and 8 kHz.
        if ((f = await Feature(ch, info, ExtendedReportRate)) != 0)
        {
            int[] hz = { 125, 250, 500, 1000, 2000, 4000, 8000 };
            if (await ch.Request(info.Index, f, 0x20) is { } r && r[4] < hz.Length) info.RateHz = hz[r[4]];
            if (await ch.Request(info.Index, f, 0x10) is { } l)
            {
                int flags = l[4] << 8 | l[5];
                for (int i = 0; i < hz.Length; i++) if ((flags & 1 << i) != 0) info.Rates.Add(hz[i]);
                if (info.Rates.Count == 0) { flags = l[4] | l[5] << 8; for (int i = 0; i < hz.Length; i++) if ((flags & 1 << i) != 0) info.Rates.Add(hz[i]); }
            }
        }
        else if ((f = await Feature(ch, info, ReportRate)) != 0)
        {
            if (await ch.Request(info.Index, f, 0x10) is { } r && r[4] > 0) info.RateHz = 1000 / r[4];
            if (await ch.Request(info.Index, f, 0x00) is { } l)
                for (int i = 0; i < 8; i++) if ((l[4] & 1 << i) != 0) info.Rates.Add(1000 / (i + 1));
            info.Rates = info.Rates.Where(x => x is 125 or 250 or 500 or 1000).Distinct().OrderBy(x => x).ToList();
        }
        info.Rates = info.Rates.Distinct().OrderBy(x => x).ToList();
    }

    /// <summary>Turns the DPI list bytes into values: plain 16-bit values, or "step" markers (0xE000 | step) followed by the end of a range.</summary>
    internal static List<int> DpiList(IReadOnlyList<byte> b)
    {
        var list = new List<int>();
        for (int i = 0; i + 1 < b.Count;)
        {
            int v = b[i] << 8 | b[i + 1];
            if (v == 0) break;
            if (v >> 13 == 0b111 && i + 3 < b.Count && list.Count > 0)
            {
                int step = v & 0x1FFF, last = b[i + 2] << 8 | b[i + 3];
                for (int x = list[^1] + step; step > 0 && x <= last && list.Count < 2000; x += step) list.Add(x);
                i += 4;
            }
            else { list.Add(v); i += 2; }
        }
        return list;
    }

    /// <summary>Sets the DPI and reads it back. False if the mouse kept another value (its onboard profile is in charge).</summary>
    public static async Task<bool> SetDpi(LogiInfo info, int dpi)
    {
        using var ch = Open(info.Path);
        if (ch == null || !info.Features.TryGetValue(AdjustableDpi, out var f) || f == 0) return false;
        await ch.Request(info.Index, f, 0x30, 0, (byte)(dpi >> 8), (byte)dpi);
        var d = await ch.Request(info.Index, f, 0x20, 0);
        int now = d == null ? 0 : d[5] << 8 | d[6];
        if (now > 0) info.Dpi = now;
        return now == dpi;
    }

    /// <summary>Sets the report rate and reads it back.</summary>
    public static async Task<bool> SetRate(LogiInfo info, int hz)
    {
        using var ch = Open(info.Path);
        if (ch == null) return false;
        if (info.Features.TryGetValue(ExtendedReportRate, out var f) && f != 0)
        {
            int[] list = { 125, 250, 500, 1000, 2000, 4000, 8000 };
            int i = Array.IndexOf(list, hz);
            if (i < 0) return false;
            await ch.Request(info.Index, f, 0x30, (byte)i);
            var r = await ch.Request(info.Index, f, 0x20);
            if (r != null && r[4] < list.Length) info.RateHz = list[r[4]];
        }
        else if (info.Features.TryGetValue(ReportRate, out f) && f != 0)
        {
            await ch.Request(info.Index, f, 0x20, (byte)(1000 / hz));
            var r = await ch.Request(info.Index, f, 0x10);
            if (r != null && r[4] > 0) info.RateHz = 1000 / r[4];
        }
        return info.RateHz == hz;
    }

    static Channel Open(string path)
    {
        var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, 3, IntPtr.Zero, 3, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return new Channel(new FileStream(h, FileAccess.ReadWrite, 1, isAsync: true));
    }

    /// <summary>One open HID++ channel: send a 20-byte request, wait for its answer.</summary>
    sealed class Channel : IDisposable
    {
        readonly FileStream _fs;
        public Channel(FileStream fs) => _fs = fs;

        /// <summary>The 20-byte reply (parameters start at byte 4), or null on error or no answer within 700 ms.</summary>
        public async Task<byte[]> Request(byte index, byte feature, byte function, params byte[] args)
        {
            var req = new byte[20];
            req[0] = LongReport; req[1] = index; req[2] = feature; req[3] = (byte)(function | SwId);
            Array.Copy(args, 0, req, 4, Math.Min(args.Length, 16));
            using var cts = new CancellationTokenSource(700);
            try
            {
                await _fs.WriteAsync(req, cts.Token);
                var buf = new byte[64];
                while (!cts.IsCancellationRequested)
                {
                    int n = await _fs.ReadAsync(buf, cts.Token);
                    if (n < 7 || buf[1] != index) continue;
                    // HID++ 2.0 error (0xFF) or 1.0 error (0x8F) about this request
                    if ((buf[2] == 0xFF || buf[2] == 0x8F) && buf[3] == feature) return null;
                    if (buf[2] == feature && buf[3] == req[3]) return buf.Take(20).ToArray();
                }
            }
            catch { }
            return null;
        }

        public void Dispose() { try { _fs.Dispose(); } catch { } }
    }
}
