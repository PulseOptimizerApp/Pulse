using System.Runtime.InteropServices;

namespace Pulse.Core.Input;

/// <summary>
/// What the user is physically pressing, read with Windows raw input on a hidden window of its own. Input sent by
/// programs (including Pulse's own macros) arrives with no device and is ignored, so a macro pressing a key never
/// counts as the user pressing it, and a trigger can't set itself off. Nothing is blocked or changed: Pulse only
/// listens, the same way games read the mouse.
///
/// Runs only while macros are on. Falls back to GetAsyncKeyState if raw input can't be set up.
/// </summary>
public sealed class PhysicalInput : IDisposable
{
    readonly bool[] _down = new bool[256];
    Thread _thread;
    IntPtr _hwnd;
    // One window class for the whole run, with one delegate that lives forever: Windows keeps calling the class's
    // window procedure, so it must never be a delegate that can be garbage-collected (that crashed Pulse when
    // macros were turned on a second time).
    static readonly WndProc ClassProc = StaticProc;
    static readonly Dictionary<IntPtr, PhysicalInput> Windows = new();
    static bool _classRegistered;
    static readonly object ClassGate = new();
    readonly ManualResetEventSlim _ready = new();

    /// <summary>Raised on the input thread for every physical press and release (virtual-key code, down).</summary>
    public event Action<int, bool> Changed;
    /// <summary>Raised on the input thread for physical scroll clicks (positive = up).</summary>
    public event Action<int> Scrolled;
    /// <summary>Set on every change, so a waiting thread wakes at once.</summary>
    public readonly AutoResetEvent Signal = new(false);
    /// <summary>True when raw input is working (otherwise <see cref="IsDown"/> also sees injected input).</summary>
    public bool Working { get; private set; }
    public bool Running => _thread != null;

    // Windows gives each program one raw-input listener per device type. Pulse's other listeners (the clip input
    // overlay, the mouse polling check) take it over while they run and pass every message on through
    // FromOtherWindow; when they finish, this one takes it back.
    static volatile PhysicalInput _current;
    static int _others;
    volatile bool _owns;
    readonly object _readGate = new();

    /// <summary>Another Pulse window just registered for raw keyboard/mouse input.</summary>
    public static void OtherStarted() { Interlocked.Increment(ref _others); if (_current != null) _current._owns = false; }

