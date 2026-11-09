using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Pulse.Core;
using Pulse.Core.Games;
using Pulse.Core.Games.Live;
using Pulse.Core.Games.Valorant;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// The tracker's search, every match Pulse has (not only the coach's newest 60), downloading your older matches,
/// loading Riot IDs for a match's players, and the Pulse score badge.
/// </summary>
public partial class ValorantPage
{
    // Riot Client sign-in, checked in the background (searching needs it).
    static bool? _riotOk;
    static DateTime _riotAt;
    static bool _riotChecking;
    TextBlock _riotText;
    Ellipse _riotDot;
    TextBlock _trHint;
    /// <summary>When names were last asked for each match (asked again after a while if some are still missing).</summary>
    static readonly Dictionary<string, DateTime> _namesAsked = new();
    static bool _allNamesAsked;
    /// <summary>Why names couldn't be loaded, for the scoreboard (null when they could).</summary>
    static string _namesProblem;
    static bool _allBusy;

    /// <summary>Every saved match of the account the tracker shows (the coach only looks at the newest 60).</summary>
    static List<VMatch> TrackerMatches() => Memo("trackerms:" + ValorantMatches.SignedIn, () =>
    {
        var all = VStore.ForCoach();
        if (all.Count == 0) return all;
        var me = ValorantMatches.SignedIn is { } signed && all.Any(m => m.Me == signed) ? signed : all[0].Me;
        return all.Where(m => m.Me == me).OrderByDescending(m => m.StartUtc).ToList();
    });

    // ───────── the search card ─────────

