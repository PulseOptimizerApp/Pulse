using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Pulse.Core.Devices;

/// <summary>One HID top-level collection (Windows gives each its own path).</summary>
public sealed class HidCollection
{
    public string Path { get; init; }
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }
    public int InputLength { get; init; }
    public int OutputLength { get; init; }
    public int FeatureLength { get; init; }
    public List<byte> InputIds { get; } = new();
    public List<byte> OutputIds { get; } = new();
    public List<byte> FeatureIds { get; } = new();
}

/// <summary>A USB HID device as WebHID sees it: one per device interface, with all its collections.</summary>
public sealed class HidDeviceInfo
{
    public string Key { get; init; }
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public string ProductName { get; set; } = "";
    public List<HidCollection> Collections { get; } = new();
}

/// <summary>
/// Windows HID access for the mouse drivers: lists HID devices the way Chrome's WebHID groups them (one device per
/// USB interface, several collections), and sends and receives reports. Mouse and keyboard input collections are
/// listed but never opened (Windows keeps those to itself, and Pulse doesn't read your input).
/// </summary>
public static class NativeHid
{
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder b, int len);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("hid.dll")] static extern int HidP_GetButtonCaps(int type, byte[] caps, ref ushort len, IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort len, IntPtr data);
    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int required, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [StructLayout(LayoutKind.Sequential)] struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10, HIDP_STATUS_SUCCESS = 0x00110000, CapSize = 72;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_FLAG_OVERLAPPED = 0x40000000, OPEN_EXISTING = 3, SHARE_RW = 3;

    /// <summary>Input collections Windows reserves (mouse, keyboard, system controls): listed, never opened.</summary>
    static bool Protected(HidCollection c) => c.UsagePage == 0x01 && c.Usage is 0x02 or 0x06 or 0x80;

    public static List<HidDeviceInfo> List()
    {
        var byKey = new Dictionary<string, HidDeviceInfo>();
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return new();
        try
        {
            for (int i = 0; ; i++)
            {
                var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref d)) break;
                SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                if (size <= 0) continue;
                var buf = Marshal.AllocHGlobal(size);
                string path;
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, size, out _, IntPtr.Zero)) continue;
                    path = Marshal.PtrToStringUni(buf + 4);
                }
                finally { Marshal.FreeHGlobal(buf); }
                if (string.IsNullOrEmpty(path)) continue;
                try { Add(path, byKey); } catch { }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return byKey.Values.Where(v => v.Collections.Count > 0).ToList();
    }

    /// <summary>"…hid#vid_046d&amp;pid_c54d&amp;mi_02&amp;col01#7&amp;2c9b4b2b&amp;0&amp;0000#{…}" → one key for all collections of that interface.</summary>
    internal static string DeviceKey(string path)
    {
        var m = Regex.Match(path.ToLowerInvariant(), @"hid#(vid_[0-9a-f]{4}&pid_[0-9a-f]{4}(?:&mi_[0-9a-f]{2})?)(?:&col[0-9a-f]{2})?#([^#]+?)(?:&[0-9a-f]{4})?#");
        return m.Success ? m.Groups[1].Value + "#" + m.Groups[2].Value : path.ToLowerInvariant();
    }

    static void Add(string path, Dictionary<string, HidDeviceInfo> byKey)
    {
        using var h = CreateFile(path, 0, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return;
        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(h, ref attr)) return;
        if (!HidD_GetPreparsedData(h, out var pp)) return;
        HidCollection col;
        try
        {
            if (HidP_GetCaps(pp, out var caps) != HIDP_STATUS_SUCCESS) return;
            col = new HidCollection
            {
                Path = path, UsagePage = caps.UsagePage, Usage = caps.Usage,
                InputLength = caps.InputReportByteLength, OutputLength = caps.OutputReportByteLength, FeatureLength = caps.FeatureReportByteLength,
            };
            ReportIds(pp, 0, caps.NumberInputButtonCaps, caps.NumberInputValueCaps, col.InputIds);
            ReportIds(pp, 1, caps.NumberOutputButtonCaps, caps.NumberOutputValueCaps, col.OutputIds);
            ReportIds(pp, 2, caps.NumberFeatureButtonCaps, caps.NumberFeatureValueCaps, col.FeatureIds);
            // A report type with bytes but no caps listed still exists with id 0.
            if (col.InputLength > 0 && col.InputIds.Count == 0) col.InputIds.Add(0);
            if (col.OutputLength > 0 && col.OutputIds.Count == 0) col.OutputIds.Add(0);
            if (col.FeatureLength > 0 && col.FeatureIds.Count == 0) col.FeatureIds.Add(0);
        }
        finally { HidD_FreePreparsedData(pp); }

        var key = DeviceKey(path);
        if (!byKey.TryGetValue(key, out var dev))
            byKey[key] = dev = new HidDeviceInfo { Key = key, VendorId = attr.VendorID, ProductId = attr.ProductID };
        if (dev.ProductName.Length == 0)
        {
            var sb = new StringBuilder(128);
            if (HidD_GetProductString(h, sb, sb.Capacity * 2)) dev.ProductName = sb.ToString().Trim();
        }
        dev.Collections.Add(col);
    }

    static void ReportIds(IntPtr pp, int type, int buttons, int values, List<byte> into)
    {
        if (buttons > 0)
        {
            ushort n = (ushort)buttons;
            var b = new byte[n * CapSize];
            if (HidP_GetButtonCaps(type, b, ref n, pp) == HIDP_STATUS_SUCCESS)
                for (int i = 0; i < n; i++) into.Add(b[i * CapSize + 2]);
        }
        if (values > 0)
        {
            ushort n = (ushort)values;
            var b = new byte[n * CapSize];
            if (HidP_GetValueCaps(type, b, ref n, pp) == HIDP_STATUS_SUCCESS)
                for (int i = 0; i < n; i++) into.Add(b[i * CapSize + 2]);
        }
        var distinct = into.Distinct().OrderBy(x => x).ToList();
        into.Clear();
        into.AddRange(distinct);
    }

    // ───────── an open device ─────────

    /// <summary>Opens every collection of a device that Windows lets Pulse open, and reads its input reports.</summary>
    public sealed class Open : IDisposable
    {
        readonly HidDeviceInfo _info;
        readonly Dictionary<HidCollection, (SafeFileHandle Handle, FileStream Stream)> _open = new();
        readonly CancellationTokenSource _stop = new();

        /// <summary>(report id, data without the id) for every input report.</summary>
        public event Action<byte, byte[]> Input;

        public Open(HidDeviceInfo info)
        {
            _info = info;
            foreach (var c in info.Collections.Where(c => !Protected(c)))
            {
                var h = CreateFile(c.Path, GENERIC_READ | GENERIC_WRITE, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (h.IsInvalid) { h.Dispose(); h = CreateFile(c.Path, GENERIC_WRITE, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero); }
                if (h.IsInvalid) { h.Dispose(); continue; }
                FileStream fs = null;
                try { fs = new FileStream(h, FileAccess.ReadWrite, 1, isAsync: true); } catch { try { fs = new FileStream(h, FileAccess.Write, 1, isAsync: true); } catch { } }
                _open[c] = (h, fs);
                if (fs != null && fs.CanRead && c.InputLength > 0) _ = ReadLoop(c, fs);
            }
            if (_open.Count == 0) throw new InvalidOperationException("Windows didn't let Pulse open this device. Close the maker's app and try again.");
        }

        async Task ReadLoop(HidCollection c, FileStream fs)
        {
            var buf = new byte[Math.Max(c.InputLength, 2)];
            while (!_stop.IsCancellationRequested)
            {
                int n;
                try { n = await fs.ReadAsync(buf, _stop.Token); } catch { break; }
                if (n <= 0) break;
                try { Input?.Invoke(buf[0], buf.AsSpan(1, n - 1).ToArray()); } catch { }
            }
        }

        (HidCollection C, FileStream S, SafeFileHandle H) For(Func<HidCollection, List<byte>> ids, Func<HidCollection, int> length, byte reportId)
        {
            foreach (var (c, (h, s)) in _open)
                if (length(c) > 0 && ids(c).Contains(reportId)) return (c, s, h);
            foreach (var (c, (h, s)) in _open)
                if (length(c) > 0 && ids(c).Contains(0) && reportId == 0) return (c, s, h);
            throw new InvalidOperationException($"No open collection takes report {reportId}.");
        }

        public async Task SendOutput(byte reportId, byte[] data)
        {
            var (c, s, _) = For(x => x.OutputIds, x => x.OutputLength, reportId);
            if (s == null) throw new InvalidOperationException("Can't write to this collection.");
            var buf = new byte[c.OutputLength];
            buf[0] = reportId;
            Array.Copy(data, 0, buf, 1, Math.Min(data.Length, buf.Length - 1));
            using var cts = new CancellationTokenSource(3000);
            await s.WriteAsync(buf, cts.Token);
        }

        public Task SendFeature(byte reportId, byte[] data) => Task.Run(() =>
        {
            var (c, _, h) = For(x => x.FeatureIds, x => x.FeatureLength, reportId);
            var buf = new byte[c.FeatureLength];
            buf[0] = reportId;
            Array.Copy(data, 0, buf, 1, Math.Min(data.Length, buf.Length - 1));
            if (!HidD_SetFeature(h, buf, buf.Length)) throw new InvalidOperationException("The device didn't accept the feature report (error " + Marshal.GetLastWin32Error() + ").");
        });

        /// <summary>A feature report; like WebHID, it starts with its report id unless the id is 0.</summary>
        public Task<byte[]> GetFeature(byte reportId) => Task.Run(() =>
        {
            var (c, _, h) = For(x => x.FeatureIds, x => x.FeatureLength, reportId);
            var buf = new byte[c.FeatureLength];
            buf[0] = reportId;
            if (!HidD_GetFeature(h, buf, buf.Length)) throw new InvalidOperationException("The device didn't return the feature report (error " + Marshal.GetLastWin32Error() + ").");
            return reportId == 0 ? buf[1..] : buf;
        });

        public void Dispose()
        {
            _stop.Cancel();
            foreach (var (_, (h, s)) in _open) { try { s?.Dispose(); } catch { } try { h.Dispose(); } catch { } }
            _open.Clear();
        }
    }
}
