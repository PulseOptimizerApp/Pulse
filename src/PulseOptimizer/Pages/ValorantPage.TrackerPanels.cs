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
/// The tracker in the layout the big VALORANT trackers use: playlist and act filters, an overview of every stat with
/// how it ranks, accuracy, roles, weapons (with the game's weapon icons), maps, agents, act history, match history by
/// day, and a match page with scoreboard, performance, economy, rounds and duels. Plain, dense panels.
/// </summary>
public partial class ValorantPage
{
    static readonly Color TpBg = Color.FromRgb(0x18, 0x1B, 0x21), TpLine = Color.FromRgb(0x22, 0x25, 0x2C), TpMuted = Color.FromRgb(0x7D, 0x81, 0x8B),
        TpUp = Color.FromRgb(0x4F, 0xC3, 0x8A), TpDown = Color.FromRgb(0xE5, 0x60, 0x5A), TpInk = Color.FromRgb(0xF1, 0xF2, 0xF4), TpBody = Color.FromRgb(0xD9, 0xDB, 0xE0),
        TpGold = Color.FromRgb(0xF5, 0xB9, 0x42), TpBlue = Color.FromRgb(0x4D, 0xA3, 0xFF);

    static string _trQueue;            // playlist filter ("all", "competitive"…); null = pick for the profile
    static string _trAct = "all";      // "all", "current", or an act id
    static string _trMatchId;          // the match page open, if any
    static string _trMatchTab = "scoreboard";
    static bool _trMoreBusy;
    Func<string, Task> _trSearchFor;   // searches a Riot ID (set by the search box)

    /// <summary>K/D bands (presentation only, not a skill standard): under 0.80 red, 0.80–0.99 neutral, 1.00–1.19 green, 1.20–1.39 blue, 1.40+ gold.</summary>
    internal static Color KdColor(double kd) => double.IsNaN(kd) ? TpMuted : kd >= 1.4 ? TpGold : kd >= 1.2 ? TpBlue : kd >= 1.0 ? TpUp : kd >= 0.8 ? TpBody : TpDown;