    StackPanel SearchCard()
    {
        var sp = new StackPanel();
        var head = new Grid();
        head.Children.Add(Heading("Find a player"));
        // Signed in to Riot? Searching uses that login.
        var status = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _riotDot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        _riotText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(_riotDot);
        status.Children.Add(_riotText);
        head.Children.Add(status);
        sp.Children.Add(head);
        ShowRiotStatus();
        CheckRiot();

        var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { MaxWidth = 460 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox { Style = (Style)FindResource("SearchBox"), Tag = Loc.T("Riot ID, for example Name#NA1"), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 14 };
        row.Children.Add(box);
        var go = Btn("Search", true);
        go.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(go, 1);
        row.Children.Add(go);
        sp.Children.Add(row);

        _trHint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        sp.Children.Add(_trHint);
        if (_trStatus != null) Hint(_trStatus, _trBusy ? "busy" : "error");

        async Task Run(string text)
        {
            if (_trBusy) return;
            text = (text ?? "").Trim();
            if (ValorantSearch.ParseRiotId(text) is null)
            {
                Hint(text.Contains('#') ? Loc.T("That doesn't look like a Riot ID. Names are 3 to 16 characters and tags 3 to 5, like Name#NA1.")
                    : Loc.T("Add the player's tag after a #, like Name#NA1. It's under their name in VALORANT."), "error");
                return;
            }
            var me = TrackerMatches().FirstOrDefault()?.Me;
            // Your own Riot ID: your profile.
            if (me != null && NameBook.Get(me) is { } myName && myName.Equals(text, StringComparison.OrdinalIgnoreCase)) { _trSel = me; _trView = "overview"; Build(); return; }
            if (!S.PlayerSearch)
            {
                await TurnOnSearch();
                if (!S.PlayerSearch)
                {
                    // Still: someone from your own matches opens with what Pulse already has.
                    var hit = NameBook.Find(text, 50).FirstOrDefault(x => x.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
                    if (hit.Puuid != null && TrackerMatches().Any(m => m.Players.Any(x => x.Id == hit.Puuid))) { OpenLocal(hit.Puuid); return; }
                    Hint(Loc.T("Player search is off, so Pulse can only show players from your own matches."), "error");
                    return;
                }
            }
            if (_riotOk == false)
            {
                _riotAt = default;
                CheckRiot();
            }
            _trBusy = true;
            _trStatus = Loc.T("Searching…");
            Hint(_trStatus, "busy");
            go.IsEnabled = false;
            var progress = new Progress<string>(m =>
            {
                _trStatus = m.StartsWith("match:", StringComparison.Ordinal) && m.Split(':') is { Length: 3 } a ? Loc.F("Reading match {0} of {1}…", a[1], a[2]) : Loc.T(m);
                Hint(_trStatus, "busy");
            });
            ValorantSearch.Result r;
            try { r = await Task.Run(() => ValorantSearch.Search(text, progress)); }
            catch (Exception ex)
            {
                TweakEngine.AddLog("valorant", "Tracker search failed: " + ex.GetType().Name + ": " + ex.Message);
                r = new ValorantSearch.Result(null, "Search failed. Try again in a minute.");
            }
            _trBusy = false;
            go.IsEnabled = true;
            if (r.Profile != null)
            {
                _trSearched = r.Profile; _trSel = r.Profile.Puuid; _trQueue = null; _trAct = "all"; _trMatchId = null; _trView = "overview";
                S.RecentSearches.RemoveAll(x => x.Equals(r.Profile.Name, StringComparison.OrdinalIgnoreCase));
                S.RecentSearches.Insert(0, r.Profile.Name);
                if (S.RecentSearches.Count > 8) S.RecentSearches.RemoveRange(8, S.RecentSearches.Count - 8);
                TweakEngine.Save();
                TweakEngine.AddLog("valorant", "Tracker search");
                _trStatus = null;
                _riotOk = true;
                if (_tab == "tracker") Build();
                return;
            }
            // Not signed in to Riot is the usual reason: say it plainly.
            bool noRiot = r.Error != null && (r.Error.Contains("Riot Client", StringComparison.OrdinalIgnoreCase) || r.Error.Contains("log in", StringComparison.OrdinalIgnoreCase));
            if (noRiot) { _riotOk = false; ShowRiotStatus(); }
            _trStatus = noRiot ? Loc.T("You need to be signed in to the Riot Client to search. Open VALORANT (or the Riot Client), sign in, then search again.") : Loc.T(r.Error ?? "Search failed. Try again in a minute.");
            Hint(_trStatus, "error");
        }
        async Task TurnOnSearch()
        {
            bool ok = await MainWindow.Instance.Confirm(Loc.T("Turn on player search?"),
                Loc.T("Players from your own matches open without this. Searching anyone else reads that player's rank and recent matches through the Riot Client's login on your PC, the way tracker apps do. It isn't an official Riot feature, and Riot's own rules only let trackers show players who opted in, so it carries a risk to your account, like the live lobby. Pulse saves nothing that identifies the players you search: only anonymous numbers (rank and stats) that make skill comparisons more accurate."),
                Loc.T("Turn it on"), Loc.T("Cancel"));
            if (!ok) return;
            S.PlayerSearch = true;
            TweakEngine.AddLog("valorant", "VALORANT player search turned on (risk accepted)");
            TweakEngine.Save();
        }
        go.Click += async (_, _) => await Run(box.Text);
        _trSearchFor = async riotId => { box.Text = riotId; await Run(riotId); };
        box.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Run(box.Text); } };

        // Suggestions while typing: players you've met whose Riot ID matches.
        var pop = new Popup { PlacementTarget = box, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        var list = new StackPanel();
        pop.Child = new Border { Background = B(Color.FromRgb(0x1B, 0x1E, 0x24)), BorderBrush = B(Color.FromRgb(0x2C, 0x30, 0x38)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(4), MinWidth = 320, Child = list };
        box.TextChanged += (_, _) =>
        {
            var text = box.Text.Trim();
            list.Children.Clear();
            if (_trHint != null && !_trBusy) _trHint.Visibility = Visibility.Collapsed;
            if (text.Length < 2) { pop.IsOpen = false; return; }
            foreach (var (id, name) in NameBook.Find(text, 8))
            {
                var met = TrackerMatches().Count(m => m.Players.Any(p => p.Id == id));
                var g = new Grid();
                g.Children.Add(new TextBlock { Text = name, FontSize = 13, Foreground = Res("TextBrush") });
                if (met > 0) g.Children.Add(new TextBlock { Text = Loc.F("met {0} times", met), FontSize = 11, Foreground = Res("TextFaintBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
                var item = new Button { Style = (Style)FindResource("GhostButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 1, 0, 1), Content = g, BorderThickness = new Thickness(0) };
                var pick = name;
                item.Click += async (_, _) => { pop.IsOpen = false; box.Text = pick; await Run(pick); };
                list.Children.Add(item);
            }
            pop.IsOpen = list.Children.Count > 0 && box.IsKeyboardFocused;
        };

        // Recent searches and the players you meet most, one click away.
        var quick = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (S.RecentSearches.Count > 0)
        {
            quick.Children.Add(Faint(Loc.T("Recent:"), 11.5, 0).Also(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 8, 6); }));
            foreach (var r in S.RecentSearches.ToList())
            {
                var b = new Button { Style = (Style)FindResource("GhostButton"), Content = r, Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 6, 6), FontSize = 12 };
                var id = r;
                b.Click += async (_, _) => { box.Text = id; await Run(id); };
                quick.Children.Add(Removable(b, () => { S.RecentSearches.Remove(id); TweakEngine.Save(); Build(); }));
            }
            var clear = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Clear"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 6), FontSize = 12 };
            clear.Click += (_, _) => { S.RecentSearches.Clear(); TweakEngine.Save(); Build(); };
            quick.Children.Add(clear);
        }
        if (quick.Children.Count > 0) sp.Children.Add(quick);

        var foot = new TextBlock { FontSize = 11.5, Foreground = Res("TextFaintBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        foot.Inlines.Add(new System.Windows.Documents.Run(Loc.T(S.PlayerSearch
            ? "Player search is on: any Riot ID can be looked up while you're signed in to the Riot Client. "
            : "Player search is off: only players from your own matches open. ")));
        var toggle = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(Loc.T(S.PlayerSearch ? "Turn it off" : "Turn it on"))) { Foreground = Res("AccentBrush") };
        toggle.Click += async (_, _) =>
        {
            if (S.PlayerSearch) { S.PlayerSearch = false; _trSearched = null; TweakEngine.Save(); }
            else await TurnOnSearch();
            Build();
        };
        foot.Inlines.Add(toggle);
        sp.Children.Add(foot);
        return sp;
    }

    void Hint(string text, string kind)
    {
        if (_trHint == null) return;
        _trHint.Text = text;
        _trHint.Foreground = kind == "error" ? B(Color.FromRgb(0xFF, 0x7A, 0x6E)) : Res("TextDimBrush");
        _trHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    void ShowRiotStatus()
    {
        if (_riotText == null) return;
        var (c, t) = _riotOk switch
        {
            true => (Color.FromRgb(0x3D, 0xDC, 0x84), ValorantMatches.SignedIn is { } me && NameBook.Get(me) is { } n ? Loc.F("Signed in to Riot as {0}", n) : Loc.T("Signed in to Riot")),
            false => (Color.FromRgb(0xFA, 0xD6, 0x63), Loc.T("Not signed in to Riot: open VALORANT to search players")),
            _ => (Color.FromRgb(0x8A, 0x8F, 0x98), Loc.T("Checking the Riot Client…")),
        };
        _riotDot.Fill = B(c);
        _riotText.Text = t;
        _riotText.Foreground = _riotOk == false ? B(c) : Res("TextDimBrush");
        _riotText.ToolTip = Loc.T("Searching players uses the Riot Client's sign-in on this PC. Nothing is sent anywhere else.");
    }

    /// <summary>Checks the Riot Client sign-in in the background, at most every 20 seconds.</summary>
    void CheckRiot()
    {
        if (_riotChecking || DateTime.UtcNow - _riotAt < TimeSpan.FromSeconds(20)) return;
        _riotChecking = true;
        _riotAt = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            bool ok;
            try { ok = await ValorantMatches.Login() != null; } catch { ok = false; }
            Dispatcher.Invoke(() => { _riotOk = ok; _riotChecking = false; ShowRiotStatus(); });
        });
    }

    /// <summary>A player's profile from your own matches (what Pulse already has, no lookup).</summary>
    void OpenLocal(string id)
    {
        CloseMatch();
        _trSel = id;
        _trQueue = null;
        _trView = "overview";
        Build();
        Scroller.ScrollToTop();
    }

    // ───────── your older matches ─────────

    /// <summary>A card on the Matches tab: how many matches Pulse has, and a button to get every older one.</summary>
    FrameworkElement OlderMatchesCard(int have)
    {
        var sp = new StackPanel();
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new StackPanel();
        t.Children.Add(Txt(Loc.F("Pulse has {0} of your matches", have), 13.5, TgText, true));
        t.Children.Add(Txt(Loc.T("Stats count only the matches Pulse has. Get your older ones from Riot to see all of them (it takes about a second per match)."), 11.5, TgDim)
            .Also(x => { x.TextWrapping = TextWrapping.Wrap; x.TextTrimming = TextTrimming.None; }));
        if (_allBusy && _downloadText != null) t.Children.Add(Txt(_downloadText, 11.5, TgYellow).Also(x => x.Margin = new Thickness(0, 4, 0, 0)));
        g.Children.Add(t);
        var b = Btn(_allBusy ? "Getting your matches…" : "Get all my older matches", !_allBusy);
        b.IsEnabled = !_allBusy && !_downloading;
        b.Margin = new Thickness(16, 0, 0, 0);
        b.VerticalAlignment = VerticalAlignment.Center;
        b.Click += async (_, _) => await DownloadAll();
        Grid.SetColumn(b, 1);
        g.Children.Add(b);
        sp.Children.Add(g);
        return TgBox(sp, 14);
    }

    async Task DownloadAll()
    {
        if (_allBusy || _downloading) return;
        _allBusy = true;
        _downloading = true;
        _downloadText = Loc.T("Reading your match list…");
        Build();
        var progress = new Progress<(string Step, int Done, int Total)>(p =>
        {
            _downloadText = p.Step == "list" ? Loc.T("Reading your match list…") : p.Done >= p.Total ? Loc.T("Saving…") : Loc.F("Downloading match {0} of {1}…", p.Done + 1, p.Total);
            if (_tab == "tracker" && _trView == "matches") Build();
        });
        ValorantMatches.BackfillResult r;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        try { r = await ValorantMatches.Backfill(1000, progress, cts.Token, maxScan: 1000); }
        catch (Exception ex) { r = new ValorantMatches.BackfillResult(0, 0, ex.Message); }
        _downloading = false;
        _allBusy = false;
        _downloadText = r.Error == "no-client" ? Loc.T("Open VALORANT or the Riot Client first, then try again.")
            : r.Error != null ? Loc.F("{0}. Try again in a minute.", Loc.T(r.Error))
            : r.Added > 0 ? Loc.F("Added {0} older matches.", r.Added)
            : Loc.T("You have every match Riot still keeps.");
        if (_tab == "tracker") Build();
    }

    // ───────── Riot IDs for a match's players ─────────

    /// <summary>When a match's players have no Riot ID on this PC yet, asks the Riot Client for them once.</summary>
    void LoadMatchNames(VMatch m)
    {
        var missing = m.Players.Where(p => Stats.NameOf(m, p.Id) == null && NameBook.Get(p.Id) == null).Select(p => p.Id).ToList();
        if (missing.Count == 0) return;
        // Ask again after 20 seconds if the last try failed (the Riot Client may have been closed, or still starting).
        if (_namesAsked.TryGetValue(m.Id, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(20)) return;
        _namesAsked[m.Id] = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            Dictionary<string, string> found;
            try { found = await ValorantSearch.NamesFor(missing); } catch { found = new(); }
            bool signedIn = ValorantMatches.SignedIn != null && ValorantMatches.LastLoginProblem == null;
            _namesProblem = found.Count > 0 ? null
                : !signedIn ? Loc.T("Open VALORANT or the Riot Client to see players' Riot IDs.")
                : Loc.T("Riot didn't send names for these players. They'll load next time you open the match.");
            if (found.Count > 0)
            {
                NameBook.Add(found);
                FillAllNames();
            }
            Dispatcher.Invoke(() => { if (_trMatchId == m.Id && MatchLayer.Visibility == Visibility.Visible) ShowMatch(); });
        });
    }

