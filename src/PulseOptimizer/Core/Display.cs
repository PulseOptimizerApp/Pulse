using System.Runtime.InteropServices;
using System.Text.Json;

namespace Pulse.Core;

public record ModeInfo(int Width, int Height, int Hz);

/// <summary>Everything Pulse knows about one connected screen.</summary>
public class MonitorInfo
{
    public string DeviceName { get; init; }      // \\.\DISPLAY1
    public string AdapterName { get; init; }     // the graphics chip driving it
    public string Name { get; set; }             // e.g. "LG ULTRAGEAR"
    public string Connection { get; set; } = ""; // HDMI, DisplayPort, built-in...
    public bool IsBuiltIn { get; set; }
    public bool IsPrimary { get; init; }
    public ModeInfo Current { get; init; }
    public List<ModeInfo> Available { get; init; } = new();
    /// <summary>Modes the monitor itself reports (EDID). Empty if Windows couldn't read them.</summary>
    public List<ModeInfo> MonitorReported { get; set; } = new();

    /// <summary>Largest resolution Windows offers: almost always the panel's native resolution.</summary>
    public ModeInfo Native => Available.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.Hz).FirstOrDefault() ?? Current;
    public int MaxHzAt(int w, int h) => Available.Where(m => m.Width == w && m.Height == h).Select(m => m.Hz).DefaultIfEmpty(0).Max();
    public int MonitorMaxHzAt(int w, int h) => MonitorReported.Where(m => m.Width == w && m.Height == h).Select(m => m.Hz).DefaultIfEmpty(0).Max();
    public int MaxHzAtCurrent => MaxHzAt(Current.Width, Current.Height);
    public string DisplayLabel => string.IsNullOrWhiteSpace(Name) ? DeviceName.TrimStart('\\', '.') : Name;
}

