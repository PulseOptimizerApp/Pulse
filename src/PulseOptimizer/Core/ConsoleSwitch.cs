using System.Runtime.InteropServices;

namespace Pulse.Core;

/// <summary>Settings for swapping one monitor (and the PC's sound) between the PC and a console.</summary>
public class ConsoleSwitchSettings
{
    /// <summary>Which monitor (its name as Windows reports it). Empty: the first one that answers.</summary>
    public string Monitor { get; set; } = "";
    /// <summary>The monitor input the PC is on, and the console's (MCCS codes: 0x0F DisplayPort 1, 0x11 HDMI 1…).</summary>
    public int PcInput { get; set; }
    public int ConsoleInput { get; set; }
    /// <summary>Where the PC plays sound in each mode (Windows device ids); empty leaves it alone.</summary>
    public string PcSound { get; set; } = "";
    public string ConsoleSound { get; set; } = "";
    /// <summary>The key swaps, so you can get back with the screen on the console.</summary>
    public bool Hotkey { get; set; } = true;
    /// <summary>The key: modifiers (1 Alt, 2 Ctrl, 4 Shift, 8 Win) and a virtual-key code. Ctrl+Alt+F12 by default.</summary>
    public int HotkeyMods { get; set; } = 3;
    public int HotkeyKey { get; set; } = 0x7B;
    public bool OnConsole { get; set; }
    /// <summary>A key that only flips the PC's sound between two devices (no screen change). Ctrl+Alt+F11 by default.</summary>
    public bool SoundKey { get; set; }
    public int SoundKeyMods { get; set; } = 3;
    public int SoundKeyKey { get; set; } = 0x7A;
    public string SoundA { get; set; } = "";
    public string SoundB { get; set; } = "";
    /// <summary>Your crosshair code from VALORANT on PC, to type in on console.</summary>
    public string CrosshairCode { get; set; } = "";
}

/// <summary>
/// Switches a monitor's input over DDC/CI (the same signal its own buttons send) and Windows' sound output, so one
/// click or key moves you between PC and console.
/// </summary>
public static class ConsoleSwitch
{
    public sealed record Screen(string Name, int Input, int Index);
    public sealed record SoundDevice(string Id, string Name);

    /// <summary>Names for the common input codes (MCCS).</summary>
    public static string InputName(int code) => (code & 0xFF) switch
    {
        0x01 => "VGA", 0x03 => "DVI 1", 0x04 => "DVI 2",
        0x0F => "DisplayPort 1", 0x10 => "DisplayPort 2",
        0x11 => "HDMI 1", 0x12 => "HDMI 2", 0x13 => "HDMI 3", 0x14 => "HDMI 4",
        0x1B => "USB-C", 0x19 => "USB-C",
        0 => "?",
        var c => "Input " + c,
    };

    /// <summary>The inputs people pick from.</summary>
    public static readonly int[] CommonInputs = { 0x0F, 0x10, 0x11, 0x12, 0x13, 0x1B, 0x03, 0x01 };

    // ───────── monitors ─────────

    /// <summary>
    /// The monitors Windows can talk to, with the input each says it's on (0 when it won't say: many monitors switch
    /// fine but don't report their input).
    /// </summary>
    public static List<Screen> Screens()
    {
        var list = new List<Screen>();
        int i = 0;
        ForEachMonitor((name, h) =>
        {
            int input = GetVCPFeatureAndVCPFeatureReply(h, 0x60, out _, out uint cur, out _) ? (int)(cur & 0xFF) : 0;
            // Same name twice (two of the same monitor): number them.
            var shown = list.Any(x => x.Name == name) ? $"{name} ({list.Count(x => x.Name.StartsWith(name, StringComparison.Ordinal)) + 1})" : name;
            list.Add(new Screen(shown, input, i));
            i++;
            return false;
        });
        return list;
    }

    /// <summary>Puts a monitor on an input. False when the monitor didn't take it.</summary>
    public static bool SetInput(string monitor, int input)
    {
        // Find it by name (numbered when two share one), else the first monitor.
        var screens = Screens();
        int index = screens.FindIndex(x => x.Name == monitor);
        if (index < 0) index = 0;
        bool ok = false;
        int i = 0;
        ForEachMonitor((name, h) =>
        {
            if (i++ != screens.ElementAtOrDefault(index)?.Index) return false;
            ok = SetVCPFeature(h, 0x60, (uint)input);
            return true;
        });
        return ok;
    }

    /// <summary>"Ctrl+Alt+F12" for a key setting.</summary>
    public static string KeyText(int mods, int vk)
    {
        var parts = new List<string>();
        if ((mods & 2) != 0) parts.Add("Ctrl");
        if ((mods & 1) != 0) parts.Add("Alt");
        if ((mods & 4) != 0) parts.Add("Shift");
        if ((mods & 8) != 0) parts.Add("Win");
        string key = vk switch
        {
            >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x60 and <= 0x69 => "Num " + (vk - 0x60),
            0x24 => "Home", 0x23 => "End", 0x2D => "Insert", 0x2E => "Delete", 0x21 => "Page Up", 0x22 => "Page Down",
            0x13 => "Pause", 0x91 => "Scroll Lock", 0x20 => "Space",
            _ => "Key " + vk,
        };
        parts.Add(key);
        return string.Join("+", parts);
    }

