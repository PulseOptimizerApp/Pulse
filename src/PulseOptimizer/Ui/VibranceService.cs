using System.Diagnostics;
using System.Runtime.InteropServices;
using Pulse.Core;
using Pulse.Core.Display;

namespace Pulse.Ui;

/// <summary>
/// Pulse Color: puts your look on only while VALORANT is the window in front, and puts back exactly what you had
/// (your own vibrance and screen curves) when you leave the game, when Pulse closes, and after a crash.
/// </summary>
public static class VibranceService
{
    static System.Threading.Timer _timer;
    static Vibrance _card;
    static bool _on, _looked;
    static ColorLook _applied;
    static int _busy;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    static VibranceSettings S => TweakEngine.State.Vibrance ??= new VibranceSettings();

    /// <summary>The graphics card's vibrance control, or null (Pulse Color still works through the screen curves).</summary>
    public static Vibrance Card
    {
        get
        {
            if (!_looked) { _looked = true; try { _card = Vibrance.Find(); } catch { } }
            return _card;
        }
    }

    public static void Init()
    {
        // Left on by a crash: put the player's own colours back first.
        try { PutBack(); } catch { }
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(700));
    }

    public static void Shutdown()
    {
        _timer?.Dispose();
        try { PutBack(); } catch { }
    }

    public static void Refresh() => Task.Run(Tick);

    static bool GameInFront()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        GetWindowThreadProcessId(h, out var pid);
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName.StartsWith("VALORANT", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var s = S;
            bool want = s.Enabled && GameInFront();
            if (want)
            {
                var look = s.Look;
                if (_on && look == _applied) return;
                if (!_on)
                {
                    // Remember exactly what the player has, before changing anything.
                    s.OwnVibrance = Card?.Get() ?? -1;
                    s.OwnRamps = PulseColor.Capture();
                    TweakEngine.Save();
                }
                // Never less vibrant than the player's own setting.
                if (Card != null) Card.Set(Math.Max(look.Vibrance, s.OwnVibrance));
                PulseColor.Set(look, s.OwnRamps ?? new());
                _on = true;
                _applied = look;
            }
            else if (_on || s.OwnVibrance >= 0 || s.OwnRamps != null) PutBack();
        }
        catch { }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Puts back the player's own vibrance and screen curves, if Pulse changed them.</summary>
    static void PutBack()
    {
        var s = S;
        if (s.OwnVibrance >= 0) Card?.Set(s.OwnVibrance);
        if (s.OwnRamps is { Count: > 0 }) PulseColor.Restore(s.OwnRamps);
        bool changed = s.OwnVibrance >= 0 || s.OwnRamps != null;
        s.OwnVibrance = -1;
        s.OwnRamps = null;
        _on = false;
        _applied = null;
        if (changed) TweakEngine.Save();
    }
}
