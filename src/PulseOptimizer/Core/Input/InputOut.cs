using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pulse.Core.Input;

/// <summary>What a macro can do to the PC. The real one sends Windows input; tests use a recorder.</summary>
public interface IInputSink
{
    /// <summary>Presses or lets go of a key by scancode (0x100 and up = extended keys, like Logitech's numbering).</summary>
    void Key(int scan, bool down);
    /// <summary>0 left, 1 right, 2 middle, 3 back (mouse 4), 4 forward (mouse 5).</summary>
    void Button(int button, bool down);
    void Move(int dx, int dy);
    /// <summary>Absolute position on the main screen, 0–65535 on each axis (Logitech's scale).</summary>
    void MoveTo(int x, int y);
    /// <summary>Scroll clicks: positive is up.</summary>
    void Wheel(int clicks);
    void Type(string text);
    (int X, int Y) MousePosition();
    /// <summary>Is this key or mouse button (virtual-key code) physically held right now (ignores macro input)?</summary>
    bool IsDown(int vk);
    bool IsLockOn(int vk);
    /// <summary>Waits accurately. Throws <see cref="Lua.LuaStopped"/> (or returns early) if the macro is stopped.</summary>
    void Sleep(double ms);
    void Log(string line);
}

/// <summary>Keys and mouse buttons, with the names Logitech G Hub scripts use.</summary>
public static class Keys
{
    public const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_MBUTTON = 0x04, VK_XBUTTON1 = 0x05, VK_XBUTTON2 = 0x06;

    /// <summary>Our mouse button index (0 left, 1 right, 2 middle, 3 back, 4 forward) → virtual-key code.</summary>
    public static readonly int[] ButtonVk = { VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, VK_XBUTTON1, VK_XBUTTON2 };

    public static bool IsMouse(int vk) => vk is VK_LBUTTON or VK_RBUTTON or VK_MBUTTON or VK_XBUTTON1 or VK_XBUTTON2;

    public static int ButtonOfVk(int vk) => Array.IndexOf(ButtonVk, vk);

    /// <summary>G Hub's key names → virtual-key codes.</summary>
    public static readonly Dictionary<string, int> Named = Build();

