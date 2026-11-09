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
/// The tracker: CS2-tracker-style profiles for you, for the people in your matches, and (when turned on) for any
/// Riot ID. Skills are placed against every player in the matches saved on this PC, with the rank they play like.
/// </summary>
public partial class ValorantPage
{
    string _trSel;                       // puuid of the profile shown (null: you)
    static Profile _trSearched;
    static string _trStatus;
    static bool _trBusy;
    static Dictionary<string, string> _trNames = new();
    static ValorantSearch.Mine _trMyRank;
    static DateTime _trMyRankAt;
    TextBlock _trStatusLabel;
    static List<int> _trRr;
    string _trAgent;
    static readonly Dictionary<string, (int Tier, int Peak)> _trPeaks = new();
    static bool _trPeaksBusy, _trNamesTried;
    static DateTime _trAutoAt;

    void BuildTracker()
    {
        var matches = CoachMatches();
        Root.Children.Add(SearchCard());
        // Skills need a few matches to measure, and players at several ranks to compare with: fetch your recent ones.
        // Also fetch again matches saved by an older Pulse (no Riot IDs, or an agent it didn't know yet). The Riot Client
        // may not be open the first time, so keep trying every couple of minutes instead of once per session.
        bool stale = matches.Take(30).Any(m => VStore.Incomplete(m.Id));
        if ((matches.Count < 20 || stale) && !_downloading && DateTime.UtcNow - _trAutoAt > TimeSpan.FromMinutes(2)) { _trAutoAt = DateTime.UtcNow; _ = Download(true); }
        if (_downloading || (_downloadText != null && matches.Count < 20))
        {
            var dl = new StackPanel();
            dl.Children.Add(Heading(_downloading ? "Getting your recent matches" : "Your matches"));
            dl.Children.Add(Dim(Loc.T("Your skills are measured from your last 30 matches, against every player in them. The more matches Pulse has, the more exact your ratings are."), 12, 4));
            _downloadLabel = Faint(_downloadText ?? Loc.T("Connecting to the Riot Client…"), 11.5, 8);
            dl.Children.Add(_downloadLabel);
            Root.Children.Add(Card(dl));
        }
        if (matches.Count == 0 && _trSearched == null)
        {
            Root.Children.Add(CoachHeader(matches));
            return;
        }
        var me = matches.Count > 0 ? matches[0].Me : null;
        var people = Tracker.People(matches).Take(14).ToList();
        // Riot IDs for players in matches saved before Pulse kept them: one lookup per session.
        if (!_trNamesTried && !_trBusy && matches.Count > 0)
        {
            var missing = NameBook.Missing(matches.SelectMany(m => m.Players.Select(x => x.Id)));
            if (missing.Count > 0) { _trNamesTried = true; _ = LoadNames(missing.Take(100).ToList()); }
        }

        // A match page replaces the profile while it's open.
        if (_trMatchId != null)
        {
            var open = (_trSearched?.Matches ?? new()).Concat(VStore.All).FirstOrDefault(m => m.Id == _trMatchId);
            if (open != null)
            {
                string viewer = _trSearched != null && _trSel == _trSearched.Puuid && !_trSearched.FromYourMatches ? _trSearched.Puuid : _trSel ?? me ?? open.Me;
                if (open.Players.All(x => x.Id != viewer)) viewer = open.Me;
                foreach (var e in MatchPage(open, viewer)) Root.Children.Add(e);
                return;
            }
            _trMatchId = null;
        }

        // Who to show.
        var pick = new WrapPanel();
        if (me != null) pick.Children.Add(Chip(NameBook.Get(me) is { } myName ? Loc.T("You") + " · " + myName : Loc.T("You"), "trwho", _trSel == null || _trSel == me, () => { _trSel = me; _trQueue = null; Build(); }));
        if (_trSearched != null)
        {
            var sp = _trSearched;
            pick.Children.Add(Chip("🔍 " + sp.Name, "trwho", _trSel == sp.Puuid && !sp.FromYourMatches, () => { _trSel = sp.Puuid; _trQueue = null; Build(); }));
        }
        foreach (var k in people)
        {
            var id = k.Puuid;
            var label = NameBook.Get(id) ?? $"{k.Agent} · {ValorantMatches.RankName(k.Tier)}".Trim(' ', '·');
            pick.Children.Add(Chip($"{label}  ×{k.Games}", "trwho", _trSel == id, () => { _trSel = id; _trQueue = null; Build(); }));
        }
        var pickCard = new StackPanel();
        pickCard.Children.Add(Label(people.Count > 0 ? "You and the people from your matches" : "Profile"));
        pickCard.Children.Add(pick);
        Root.Children.Add(Card(pickCard));

        Profile p;
        if (_trSearched != null && _trSel == _trSearched.Puuid && !_trSearched.FromYourMatches) p = _trSearched;
        else if (me != null)
        {
            p = Tracker.FromYourMatches(matches, _trSel ?? me);
            p.Name = NameBook.Get(p.Puuid) ?? (p.IsYou ? Loc.T("You") : "");
            if (p.IsYou)
            {
                if (_trMyRank is { } r)
                {
                    p.Tier = r.Tier; p.Rr = r.Rr; p.Peak = Math.Max(r.Peak, r.Tier); p.PeakAct = r.PeakAct; p.ActWinPct = r.Win; p.ActGames = r.Games;
                    p.LastTier = r.LastTier; p.Level = r.Level; p.Acts = r.Acts ?? new();
                    Tracker.Finish(p, ValorantLobby.SmurfSigns(r.Level, r.Tier, r.Peak, r.Win, r.Games, r.RrChanges ?? new()));
                }
                if (DateTime.UtcNow - _trMyRankAt > TimeSpan.FromMinutes(5)) _ = LoadMyRank();
            }
        }
        else return;
        foreach (var e in ProfileView(p)) Root.Children.Add(e);
    }

