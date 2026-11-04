using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Pulse.Core.Clips;

/// <summary>
/// Notes which keys, mouse buttons and controller buttons are held while Clips records, so clips can show an input
/// overlay. Uses Windows' Raw Input (what games themselves read) and XInput for Xbox controllers: no hooks, nothing
/// injected, and only the keys the overlay shows are kept. Off unless the user turns the overlay on.
/// </summary>
public sealed class InputCapture : IDisposable
{
    public static double NowSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    readonly object _gate = new();
    readonly List<InputFrame> _frames = new();   // times in NowSeconds
    readonly HashSet<int> _keys = new();
    readonly HashSet<int> _allowed = new(InputTimeline.OverlayKeys);
    int _mouse, _pad, _hidPad;
    int[] _sticks = new int[4], _triggers = new int[2];
    int[] _hidSticks = new int[4], _hidTriggers = new int[2];
    string _padKind = "xbox";
    HwndSource _window;
    Thread _xinput;
    volatile bool _stop;
    readonly Dictionary<IntPtr, int> _sonyProduct = new(); // 0 = not a PlayStation controller

    /// <summary>How far back to keep inputs (seconds): the replay length, or longer while a session records.</summary>
    public double KeepSeconds { get; set; } = 120;

    /// <summary>Starts listening. Call on the UI thread (Raw Input arrives as window messages).</summary>
    public void Start()
    {
        _window = new HwndSource(new HwndSourceParameters("Pulse input overlay") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _window.AddHook(WndProc);
        var devices = new[]
        {
            new RAWINPUTDEVICE { UsagePage = 1, Usage = 6, Flags = RIDEV_INPUTSINK, Target = _window.Handle }, // keyboard
            new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_INPUTSINK, Target = _window.Handle }, // mouse
            new RAWINPUTDEVICE { UsagePage = 1, Usage = 5, Flags = RIDEV_INPUTSINK, Target = _window.Handle }, // gamepad (PlayStation)
        };
        RegisterRawInputDevices(devices, devices.Length, Marshal.SizeOf<RAWINPUTDEVICE>());
        _xinput = new Thread(PollXInput) { IsBackground = true, Name = "Pulse controller input", Priority = ThreadPriority.BelowNormal };
        _xinput.Start();
    }

    /// <summary>The inputs between two moments (NowSeconds), re-timed so <paramref name="from"/> is 0.</summary>
    public InputTimeline Slice(double from, double to)
    {
        lock (_gate)
        {
            var all = new InputTimeline { PadKind = _padKind, Frames = _frames.ToList() };
            return all.Slice(from, to);
        }
    }

    void Record()
    {
        // Called with _gate held. Mouse wheel "clicks" show for one frame, then clear.
        var f = new InputFrame
        {
            T = NowSeconds,
            Keys = _keys.OrderBy(k => k).ToArray(),
            Mouse = _mouse,
            Pad = _pad | _hidPad,
            Sticks = PickSticks(),
            Triggers = Math.Max(_triggers[0], _triggers[1]) >= Math.Max(_hidTriggers[0], _hidTriggers[1]) ? _triggers.ToArray() : _hidTriggers.ToArray(),
        };
        if (_frames.Count > 0 && _frames[^1].SameAs(f)) return;
        _frames.Add(f);
        double cutoff = f.T - KeepSeconds;
        int drop = 0;
        while (drop < _frames.Count - 1 && _frames[drop + 1].T < cutoff) drop++;
        if (drop > 0) _frames.RemoveRange(0, drop);
    }

    int[] PickSticks() => _sticks.Sum(Math.Abs) >= _hidSticks.Sum(Math.Abs) ? _sticks.ToArray() : _hidSticks.ToArray();

