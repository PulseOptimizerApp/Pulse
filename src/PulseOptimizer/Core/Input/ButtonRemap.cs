using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Pulse.Core.Input;

/// <summary>
/// Gives the mouse's middle, back and forward buttons another job (a key, another click, scroll, media keys, or
/// nothing), for any mouse, while Pulse runs. Used when the mouse can't store remaps itself.
///
/// Windows' low-level mouse hook: Windows calls it in Pulse's own process before the press reaches anything else, and
/// Pulse either lets the press through or swallows it and sends the chosen input with SendInput. Nothing is loaded
/// into another program. Input that programs send (including Pulse's own) is always let through, so a remap can't
/// trigger itself. Left and right click are never remapped, so the mouse can always click. Runs only while at least
/// one button is remapped.
/// </summary>
public static class ButtonRemap
{
    /// <summary>The buttons that can be remapped, as saved: middle, back (mouse 4), forward (mouse 5).</summary>
    public static readonly string[] Buttons = { "middle", "back", "forward" };

    /// <summary>
    /// What a button can do, as saved: "" (its normal job), "off", "button:N" (0 left … 4 forward), "wheel:up",
    /// "wheel:down", "key:VK" (a keyboard key, held as long as the button is), "media:VK" (media and volume keys).
    /// </summary>
    public static bool IsValid(string action) => Parse(action) is not null;

    static readonly object Gate = new();
    static Dictionary<int, Act> _map = new();
    static Thread _thread;
    static uint _threadId;
    static readonly HookProc Proc = OnMouse;   // kept alive for as long as Windows may call it
    static readonly BlockingCollection<(Act Act, bool Down)> Queue = new();
    static Thread _sender;
    static readonly HashSet<(char Kind, int Code)> Held = new();

    record Act(char Kind, int Code);   // o off, b button, w wheel, k key (scan code), m media (virtual key)

    static Act Parse(string action)
    {
        if (string.IsNullOrEmpty(action)) return null;
        if (action == "off") return new Act('o', 0);
        if (action == "wheel:up") return new Act('w', 1);
        if (action == "wheel:down") return new Act('w', -1);
        var parts = action.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int n)) return null;
        return parts[0] switch
        {
            "button" when n is >= 0 and <= 4 => new Act('b', n),
            "key" when n is > 0 and < 256 && !Keys.IsMouse(n) => new Act('k', Keys.ScanOfVk(n)),
            "media" when n is >= 0xAD and <= 0xB3 => new Act('m', n),
            _ => null,
        };
    }

    /// <summary>Uses these remaps (button name → action); starts or stops the hook as needed.</summary>
    public static void Apply(IReadOnlyDictionary<string, string> remaps)
    {
        var map = new Dictionary<int, Act>();
        if (remaps != null)
            for (int i = 0; i < Buttons.Length; i++)
                if (remaps.TryGetValue(Buttons[i], out var a) && Parse(a) is { } act) map[i + 2] = act;
        lock (Gate)
        {
            _map = map;
            if (map.Count > 0 && _thread == null && OperatingSystem.IsWindows()) Start();
            else if (map.Count == 0 && _thread != null) Stop();
        }
    }

    public static bool Running { get { lock (Gate) return _thread != null; } }

    /// <summary>Stops remapping and lets go of anything a remap is holding.</summary>
    public static void Shutdown() { lock (Gate) { _map = new(); if (_thread != null) Stop(); } }

    static void Start()
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var hook = SetWindowsHookEx(WH_MOUSE_LL, Proc, GetModuleHandle(null), 0);
            ready.Set();
            if (hook == IntPtr.Zero) { TweakEngine.AddLog("mouse", "Button remaps couldn't start: Windows refused the mouse hook."); return; }
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
            UnhookWindowsHookEx(hook);
        }) { IsBackground = true, Name = "Pulse button remaps" };
        _thread.Start();
        ready.Wait(2000);
        _sender ??= new Thread(Send) { IsBackground = true, Name = "Pulse button remap output" };
        if (!_sender.IsAlive) _sender.Start();
    }

    static void Stop()
    {
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread = null;
        // Let go of whatever a remap was holding when it stopped.
        lock (Held) foreach (var (kind, code) in Held.ToList()) Queue.Add((new Act(kind, code), false));
    }

    // Called by Windows for every mouse event. Must answer quickly (Windows drops slow hooks), so the output is
    // handed to another thread.
    static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((info.flags & LLMHF_INJECTED) == 0)
            {
                int msg = (int)wParam, button = -1;
                bool down = false;
                switch (msg)
                {
                    case WM_MBUTTONDOWN: button = 2; down = true; break;
                    case WM_MBUTTONUP: button = 2; break;
                    case WM_XBUTTONDOWN: button = (info.mouseData >> 16) == 1 ? 3 : 4; down = true; break;
                    case WM_XBUTTONUP: button = (info.mouseData >> 16) == 1 ? 3 : 4; break;
                }
                if (button >= 0 && _map.TryGetValue(button, out var act))
                {
                    Queue.Add((act, down));
                    return (IntPtr)1;   // swallowed: the remap replaces it
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    static void Send()
    {
        foreach (var (act, down) in Queue.GetConsumingEnumerable())
        {
            try
            {
                switch (act.Kind)
                {
                    case 'b': WindowsInput.SendButtonRaw(act.Code, down); break;
                    case 'w': if (down) WindowsInput.SendWheelRaw(act.Code); break;
                    case 'k': WindowsInput.SendKeyRaw(act.Code, down); break;
                    case 'm': WindowsInput.SendVkRaw(act.Code, down); break;
                }
                if (act.Kind is 'b' or 'k' or 'm')
                    lock (Held) { if (down) Held.Add((act.Kind, act.Code)); else Held.Remove((act.Kind, act.Code)); }
            }
            catch { }
        }
    }

    // ───────── Windows ─────────

    const int WH_MOUSE_LL = 14;
    const uint WM_QUIT = 0x12;
    const int WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C;
    const uint LLMHF_INJECTED = 0x1;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
}