    /// <summary>
    /// Once per session, after a lookup works: names for everyone in your saved matches, so the tracker, encounters and
    /// every scoreboard show Riot IDs without waiting. Up to 1,000 players (10 quick requests), saved for next time.
    /// </summary>
    static void FillAllNames()
    {
        if (_allNamesAsked) return;
        _allNamesAsked = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var ids = NameBook.Missing(VStore.All.OrderByDescending(x => x.StartUtc).SelectMany(x => x.Players.Select(p => p.Id)));
                if (ids.Count == 0) return;
                NameBook.Add(await ValorantSearch.NamesFor(ids));
            }
            catch { }
        });
    }

    // ───────── the Pulse score badge ─────────

    /// <summary>The color of a Pulse score, in rank colors: Iron grey up to Radiant.</summary>
    static Color ScoreColor(double s) => double.IsNaN(s) ? Color.FromRgb(0x5C, 0x63, 0x6E) : s switch
    {
        < 300 => Color.FromRgb(0x8A, 0x8F, 0x98),
        < 450 => Color.FromRgb(0xC0, 0x8B, 0x55),
        < 600 => Color.FromRgb(0xC9, 0xD1, 0xDB),
        < 750 => Color.FromRgb(0xEC, 0xC6, 0x5B),
        < 850 => Color.FromRgb(0x4F, 0xC9, 0xC9),
        < 950 => Color.FromRgb(0xC8, 0x8E, 0xF7),
        _ => Color.FromRgb(0xFF, 0x6B, 0x7A),
    };

    /// <summary>A small hexagon emblem in the score's rank color, like the trackers' rating badges.</summary>
    static FrameworkElement ScoreBadge(double score, double size = 22)
    {
        var c = ScoreColor(score);
        var light = Color.FromRgb((byte)Math.Min(255, c.R + 45), (byte)Math.Min(255, c.G + 45), (byte)Math.Min(255, c.B + 45));
        var dark = Color.FromRgb((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45));
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M12 1 L21.5 6.5 V17.5 L12 23 L2.5 17.5 V6.5 Z"), Stretch = Stretch.Uniform,
            Fill = new LinearGradientBrush(light, dark, 90), Stroke = B(A(light, 0xC0)), StrokeThickness = 1,
        });
        g.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M12 6 L17 9 V15 L12 18 L7 15 V9 Z"), Stretch = Stretch.Uniform, Margin = new Thickness(size * 0.27),
            Fill = B(A(Colors.White, 0x30)), Stroke = B(A(Colors.White, 0x90)), StrokeThickness = 1,
        });
        return g;
    }
}