    // ───────── keyboard and mouse (Raw Input) ─────────

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg != WM_INPUT) return IntPtr.Zero;
        try { Handle(l); } catch { }
        return IntPtr.Zero;
    }

    unsafe void Handle(IntPtr raw)
    {
        uint size = 0;
        int header = Marshal.SizeOf<RAWINPUTHEADER>();
        GetRawInputData(raw, RID_INPUT, IntPtr.Zero, ref size, (uint)header);
        if (size == 0 || size > 4096) return;
        byte* buf = stackalloc byte[(int)size];
        if (GetRawInputData(raw, RID_INPUT, (IntPtr)buf, ref size, (uint)header) != size) return;
        var h = *(RAWINPUTHEADER*)buf;
        byte* data = buf + header;
        lock (_gate)
        {
            switch (h.Type)
            {
                case 1: // keyboard
                {
                    ushort flags = *(ushort*)(data + 2);
                    int vk = InputTimeline.Normalize(*(ushort*)(data + 6));
                    if (!_allowed.Contains(vk)) return; // only keys the overlay shows are ever kept
                    if ((flags & RI_KEY_BREAK) != 0) _keys.Remove(vk); else _keys.Add(vk);
                    Record();
                    break;
                }
                case 0: // mouse
                {
                    ushort b = *(ushort*)(data + 4);
                    short wheel = *(short*)(data + 6);
                    void Btn(ushort down, ushort up, MouseButton m)
                    {
                        if ((b & down) != 0) _mouse |= (int)m;
                        if ((b & up) != 0) _mouse &= ~(int)m;
                    }
                    Btn(0x0001, 0x0002, MouseButton.Left);
                    Btn(0x0004, 0x0008, MouseButton.Right);
                    Btn(0x0010, 0x0020, MouseButton.Middle);
                    Btn(0x0040, 0x0080, MouseButton.Back);
                    Btn(0x0100, 0x0200, MouseButton.Forward);
                    if (b == 0) return; // just movement
                    if ((b & 0x0400) != 0)
                    {
                        _mouse |= (int)(wheel > 0 ? MouseButton.WheelUp : MouseButton.WheelDown);
                        Record();
                        _mouse &= ~(int)(MouseButton.WheelUp | MouseButton.WheelDown);
                    }
                    Record();
                    break;
                }
                case 2: // HID: PlayStation controllers
                {
                    int product = SonyProduct(h.Device);
                    if (product == 0) return;
                    uint sizeHid = *(uint*)data, count = *(uint*)(data + 4);
                    if (sizeHid == 0 || count == 0 || sizeHid > 512) return;
                    var report = new ReadOnlySpan<byte>(data + 8, (int)sizeHid);
                    if (!ParsePlayStation(report, product is 0x0CE6 or 0x0DF2, out var pad, out var sticks, out var trig)) return;
                    _padKind = "ps";
                    _hidPad = pad; _hidSticks = sticks; _hidTriggers = trig;
                    Record();
                    break;
                }
            }
        }
    }

    int SonyProduct(IntPtr device)
    {
        if (_sonyProduct.TryGetValue(device, out var known)) return known;
        int product = 0;
        var info = new RID_DEVICE_INFO { Size = (uint)Marshal.SizeOf<RID_DEVICE_INFO>() };
        uint sz = info.Size;
        if (GetRawInputDeviceInfo(device, RIDI_DEVICEINFO, ref info, ref sz) > 0 && info.Type == 2 && info.VendorId == 0x054C)
            product = (int)info.ProductId;
        return _sonyProduct[device] = product;
    }

    /// <summary>
    /// Reads a DualSense or DualShock 4 input report (USB, or Bluetooth in either report format) into the shared
    /// button bits. Exposed for tests.
    /// </summary>
    internal static bool ParsePlayStation(ReadOnlySpan<byte> r, bool dualSense, out int pad, out int[] sticks, out int[] triggers)
    {
        pad = 0; sticks = new int[4]; triggers = new int[2];
        if (r.Length < 10) return false;
        int lx, ly, rx, ry, l2, r2, b0, b1, b2;
        if (r[0] == 0x31 && r.Length >= 12) // DualSense over Bluetooth, full report
        { lx = r[2]; ly = r[3]; rx = r[4]; ry = r[5]; l2 = r[6]; r2 = r[7]; b0 = r[9]; b1 = r[10]; b2 = r[11]; }
        else if (r[0] == 0x01 && dualSense && r.Length >= 30) // DualSense over USB
        { lx = r[1]; ly = r[2]; rx = r[3]; ry = r[4]; l2 = r[5]; r2 = r[6]; b0 = r[8]; b1 = r[9]; b2 = r[10]; }
        else if (r[0] == 0x01) // DualShock 4 (USB or Bluetooth), or DualSense's short Bluetooth report
        { lx = r[1]; ly = r[2]; rx = r[3]; ry = r[4]; b0 = r[5]; b1 = r[6]; b2 = r[7]; l2 = r[8]; r2 = r[9]; }
        else return false;

        int Axis(int v) => Math.Clamp((v - 128) * 100 / 127, -100, 100);
        sticks = new[] { Axis(lx), -Axis(ly), Axis(rx), -Axis(ry) };
        triggers = new[] { l2 * 100 / 255, r2 * 100 / 255 };
        int hat = b0 & 0x0F;
        if (hat is 0 or 1 or 7) pad |= (int)PadButton.Up;
        if (hat is 1 or 2 or 3) pad |= (int)PadButton.Right;
        if (hat is 3 or 4 or 5) pad |= (int)PadButton.Down;
        if (hat is 5 or 6 or 7) pad |= (int)PadButton.Left;
        if ((b0 & 0x10) != 0) pad |= (int)PadButton.West;   // square
        if ((b0 & 0x20) != 0) pad |= (int)PadButton.South;  // cross
        if ((b0 & 0x40) != 0) pad |= (int)PadButton.East;   // circle
        if ((b0 & 0x80) != 0) pad |= (int)PadButton.North;  // triangle
        if ((b1 & 0x01) != 0) pad |= (int)PadButton.LeftBumper;
        if ((b1 & 0x02) != 0) pad |= (int)PadButton.RightBumper;
        if ((b1 & 0x04) != 0) pad |= (int)PadButton.LeftTrigger;
        if ((b1 & 0x08) != 0) pad |= (int)PadButton.RightTrigger;
        if ((b1 & 0x10) != 0) pad |= (int)PadButton.Back;     // create / share
        if ((b1 & 0x20) != 0) pad |= (int)PadButton.Start;    // options
        if ((b1 & 0x40) != 0) pad |= (int)PadButton.LeftStick;
        if ((b1 & 0x80) != 0) pad |= (int)PadButton.RightStick;
        if ((b2 & 0x01) != 0) pad |= (int)PadButton.Home;
        if ((b2 & 0x02) != 0) pad |= (int)PadButton.Touchpad;
        // Small stick wobble isn't shown.
        for (int i = 0; i < 4; i++) if (Math.Abs(sticks[i]) < 8) sticks[i] = 0;
        return true;
    }

    // ───────── Xbox controllers (XInput) ─────────

    void PollXInput()
    {
        while (!_stop)
        {
            Thread.Sleep(16);
            int pad = 0; int[] st = new int[4], tr = new int[2];
            bool any = false;
            for (uint i = 0; i < 4; i++)
            {
                try
                {
                    if (XInputGetState(i, out var s) != 0) continue;
                    any = true;
                    pad |= MapXInput(s.Gamepad.Buttons);
                    tr[0] = Math.Max(tr[0], s.Gamepad.LeftTrigger * 100 / 255);
                    tr[1] = Math.Max(tr[1], s.Gamepad.RightTrigger * 100 / 255);
                    int A(short v) { int x = v * 100 / 32767; return Math.Abs(x) < 8 ? 0 : Math.Clamp(x, -100, 100); }
                    st = new[] { A(s.Gamepad.ThumbLX), A(s.Gamepad.ThumbLY), A(s.Gamepad.ThumbRX), A(s.Gamepad.ThumbRY) };
                    if (tr[0] > 30) pad |= (int)PadButton.LeftTrigger;
                    if (tr[1] > 30) pad |= (int)PadButton.RightTrigger;
                    break;
                }
                catch (DllNotFoundException) { return; }
                catch (EntryPointNotFoundException) { return; }
            }
            if (!any) continue;
            lock (_gate)
            {
                // Sticks are kept in 10% steps so small movement doesn't flood the timeline.
                for (int j = 0; j < 4; j++) st[j] = st[j] / 10 * 10;
                if (pad == _pad && st.SequenceEqual(_sticks) && tr.SequenceEqual(_triggers)) continue;
                if (pad != 0 || st.Any(v => v != 0) || tr.Any(v => v > 10)) _padKind = "xbox";
                _pad = pad; _sticks = st; _triggers = tr;
                Record();
            }
        }
    }

    internal static int MapXInput(ushort b)
    {
        int p = 0;
        void M(ushort bit, PadButton to) { if ((b & bit) != 0) p |= (int)to; }
        M(0x0001, PadButton.Up); M(0x0002, PadButton.Down); M(0x0004, PadButton.Left); M(0x0008, PadButton.Right);
        M(0x0010, PadButton.Start); M(0x0020, PadButton.Back); M(0x0040, PadButton.LeftStick); M(0x0080, PadButton.RightStick);
        M(0x0100, PadButton.LeftBumper); M(0x0200, PadButton.RightBumper);
        M(0x1000, PadButton.South); M(0x2000, PadButton.East); M(0x4000, PadButton.West); M(0x8000, PadButton.North);
        return p;
    }

    public void Dispose()
    {
        _stop = true;
        try
        {
            if (_window != null)
            {
                var off = new[]
                {
                    new RAWINPUTDEVICE { UsagePage = 1, Usage = 6, Flags = RIDEV_REMOVE },
                    new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_REMOVE },
                    new RAWINPUTDEVICE { UsagePage = 1, Usage = 5, Flags = RIDEV_REMOVE },
                };
                RegisterRawInputDevices(off, off.Length, Marshal.SizeOf<RAWINPUTDEVICE>());
                _window.Dispose();
            }
        }
        catch { }
    }

    // ───────── Win32 ─────────

    const int WM_INPUT = 0x00FF, RID_INPUT = 0x10000003, RIDI_DEVICEINFO = 0x2000000b;
    const uint RIDEV_INPUTSINK = 0x100, RIDEV_REMOVE = 0x1;
    const ushort RI_KEY_BREAK = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER { public uint Type, Size; public IntPtr Device, WParam; }

    [StructLayout(LayoutKind.Sequential)]
    struct RID_DEVICE_INFO
    {
        public uint Size, Type;
        public uint VendorId, ProductId, VersionNumber; // HID part of the union
        public ushort UsagePage, Usage;
        uint _pad1, _pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short ThumbLX, ThumbLY, ThumbRX, ThumbRY; }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE { public uint PacketNumber; public XINPUT_GAMEPAD Gamepad; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, int count, int size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr raw, int command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr device, int command, ref RID_DEVICE_INFO data, ref uint size);
    [DllImport("xinput1_4.dll")] static extern int XInputGetState(uint index, out XINPUT_STATE state);
}