    static void ForEachMonitor(Func<string, IntPtr, bool> each)
    {
        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hm, IntPtr _, ref Rect _, IntPtr _) => { handles.Add(hm); return true; }, IntPtr.Zero);
        foreach (var hm in handles)
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hm, out uint n) || n == 0) continue;
            var arr = new PhysicalMonitor[n];
            if (!GetPhysicalMonitorsFromHMONITOR(hm, n, arr)) continue;
            try
            {
                foreach (var pm in arr)
                    if (each(string.IsNullOrWhiteSpace(pm.Description) ? "Monitor" : pm.Description.Trim(), pm.Handle)) return;
            }
            finally { DestroyPhysicalMonitors(n, arr); }
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int L, T, R, B; }
    delegate bool MonitorEnum(IntPtr hMonitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnum cb, IntPtr data);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, out uint type, out uint current, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)] static extern bool SetVCPFeature(IntPtr h, byte code, uint value);

    // ───────── sound ─────────

    /// <summary>Speakers and headsets Windows can play to now, and which one it uses.</summary>
    public static (List<SoundDevice> Devices, string Default) Sound()
    {
        var list = new List<SoundDevice>();
        string def = "";
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint(0, 1, out var d) == 0 && d.GetId(out var did) == 0) def = did;
            if (en.EnumAudioEndpoints(0, 1, out var col) != 0) return (list, def);
            col.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var dev) != 0 || dev.GetId(out var id) != 0) continue;
                list.Add(new SoundDevice(id, FriendlyName(dev) ?? id));
            }
        }
        catch { }
        return (list, def);
    }

    /// <summary>Makes a device where Windows plays sound (for everything: games, calls, system).</summary>
    public static bool SetSound(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        try
        {
            var pc = (IPolicyConfig)new PolicyConfigClient();
            bool ok = true;
            for (int role = 0; role < 3; role++) ok &= pc.SetDefaultEndpoint(id, role) == 0;
            return ok;
        }
        catch { return false; }
    }

    static string FriendlyName(IMMDevice dev)
    {
        try
        {
            if (dev.OpenPropertyStore(0, out var store) != 0) return null;
            var key = new PropertyKey { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 };
            if (store.GetValue(ref key, out var pv) != 0) return null;
            return pv.Vt == 31 ? Marshal.PtrToStringUni(pv.Ptr) : null;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Fmtid; public int Pid; }
    [StructLayout(LayoutKind.Sequential)] struct PropVariant { public ushort Vt; ushort r1, r2, r3; public IntPtr Ptr; IntPtr p2; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClient { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int state, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint n);
        [PreserveSig] int GetAt(uint i, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    /// <summary>Windows' own (undocumented, long-stable) way to set the default sound device, as the Sound panel does.</summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(); [PreserveSig] int GetDeviceFormat(); [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat(); [PreserveSig] int GetProcessingPeriod(); [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode(); [PreserveSig] int SetShareMode(); [PreserveSig] int GetPropertyValue(); [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }

    /// <summary>
    /// Flips the PC's sound to the other of two devices: to B when it's on A, otherwise to A. Returns the device's
    /// name, or null when it didn't work.
    /// </summary>
    public static string FlipSound(ConsoleSwitchSettings s)
    {
        if (string.IsNullOrEmpty(s.SoundA) || string.IsNullOrEmpty(s.SoundB)) return null;
        var (devices, current) = Sound();
        var to = current == s.SoundA ? s.SoundB : s.SoundA;
        if (!SetSound(to)) return null;
        return devices.FirstOrDefault(d => d.Id == to)?.Name ?? "";
    }

    // ───────── the swap ─────────

    /// <summary>Moves to the console (or back to the PC). Returns what happened, for people.</summary>
    public static string Swap(ConsoleSwitchSettings s, bool toConsole)
    {
        if (s.ConsoleInput == 0 || s.PcInput == 0) return Ui.Loc.T("Set up the switch first: pick your monitor's PC and console inputs.");
        bool screen = SetInput(s.Monitor, toConsole ? s.ConsoleInput : s.PcInput);
        var sound = toConsole ? s.ConsoleSound : s.PcSound;
        bool snd = string.IsNullOrEmpty(sound) || SetSound(sound);
        if (screen) { s.OnConsole = toConsole; TweakEngine.Save(); }
        if (!screen) return Ui.Loc.T("Your monitor didn't switch. Turn on DDC/CI in its on-screen menu, then try again.");
        if (!snd) return Ui.Loc.T("Switched the screen, but Windows didn't take the sound change.");
        return toConsole ? Ui.Loc.F("On console. Press {0} to come back.", KeyText(s.HotkeyMods, s.HotkeyKey)) : Ui.Loc.T("Back on PC.");
    }
}