    static Dictionary<string, int> Build()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["escape"] = 0x1B, ["esc"] = 0x1B, ["printscreen"] = 0x2C, ["scrolllock"] = 0x91, ["pause"] = 0x13,
            ["tilde"] = 0xC0, ["minus"] = 0xBD, ["equal"] = 0xBB, ["backspace"] = 0x08, ["tab"] = 0x09,
            ["lbracket"] = 0xDB, ["rbracket"] = 0xDD, ["backslash"] = 0xDC, ["capslock"] = 0x14, ["semicolon"] = 0xBA,
            ["quote"] = 0xDE, ["enter"] = 0x0D, ["return"] = 0x0D, ["non_us_slash"] = 0xE2, ["comma"] = 0xBC, ["period"] = 0xBE,
            ["slash"] = 0xBF, ["lshift"] = 0xA0, ["rshift"] = 0xA1, ["shift"] = 0xA0, ["lctrl"] = 0xA2, ["rctrl"] = 0xA3,
            ["ctrl"] = 0xA2, ["lalt"] = 0xA4, ["ralt"] = 0xA5, ["alt"] = 0xA4, ["lgui"] = 0x5B, ["rgui"] = 0x5C, ["appkey"] = 0x5D,
            ["spacebar"] = 0x20, ["space"] = 0x20, ["insert"] = 0x2D, ["home"] = 0x24, ["pageup"] = 0x21, ["delete"] = 0x2E,
            ["end"] = 0x23, ["pagedown"] = 0x22, ["up"] = 0x26, ["left"] = 0x25, ["down"] = 0x28, ["right"] = 0x27,
            ["numlock"] = 0x90, ["numslash"] = 0x6F, ["numstar"] = 0x6A, ["nummult"] = 0x6A, ["numminus"] = 0x6D,
            ["numplus"] = 0x6B, ["numperiod"] = 0x6E, ["numenter"] = 0x0D,
            ["media_play_pause"] = 0xB3, ["media_stop"] = 0xB2, ["media_next"] = 0xB0, ["media_prev"] = 0xB1,
            ["volume_up"] = 0xAF, ["volume_down"] = 0xAE, ["volume_mute"] = 0xAD,
        };
        for (int i = 0; i < 26; i++) d[((char)('a' + i)).ToString()] = 0x41 + i;
        for (int i = 0; i <= 9; i++) { d[i.ToString()] = 0x30 + i; d["num" + i] = 0x60 + i; }
        for (int i = 1; i <= 24; i++) d["f" + i] = 0x6F + i;
        return d;
    }

    /// <summary>Scancodes for keys whose virtual-key code alone doesn't say which physical key it is.</summary>
    static readonly Dictionary<string, int> NamedScan = new(StringComparer.OrdinalIgnoreCase) { ["numenter"] = 0x11C };

    /// <summary>A key name or a scancode number (what G Hub's PressKey accepts) → scancode, or -1.</summary>
    public static int ScanOfName(object key)
    {
        if (key is double d) return (int)d;
        if (key is not string s) return -1;
        if (NamedScan.TryGetValue(s, out var sc)) return sc;
        return Named.TryGetValue(s, out var vk) ? ScanOfVk(vk) : -1;
    }

    /// <summary>The virtual-key code G Hub's name stands for, or -1.</summary>
    public static int VkOfName(string s) => s != null && Named.TryGetValue(s, out var vk) ? vk : -1;

    /// <summary>Keys that share a scancode with a number-pad key and are told apart by the "extended" flag.</summary>
    static readonly HashSet<int> Extended = new() { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0xA3, 0xA5, 0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3 };

    /// <summary>Scancode for a virtual-key code (0x100 bit = extended key).</summary>
    public static int ScanOfVk(int vk)
    {
        if (!OperatingSystem.IsWindows()) return vk;
        uint r = MapVirtualKeyW((uint)vk, 4 /* MAPVK_VK_TO_VSC_EX */);
        if (r == 0) return 0;
        bool ext = (r & 0xFF00) is 0xE000 or 0xE100 || Extended.Contains(vk);
        return ext ? 0x100 | (int)(r & 0xFF) : (int)(r & 0xFF);
    }

    /// <summary>A key or mouse button as people call it.</summary>
    public static string Display(int vk) => vk switch
    {
        0 => "–",
        VK_LBUTTON => "Left click",
        VK_RBUTTON => "Right click",
        VK_MBUTTON => "Middle mouse",
        VK_XBUTTON1 => "Mouse 4",
        VK_XBUTTON2 => "Mouse 5",
        0x10 or 0xA0 => "Shift", 0xA1 => "Right Shift",
        0x11 or 0xA2 => "Ctrl", 0xA3 => "Right Ctrl",
        0x12 or 0xA4 => "Alt", 0xA5 => "Right Alt",
        0x20 => "Space", 0x0D => "Enter", 0x1B => "Esc", 0x08 => "Backspace", 0x09 => "Tab", 0x14 => "Caps Lock",
        0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Num " + (vk - 0x60),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        0xC0 => "`", 0xBD => "-", 0xBB => "=", 0xDB => "[", 0xDD => "]", 0xDC => "\\", 0xBA => ";", 0xDE => "'",
        0xBC => ",", 0xBE => ".", 0xBF => "/",
        0x2D => "Insert", 0x2E => "Delete", 0x24 => "Home", 0x23 => "End", 0x21 => "Page Up", 0x22 => "Page Down",
        _ => OperatingSystem.IsWindows() ? WindowsName(vk) : $"Key {vk}",
    };

    static string WindowsName(int vk)
    {
        int scan = ScanOfVk(vk);
        var sb = new System.Text.StringBuilder(64);
        int lp = ((scan & 0xFF) << 16) | ((scan & 0x100) != 0 ? 1 << 24 : 0);
        return GetKeyNameTextW(lp, sb, 64) > 0 ? sb.ToString() : $"Key {vk}";
    }

    [DllImport("user32.dll")] static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetKeyNameTextW(int lParam, System.Text.StringBuilder name, int size);
}

/// <summary>
/// Sends a macro's input to Windows with SendInput, like any input program (nothing touches a game's memory), and
/// remembers everything it's holding so stopping a macro always lets go of every key and button.
/// </summary>
public sealed class WindowsInput : IInputSink, IDisposable
{
    readonly PhysicalInput _phys;
    readonly HashSet<int> _keys = new();
    readonly HashSet<int> _buttons = new();
    IntPtr _timer;
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>Set to stop: waits end early and no more presses are sent.</summary>
    public volatile bool Stopped;
    /// <summary>False while the macro isn't allowed to act here (wrong window): presses are skipped, releases still go out.</summary>
    public volatile bool Allowed = true;
    /// <summary>Set while mouse movement is blocked (a competitive shooter is running), with its name.</summary>
    public Func<string> MoveBlockedBy = () => null;
    public Action<string> LogTo = _ => { };
    /// <summary>Raised after each button press, for the live speed.</summary>
    public Action<int> Pressed;
    bool _toldMoveBlocked;

    public WindowsInput(PhysicalInput phys) { _phys = phys; }

    public void Log(string line) => LogTo(line);

    public void Key(int scan, bool down)
    {
        if (scan <= 0) return;
        lock (_keys)
        {
            if (down) { if (Stopped || !Allowed) return; _keys.Add(scan); }
            else if (!_keys.Remove(scan) && Stopped) return;
        }
        SendKey(scan, down);
    }

    public void Button(int button, bool down)
    {
        if (button < 0 || button > 4) return;
        lock (_buttons)
        {
            if (down) { if (Stopped || !Allowed) return; _buttons.Add(button); }
            else if (!_buttons.Remove(button) && Stopped) return;
        }
        SendButton(button, down);
        if (down) Pressed?.Invoke(button);
    }

    bool CanMove()
    {
        if (Stopped || !Allowed) return false;
        var game = MoveBlockedBy();
        if (game == null) { _toldMoveBlocked = false; return true; }
        if (!_toldMoveBlocked) { _toldMoveBlocked = true; Log($"Mouse movement is paused while {game} is open (competitive shooters ban for it). You can change this in Settings, but we strongly advise against it."); }
        return false;
    }

    public void Move(int dx, int dy)
    {
        if (!CanMove() || (dx == 0 && dy == 0)) return;
        Send(new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } } });
    }

    public void MoveTo(int x, int y)
    {
        if (!CanMove()) return;
        Send(new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = Math.Clamp(x, 0, 65535), dy = Math.Clamp(y, 0, 65535), dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE } } });
    }

    public void Wheel(int clicks)
    {
        if (Stopped || !Allowed || clicks == 0) return;
        Send(new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { mouseData = (uint)(clicks * 120), dwFlags = MOUSEEVENTF_WHEEL } } });
    }

    public void Type(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (char c in text)
        {
            if (Stopped || !Allowed) return;
            if (c == '\n') { SendKey(0x1C, true); SendKey(0x1C, false); }
            else
            {
                Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } } });
                Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
            }
            Sleep(8);
        }
    }

    public (int X, int Y) MousePosition()
    {
        GetCursorPos(out var p);
        int w = Math.Max(GetSystemMetrics(0) - 1, 1), h = Math.Max(GetSystemMetrics(1) - 1, 1);
        return (Math.Clamp(p.X * 65535 / w, 0, 65535), Math.Clamp(p.Y * 65535 / h, 0, 65535));
    }

    public bool IsDown(int vk) => _phys?.IsDown(vk) ?? (GetAsyncKeyState(vk) & 0x8000) != 0;

    public bool IsLockOn(int vk) => (GetKeyState(vk) & 1) != 0;

    public void Sleep(double ms)
    {
        if (ms <= 0) return;
        double end = Clock.Elapsed.TotalMilliseconds + ms;
        while (!Stopped)
        {
            double left = end - Clock.Elapsed.TotalMilliseconds;
            if (left <= 0) return;
            // Long waits in short pieces, so stopping is instant.
            Wait(Math.Min(left, 40));
        }
    }

    /// <summary>Waits about <paramref name="ms"/> ms, accurately: Windows' high-resolution timer, then a short spin.</summary>
    public void Wait(double ms)
    {
        if (ms <= 0) return;
        double end = Clock.Elapsed.TotalMilliseconds + ms;
        if (_timer == IntPtr.Zero) _timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /* HIGH_RESOLUTION */, 0x1F0003);
        if (_timer != IntPtr.Zero && ms > 1.2)
        {
            long due = -(long)((ms - 0.6) * 10_000);
            if (SetWaitableTimerEx(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0)) WaitForSingleObject(_timer, 1000);
        }
        else if (ms > 2) Thread.Sleep((int)(ms - 1.5));
        while (Clock.Elapsed.TotalMilliseconds < end) Thread.SpinWait(40);
    }

    /// <summary>Lets go of everything this macro is holding.</summary>
    public void ReleaseAll()
    {
        int[] keys, buttons;
        lock (_keys) { keys = _keys.ToArray(); _keys.Clear(); }
        lock (_buttons) { buttons = _buttons.ToArray(); _buttons.Clear(); }
        foreach (var k in keys) SendKey(k, false);
        foreach (var b in buttons) SendButton(b, false);
    }

    public void Dispose()
    {
        Stopped = true;
        ReleaseAll();
        if (_timer != IntPtr.Zero) { CloseHandle(_timer); _timer = IntPtr.Zero; }
    }

    static void SendKey(int scan, bool down)
    {
        uint flags = KEYEVENTF_SCANCODE | (down ? 0 : KEYEVENTF_KEYUP) | ((scan & 0x100) != 0 ? KEYEVENTF_EXTENDEDKEY : 0);
        Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = (ushort)(scan & 0xFF), dwFlags = flags } } });
    }

    static void SendButton(int button, bool down)
    {
        uint flags; uint data = 0;
        switch (button)
        {
            case 1: flags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
            case 2: flags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
            case 3: flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 1; break;
            case 4: flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 2; break;
            default: flags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
        }
        Send(new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } } });
    }

    static void Send(INPUT input) => SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());

    // ───────── Windows ─────────

    const int INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4, MOUSEEVENTF_RIGHTDOWN = 0x8,
        MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_XDOWN = 0x80,
        MOUSEEVENTF_XUP = 0x100, MOUSEEVENTF_WHEEL = 0x800, MOUSEEVENTF_ABSOLUTE = 0x8000;
    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4, KEYEVENTF_SCANCODE = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public int type; public InputUnion u; }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, IntPtr wakeContext, uint tolerableDelay);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
