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
        ValorantMatches.InGame += () =>
        {
            if (LobbyOn) LobbyOverlay.MatchFound();
            if (S?.ValorantHighlights == true)
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    var why = ClipsService.StartMatchTape();
                    if (why != null) TweakEngine.AddLog("clips", "No VALORANT highlights this match: " + why);
                });
        };
        // When the match is in Riot's records: cut its best plays out of the match file.
        ValorantMatches.MatchSaved += vm =>
        {
            if (S?.ValorantHighlights == true || ClipsService.TapeActive)
                _ = Task.Run(() => ClipsService.FinishMatchTape(vm));
        };
        // After the match: a few seconds after the recap, the match summary and (if on) both teams (no risk: it's over).
        ValorantMatches.MatchSaved += vm =>
        {
            bool summary = S?.PostMatchSummary == true, lobby = S?.AfterMatchLobby == true;
            if (!summary && !lobby) return;
            _ = Task.Delay(6000).ContinueWith(_ => LobbyOverlay.AfterMatch(vm, summary, lobby));
        };
        _watch = new System.Threading.Timer(_ => Watch(), null, 3000, 5000);
        BrawlhallaWatch.Init();
        StoreWatch.Init();
        ConsoleSwitchService.Init();
    }

    public static void Shutdown()
    {
        _watch?.Dispose();
        BrawlhallaWatch.Shutdown();
        StoreWatch.Shutdown();
        ConsoleSwitchService.Shutdown();
        _gsi?.Dispose();
        League.Dispose();
        Valorant.Dispose();
    }

    static bool Wanted => S is { Enabled: true } && (S.AutoClip || S.MatchRecaps);
    static bool LobbyOn => S is { LiveLobby: true, LiveLobbyRisksAccepted: true };
    /// <summary>VALORANT's match watcher is needed for the live warnings and the after-match lobby.</summary>
    static bool ValorantWatch => LobbyOn || S is { AfterMatchLobby: true } || S is { PostMatchSummary: true } || S is { ValorantHighlights: true };

    /// <summary>Every 5 seconds: start or stop the game integrations.</summary>
    static void Watch()
    {
        // A match file that never got its match (a remake, a crash): don't keep it past 100 minutes.
        if (ClipsService.TapeActive && ClipsService.TapeSeconds > 100 * 60)
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(ClipsService.DropMatchTape);
        try
        {
            if (!Wanted)
            {
                if (_leagueOn) { League.Stop(); _leagueOn = false; }
                // The lobby features only need VALORANT's match watcher.
                if (ValorantWatch)
                {
                    bool v = ValorantMatches.Running();
                    if (v && !_valorantOn) { Valorant.Start(); _valorantOn = true; }
                    else if (!v && _valorantOn) { Valorant.Stop(); _valorantOn = false; }
                }
                else if (_valorantOn) { Valorant.Dispose(); _valorantOn = false; }
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
            if (S.MatchRecaps || ValorantWatch)
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
            string best = null;
            try
            {
                var inMatch = Core.Clips.ClipLibrary.List(S).Where(c => c.Created >= m.Start.AddMinutes(-1) && c.Created <= m.End.AddMinutes(2)).ToList();
                clips = inMatch.Count;
                // The best clip: a big play (auto clips have titles like "3K"), otherwise the latest one.
                best = (inMatch.Where(c => !string.IsNullOrEmpty(c.Title)).OrderByDescending(c => c.Created).FirstOrDefault()
                        ?? inMatch.OrderByDescending(c => c.Created).FirstOrDefault())?.Path;
            }
            catch { }
            if (m.Session)
            {
                // Quiet for sessions: only worth a popup when there's something to watch.
                if (clips > 0) ClipToast.Show(Loc.F("{0} session", m.Game), Loc.F("Played {0}", m.Played) + " · " + Loc.F("{0} clips", clips), true);
                MatchAdded?.Invoke(m);
                return;
            }
            // The recap card, laid out like Medal's: agent, result, rank, score, K/D/A, headshots, big plays, best clip.
            ClipToast.Corner = S.PopupCorner;
            ClipToast.Recap(m, clips, best);
            MatchAdded?.Invoke(m);
        });
    }
}
