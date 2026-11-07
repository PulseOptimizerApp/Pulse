using System.Security.Cryptography;
using Pulse.Core;
using Pulse.Core.Games;
using Pulse.Core.Games.Live;

namespace Pulse.Ui;

/// <summary>
/// Connects the games' own data to Clips: big plays get clipped automatically, and every match ends with a recap
/// (map, agent/champion, result, K/D/A, headshots, rank) like Medal's. Uses only what games offer for this:
/// Valve's Game State Integration (CS2, Dota 2), Riot's Live Client Data API (League) and the Riot Client's own
/// match history (VALORANT). Nothing reads game memory.
/// </summary>
public static class MatchService
{
    static GsiServer _gsi;
    static readonly LeagueLive League = new();
    static readonly ValorantMatches Valorant = new();
    static System.Threading.Timer _watch;
    static bool _leagueOn, _valorantOn;
    static string _sessionGame;
    static DateTime _sessionStart, _sessionSeen;

    /// <summary>Games whose recaps come from their own match data (the rest get session recaps).</summary>
    static readonly HashSet<string> OwnRecaps = new(StringComparer.OrdinalIgnoreCase) { "VALORANT", "Counter-Strike 2", "League of Legends", "Dota 2" };

    static Core.Clips.ClipSettings S => TweakEngine.State.Clips;

    /// <summary>Raised (on the UI thread) when a match recap is added.</summary>
    public static event Action<MatchRecord> MatchAdded;

    /// <summary>Games set up for auto clips (after Init).</summary>
    public static List<string> Ready { get; private set; } = new();

    public static void Init()
    {
        MatchEvents.Highlight += OnHighlight;
        MatchEvents.Finished += OnFinished;
        MatchEvents.Moments += list => MomentLog.Add(list);
        _watch = new System.Threading.Timer(_ => Watch(), null, 3000, 5000);
    }

    public static void Shutdown()
    {
        _watch?.Dispose();
        _gsi?.Dispose();
        League.Dispose();
        Valorant.Dispose();
    }

    static bool Wanted => S is { Enabled: true } && (S.AutoClip || S.MatchRecaps);

    /// <summary>Every 5 seconds: start or stop the game integrations.</summary>
    static void Watch()
    {
        try
        {
            if (!Wanted)
            {
                if (_leagueOn) { League.Stop(); _leagueOn = false; }
                if (_valorantOn) { Valorant.Dispose(); _valorantOn = false; }
                return;
            }
            if (_gsi == null)
            {
                if (string.IsNullOrEmpty(S.GsiToken)) { S.GsiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)); TweakEngine.Save(); }
                Ready = GsiServer.Install(S.GsiToken);
                var gsi = new GsiServer(S.GsiToken);
                if (gsi.Start()) _gsi = gsi; else gsi.Dispose();
                if (Ready.Count > 0) TweakEngine.AddLog("clips", "Auto clips set up for " + string.Join(", ", Ready));
            }
            bool league = LeagueLive.InGame();
            if (league && !_leagueOn) { League.Start(); _leagueOn = true; }
            else if (!league && _leagueOn) { League.Stop(); _leagueOn = false; }
            if (S.MatchRecaps) TrackSession(ClipsService.Game);
            if (S.MatchRecaps)
            {
                bool val = ValorantMatches.Running();
                if (val && !_valorantOn) { Valorant.Start(); _valorantOn = true; }
                else if (!val && _valorantOn) { Valorant.Stop(); _valorantOn = false; }
            }
        }
        catch { }
    }

    /// <summary>
    /// Every other game: a recap card for each play session (5 minutes or more) with how long you played and its
    /// clips. Marvel Rivals, Overwatch 2, Fortnite, Apex and the rest don't share kills or results with other apps.
    /// </summary>
    static void TrackSession(string game)
    {
        var now = DateTime.Now;
        if (!string.IsNullOrEmpty(game) && game != "Any other game" && !OwnRecaps.Contains(game))
        {
            if (_sessionGame != game)
            {
                EndSession();
                _sessionGame = game;
                _sessionStart = now;
            }
            _sessionSeen = now;
            return;
        }
        // Gone for 30 seconds (not just a loading screen or an alt-tab blip): the session is over.
        if (_sessionGame != null && (now - _sessionSeen).TotalSeconds > 30) EndSession();
    }

    static void EndSession()
    {
        if (_sessionGame == null) return;
        var game = _sessionGame;
        _sessionGame = null;
        if ((_sessionSeen - _sessionStart).TotalMinutes < 5) return;
        var m = new MatchRecord { Game = game, Character = game, Mode = "Session", Session = true, Start = _sessionStart, End = _sessionSeen };
        try
        {
            m.Highlights = Core.Clips.ClipLibrary.List(S)
                .Where(c => c.Created >= m.Start && c.Created <= m.End.AddMinutes(1) && !string.IsNullOrEmpty(c.Title))
                .Select(c => c.Title).ToList();
        }
        catch { }
        OnFinished(m);
    }

    static void OnHighlight(string game, string label, int seconds)
    {
        if (!S.AutoClip || ClipsService.State != ClipsState.Recording) return;
        // A moment later, so the last kill is in the replay.
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                var path = await ClipsService.SaveAutoClip(seconds, label);
                if (path != null) TweakEngine.AddLog("clips", $"Auto clip: {label} in {game}");
            }).Task.Unwrap();
        });
    }

    static void OnFinished(MatchRecord m)
    {
        if (!S.MatchRecaps) return;
        MatchStore.Add(m);
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            int clips = 0;
            try { clips = Core.Clips.ClipLibrary.List(S).Count(c => c.Created >= m.Start.AddMinutes(-1) && c.Created <= m.End.AddMinutes(2)); } catch { }
            if (m.Session)
            {
                // Quiet for sessions: only worth a popup when there's something to watch.
                if (clips > 0) ClipToast.Show(Loc.F("{0} session", m.Game), Loc.F("Played {0}", m.Played) + " · " + Loc.F("{0} clips", clips), true);
                MatchAdded?.Invoke(m);
                return;
            }
            var title = string.Join(" · ", new[] { Loc.T(m.Result), m.Map, m.Score }.Where(x => !string.IsNullOrEmpty(x)));
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(m.Character)) parts.Add(m.Character);
            parts.Add(m.Kda);
            if (m.HeadshotPercent >= 0) parts.Add(Loc.F("{0}% HS", m.HeadshotPercent));
            if (clips > 0) parts.Add(Loc.F("{0} clips", clips));
            ClipToast.Show(title.Length > 0 ? title : m.Game, string.Join(" · ", parts), m.Result != "Loss");
            MatchAdded?.Invoke(m);
        });
    }
}
