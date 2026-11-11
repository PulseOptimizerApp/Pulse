using System.Diagnostics;
using System.Runtime.InteropServices;
using Pulse.Core;
using Pulse.Core.Display;

namespace Pulse.Ui;

/// <summary>
/// Makes VALORANT more vibrant only while it's the window in front: the graphics card's vibrance goes up when you
/// switch to the game and back to normal when you leave it (and when Pulse closes).
/// </summary>
public static class VibranceService
{
    static System.Threading.Timer _timer;
    static Vibrance _card;
    static bool _boosted, _looked;
    static int _busy;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    static VibranceSettings S => TweakEngine.State.Vibrance ??= new VibranceSettings();

    /// <summary>The graphics card's control, or null when there's none (looked up once).</summary>
    public static Vibrance Card
    {
        get
        {
            if (!_looked) { _looked = true; _card = Vibrance.Find(); }
            return _card;
        }
    }

    public static void Init() => _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(700));

    public static void Shutdown()
    {
        _timer?.Dispose();
        if (_boosted) try { Card?.Set(50); } catch { }
        _boosted = false;
    }

    /// <summary>Right now: back to normal (switching off) or the game's level if it's in front.</summary>
    public static void Refresh() => Tick();

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
            if (want == _boosted && !(want && _level != s.Level)) return;
            if (Card == null) return;
            if (want) { if (Card.Set(s.Level)) { _boosted = true; _level = s.Level; } }
            else if (Card.Set(50)) _boosted = false;
        }
        catch { }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    static int _level = -1;

    /// <summary>Shows a level on the screen for a moment (to try it), then goes back to normal.</summary>
    public static async Task Preview(int level)
    {
        if (Card == null) return;
        await Task.Run(() => Card.Set(level));
        await Task.Delay(3000);
        if (!_boosted) await Task.Run(() => Card.Set(50));
    }
}
