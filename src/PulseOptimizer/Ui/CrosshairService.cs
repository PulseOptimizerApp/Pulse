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
    const int HotkeyId = 0x5C01, CycleId = 0x5C02;
    static DispatcherTimer _input;
    static string _lastGame;
    static double _spread;

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
        UpdateInput();
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
            string game = null;
            if (S.OnlyInGames || S.GameCrosshairs.Count > 0) game = await Task.Run(GameDetector.RunningGame);
            if (want && S.OnlyInGames) want = game != null;
            if (game != _lastGame)
            {
                _lastGame = game;
                if (game != null && GameCrosshairFor(game) is { } sc) { S.Style = sc.Style.Copy(); _win?.Apply(S); Changed?.Invoke(); }
            }
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
        UpdateInput();
    }

    static void Hide()
    {
        _input?.Stop();
        if (_win == null) return;
        _win.Close();
        _win = null;
        Changed?.Invoke();
    }

    /// <summary>The saved crosshair linked to this game, if any.</summary>
    public static SavedCrosshair GameCrosshairFor(string game)
    {
        foreach (var (g, name) in S.GameCrosshairs)
            if (g.Equals(game, StringComparison.OrdinalIgnoreCase)) return S.Saved.FirstOrDefault(x => x.Name == name);
        return null;
    }

    /// <summary>The game running right now (as Pulse names it), for linking a crosshair to it.</summary>
    public static string CurrentGame => _lastGame;

    // ───────── mouse buttons: hide while aiming, spread while firing ─────────
    // Reads the button state 60 times a second while the crosshair is on screen (no hooks, nothing injected),
    // and only when one of these options is on.

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    static void UpdateInput()
    {
        bool need = _win != null && (S.HideWhileAiming || S.Bloom);
        if (!need) { _input?.Stop(); _win?.SetMotion(0, true); return; }
        _input ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => InputTick(), Application.Current.Dispatcher);
        _input.Start();
    }

    static void InputTick()
    {
        if (_win == null) { _input?.Stop(); return; }
        bool left = (GetAsyncKeyState(0x01) & 0x8000) != 0, right = (GetAsyncKeyState(0x02) & 0x8000) != 0;
        double target = S.Bloom && left ? S.BloomPx : 0;
        _spread += (target - _spread) * (target > _spread ? 0.5 : 0.18);
        if (Math.Abs(_spread - target) < 0.05) _spread = target;
        _win.SetMotion(_spread, !(S.HideWhileAiming && right));
    }

    // ───────── Ctrl+Shift+X: show / hide ─────────

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000, VK_X = 0x58;

    /// <summary>False when another app already owns Ctrl+Shift+X.</summary>
    public static bool HotkeyWorks { get; private set; } = true;

    static (bool, bool) _registered;

    static void RegisterHotkey()
    {
        if (_hotkeyWindow != null && _registered == (S.Hotkey, S.CycleHotkey)) return; // settings refresh often; only redo on change
        UnregisterHotkey(keepHidden: true);
        _registered = (S.Hotkey, S.CycleHotkey);
        if (!S.Hotkey && !S.CycleHotkey) { _hiddenByKey = false; return; }
        _hotkeyWindow = new HwndSource(new HwndSourceParameters("Pulse crosshair hotkey") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /* message-only */ });
        _hotkeyWindow.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x0312 && w.ToInt32() == HotkeyId)
            {
                _hiddenByKey = !_hiddenByKey;
                _ = Tick();
                handled = true;
            }
            else if (msg == 0x0312 && w.ToInt32() == CycleId)
            {
                Cycle();
                handled = true;
            }
            return IntPtr.Zero;
        });
        HotkeyWorks = !S.Hotkey || RegisterHotKey(_hotkeyWindow.Handle, HotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_X);
        if (!S.Hotkey) _hiddenByKey = false;
        CycleHotkeyWorks = !S.CycleHotkey || RegisterHotKey(_hotkeyWindow.Handle, CycleId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x43 /* C */);
    }

    /// <summary>False when another app already owns Ctrl+Shift+C.</summary>
    public static bool CycleHotkeyWorks { get; private set; } = true;

    /// <summary>Ctrl+Shift+C: the next saved crosshair.</summary>
    static void Cycle()
    {
        if (S.Saved.Count == 0) return;
        int i = S.Saved.FindIndex(x => PulseCode.Write(x.Style) == PulseCode.Write(S.Style));
        S.Style = S.Saved[(i + 1) % S.Saved.Count].Style.Copy();
        _win?.Apply(S);
        TweakEngine.Save();
        Changed?.Invoke();
    }

    static void UnregisterHotkey(bool keepHidden = false)
    {
        if (_hotkeyWindow == null) return;
        UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
        UnregisterHotKey(_hotkeyWindow.Handle, CycleId);
        _hotkeyWindow.Dispose();
        _hotkeyWindow = null;
        if (!keepHidden) _hiddenByKey = false;
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

    /// <summary>Bloom spread and visibility from the mouse buttons (cheap: no window changes).</summary>
    public void SetMotion(double spread, bool visible)
    {
        _visual.Spread = spread;
        _visual.Opacity = visible ? 1 : 0;
    }

    public void Apply(CrosshairSettings s)
    {
        _s = s;
        _visual.Crosshair = s.Style;
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
        int half = (int)Math.Ceiling(_s.Style.Reach() + (_s.Bloom ? _s.BloomPx * Math.Max(0.1, _s.Style.Size) * 1.42 : 0)) + 4;
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