    // ───────── search ─────────

    FrameworkElement SearchCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Player tracker"));
        sp.Children.Add(Dim(Loc.T("Ranks, skills and recent form, measured against the players in your own matches."), 12.5, 4));
        var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { MaxWidth = 380 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox { Style = (Style)FindResource("SearchBox"), Tag = Loc.T("Search a Riot ID: Name#TAG"), VerticalContentAlignment = VerticalAlignment.Center };
        row.Children.Add(box);
        var go = Btn("Search", true);
        go.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(go, 1);
        row.Children.Add(go);
        sp.Children.Add(row);
        // Someone from your own matches opens straight away, with nothing asked of Riot.
        bool OpenKnown(string text)
        {
            var hit = NameBook.Find(text.Trim(), 50).FirstOrDefault(x => x.Name.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (hit.Puuid == null || !CoachMatches().Any(m => m.Players.Any(x => x.Id == hit.Puuid))) return false;
            OpenPlayer(hit.Puuid);
            return true;
        }
        async Task Run()
        {
            if (_trBusy) return;
            if (OpenKnown(box.Text)) return;
            if (!S.PlayerSearch) { await TurnOnSearch(); if (!S.PlayerSearch) return; }
            _trBusy = true;
            _trStatus = Loc.T("Searching…");
            SetTrStatus();
            var progress = new Progress<string>(m =>
            {
                _trStatus = m.StartsWith("match:", StringComparison.Ordinal) && m.Split(':') is { Length: 3 } a ? Loc.F("Reading match {0} of {1}…", a[1], a[2]) : Loc.T(m);
                SetTrStatus();
            });
            ValorantSearch.Result r;
            var riotId = box.Text; // read on the UI thread; the search itself runs in the background
            try { r = await Task.Run(() => ValorantSearch.Search(riotId, progress)); }
            catch (Exception ex)
            {
                TweakEngine.AddLog("valorant", "Tracker search failed: " + ex.GetType().Name + ": " + ex.Message);
                r = new ValorantSearch.Result(null, "Search failed. Try again in a minute.");
            }
            _trBusy = false;
            if (r.Profile != null)
            {
                _trSearched = r.Profile; _trSel = r.Profile.Puuid; _trQueue = null; _trAct = "all"; _trMatchId = null;
                S.RecentSearches.RemoveAll(x => x.Equals(r.Profile.Name, StringComparison.OrdinalIgnoreCase));
                S.RecentSearches.Insert(0, r.Profile.Name);
                if (S.RecentSearches.Count > 8) S.RecentSearches.RemoveRange(8, S.RecentSearches.Count - 8);
                TweakEngine.Save();
            }
            _trStatus = r.Error != null ? Loc.T(r.Error) : null;
            if (r.Profile != null) TweakEngine.AddLog("valorant", "Tracker search");
            if (_tab == "tracker") Build();
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
        go.Click += async (_, _) => await Run();
        _trSearchFor = async riotId => { box.Text = riotId; await Run(); };
        // Suggestions while typing: everyone in your matches whose Riot ID matches.
        var pop = new Popup { PlacementTarget = box, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        var list = new StackPanel();
        pop.Child = new Border { Background = B(Color.FromRgb(0x1B, 0x1E, 0x24)), BorderBrush = B(Color.FromRgb(0x2C, 0x30, 0x38)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(4), MinWidth = 300, Child = list };
        box.TextChanged += (_, _) =>
        {
            var text = box.Text.Trim();
            list.Children.Clear();
            if (text.Length < 2) { pop.IsOpen = false; return; }
            foreach (var (id, name) in NameBook.Find(text, 8))
            {
                var item = new Button { Style = (Style)FindResource("GhostButton"), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 1, 0, 1), Content = name };
                var pick = name;
                item.Click += async (_, _) => { pop.IsOpen = false; box.Text = pick; await Run(); };
                list.Children.Add(item);
            }
            pop.IsOpen = list.Children.Count > 0 && box.IsKeyboardFocused;
        };
        box.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { pop.IsOpen = false; await Run(); } };
        if (S.RecentSearches.Count > 0)
        {
            var recent = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            recent.Children.Add(Faint(Loc.T("Recent:"), 11.5, 0).Also(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 8, 6); }));
            foreach (var r in S.RecentSearches)
            {
                var b = new Button { Style = (Style)FindResource("GhostButton"), Content = r, Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 6, 6), FontSize = 12 };
                var id = r;
                b.Click += async (_, _) => { box.Text = id; await Run(); };
                recent.Children.Add(b);
            }
            sp.Children.Add(recent);
        }
        _trStatusLabel = Faint(_trStatus ?? "", 11.5, 8);
        _trStatusLabel.Visibility = _trStatus != null ? Visibility.Visible : Visibility.Collapsed;
        sp.Children.Add(_trStatusLabel);
        if (S.PlayerSearch)
        {
            var off = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Turn off player search"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            off.Click += (_, _) => { S.PlayerSearch = false; _trSearched = null; _trNames.Clear(); TweakEngine.Save(); Build(); };
            sp.Children.Add(off);
        }
        return Card(sp);
    }

    void SetTrStatus()
    {
        if (_trStatusLabel == null) return;
        _trStatusLabel.Text = _trStatus ?? "";
        _trStatusLabel.Visibility = _trStatus != null ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task LoadNames(List<string> ids)
    {
        _trBusy = true;
        try
        {
            var found = await Task.Run(() => ValorantSearch.NamesFor(ids));
            NameBook.Add(found);
            foreach (var kv in found) _trNames[kv.Key] = kv.Value;
        }
        catch { }
        _trBusy = false;
        if (_tab == "tracker") Build();
    }

    async Task LoadMyRank()
    {
        _trMyRankAt = DateTime.UtcNow;
        try { _trMyRank = await Task.Run(() => ValorantSearch.MyRank()); } catch { }
        _trRr = _trMyRank?.RrChanges;
        if (_trMyRank != null && _tab == "tracker") Build();
    }

    FrameworkElement YourAgentsCard(List<Tracker.AgentLine> lines, IReadOnlyList<VMatch> all, string me)
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("How you play your agents"));
        sp.Children.Add(Dim(Loc.T("Each agent you've played twice or more, against every other player on that agent in the matches saved on this PC. Ranked by combat score per round (ACS), the number VALORANT's scoreboard uses."), 12.5, 4));
        _trAgent ??= lines[0].Agent;
        foreach (var a in lines)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (MatchArt.AgentIcon(a.Agent) is { } ai) g.Children.Add(new Image { Source = ai, Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Left }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = a.Agent, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            name.Children.Add(new TextBlock { Text = Loc.F("{0} matches · {1:0}% won", a.Matches, a.WinPct), FontSize = 11.5, Foreground = Res("TextDimBrush") });
            Grid.SetColumn(name, 1); g.Children.Add(name);
            var stats = new TextBlock
            {
                Text = Loc.F("{0:0} ACS · {1:0.00} K/D · {2:0} ADR · {3:0}% HS · {4:0.0} first kills per 100 rounds · {5:0.0} abilities a round",
                    a.Acs, a.Kd, a.Adr, double.IsNaN(a.HsPct) ? 0 : a.HsPct, a.FirstKillsPer100, double.IsNaN(a.CastsPerRound) ? 0 : a.CastsPerRound),
                FontSize = 12, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
            };
            Grid.SetColumn(stats, 2); g.Children.Add(stats);
            var tags = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            var c = double.IsNaN(a.TopPercent) ? Color.FromRgb(0x8A, 0x8F, 0x98) : a.TopPercent <= 25 ? Color.FromRgb(0x3D, 0xDC, 0x84) : a.TopPercent <= 60 ? Color.FromRgb(0xFA, 0xD6, 0x63) : Color.FromRgb(0xFF, 0x5A, 0x4E);
            tags.Children.Add(new TextBlock
            {
                Text = double.IsNaN(a.TopPercent) ? Loc.F("{0} other {1} players so far", a.OthersSeen, a.Agent) : Loc.F("Top {0:0}% of {1} players", a.TopPercent, a.Agent),
                FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(c), HorizontalAlignment = HorizontalAlignment.Right,
            });
            if (a.PlaysLikeTier > 0) tags.Children.Add(new TextBlock { Text = Loc.F("plays like {0}", ValorantMatches.RankName(a.PlaysLikeTier)), FontSize = 11.5, Foreground = Res("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Right });
            Grid.SetColumn(tags, 3); g.Children.Add(tags);
            var agent = a.Agent;
            var row = new Border
            {
                Background = _trAgent == a.Agent ? Res("Surface2Brush") : Brushes.Transparent, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 12, 8),
                Margin = new Thickness(0, 8, 0, 0), Child = g, Cursor = Cursors.Hand, ToolTip = Loc.F("See the best {0} players you've met", a.Agent),
            };
            row.MouseLeftButtonUp += (_, _) => { _trAgent = agent; Build(); };
            sp.Children.Add(row);
        }

        // Best players on the chosen agent.
        var top = Tracker.TopOn(_trAgent, all, me);
        if (top.Count > 1)
        {
            sp.Children.Add(Label(Loc.F("Best {0} players you've met", _trAgent)).Also(l => l.Margin = new Thickness(0, 18, 0, 6)));
            int rank = 1;
            foreach (var t in top)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 3) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.Children.Add(new TextBlock { Text = $"#{rank++}", FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = Res(t.You ? "AccentBrush" : "TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center });
                if (MatchArt.RankIcon(t.Tier) is { } ri) { var im = new Image { Source = ri, Width = 24, Height = 24 }; Grid.SetColumn(im, 1); g.Children.Add(im); }
                var who = t.You ? Loc.T("You") : NameBook.Get(t.Puuid) is { } n ? n : ValorantMatches.RankName(t.Tier) is { Length: > 0 } rn ? Loc.F("{0} player", rn) : Loc.T("Player");
                var w = new TextBlock { Text = who, FontSize = 12.5, FontWeight = t.You ? FontWeights.Bold : FontWeights.SemiBold, Foreground = Res(t.You ? "AccentBrush" : "TextBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(w, 2); g.Children.Add(w);
                var st = new TextBlock { Text = Loc.F("{0:0} ACS · {1:0.00} K/D · {2} matches", t.Acs, t.Kd, t.Matches), FontSize = 12, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(st, 3); g.Children.Add(st);
                var row = new Border { Background = t.You ? Res("Surface2Brush") : Brushes.Transparent, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 4), Child = g };
                if (!t.You) { var id = t.Puuid; row.Cursor = Cursors.Hand; row.MouseLeftButtonUp += (_, _) => OpenPlayer(id); }
                sp.Children.Add(row);
            }
        }
        sp.Children.Add(Faint(Loc.T("Riot's match data has the moment of each kill but not when the damage started, so time-to-kill can't be measured honestly; ACS and first kills show how fast you win fights instead."), 11.5, 10));
        return Card2(sp, Color.FromRgb(0x4D, 0xA3, 0xFF));
    }

    FrameworkElement TierListCard(List<Tracker.TierEntry> tiers, int matches)
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Agent tier list in your lobbies"));
        sp.Children.Add(Dim(Loc.F("Win rate of every agent across all players in the {0} matches saved on this PC. It's your rank and servers, not the whole game, and it gets steadier with more matches.", matches), 12.5, 4));
        foreach (var grade in new[] { "S", "A", "B", "C", "D" })
        {
            var group = tiers.Where(t => t.Grade == grade).ToList();
            if (group.Count == 0) continue;
            var col = grade switch { "S" => Color.FromRgb(0xFF, 0x7A, 0x59), "A" => Color.FromRgb(0xFA, 0xD6, 0x63), "B" => Color.FromRgb(0x3D, 0xDC, 0x84), "C" => Color.FromRgb(0x4D, 0xA3, 0xFF), _ => Color.FromRgb(0x8A, 0x8F, 0x98) };
            var g = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(col), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = grade, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            var wrap = new WrapPanel();
            foreach (var t in group)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 6) };
                if (MatchArt.AgentIcon(t.Agent) is { } ai) item.Children.Add(new Image { Source = ai, Width = 34, Height = 34, Margin = new Thickness(0, 0, 6, 0) }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
                var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                txt.Children.Add(new TextBlock { Text = t.Agent, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
                txt.Children.Add(new TextBlock { Text = Loc.F("{0:0}% win · picked in {1:0}% · {2} games", t.WinPct, t.PickPct, t.Picks), FontSize = 11, Foreground = Res("TextDimBrush") });
                item.Children.Add(txt);
                wrap.Children.Add(item);
            }
            Grid.SetColumn(wrap, 1);
            g.Children.Add(wrap);
            sp.Children.Add(g);
        }
        sp.Children.Add(Faint(Loc.T("Grades by win rate: S 55%+, A 52%+, B 48%+, C 45%+, D below. Agents need 15 games here to be listed."), 11.5, 10));
        return Card2(sp, Color.FromRgb(0xFA, 0xD6, 0x63));
    }

    static string RankOf(double avgTier) => avgTier >= 3 ? ValorantMatches.RankName((int)Math.Round(avgTier)) : "–";

    /// <summary>How far an average rank is from yours, in divisions (Gold 1 → Gold 2 is one).</summary>
    static string RankGap(double them, double me)
    {
        double d = them - me;
        if (me < 3 || them < 3 || Math.Abs(d) < 1) return Loc.T("about your rank");
        int n = (int)Math.Round(Math.Abs(d));
        return d > 0 ? Loc.F("{0} divisions above you", n) : Loc.F("{0} divisions below you", n);
    }

    /// <summary>Who you've been up against in your last 5 ranked matches.</summary>
    FrameworkElement OppositionCard(Tracker.Opposition o)
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Who you're up against"));
        sp.Children.Add(Dim(Loc.F("Your last {0} ranked matches. Each player's rank is the one they had in that match.", o.Matches), 12.5, 4));
        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        void Stat(string label, double tier, string sub)
        {
            var b = new StackPanel { Margin = new Thickness(0, 0, 34, 8) };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            if (MatchArt.RankIcon((int)Math.Round(tier)) is { } icon) line.Children.Add(new Image { Source = icon, Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 0) }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
            line.Children.Add(new TextBlock { Text = RankOf(tier), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
            b.Children.Add(new TextBlock { Text = Loc.T(label).ToUpper(CultureInfo.CurrentCulture), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 0, 0, 4) });
            b.Children.Add(line);
            if (sub.Length > 0) b.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 2, 0, 0) });
            row.Children.Add(b);
        }
        Stat("Average enemy", o.EnemyAvg, RankGap(o.EnemyAvg, o.MyAvg));
        Stat("Average teammate", o.TeamAvg, RankGap(o.TeamAvg, o.MyAvg));
        if (o.Toughest != null) Stat("Toughest enemy", o.Toughest.Tier, o.Toughest.Agent);
        sp.Children.Add(row);

        // Peaks: their best rank ever (a Riot lookup per player, so only with player search on).
        if (S.PlayerSearch)
        {
            var known = o.EnemyIds.Where(_trPeaks.ContainsKey).Select(id => _trPeaks[id]).ToList();
            if (known.Count > 0)
            {
                double avgPeak = known.Where(k => k.Peak >= 3).Select(k => (double)k.Peak).DefaultIfEmpty(0).Average();
                var best = known.Max(k => k.Peak);
                sp.Children.Add(Dim(Loc.F("Their peaks: average {0}, highest {1}.", RankOf(avgPeak), ValorantMatches.RankName(best)), 12.5, 4)
                    .Also(t => t.Foreground = Res("TextBrush")));
            }
            else
            {
                var b = Btn(_trPeaksBusy ? "Looking up peaks…" : "Look up their peak ranks");
                b.IsEnabled = !_trPeaksBusy;
                b.HorizontalAlignment = HorizontalAlignment.Left;
                b.Margin = new Thickness(0, 6, 0, 0);
                b.Click += async (_, _) =>
                {
                    _trPeaksBusy = true;
                    Build();
                    try { foreach (var kv in await Task.Run(() => ValorantSearch.Peaks(o.EnemyIds))) _trPeaks[kv.Key] = kv.Value; } catch { }
                    _trPeaksBusy = false;
                    if (_tab == "tracker") Build();
                };
                sp.Children.Add(b);
            }
        }
        else sp.Children.Add(Faint(Loc.T("Turn on player search to see their peak ranks too."), 11.5, 4));

        if (o.Repeats.Count > 0)
        {
            sp.Children.Add(Label("Enemies you keep meeting").Also(l => l.Margin = new Thickness(0, 16, 0, 6)));
            foreach (var r in o.Repeats)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                if (MatchArt.AgentIcon(r.Agent) is { } ai) g.Children.Add(new Image { Source = ai, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
                var name = NameBook.Get(r.Puuid) ?? $"{r.Agent} · {ValorantMatches.RankName(r.Tier)}";
                var t1 = new TextBlock { Text = name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(t1, 1); g.Children.Add(t1);
                var t2 = new TextBlock { Text = Loc.F("met {0} times · you won {1}", r.Times, r.YourWins), FontSize = 12, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(t2, 2); g.Children.Add(t2);
                var row2 = new Border { Background = Res("Surface2Brush"), CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 0, 4), Child = g, Cursor = Cursors.Hand };
                var id = r.Puuid;
                row2.MouseLeftButtonUp += (_, _) => { _trSel = id; Build(); };
                sp.Children.Add(row2);
            }
        }
        return Card2(sp, Color.FromRgb(0xFF, 0x5A, 0x4E));
    }

    /// <summary>The hidden MMR estimate: lobby ranks and RR per win/loss, and what they say together.</summary>
    FrameworkElement MmrCard(Tracker.MmrEstimate e, Profile p)
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Hidden MMR estimate"));
        sp.Children.Add(Dim(Loc.T("Riot never shows your MMR (the hidden rating matchmaking uses). This estimates it the way MMR calculators do: from the ranks of the lobbies Riot puts you in, and from how much RR you gain and lose."), 12.5, 4));
        if (e.Side == Tracker.MmrSide.Unknown)
        {
            sp.Children.Add(Dim(Loc.T("Play a few more competitive matches (and keep VALORANT open so Pulse can read your RR) for an estimate."), 12.5, 10));
            return Card2(sp, Color.FromRgb(0xC9, 0xA2, 0xFF));
        }
        var (word, color, explain) = e.Side switch
        {
            Tracker.MmrSide.Above => ("Above your rank", Color.FromRgb(0x3D, 0xDC, 0x84), "Your MMR looks higher than your rank: expect bigger wins and smaller losses until your rank catches up."),
            Tracker.MmrSide.Below => ("Below your rank", Color.FromRgb(0xFF, 0x5A, 0x4E), "Your MMR looks lower than your rank: wins give less and losses take more until it catches up. Winning streaks fix it fastest."),
            _ => ("About your rank", Color.FromRgb(0xFA, 0xD6, 0x63), "Your MMR looks about the same as your rank: wins and losses should be roughly even."),
        };
        var big = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        if (e.EstimateTier >= 3 && MatchArt.RankIcon(e.EstimateTier) is { } icon) big.Children.Add(new Image { Source = icon, Width = 46, Height = 46, Margin = new Thickness(0, 0, 12, 0) }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
        var bt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        bt.Children.Add(new TextBlock { Text = Loc.T(word), FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color), FontFamily = (FontFamily)FindResource("DisplayFont") });
        if (e.EstimateTier >= 3) bt.Children.Add(new TextBlock { Text = Loc.F("Estimated around {0}", ValorantMatches.RankName(e.EstimateTier)), FontSize = 12.5, Foreground = Res("TextDimBrush") });
        big.Children.Add(bt);
        sp.Children.Add(big);
        sp.Children.Add(Dim(Loc.T(explain), 12.5, 10));
        var ev = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        void Ev(string label, string value)
        {
            var b = new StackPanel { Margin = new Thickness(0, 0, 30, 6) };
            b.Children.Add(new TextBlock { Text = value, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            b.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 11, Foreground = Res("TextFaintBrush") });
            ev.Children.Add(b);
        }
        if (e.Matches >= 3) { Ev("Your lobbies' average rank", RankOf(e.LobbyAvg)); Ev("Your rank in those matches", RankOf(e.MyAvg)); }
        if (!double.IsNaN(e.AvgWin)) Ev("RR per win", $"+{e.AvgWin:0}");
        if (!double.IsNaN(e.AvgLoss)) Ev("RR per loss", $"−{e.AvgLoss:0}");
        sp.Children.Add(ev);
        sp.Children.Add(Faint(Loc.F("Based on your last {0} competitive matches. It's an estimate: Riot's real number is private.", Math.Max(e.Matches, _trRr?.Count ?? 0)), 11.5, 6));
        return Card2(sp, Color.FromRgb(0xC9, 0xA2, 0xFF));
    }

    static bool WonFor(VMatch m, string puuid)
    {
        var team = m.TeamOf(puuid);
        return m.Rounds.Count(r => r.Winner == team) > m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != team);
    }

    FrameworkElement MatchRow(VMatch m, string puuid)
    {
        var pl = m.Players.FirstOrDefault(x => x.Id == puuid);
        bool won = WonFor(m, puuid);
        var team = m.TeamOf(puuid);
        int us = m.Rounds.Count(r => r.Winner == team), them = m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != team);
        var t = Analysis.Totals(m, puuid);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        if (MatchArt.AgentIcon(pl?.Agent) is { } ai) g.Children.Add(new Image { Source = ai, Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
        void Cell(int col, string text, Brush b, bool bold = false)
        {
            var tb = new TextBlock { Text = text, FontSize = 12.5, Foreground = b, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(tb, col); g.Children.Add(tb);
        }
        Cell(1, $"{m.Map} · {pl?.Agent}", Res("TextBrush"), true);
        Cell(2, $"{us}–{them}", new SolidColorBrush(won ? Color.FromRgb(0x3D, 0xDC, 0x84) : Color.FromRgb(0xFF, 0x5A, 0x4E)), true);
        Cell(3, $"{t.Kills} / {t.Deaths} / {pl?.Assists ?? 0}", Res("TextBrush"));
        Cell(4, Loc.F("{0:0} ADR · {1:0}% HS · {2:0} ACS", t.Adr, double.IsNaN(t.HeadshotPct) ? 0 : t.HeadshotPct, t.Acs), Res("TextDimBrush"));
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Cell(5, m.StartUtc.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture), Res("TextFaintBrush"));
        return new Border { Background = new SolidColorBrush(Color.FromRgb(0x18, 0x19, 0x1E)), CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 7, 14, 7), Margin = new Thickness(0, 0, 0, 6), Child = g };
    }
}
