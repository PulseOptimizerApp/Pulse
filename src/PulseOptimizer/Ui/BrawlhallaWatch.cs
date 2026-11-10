using System.Diagnostics;
using Pulse.Core;
using Pulse.Core.Games.Brawlhalla;

namespace Pulse.Ui;

/// <summary>
/// Logs your Brawlhalla matches as you play: while Brawlhalla is open (and for a few minutes after it closes, since
/// the last match's stats can land late), checks your stats once a minute. When they changed, the difference becomes
/// matches (see <see cref="BrawlhallaMatches"/>) and a small summary pops up in the corner. Needs "This is me" set on
/// the Brawlhalla page. Brawlhalla's public API only: nothing reads the game.
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

    public static void Shutdown() => _timer?.Dispose();

    static bool GameOpen()
    {
        var ps = Process.GetProcessesByName("Brawlhalla");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    static void Tick()
    {
        if (S is not { MyId: > 0, TrackMatches: true } s) return;
        var now = DateTime.Now;
        if (GameOpen()) _lastOpen = now;
        if (now - _lastOpen > TimeSpan.FromMinutes(5) || now - _lastCheck < TimeSpan.FromSeconds(60)) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _lastCheck = now;
        _ = Task.Run(async () =>
        {
            try
            {
                long id = s.MyId;
                var p = await Brawlhalla.Profile(id, lookUpTier: false);
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
