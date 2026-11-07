using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Crosshair;
using Pulse.Core.Measurement;

namespace Pulse.Ui;

/// <summary>
/// Shows the crosshair overlay: a small see-through window in the middle of the screen that mouse clicks pass
/// through. It never touches the game (no injection, no reading its memory), like Crosshair X. Works over games in
/// borderless or windowed mode; exclusive fullscreen draws over every window, so there it can't show.
/// Costs nothing while off: no timer, no window.
/// </summary>
public static class CrosshairService
{
    public static CrosshairSettings S => TweakEngine.State.Crosshair ??= new CrosshairSettings();

    static CrosshairWindow _win;
    static DispatcherTimer _timer;
    static bool _hiddenByKey, _checking;
    static HwndSource _hotkeyWindow;
    const int HotkeyId = 0x5C01;

    /// <summary>Whether the crosshair is on screen right now.</summary>
    public static bool Showing => _win?.IsVisible == true;
    public static event Action Changed;

    public static void Init()
    {
        if (S.Enabled) Refresh();
    }

    /// <summary>Settings changed (or Pulse started): start, stop or redraw.</summary>
    public static void Refresh()
    {
        if (!S.Enabled)
        {
            _timer?.Stop();
            Hide();
            UnregisterHotkey();
            Changed?.Invoke();
            return;
        }
        RegisterHotkey();
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, async (_, _) => await Tick(), Application.Current.Dispatcher);
        _timer.Start();
        _win?.Apply(S);
        _ = Tick();
    }

    public static void Shutdown()
    {
        _timer?.Stop();
        Hide();
        UnregisterHotkey();
    }

    static async Task Tick()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            bool want = S.Enabled && !_hiddenByKey;
            if (want && S.OnlyInGames) want = await Task.Run(GameDetector.RunningGame) != null;
            if (want) Show(); else Hide();
            _win?.KeepOnTop(S);
        }
        catch { }
        finally { _checking = false; }
    }

    static void Show()
    {
        if (_win == null) { _win = new CrosshairWindow(); _win.Apply(S); }
        if (!_win.IsVisible) { _win.Show(); _win.Apply(S); Changed?.Invoke(); }
    }

    static void Hide()
    {
        if (_win == null) return;
        _win.Close();
        _win = null;
        Changed?.Invoke();
    }

    // ───────── Ctrl+Shift+X: show / hide ─────────

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000, VK_X = 0x58;

    /// <summary>False when another app already owns Ctrl+Shift+X.</summary>
    public static bool HotkeyWorks { get; private set; } = true;

    static void RegisterHotkey()
    {
        if (!S.Hotkey) { UnregisterHotkey(); return; }
        if (_hotkeyWindow != null) return;
        _hotkeyWindow = new HwndSource(new HwndSourceParameters("Pulse crosshair hotkey") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /* message-only */ });
        _hotkeyWindow.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x0312 && w.ToInt32() == HotkeyId)
            {
                _hiddenByKey = !_hiddenByKey;
                _ = Tick();
                handled = true;
            }
            return IntPtr.Zero;
        });
        HotkeyWorks = RegisterHotKey(_hotkeyWindow.Handle, HotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_X);
    }

    static void UnregisterHotkey()
    {
        if (_hotkeyWindow == null) return;
        UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
        _hotkeyWindow.Dispose();
        _hotkeyWindow = null;
        _hiddenByKey = false;
    }
}

/// <summary>The see-through, click-through window that holds the crosshair, placed by screen pixels.</summary>
sealed class CrosshairWindow : Window
{
    readonly CrosshairVisual _visual = new();
    IntPtr _hwnd;

    public CrosshairWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        Focusable = false; IsHitTestVisible = false; Width = 64; Height = 64; Title = "Pulse crosshair";
        Content = _visual;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
    }

    CrosshairSettings _s;

    public void Apply(CrosshairSettings s)
    {
        _s = s;
        _visual.Style = s.Style;
        if (_hwnd == IntPtr.Zero) return;
        // Leave it out of clips and screenshots if asked (Windows 10 2004+; older Windows just shows it).
        SetWindowDisplayAffinity(_hwnd, s.HideFromCapture ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        Place();
    }

    /// <summary>Puts the window's center exactly on the monitor's center pixel (plus the user's nudge), in screen pixels.</summary>
    void Place()
    {
        if (_hwnd == IntPtr.Zero || _s == null) return;
        var mon = IntPtr.Zero;
        if (_s.Monitor == "game" && GameDetector.GameWindow is var g && g != IntPtr.Zero) mon = MonitorFromWindow(g, 2);
        if (mon == IntPtr.Zero) mon = MonitorFromPoint(default, 1 /* primary */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return;
        var m = mi.rcMonitor;
        int half = (int)Math.Ceiling(_s.Style.Reach() * 1.0) + 4;
        int cx = m.Left + (m.Right - m.Left) / 2 + _s.OffsetX, cy = m.Top + (m.Bottom - m.Top) / 2 + _s.OffsetY;
        SetWindowPos(_hwnd, HWND_TOPMOST, cx - half, cy - half, half * 2, half * 2, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Some games push themselves on top when they get focus: put the crosshair back above them.</summary>
    public void KeepOnTop(CrosshairSettings s)
    {
        if (_hwnd == IntPtr.Zero) return;
        _s = s;
        Place();
    }

    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
