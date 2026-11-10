using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Pulse.Core;
using Pulse.Core.Games.Brawlhalla;

namespace Pulse.Ui;

/// <summary>
/// Logs your Brawlhalla matches as you play: while Brawlhalla is open (and for a few minutes after it closes, since
/// the last match's stats can land late), checks your stats once a minute. When they changed, the difference becomes
/// matches (see <see cref="BrawlhallaMatches"/>) and a small summary pops up in the corner. Needs "This is me" set on
/// the Brawlhalla page. Also owns Ctrl+Shift+B (Matchup) while Brawlhalla is open. Brawlhalla's public API only:
/// nothing reads the game.
/// </summary>
public static class BrawlhallaWatch
{
    static System.Threading.Timer _timer;
    static DateTime _lastOpen = DateTime.MinValue, _lastCheck = DateTime.MinValue;
    static int _busy;

    /// <summary>Raised on the UI thread after new matches were logged for a player.</summary>
    public static event Action<long> Logged;

    static BrawlhallaSettings S => TweakEngine.State?.Brawlhalla;

    public static void Init() => _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(15));

    public static void Shutdown()
    {
        _timer?.Dispose();
        try { System.Windows.Application.Current?.Dispatcher.Invoke(UnregisterKey); } catch { }
    }

    // ───────── Ctrl+Shift+B: Matchup ─────────

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    const int KeyId = 0x4248;
    const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000, VK_B = 0x42;
    static HwndSource _keyWindow;
    static volatile bool _keyOn;

    /// <summary>False when another app already owns Ctrl+Shift+B.</summary>
    public static bool HotkeyWorks { get; private set; } = true;

    static void RegisterKey()
    {
        if (_keyWindow != null) return;
        _keyWindow = new HwndSource(new HwndSourceParameters("Pulse Brawlhalla hotkey") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /* message-only */ });
        _keyWindow.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x0312 && w.ToInt32() == KeyId)
            {
                handled = true;
                if (System.Windows.Application.Current?.MainWindow is MainWindow mw)
                {
                    mw.ShowFromTray();
                    mw.Navigate("brawlhalla", "matchup");
                }
            }
            return IntPtr.Zero;
        });
        HotkeyWorks = RegisterHotKey(_keyWindow.Handle, KeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_B);
    }

    static void UnregisterKey()
    {
        if (_keyWindow == null) return;
        UnregisterHotKey(_keyWindow.Handle, KeyId);
        _keyWindow.Dispose();
        _keyWindow = null;
    }

    static bool GameOpen()
    {
        var ps = Process.GetProcessesByName("Brawlhalla");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    static void Tick()
    {
        if (S is not { } s) return;
        var now = DateTime.Now;
        bool open = GameOpen();
        if (open) _lastOpen = now;
        // Ctrl+Shift+B only while Brawlhalla is open, so it isn't taken from other apps the rest of the time.
        bool wantKey = open && s.MatchupHotkey;
        if (wantKey != _keyOn)
        {
            _keyOn = wantKey;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(wantKey ? RegisterKey : UnregisterKey);
        }
        if (s is not { MyId: > 0, TrackMatches: true }) return;
        if (now - _lastOpen > TimeSpan.FromMinutes(5) || now - _lastCheck < TimeSpan.FromSeconds(60)) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _lastCheck = now;
        _ = Task.Run(async () =>
        {
            try
            {
                long id = s.MyId;
                var p = await Brawlhalla.Profile(id, lookUpTier: false);
                if (p?.SavedAt != null) return;   // their servers didn't answer: nothing new to compare
                var fresh = BrawlhallaMatches.Record(p);
                if (fresh.Count == 0) return;
                TweakEngine.AddLog("brawlhalla", $"Brawlhalla: logged {fresh.Sum(m => m.Games)} match(es)");
                var app = System.Windows.Application.Current;
                app?.Dispatcher.BeginInvoke(() => Logged?.Invoke(id));
                if (s.MatchPopup)
                {
                    var legends = await Brawlhalla.Legends();
                    var today = BrawlhallaMatches.Sessions(BrawlhallaMatches.For(id)).FirstOrDefault();
                    LobbyOverlay.ShowBrawlhalla(fresh, legends, today);
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }
}