    /// <summary>Another Pulse window stopped listening (and removed the registration): listen again ourselves.</summary>
    public static void OtherStopped()
    {
        if (Interlocked.Decrement(ref _others) < 0) Interlocked.Exchange(ref _others, 0);
        var c = _current;
        if (c != null && c._hwnd != IntPtr.Zero) PostMessageW(c._hwnd, WM_REREGISTER, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Called by Pulse's other raw-input windows for each WM_INPUT, so macros keep seeing physical input.</summary>
    public static void FromOtherWindow(IntPtr lParam)
    {
        var c = _current;
        if (c == null || c._owns) return;
        try { c.Read(lParam); } catch { }
    }

    bool Register()
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd }, // mouse
            new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd }, // keyboard
        };
        return _owns = RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    public bool IsDown(int vk)
    {
        if (vk <= 0 || vk > 255) return false;
        if (!Working) return (GetAsyncKeyState(vk) & 0x8000) != 0;
        return vk switch
        {
            0x10 => _down[0xA0] || _down[0xA1],
            0x11 => _down[0xA2] || _down[0xA3],
            0x12 => _down[0xA4] || _down[0xA5],
            _ => _down[vk],
        };
    }

    public void Start()
    {
        if (_thread != null) return;
        _ready.Reset();
        _thread = new Thread(Loop) { IsBackground = true, Name = "Pulse physical input", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait(2000);
    }

    public void Stop()
    {
        var t = _thread;
        if (t == null) return;
        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        t.Join(1000);
        _thread = null;
        Array.Clear(_down);
        Working = false;
    }

    public void Dispose() => Stop();

    void Loop()
    {
        try
        {
            var hInst = GetModuleHandleW(null);
            lock (ClassGate)
            {
                if (!_classRegistered)
                {
                    var cls = new WNDCLASSEXW
                    {
                        cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                        lpfnWndProc = Marshal.GetFunctionPointerForDelegate(ClassProc),
                        hInstance = hInst,
                        lpszClassName = "PulsePhysicalInput",
                    };
                    _classRegistered = RegisterClassExW(ref cls) != 0;
                }
            }
            _hwnd = CreateWindowExW(0, "PulsePhysicalInput", "", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hwnd != IntPtr.Zero) lock (Windows) Windows[_hwnd] = this;
            if (_hwnd != IntPtr.Zero)
            {
                _current = this;
                // If another Pulse window is listening, it passes the input on; otherwise listen ourselves.
                Working = Volatile.Read(ref _others) > 0 || Register();
            }
        }
        catch { Working = false; }
        _ready.Set();
        if (_hwnd == IntPtr.Zero) return;
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        _hwnd = IntPtr.Zero;
    }

    byte[] _buf = new byte[128];

    static IntPtr StaticProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        PhysicalInput me;
        lock (Windows) Windows.TryGetValue(hwnd, out me);
        if (msg == WM_DESTROY)
        {
            lock (Windows) Windows.Remove(hwnd);
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        try { if (me != null) return me.Proc(hwnd, msg, wParam, lParam); } catch { }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_INPUT:
                try { Read(lParam); } catch { }
                break;
            case WM_REREGISTER:
                if (Volatile.Read(ref _others) == 0) Register();
                return IntPtr.Zero;
            case WM_CLOSE:
                if (_current == this) _current = null;
                if (!_owns) { DestroyWindow(hwnd); return IntPtr.Zero; } // someone else's registration: leave it
                var off = new[]
                {
                    new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = RIDEV_REMOVE },
                    new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = RIDEV_REMOVE },
                };
                RegisterRawInputDevices(off, 2, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
                DestroyWindow(hwnd);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    void Read(IntPtr handle)
    {
        lock (_readGate) ReadLocked(handle);
    }

    void ReadLocked(IntPtr handle)
    {
        int headerSize = 8 + 2 * IntPtr.Size;
        uint size = 0;
        GetRawInputData(handle, RID_INPUT, null, ref size, (uint)headerSize);
        if (size == 0) return;
        if (_buf.Length < size) _buf = new byte[size];
        if (GetRawInputData(handle, RID_INPUT, _buf, ref size, (uint)headerSize) != size) return;
        uint type = BitConverter.ToUInt32(_buf, 0);
        // Input sent by a program comes from no device: that's not the user.
        long device = IntPtr.Size == 8 ? BitConverter.ToInt64(_buf, 8) : BitConverter.ToInt32(_buf, 8);
        if (device == 0) return;
        int h = headerSize;
        if (type == RIM_TYPEMOUSE)
        {
            ushort flags = BitConverter.ToUInt16(_buf, h + 4);
            short data = BitConverter.ToInt16(_buf, h + 6);
            if (flags == 0) return; // just movement
            Mouse(flags, 0x0001, 0x0002, Keys.VK_LBUTTON);
            Mouse(flags, 0x0004, 0x0008, Keys.VK_RBUTTON);
            Mouse(flags, 0x0010, 0x0020, Keys.VK_MBUTTON);
            Mouse(flags, 0x0040, 0x0080, Keys.VK_XBUTTON1);
            Mouse(flags, 0x0100, 0x0200, Keys.VK_XBUTTON2);
            if ((flags & 0x0400) != 0 && data != 0) { Scrolled?.Invoke(data / 120 == 0 ? Math.Sign(data) : data / 120); Signal.Set(); }
        }
        else if (type == RIM_TYPEKEYBOARD)
        {
            ushort make = BitConverter.ToUInt16(_buf, h);
            ushort kflags = BitConverter.ToUInt16(_buf, h + 2);
            int vk = BitConverter.ToUInt16(_buf, h + 6);
            if (vk == 0xFF || vk == 0) return; // part of a multi-part key (e.g. Pause)
            bool e0 = (kflags & 0x2) != 0;
            vk = vk switch
            {
                0x10 => make == 0x36 ? 0xA1 : 0xA0,
                0x11 => e0 ? 0xA3 : 0xA2,
                0x12 => e0 ? 0xA5 : 0xA4,
                _ => vk,
            };
            Set(vk, (kflags & 0x1) == 0);
        }
    }

    void Mouse(ushort flags, ushort downBit, ushort upBit, int vk)
    {
        if ((flags & downBit) != 0) Set(vk, true);
        if ((flags & upBit) != 0) Set(vk, false);
    }

    void Set(int vk, bool down)
    {
        if (vk <= 0 || vk > 255) return;
        bool was = _down[vk];
        _down[vk] = down;
        if (was == down) return; // key repeat
        Changed?.Invoke(vk, down);
        Signal.Set();
    }

    // ───────── Windows ─────────

    const uint WM_INPUT = 0x00FF, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002, WM_REREGISTER = 0x8000 + 41;
    const uint RID_INPUT = 0x10000003, RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1;
    const uint RIDEV_INPUTSINK = 0x100, RIDEV_REMOVE = 0x1;

    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEXW cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr handle, uint command, byte[] data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
}
