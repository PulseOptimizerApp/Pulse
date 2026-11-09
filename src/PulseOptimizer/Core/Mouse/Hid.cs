using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pulse.Core.Mouse;

/// <summary>
/// Gaming mice's settings channel, the way a browser's WebHID sees it, from Windows' own HID functions. Windows lists
/// each "top-level collection" of a USB interface as its own device path; WebHID groups them back into one device with
/// several collections, and so does this, so OpenMouse's drivers see exactly what they'd see in Chrome. Reports are
/// sent to the collection that declares that report ID.
/// </summary>
public static class Hid
{
    public sealed record Report(byte Id, int Length);

    public sealed class Collection
    {
        public string Path { get; init; } = "";
        public ushort UsagePage { get; init; }
        public ushort Usage { get; init; }
        public int InputLength { get; init; }
        public int OutputLength { get; init; }
        public int FeatureLength { get; init; }
        public List<byte> InputIds { get; } = new();
        public List<byte> OutputIds { get; } = new();
        public List<byte> FeatureIds { get; } = new();
    }

    /// <summary>One USB interface of a device (what WebHID calls a HIDDevice).</summary>
    public sealed class DeviceInfo
    {
        public string Key { get; init; } = "";
        public ushort VendorId { get; init; }
        public ushort ProductId { get; init; }
        public string ProductName { get; init; } = "";
        public List<Collection> Collections { get; } = new();
    }