/// <summary>
/// Reads monitors with documented Windows APIs (EnumDisplayDevices/EnumDisplaySettings and the WMI monitor classes)
/// and changes the refresh rate with ChangeDisplaySettingsEx, testing the mode first.
/// </summary>
public static class Displays
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);

    const int ENUM_CURRENT_SETTINGS = -1;
    const int ATTACHED = 0x1, PRIMARY = 0x4;
    const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x1;
    const uint CDS_UPDATEREGISTRY = 0x1, CDS_TEST = 0x2;
    const int DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    const int DISP_CHANGE_SUCCESSFUL = 0, DISP_CHANGE_RESTART = 1;

    static DEVMODE NewMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    public static List<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        var wmiKeys = new Dictionary<MonitorInfo, string>();
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & ATTACHED) == 0) continue;

            var cur = NewMode();
            if (!EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref cur)) continue;

            var modes = new HashSet<ModeInfo>();
            for (int m = 0; ; m++)
            {
                var dm = NewMode();
                if (!EnumDisplaySettings(dd.DeviceName, m, ref dm)) break;
                if (dm.dmDisplayFrequency > 1) modes.Add(new ModeInfo(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
            }

            var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            bool hasMon = EnumDisplayDevices(dd.DeviceName, 0, ref mon, EDD_GET_DEVICE_INTERFACE_NAME);

            var info = new MonitorInfo
            {
                DeviceName = dd.DeviceName,
                AdapterName = dd.DeviceString,
                Name = hasMon ? mon.DeviceString : "",
                IsPrimary = (dd.StateFlags & PRIMARY) != 0,
                Current = new ModeInfo(cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency),
                Available = modes.ToList(),
            };
            list.Add(info);
            if (hasMon) wmiKeys[info] = WmiKeyFromInterface(mon.DeviceID);
        }
        try { AddWmiDetails(wmiKeys); } catch { /* WMI monitor classes unavailable: keep basics */ }
        return list;
    }

    /// <summary>\\?\DISPLAY#GSM5B7F#5&amp;1a2b&amp;0&amp;UID4352#{guid} → DISPLAY\GSM5B7F\5&amp;1a2b&amp;0&amp;UID4352</summary>
    internal static string WmiKeyFromInterface(string deviceInterface)
    {
        if (string.IsNullOrEmpty(deviceInterface)) return "";
        var s = deviceInterface.StartsWith(@"\\?\") ? deviceInterface[4..] : deviceInterface;
        var parts = s.Split('#');
        return parts.Length >= 3 ? $@"{parts[0]}\{parts[1]}\{parts[2]}".ToUpperInvariant() : "";
    }

    internal static string ConnectionName(long tech, out bool builtIn)
    {
        builtIn = tech is 6 or 11 or 13 or 0x80000000L or -2147483648L;
        return tech switch
        {
            0 => "VGA",
            4 => "DVI",
            5 => "HDMI",
            6 or 11 or 13 or 0x80000000L or -2147483648L => "Built-in screen",
            10 => "DisplayPort",
            12 => "UDI",
            15 => "Wireless (Miracast)",
            16 or 17 => "Virtual or indirect display",
            _ => "",
        };
    }

    static void AddWmiDetails(Dictionary<MonitorInfo, string> keys)
    {
        if (keys.Count == 0) return;
        const string script = @"
$ErrorActionPreference = 'SilentlyContinue'
$out = @{}
foreach ($m in Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID) {
  $name = ($m.UserFriendlyName | Where-Object { $_ -ne 0 } | ForEach-Object { [char]$_ }) -join ''
  $out[$m.InstanceName] = [ordered]@{ Name = $name; Tech = $null; Modes = @() }
}
foreach ($c in Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorConnectionParams) {
  if (-not $out.ContainsKey($c.InstanceName)) { $out[$c.InstanceName] = [ordered]@{ Name = ''; Tech = $null; Modes = @() } }
  $out[$c.InstanceName].Tech = [long]$c.VideoOutputTechnology
}
foreach ($s in Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorListedSupportedSourceModes) {
  if (-not $out.ContainsKey($s.InstanceName)) { continue }
  $out[$s.InstanceName].Modes = @($s.MonitorSourceModes | ForEach-Object {
    $den = [double]$_.VerticalRefreshRateDenominator; if ($den -le 0) { $den = 1 }
    @([int]$_.HorizontalActivePixels, [int]$_.VerticalActivePixels, [int][math]::Round([double]$_.VerticalRefreshRateNumerator / $den))
  })
}
ConvertTo-Json -Depth 5 -Compress -InputObject $out";
        var r = Shell.PowerShell(script, 60_000);
        using var doc = JsonDocument.Parse(r.Output.Trim().Length == 0 ? "{}" : r.Output);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var inst = prop.Name.ToUpperInvariant();
            var match = keys.FirstOrDefault(k => k.Value.Length > 0 && inst.StartsWith(k.Value)).Key;
            if (match == null) continue;
            var e = prop.Value;
            if (e.TryGetProperty("Name", out var n) && n.GetString() is { Length: > 0 } name) match.Name = name.Trim();
            if (e.TryGetProperty("Tech", out var t) && t.ValueKind == JsonValueKind.Number)
            {
                match.Connection = ConnectionName(t.GetInt64(), out var builtIn);
                match.IsBuiltIn = builtIn;
            }
            if (e.TryGetProperty("Modes", out var ms) && ms.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in ms.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Array || m.GetArrayLength() < 3) continue;
                    match.MonitorReported.Add(new ModeInfo(m[0].GetInt32(), m[1].GetInt32(), m[2].GetInt32()));
                }
            }
        }
    }

    /// <summary>Switches a display to a mode Windows already lists as supported. Returns the previous mode for undo.</summary>
    public static (bool ok, string message, ModeInfo previous) SetMode(string device, int width, int height, int hz)
    {
        var cur = NewMode();
        if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref cur))
            return (false, "Windows couldn't read the current display mode.", null);
        var prev = new ModeInfo(cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency);

        // Start from a mode Windows returned (as Microsoft recommends), so every field is valid for this driver.
        DEVMODE target = default;
        bool found = false;
        for (int m = 0; ; m++)
        {
            var dm = NewMode();
            if (!EnumDisplaySettings(device, m, ref dm)) break;
            if (dm.dmPelsWidth == width && dm.dmPelsHeight == height && dm.dmDisplayFrequency == hz && dm.dmBitsPerPel == cur.dmBitsPerPel)
            { target = dm; found = true; break; }
        }
        if (!found) return (false, $"{width}×{height} at {hz} Hz isn't in Windows' list of supported modes for this screen.", prev);
        target.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;

        int test = ChangeDisplaySettingsEx(device, ref target, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
        if (test != DISP_CHANGE_SUCCESSFUL) return (false, $"Windows rejected that mode in a test (code {test}).", prev);

        int r = ChangeDisplaySettingsEx(device, ref target, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        return r switch
        {
            DISP_CHANGE_SUCCESSFUL => (true, "", prev),
            DISP_CHANGE_RESTART => (true, "Windows will finish the change after a restart.", prev),
            _ => (false, $"Windows couldn't switch the mode (code {r}).", prev),
        };
    }
}