    /// <summary>A flat panel with a small caps title and a divider under it.</summary>
    Border Flat(string title, UIElement body, string note = null, double bodyPad = 0)
    {
        var sp = new StackPanel();
        var head = new Grid { Margin = new Thickness(12, 9, 12, 9) };
        head.Children.Add(new TextBlock { Text = Loc.T(title).ToUpper(CultureInfo.CurrentCulture), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = B(Color.FromRgb(0x8B, 0x8F, 0x99)) });
        if (note != null) head.Children.Add(new TextBlock { Text = note, FontSize = 11, Foreground = B(TpMuted), HorizontalAlignment = HorizontalAlignment.Right });
        sp.Children.Add(head);
        sp.Children.Add(new Border { Height = 1, Background = B(TpLine) });
        sp.Children.Add(new Border { Padding = new Thickness(bodyPad), Child = body });
        return new Border { Background = B(TpBg), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 10, 10), Child = sp, ClipToBounds = true };
    }

    static TextBlock Txt(string t, double size = 12.5, Color? c = null, bool bold = false, HorizontalAlignment align = HorizontalAlignment.Left) => new()
    {
        Text = t, FontSize = size, Foreground = B(c ?? TpBody), FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, HorizontalAlignment = align,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    static string N(double v, string f = "0") => double.IsNaN(v) || double.IsInfinity(v) ? "–" : v.ToString(f, CultureInfo.CurrentCulture);
    static string Signed(double v) => double.IsNaN(v) ? "–" : (Math.Round(v) > 0 ? "+" : Math.Round(v) < 0 ? "−" : "") + Math.Abs(v).ToString("0", CultureInfo.CurrentCulture);
    static Color SignColor(double v) => double.IsNaN(v) || Math.Round(v) == 0 ? TpBody : v > 0 ? TpUp : TpDown;

    /// <summary>Panels side by side: widths of 10 or more are pixels, smaller numbers share the rest.</summary>
    static Grid PanelRow(params (UIElement El, double W)[] items)
    {
        var g = new Grid { Margin = new Thickness(0, 0, -10, 0) };
        for (int i = 0; i < items.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = items[i].W >= 10 ? new GridLength(items[i].W) : new GridLength(items[i].W > 0 ? items[i].W : 1, GridUnitType.Star) });
            Grid.SetColumn(items[i].El, i);
            g.Children.Add(items[i].El);
        }
        return g;
    }

    /// <summary>A plain table: a header row, then rows; widths in pixels, 0 = take the rest. Column 0 is left-aligned.</summary>
    Grid Table(string[] heads, double[] widths, IEnumerable<UIElement[]> rows, Func<int, Color?> accent = null, Func<int, bool> highlight = null, Action<int> click = null)
    {
        var g = new Grid();
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
        int r = 0;
        void AddRow(UIElement[] cells, bool head)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (!head)
            {
                int index = r - 1;
                var line = new Border
                {
                    BorderBrush = B(TpLine), BorderThickness = new Thickness(0, 1, 0, 0),
                    Background = highlight?.Invoke(index) == true ? B(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent,
                };
                if (accent?.Invoke(index) is Color a) line.Child = new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = B(a) };
                if (click != null)
                {
                    line.Cursor = Cursors.Hand;
                    line.MouseEnter += (_, _) => { if (highlight?.Invoke(index) != true) line.Background = B(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)); };
                    line.MouseLeave += (_, _) => { if (highlight?.Invoke(index) != true) line.Background = Brushes.Transparent; };
                    line.MouseLeftButtonUp += (_, _) => click(index);
                }
                Grid.SetRow(line, r); Grid.SetColumnSpan(line, widths.Length); g.Children.Add(line);
            }
            for (int c = 0; c < cells.Length && c < widths.Length; c++)
            {
                var host = new Border { Padding = new Thickness(c == 0 ? 12 : 6, head ? 7 : 6, c == widths.Length - 1 ? 12 : 6, head ? 7 : 6), Child = cells[c], IsHitTestVisible = !head && click == null };
                if (cells[c] is FrameworkElement fe && c > 0) fe.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetRow(host, r); Grid.SetColumn(host, c); g.Children.Add(host);
            }
            r++;
        }
        AddRow(heads.Select(h => (UIElement)Txt(Loc.T(h), 11, TpMuted, true)).ToArray(), true);
        foreach (var row in rows) AddRow(row, false);
        return g;
    }

    static Image Pic(ImageSource src, double w, double h, double right = 8) => new Image
    {
        Source = src, Width = w, Height = h, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, right, 0), VerticalAlignment = VerticalAlignment.Center,
    }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality));

    static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        return d.TotalMinutes < 60 ? Loc.F("{0:0}m ago", Math.Max(1, d.TotalMinutes)) : d.TotalHours < 24 ? Loc.F("{0:0}h ago", d.TotalHours) : Loc.F("{0:0}d ago", d.TotalDays);
    }

    static string Hours(long ms) => TimeSpan.FromMilliseconds(ms).TotalHours >= 1 ? Loc.F("{0:0} h", TimeSpan.FromMilliseconds(ms).TotalHours) : Loc.F("{0:0} min", TimeSpan.FromMilliseconds(ms).TotalMinutes);

    /// <summary>A player's Riot ID when Pulse knows it, else their agent.</summary>
    static string NameOrAgent(VMatch m, VPlayer p) => Stats.NameOf(m, p.Id) ?? NameBook.Get(p.Id) ?? (p.Agent.Length > 0 ? p.Agent : Loc.T("Unknown agent"));

    // ───────── filters ─────────

    /// <summary>The playlist to show first: Competitive when the player has competitive matches (like the trackers), else all.</summary>
    static string DefaultQueue(IEnumerable<VMatch> ms) => ms.Any(m => m.Queue == "competitive") ? "competitive" : "all";

    TrackerFilter CurrentFilter(Profile p)
    {
        string q = _trQueue ?? DefaultQueue(p.Matches);
        if (_trAct == "all") return new TrackerFilter(q);
        var acts = ValorantLobby.Acts();
        var act = _trAct == "current" ? acts.LastOrDefault(a => a.Active) : acts.FirstOrDefault(a => a.Id == _trAct);
        if (act.Id == null) return new TrackerFilter(q);
        return new TrackerFilter(q, act.Start, act.End == DateTime.MaxValue ? null : act.End);
    }

    // ───────── overview ─────────

    /// <summary>
    /// The current act and every earlier one in a row: rank, record and peak. From Riot's rank record when Pulse can
    /// read it; otherwise from Pulse's own saved competitive matches (and it says so). Click one to filter by it.
    /// </summary>
    FrameworkElement ActsStrip(Profile p)
    {
        var acts = p.Acts.Where(a => a.Games > 0 || a.Tier >= 3).ToList();
        bool fromMatches = false;
        if (acts.Count == 0) { acts = ValorantLobby.ActsFromMatches(p.Matches, p.Puuid); fromMatches = acts.Count > 0; }
        if (acts.Count == 0)
            return Flat("Competitive acts", Txt(Loc.T(ValorantLobby.Acts().Count == 0
                ? "Open VALORANT once and Pulse reads every act: rank, record and peak. After that they show here even when the game is closed."
                : "No competitive matches saved for this player yet."), 12, TpMuted).Also(t => { t.Margin = new Thickness(12, 10, 12, 12); t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; }));

        var cur = acts.FirstOrDefault(a => a.Current) ?? (acts[0].EndUtc > DateTime.UtcNow ? acts[0] : null);
        string KeyOf(ValorantLobby.ActLine a) => a == cur ? "current" : a.Id;
        var sel = acts.FirstOrDefault(a => KeyOf(a) == _trAct) ?? cur ?? acts[0];

        var g = new Grid { Margin = new Thickness(12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        g.ColumnDefinitions.Add(new ColumnDefinition());

        // The selected act, big.
        // Peak first, like the trackers: the best rank reached that act, with where it ended underneath.
        int Best(ValorantLobby.ActLine a) => a.Peak >= 3 ? a.Peak : a.Tier;
        var rc = RankColor(Best(sel));
        var card = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(Txt(sel.Name.Length > 0 ? sel.Name : Loc.T("Earlier act"), 12.5, TpInk, true));
        if (sel == cur) top.Children.Add(new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(8, 0, 0, 0), Background = B(Color.FromArgb(0x2A, TpUp.R, TpUp.G, TpUp.B)), Child = Txt(Loc.T("CURRENT"), 9.5, TpUp, true) });
        card.Children.Add(top);
        var rankRow = new Grid();
        rankRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rankRow.ColumnDefinitions.Add(new ColumnDefinition());
        rankRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (MatchArt.RankIcon(Best(sel)) is { } big) rankRow.Children.Add(Pic(big, 68, 68, 14));
        else rankRow.Children.Add(new Border { Width = 68, Height = 68, Margin = new Thickness(0, 0, 14, 0), CornerRadius = new CornerRadius(34), Background = B(TpLine) });
        var rn = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        rn.Children.Add(Txt(Loc.T("Peak rank"), 10.5, TpMuted, true));
        rn.Children.Add(Txt(Best(sel) >= 3 ? ValorantMatches.RankName(Best(sel)) : Loc.T("Unrated"), 20, Best(sel) >= 3 ? rc : TpMuted, true));
        if (sel.Tier >= 3 && sel.Tier != Best(sel)) rn.Children.Add(Txt(Loc.F(sel == cur ? "Now {0}" : "Ended {0}", ValorantMatches.RankName(sel.Tier)) + (sel == cur && p.IsYou && p.Tier >= 3 ? $" · {p.Rr} RR" : ""), 12, TpBody));
        else if (sel == cur && p.IsYou && p.Tier >= 3) rn.Children.Add(Txt($"{p.Rr} RR", 12, TpBody));
        Grid.SetColumn(rn, 1); rankRow.Children.Add(rn);
        if (sel.Peak >= 3 && sel.Tier >= 3 && sel.Peak != sel.Tier)
        {
            var pk = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            pk.Children.Add(Txt(Loc.T(sel == cur ? "Now" : "Ended"), 10.5, TpMuted, true, HorizontalAlignment.Center));
            if (MatchArt.RankIcon(sel.Tier) is { } pi) pk.Children.Add(Pic(pi, 34, 34, 0).Also(i => { i.HorizontalAlignment = HorizontalAlignment.Center; i.Margin = new Thickness(0, 2, 0, 2); }));
            pk.Children.Add(Txt(ValorantMatches.RankName(sel.Tier), 11, RankColor(sel.Tier), true, HorizontalAlignment.Center));
            Grid.SetColumn(pk, 2); rankRow.Children.Add(pk);
        }
        card.Children.Add(rankRow);
        var nums = new UniformGrid { Columns = 3, Margin = new Thickness(0, 12, 0, 6) };
        void Num(string label, string value, Color c)
        {
            var n = new StackPanel();
            n.Children.Add(Txt(Loc.T(label), 10.5, TpMuted, true));
            n.Children.Add(Txt(value, 16, c, true));
            nums.Children.Add(n);
        }
        Num("Matches", sel.Games.ToString(CultureInfo.CurrentCulture), TpInk);
        Num("Wins", $"{sel.Wins}–{sel.Games - sel.Wins}", TpInk);
        Num("Win %", sel.Games > 0 ? N(sel.WinPct, "0.0") + "%" : "–", sel.Games == 0 ? TpMuted : sel.WinPct >= 50 ? TpUp : TpDown);
        card.Children.Add(nums);
        var bar = new Grid { Height = 5 };
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = B(Color.FromArgb(0x50, TpDown.R, TpDown.G, TpDown.B)) });
        if (sel.Games > 0) bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = B(TpUp), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(4, 290 * sel.Wins / (double)sel.Games) });
        card.Children.Add(bar);
        g.Children.Add(new Border
        {
            Child = card, Padding = new Thickness(16, 14, 16, 16), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 12, 0),
            BorderThickness = new Thickness(1), BorderBrush = B(Color.FromArgb(0x60, rc.R, rc.G, rc.B)),
            Background = new LinearGradientBrush(Color.FromArgb(0x26, rc.R, rc.G, rc.B), Color.FromArgb(0x06, rc.R, rc.G, rc.B), 90),
        });

        // Every act, newest first: click one to see it (and filter the page to it).
        var grid = new WrapPanel();
        foreach (var a in acts.Take(16))
        {
            var key = KeyOf(a);
            bool on = a == sel;
            var ac = RankColor(Best(a));
            var t = new Grid { Width = 176 };
            t.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            t.ColumnDefinitions.Add(new ColumnDefinition());
            if (MatchArt.RankIcon(Best(a)) is { } ri) t.Children.Add(Pic(ri, 30, 30, 10).Also(im => im.ToolTip = Loc.T("Peak rank that act")));
            else t.Children.Add(new Border { Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0), CornerRadius = new CornerRadius(15), Background = B(TpLine) });
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(Txt(a == cur ? Loc.T("Current act") : (a.Name.Length > 0 ? a.Name : Loc.T("Earlier act")), 10.5, a == cur ? TpUp : TpMuted, true));
            info.Children.Add(Txt(Best(a) >= 3 ? ValorantMatches.RankName(Best(a)) : Loc.T("Unrated"), 12.5, Best(a) >= 3 ? ac : TpMuted, true).Also(x => x.ToolTip = a.Tier >= 3 && a.Tier != Best(a) ? Loc.F("Peak · ended {0}", ValorantMatches.RankName(a.Tier)) : Loc.T("Peak rank that act")));
            info.Children.Add(Txt(a.Games > 0 ? $"{a.Wins}W {a.Games - a.Wins}L · {N(a.WinPct)}%" : Loc.T("no games"), 10.5, TpBody));
            Grid.SetColumn(info, 1); t.Children.Add(info);
            var tile = new Border
            {
                Child = t, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1), BorderBrush = B(on ? TpBlue : Color.FromRgb(0x2A, 0x2D, 0x35)),
                Background = B(on ? Color.FromArgb(0x1E, TpBlue.R, TpBlue.G, TpBlue.B) : Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
            };
            tile.MouseEnter += (_, _) => { if (!on) tile.BorderBrush = B(Color.FromRgb(0x44, 0x48, 0x52)); };
            tile.MouseLeave += (_, _) => { if (!on) tile.BorderBrush = B(Color.FromRgb(0x2A, 0x2D, 0x35)); };
            tile.MouseLeftButtonUp += (_, _) => { _trAct = key; Build(); };
            grid.Children.Add(tile);
        }
        Grid.SetColumn(grid, 1);
        g.Children.Add(grid);
        var note = fromMatches ? Loc.T("from Pulse's saved matches · open VALORANT for Riot's full record")
            : _trAct != "all" ? Loc.F("{0} acts · the page below shows {1}", acts.Count, sel.Name.Length > 0 ? sel.Name : Loc.T("this act")) : Loc.F("{0} acts", acts.Count);
        return Flat("Competitive acts", g, note);
    }

    // ───────── accuracy, weapons, roles, maps, agents ─────────

    // ───────── match history ─────────

    static string _hfMap = "", _hfAgent = "", _hfResult = "";
    static int _hfDays;

    static List<VMatch> HistoryFiltered(Profile p, List<VMatch> ms) => ms.Where(m =>
        (_hfMap.Length == 0 || m.Map == _hfMap)
        && (_hfAgent.Length == 0 || m.Players.FirstOrDefault(x => x.Id == p.Puuid)?.Agent == _hfAgent)
        && (_hfResult.Length == 0 || Pulse.Core.Games.Valorant.Intel.Util.Won(m, p.Puuid) == (_hfResult == "win"))
        && (_hfDays == 0 || m.StartUtc.ToLocalTime().Date > DateTime.Today.AddDays(-_hfDays))).ToList();

    async Task LoadMore(Profile p)
    {
        _trMoreBusy = true;
        Build();
        string err = null;
        try { err = await Task.Run(() => ValorantSearch.More(p)); }
        catch (Exception ex) { err = "Couldn't load more matches. Try again in a minute."; TweakEngine.AddLog("valorant", "Tracker load more failed: " + ex.GetType().Name + ": " + ex.Message); }
        _trMoreBusy = false;
        _trStatus = err != null ? Loc.T(err) : null;
        if (_tab == "tracker") Build();
    }

    // ───────── the match page ─────────

    static readonly Color[] PartyColors = { Color.FromRgb(0xF5, 0xB9, 0x42), Color.FromRgb(0x4D, 0xA3, 0xFF), Color.FromRgb(0xC9, 0xA2, 0xFF), Color.FromRgb(0x59, 0xD4, 0xE8), Color.FromRgb(0xFF, 0x7A, 0x59) };

    /// <summary>What happened in the match (facts) kept apart from what it might mean (interpretations).</summary>
    FrameworkElement AutopsyView(VMatch m, string viewer)
    {
        var pool = _trSearched != null && _trSearched.Puuid == viewer ? _trSearched.Matches : VStore.All.Where(x => x.Console == _trConsole && x.Players.Any(p => p.Id == viewer)).OrderByDescending(x => x.StartUtc).ToList();
        Pulse.Core.Games.Valorant.Intel.Expected.Line line = null;
        try
        {
            var mmr = Pulse.Core.Games.Valorant.Intel.MmrModel.Run(pool, viewer);
            line = Pulse.Core.Games.Valorant.Intel.Expected.Of(pool, viewer, mmr).Lines.FirstOrDefault(l => l.Match.Id == m.Id);
        }
        catch { }
        var r = Pulse.Core.Games.Valorant.Intel.Autopsy.Of(m, viewer, line);
        var sp = new StackPanel();
        void Block(string title, List<string> items, Color dot)
        {
            sp.Children.Add(Txt(Loc.T(title), 12, TpMuted, true).Also(t => t.Margin = new Thickness(12, 10, 12, 4)));
            if (items.Count == 0) sp.Children.Add(Txt(Loc.T("Nothing stands out."), 12.5, TpMuted).Also(t => t.Margin = new Thickness(12, 2, 12, 4)));
            foreach (var i in items)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 3, 12, 3) };
                row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = B(dot), Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top });
                row.Children.Add(Txt(i, 12.5, TpBody).Also(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; t.MaxWidth = 900; }));
                sp.Children.Add(row);
            }
        }
        Block("What happened", r.Facts, TpInk);
        Block("What it might mean", r.Interpretations, TpGold);
        sp.Children.Add(Txt(Loc.T(line == null ? "No expectation for this match: it needs 3 earlier matches from this player." : "Facts come straight from Riot's match record; the second list is Pulse's reading of them and can be wrong."), 11.5, TpMuted)
            .Also(t => { t.Margin = new Thickness(12, 10, 12, 12); t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; }));
        return Flat("Match autopsy", sp);
    }

    /// <summary>Players who queued together get the same colored dot (parties of two or more).</summary>
    static Dictionary<string, Color> Parties(VMatch m)
    {
        var map = new Dictionary<string, Color>();
        int i = 0;
        foreach (var g in m.Players.Where(p => p.Party.Length > 0).GroupBy(p => p.Party).Where(g => g.Count() >= 2))
        {
            var c = PartyColors[i++ % PartyColors.Length];
            foreach (var p in g) map[p.Id] = c;
        }
        return map;
    }

    /// <summary>
    /// Opens a player's tracker. With player search on (and their Riot ID known) it's their full tracker, looked up
    /// like a search; otherwise what your own matches say about them. A Riot ID not on this PC yet is asked for first.
    /// </summary>
    void OpenPlayer(string id)
    {
        var me = TrackerMatches().FirstOrDefault()?.Me;
        if (id == me) { OpenLocal(id); return; }
        string name = NameBook.Get(id) ?? _trSearched?.Matches.SelectMany(m => m.Players).FirstOrDefault(x => x.Id == id)?.Name
            ?? VStore.All.Select(m => Stats.NameOf(m, id)).FirstOrDefault(n => n != null);
        bool inMine = TrackerMatches().Any(m => m.Players.Any(x => x.Id == id));
        if (name != null && _trSearchFor != null && (S.PlayerSearch || !inMine))
        {
            CloseMatch();
            _trView = "overview";
            Scroller.ScrollToTop();
            _ = _trSearchFor(name);
            return;
        }
        if (inMine) { OpenLocal(id); return; }
        // No Riot ID yet: ask the Riot Client for it, then open.
        _ = Task.Run(async () =>
        {
            Dictionary<string, string> found;
            try { found = await ValorantSearch.NamesFor(new List<string> { id }); } catch { return; }
            NameBook.Add(found);
            if (found.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n)) Dispatcher.Invoke(() => { CloseMatch(); _ = _trSearchFor?.Invoke(n); });
        });
    }

    static string BuyName(Stats.Buy b) => b switch { Stats.Buy.Eco => "Eco", Stats.Buy.SemiEco => "Semi-eco", Stats.Buy.SemiBuy => "Semi-buy", _ => "Full buy" };

    static string RoundHow(string result) => result switch
    {
        "Elimination" => "Elimination",
        "Bomb detonated" or "Detonate" => "Spike detonated",
        "Bomb defused" or "Defuse" => "Spike defused",
        "Round timer expired" => "Time ran out",
        "Surrendered" => "Surrender",
        _ => result,
    };

    static string QueueName(string q) => q switch
    {
        "competitive" => "Competitive", "unrated" => "Unrated", "premier" => "Premier", "swiftplay" => "Swiftplay", _ => q,
    };
}