    // ───────── Windows ─────────

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    // HIDP_BUTTON_CAPS and HIDP_VALUE_CAPS are both 72 bytes; the report ID is a byte at offset 2 in both.
    const int CapsSize = 72;
    const int HidpInput = 0, HidpOutput = 1, HidpFeature = 2;

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("hid.dll")] static extern int HidP_GetButtonCaps(int type, byte[] caps, ref ushort length, IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort length, IntPtr data);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, char[] buffer, int length);
    [DllImport("hid.dll")] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll")] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

    /// <summary>Every HID interface on this PC, grouped like WebHID does.</summary>
    public static List<DeviceInfo> List()
    {
        var byKey = new Dictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return new();
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return new();
        try
        {
            for (int i = 0; ; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                var path = DetailPath(set, ref data);
                if (path == null) continue;
                try { Add(byKey, path); } catch { }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return byKey.Values.ToList();
    }

    static string DetailPath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
        if (size <= 0) return null;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);   // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
            return SetupDiGetDeviceInterfaceDetail(set, ref data, buf, size, out _, IntPtr.Zero) ? Marshal.PtrToStringUni(buf + 4) : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>The interface a collection path belongs to: the path without its "&amp;col0N" part.</summary>
    public static string InterfaceKey(string path) =>
        System.Text.RegularExpressions.Regex.Replace(path.ToLowerInvariant(), @"&col[0-9a-f]{2}", "");

    static void Add(Dictionary<string, DeviceInfo> byKey, string path)
    {
        // No access needed to read what the device is: this works even for mice and keyboards Windows holds on to.
        using var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return;
        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(h, ref attr)) return;
        if (!HidD_GetPreparsedData(h, out var pre)) return;
        try
        {
            HidP_GetCaps(pre, out var caps);
            var col = new Collection
            {
                Path = path, UsagePage = caps.UsagePage, Usage = caps.Usage,
                InputLength = caps.InputReportByteLength, OutputLength = caps.OutputReportByteLength, FeatureLength = caps.FeatureReportByteLength,
            };
            col.InputIds.AddRange(Ids(pre, HidpInput, caps.NumberInputButtonCaps, caps.NumberInputValueCaps, caps.InputReportByteLength));
            col.OutputIds.AddRange(Ids(pre, HidpOutput, caps.NumberOutputButtonCaps, caps.NumberOutputValueCaps, caps.OutputReportByteLength));
            col.FeatureIds.AddRange(Ids(pre, HidpFeature, caps.NumberFeatureButtonCaps, caps.NumberFeatureValueCaps, caps.FeatureReportByteLength));
            var key = InterfaceKey(path);
            if (!byKey.TryGetValue(key, out var dev))
            {
                var name = new char[256];
                string product = HidD_GetProductString(h, name, name.Length * 2) ? new string(name).TrimEnd('\0') : "";
                byKey[key] = dev = new DeviceInfo { Key = key, VendorId = attr.VendorID, ProductId = attr.ProductID, ProductName = product };
            }
            dev.Collections.Add(col);
        }
        finally { HidD_FreePreparsedData(pre); }
    }

    /// <summary>The report IDs a collection declares for one direction (0 when it doesn't use IDs).</summary>
    static IEnumerable<byte> Ids(IntPtr pre, int type, int buttons, int values, int length)
    {
        var ids = new SortedSet<byte>();
        foreach (var (count, button) in new[] { (buttons, true), (values, false) })
        {
            if (count <= 0) continue;
            var buf = new byte[count * CapsSize];
            ushort n = (ushort)count;
            int status = button ? HidP_GetButtonCaps(type, buf, ref n, pre) : HidP_GetValueCaps(type, buf, ref n, pre);
            if (status != 0x00110000) continue;   // HIDP_STATUS_SUCCESS
            for (int i = 0; i < n; i++) ids.Add(buf[i * CapsSize + 2]);
        }
        if (ids.Count == 0 && length > 0) ids.Add(0);
        return ids;
    }

    // ───────── an opened interface ─────────

    /// <summary>
    /// An interface opened for settings. Each collection that can be opened read/write is; Windows keeps the pointer
    /// and keyboard collections for itself, so those stay closed (drivers don't need them).
    /// </summary>
    public sealed class Connection : IDisposable
    {
        readonly DeviceInfo _info;
        readonly Dictionary<Collection, FileStream> _open = new();
        readonly CancellationTokenSource _stop = new();

        /// <summary>An input report arrived: report ID and its data (without the ID).</summary>
        public event Action<byte, byte[]> Input;

        public Connection(DeviceInfo info)
        {
            _info = info;
            foreach (var c in info.Collections)
            {
                if (c.UsagePage == 0x01 && c.Usage is 0x02 or 0x06) continue;   // the pointer and keyboard: Windows owns them
                var h = CreateFile(c.Path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (h.IsInvalid)
                {
                    h.Dispose();
                    h = CreateFile(c.Path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                }
                if (h.IsInvalid) { h.Dispose(); continue; }
                var fs = new FileStream(h, FileAccess.ReadWrite, 0, true);
                _open[c] = fs;
                if (c.InputLength > 0) _ = ReadLoop(c, fs);
            }
            if (_open.Count == 0) throw new IOException("Windows didn't let Pulse open this device's settings channel.");
        }

        FileStream For(Func<Collection, List<byte>> ids, byte reportId, out Collection col)
        {
            foreach (var (c, fs) in _open)
                if (ids(c).Contains(reportId)) { col = c; return fs; }
            throw new IOException($"No collection on this device takes report {reportId}.");
        }

        public async Task SendReport(byte reportId, byte[] data)
        {
            var fs = For(c => c.OutputIds, reportId, out var col);
            var buf = new byte[Math.Max(col.OutputLength, data.Length + 1)];
            buf[0] = reportId;
            Array.Copy(data, 0, buf, 1, Math.Min(data.Length, buf.Length - 1));
            await fs.WriteAsync(buf, _stop.Token);
        }

        public Task SendFeature(byte reportId, byte[] data) => Task.Run(() =>
        {
            var fs = For(c => c.FeatureIds, reportId, out var col);
            var buf = new byte[Math.Max(col.FeatureLength, data.Length + 1)];
            buf[0] = reportId;
            Array.Copy(data, 0, buf, 1, Math.Min(data.Length, buf.Length - 1));
            if (!HidD_SetFeature(fs.SafeFileHandle, buf, buf.Length)) throw new IOException($"The device refused feature report {reportId} (error {Marshal.GetLastWin32Error()}).");
        });

        /// <summary>A feature report, with its ID as the first byte (as Chrome returns it), unless the device uses no IDs.</summary>
        public Task<byte[]> GetFeature(byte reportId) => Task.Run(() =>
        {
            var fs = For(c => c.FeatureIds, reportId, out var col);
            var buf = new byte[Math.Max(col.FeatureLength, 2)];
            buf[0] = reportId;
            if (!HidD_GetFeature(fs.SafeFileHandle, buf, buf.Length)) throw new IOException($"The device didn't answer feature report {reportId} (error {Marshal.GetLastWin32Error()}).");
            return reportId == 0 ? buf[1..] : buf;
        });

        async Task ReadLoop(Collection c, FileStream fs)
        {
            var buf = new byte[c.InputLength];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    int n = await fs.ReadAsync(buf, _stop.Token);
                    if (n <= 0) break;
                    Input?.Invoke(buf[0], buf[1..n]);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            foreach (var fs in _open.Values) try { fs.Dispose(); } catch { }
            _open.Clear();
        }
    }
}
