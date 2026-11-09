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

    FrameworkElement FilterBar(Profile p)
    {
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var q = _trQueue ?? DefaultQueue(p.Matches);
        var chips = new WrapPanel();
        foreach (var (key, label) in new[] { ("all", "All"), ("competitive", "Competitive"), ("unrated", "Unrated"), ("premier", "Premier"), ("swiftplay", "Swiftplay") })
        {
            int n = key == "all" ? p.Matches.Count(m => VMatch.Counts(m.Queue)) : p.Matches.Count(m => m.Queue == key);
            if (n == 0 && key != "all" && key != q) continue;
            chips.Children.Add(Chip($"{Loc.T(label)}  {n}", "trqueue", q == key, () => { _trQueue = key; Build(); }));
        }
        bar.Children.Add(chips);
        // Acts: this one, every earlier one Riot has a rank for, or all saved matches.
        var box = new ComboBox { MinWidth = 200, VerticalAlignment = VerticalAlignment.Top };
        var items = new List<(string Key, string Label)> { ("all", Loc.T("All acts")), ("current", Loc.T("This act")) };
        foreach (var a in p.Acts.Where(a => !a.Current)) items.Add((a.Id, a.Name.Length > 0 ? a.Name : Loc.T("Earlier act")));
        foreach (var it in items) box.Items.Add(new ComboBoxItem { Content = it.Label, Tag = it.Key });
        box.SelectedIndex = Math.Max(0, items.FindIndex(i => i.Key == _trAct));
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem ci && ci.Tag is string k && k != _trAct) { _trAct = k; Build(); } };
        Grid.SetColumn(box, 1);
        bar.Children.Add(box);
        return bar;
    }

    // ───────── overview ─────────

    FrameworkElement OverviewPanel(List<VMatch> ms, string id)
    {
        var o = Stats.Of(ms, id);
        var sp = new StackPanel();
        // The four big ones.
        var big = new UniformGrid { Columns = 4 };
        void Big(string label, string value, string topId, double raw, Color c, string sub)
        {
            var cell = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            cell.Children.Add(Txt(Loc.T(label), 12, TpMuted));
            cell.Children.Add(Txt(value, 26, c, true).Also(t => t.Margin = new Thickness(0, 2, 0, 2)));
            var top = topId != null ? Tracker.TopPercentOf(topId, raw) : double.NaN;
            cell.Children.Add(Txt(!double.IsNaN(top) ? Loc.F("Top {0:0}%", top) + (sub.Length > 0 ? " · " + sub : "") : sub, 11.5, !double.IsNaN(top) && top <= 25 ? TpUp : TpMuted));
            big.Children.Add(new Border { Child = cell, BorderBrush = B(TpLine), BorderThickness = new Thickness(big.Children.Count == 0 ? 0 : 1, 0, 0, 0) });
        }
        Big("Damage/Round", N(o.DamagePerRound, "0.0"), "adr", o.DamagePerRound, TpInk, "");
        Big("K/D Ratio", N(o.Kd, "0.00"), "kd", o.Kd, KdColor(o.Kd), "");
        Big("Headshot %", double.IsNaN(o.HeadshotPct) ? "–" : N(o.HeadshotPct, "0.0") + "%", "hs", o.HeadshotPct, TpInk, "");
        Big("Win %", double.IsNaN(o.WinPct) ? "–" : N(o.WinPct, "0.0") + "%", null, 0, o.WinPct >= 50 ? TpUp : TpDown, $"{o.Wins}W {o.Losses}L");
        sp.Children.Add(big);
        sp.Children.Add(new Border { Height = 1, Background = B(TpLine) });
        // Everything else.
        var grid = new UniformGrid { Columns = 6 };
        void Small(string label, string value, string topId = null, double raw = double.NaN, Color? c = null, string tip = null)
        {
            var cell = new StackPanel { Margin = new Thickness(14, 9, 10, 9), ToolTip = tip == null ? null : Loc.T(tip) };
            cell.Children.Add(Txt(Loc.T(label), 11.5, TpMuted));
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(Txt(value, 15, c ?? TpInk, true));
            var top = topId != null ? Tracker.TopPercentOf(topId, raw) : double.NaN;
            if (!double.IsNaN(top)) line.Children.Add(Txt("  " + Loc.F("Top {0:0}%", top), 10.5, top <= 25 ? TpUp : TpMuted));
            cell.Children.Add(line);
            grid.Children.Add(cell);
        }
        Small("Wins", o.Wins.ToString(CultureInfo.CurrentCulture));
        Small("KAST", double.IsNaN(o.KastPct) ? "–" : N(o.KastPct, "0.0") + "%", "kast", o.KastPct, null, "Rounds with a kill, assist, survival or trade.");
        Small("DDΔ/Round", Signed(o.DdPerRound), "dd", o.DdPerRound, SignColor(o.DdPerRound), "Damage dealt minus damage taken, per round.");
        Small("Kills", o.Kills.ToString("N0", CultureInfo.CurrentCulture));
        Small("Deaths", o.Deaths.ToString("N0", CultureInfo.CurrentCulture));
        Small("Assists", o.Assists.ToString("N0", CultureInfo.CurrentCulture));
        Small("ACS", N(o.Acs, "0.0"), "acs", o.Acs, null, "Average combat score per round, as on VALORANT's scoreboard.");
        Small("KAD Ratio", N(o.Kad, "0.00"), "kad", o.Kad);
        Small("Kills/Round", N(o.KillsPerRound, "0.00"), "kpr", o.KillsPerRound);
        Small("First Bloods", o.FirstBloods.ToString(CultureInfo.CurrentCulture));
        Small("Flawless Rounds", o.Flawless.ToString(CultureInfo.CurrentCulture), tip: "Rounds your team won without losing anyone.");
        Small("Aces", o.Aces.ToString(CultureInfo.CurrentCulture));
        Small("Clutches", o.ClutchChances > 0 ? $"{o.ClutchWins}/{o.ClutchChances}" : "0", tip: "Rounds won as the last one alive, of the times you were.");
        Small("Multi-kills", o.MultiKills.ToString(CultureInfo.CurrentCulture), tip: "Rounds with 2 or more kills.");
        Small("First Deaths", o.FirstDeaths.ToString(CultureInfo.CurrentCulture));
        Small("Plants", o.Plants.ToString(CultureInfo.CurrentCulture));
        Small("Defuses", o.Defuses.ToString(CultureInfo.CurrentCulture));
        Small("Econ Rating", N(o.Econ), tip: "Damage for every 1,000 credits spent.");
        sp.Children.Add(grid);
        return Flat("Overview", sp, Loc.F("{0} matches · {1:N0} rounds · {2}", o.Matches, o.Rounds, Hours(o.PlayMs)));
    }

    FrameworkElement ScorePanel(List<VMatch> ms, string id)
    {
        var scores = ms.Select(m => Tracker.MatchScore(m, id)).ToList();
        var known = scores.Where(x => !double.IsNaN(x)).ToList();
        double avg = known.Count > 0 ? Math.Round(known.Average()) : double.NaN;
        var sp = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(Txt(N(avg), 34, double.IsNaN(avg) ? TpMuted : avg >= 800 ? TpGold : avg >= 650 ? TpBlue : TpInk, true));
        top.Children.Add(Txt(" / 1000", 12, TpMuted).Also(t => t.Margin = new Thickness(4, 12, 0, 0)));
        sp.Children.Add(top);
        sp.Children.Add(new TextBlock
        {
            Text = double.IsNaN(avg) ? Loc.T("Shows after your first match.") : Loc.F("Better than {0:0}% of the players Pulse has seen", avg / 10),
            FontSize = 11.5, Foreground = B(TpMuted), TextWrapping = TextWrapping.Wrap,
        });
        var hist = scores.Take(20).Reverse().ToList();
        if (hist.Any(x => !double.IsNaN(x)))
        {
            var bars = new UniformGrid { Rows = 1, Height = 36, Margin = new Thickness(0, 12, 0, 0) };
            foreach (var v in hist)
                bars.Children.Add(new Border
                {
                    Height = double.IsNaN(v) ? 2 : Math.Max(2, v / 1000 * 36), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1, 0, 1, 0),
                    Background = B(double.IsNaN(v) ? TpLine : v >= 800 ? TpGold : v >= 500 ? Color.FromRgb(0x8A, 0x93, 0xA6) : TpDown), ToolTip = double.IsNaN(v) ? null : N(v),
                });
            sp.Children.Add(bars);
            sp.Children.Add(Txt(Loc.F("Last {0} matches · 500 = average", hist.Count), 11, TpMuted).Also(t => t.Margin = new Thickness(0, 4, 0, 0)));
        }
        return Flat("Pulse score", sp);
    }

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
        var rc = RankColor(sel.Tier);
        var card = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(Txt(sel.Name.Length > 0 ? sel.Name : Loc.T("Earlier act"), 12.5, TpInk, true));
        if (sel == cur) top.Children.Add(new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(8, 0, 0, 0), Background = B(Color.FromArgb(0x2A, TpUp.R, TpUp.G, TpUp.B)), Child = Txt(Loc.T("CURRENT"), 9.5, TpUp, true) });
        card.Children.Add(top);
        var rankRow = new Grid();
        rankRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rankRow.ColumnDefinitions.Add(new ColumnDefinition());
        rankRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (MatchArt.RankIcon(sel.Tier) is { } big) rankRow.Children.Add(Pic(big, 68, 68, 14));
        else rankRow.Children.Add(new Border { Width = 68, Height = 68, Margin = new Thickness(0, 0, 14, 0), CornerRadius = new CornerRadius(34), Background = B(TpLine) });
        var rn = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        rn.Children.Add(Txt(Loc.T(sel == cur ? "Rating" : "Final rank"), 10.5, TpMuted, true));
        rn.Children.Add(Txt(sel.Tier >= 3 ? ValorantMatches.RankName(sel.Tier) : Loc.T("Unrated"), 20, sel.Tier >= 3 ? rc : TpMuted, true));
        if (sel == cur && p.IsYou && p.Tier >= 3) rn.Children.Add(Txt($"{p.Rr} RR", 12, TpBody));
        Grid.SetColumn(rn, 1); rankRow.Children.Add(rn);
        if (sel.Peak >= 3)
        {
            var pk = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            pk.Children.Add(Txt(Loc.T("Peak"), 10.5, TpMuted, true, HorizontalAlignment.Center));
            if (MatchArt.RankIcon(sel.Peak) is { } pi) pk.Children.Add(Pic(pi, 34, 34, 0).Also(i => { i.HorizontalAlignment = HorizontalAlignment.Center; i.Margin = new Thickness(0, 2, 0, 2); }));
            pk.Children.Add(Txt(ValorantMatches.RankName(sel.Peak), 11, RankColor(sel.Peak), true, HorizontalAlignment.Center));
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
            var ac = RankColor(a.Tier);
            var t = new Grid { Width = 176 };
            t.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            t.ColumnDefinitions.Add(new ColumnDefinition());
            if (MatchArt.RankIcon(a.Tier) is { } ri) t.Children.Add(Pic(ri, 30, 30, 10));
            else t.Children.Add(new Border { Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0), CornerRadius = new CornerRadius(15), Background = B(TpLine) });
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(Txt(a == cur ? Loc.T("Current act") : (a.Name.Length > 0 ? a.Name : Loc.T("Earlier act")), 10.5, a == cur ? TpUp : TpMuted, true));
            info.Children.Add(Txt(a.Tier >= 3 ? ValorantMatches.RankName(a.Tier) : Loc.T("Unrated"), 12.5, a.Tier >= 3 ? ac : TpMuted, true));
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

    FrameworkElement ActsPanel(Profile p)
    {
        var sp = new StackPanel();
        int i = 0;
        foreach (var a in p.Acts.Take(8))
        {
            var g = new Grid { Margin = new Thickness(12, 7, 12, 7) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (MatchArt.RankIcon(a.Tier) is { } ri) g.Children.Add(Pic(ri, 28, 28, 0));
            var n = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            n.Children.Add(Txt(a.Tier >= 3 ? ValorantMatches.RankName(a.Tier) : Loc.T("Unrated"), 12.5, TpInk, true));
            n.Children.Add(Txt((a.Name.Length > 0 ? a.Name : Loc.T("Earlier act")) + (a.Current ? " · " + Loc.T("now") : ""), 11, TpMuted));
            Grid.SetColumn(n, 1); g.Children.Add(n);
            var r = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            r.Children.Add(Txt(a.Games > 0 ? $"{a.Wins}W {a.Games - a.Wins}L" : "–", 12, TpBody, false, HorizontalAlignment.Right));
            if (a.Peak >= 3) r.Children.Add(Txt(Loc.F("peak {0}", ValorantMatches.RankName(a.Peak)), 11, TpMuted, false, HorizontalAlignment.Right));
            Grid.SetColumn(r, 2); g.Children.Add(r);
            var row = new Border { BorderBrush = B(TpLine), BorderThickness = new Thickness(0, i++ == 0 ? 0 : 1, 0, 0), Child = g, Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = Loc.T("Show this act") };
            var key = a.Current ? "current" : a.Id;
            row.MouseLeftButtonUp += (_, _) => { _trAct = key; Build(); };
            sp.Children.Add(row);
        }
        return Flat("Act history", sp);
    }

    // ───────── accuracy, weapons, roles, maps, agents ─────────

    FrameworkElement AccuracyPanel(List<VMatch> ms, string id)
    {
        var a = Panels.AccuracyOf(ms, id);
        var g = new Grid { Margin = new Thickness(12, 10, 12, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        Color Shade(double pct, Color hot) => double.IsNaN(pct) ? Color.FromRgb(0x30, 0x34, 0x3C) : Color.FromArgb((byte)Math.Clamp(70 + pct * 3, 70, 255), hot.R, hot.G, hot.B);
        var fig = new Canvas { Width = 40, Height = 92, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        fig.Children.Add(new System.Windows.Shapes.Ellipse { Width = 15, Height = 15, Fill = B(Shade(a.Pct(a.Head), TpUp)) }.Also(e => { Canvas.SetLeft(e, 12.5); Canvas.SetTop(e, 1); }));
        fig.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M10 19 H30 Q34 19 34 24 V46 H29 V41 H11 V46 H6 V24 Q6 19 10 19Z M11 41 H29 V50 H11Z"), Fill = B(Shade(a.Pct(a.Body) / 3, Color.FromRgb(0x9A, 0xA0, 0xAE))) });
        fig.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M11 50 H19 V90 H13Z M21 50 H29 L27 90 H21Z"), Fill = B(Shade(a.Pct(a.Legs) * 3, Color.FromRgb(0x9A, 0xA0, 0xAE))) });
        g.Children.Add(fig);
        var rows = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        void Line3(string name, int n, Color c)
        {
            var r = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            r.ColumnDefinitions.Add(new ColumnDefinition());
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            r.Children.Add(Txt(Loc.T(name), 12.5, TpMuted));
            var pct = Txt(a.Hits > 0 ? N(a.Pct(n), "0.0") + "%" : "–", 12.5, c, true, HorizontalAlignment.Right); Grid.SetColumn(pct, 1); r.Children.Add(pct);
            var hits = Txt(Loc.F("{0:N0} hits", n), 11.5, TpMuted, false, HorizontalAlignment.Right); Grid.SetColumn(hits, 2); r.Children.Add(hits);
            rows.Children.Add(r);
        }
        Line3("Head", a.Head, TpUp);
        Line3("Body", a.Body, TpInk);
        Line3("Legs", a.Legs, TpInk);
        if (a.HsPerMatch.Count >= 3)
        {
            double mx = Math.Max(10, a.HsPerMatch.Max()), mn = Math.Min(a.HsPerMatch.Min(), mx - 10);
            var pts = new PointCollection(a.HsPerMatch.Select((v, i) => new Point(i * 200.0 / (a.HsPerMatch.Count - 1), 30 - (v - mn) / (mx - mn) * 30)));
            rows.Children.Add(new System.Windows.Shapes.Polyline { Points = pts, Stroke = B(TpUp), StrokeThickness = 1.5, Stretch = Stretch.Fill, Height = 30, Margin = new Thickness(0, 8, 0, 0) });
            rows.Children.Add(Txt(Loc.F("Headshot rate, last {0} matches", a.HsPerMatch.Count), 11, TpMuted));
        }
        Grid.SetColumn(rows, 1);
        g.Children.Add(rows);
        return Flat("Accuracy", g, Loc.F("last {0} matches", Math.Min(20, ms.Count)));
    }

    FrameworkElement WeaponsPanel(List<VMatch> ms, string id)
    {
        var list = Panels.Weapons(ms, id).Take(5).ToList();
        var sp = new StackPanel();
        if (list.Count == 0) sp.Children.Add(Txt(Loc.T("No weapon kills yet."), 12, TpMuted).Also(t => t.Margin = new Thickness(12)));
        int i = 0;
        foreach (var w in list)
        {
            var g = new Grid { Margin = new Thickness(12, 8, 12, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (MatchArt.WeaponIcon(w.Id) is { } icon) g.Children.Add(Pic(icon, 88, 28, 0).Also(im => im.HorizontalAlignment = HorizontalAlignment.Left));
            else g.Children.Add(Txt(w.Name, 11, TpMuted));
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(Txt(w.Name, 12.5, TpInk, true));
            var sub = new List<string>();
            if (w.Category.Length > 0) sub.Add(Loc.T(w.Category));
            if (!double.IsNaN(w.AvgMeters)) sub.Add(Loc.F("avg {0:0} m", w.AvgMeters));
            name.Children.Add(Txt(string.Join(" · ", sub), 11, TpMuted));
            Grid.SetColumn(name, 1); g.Children.Add(name);
            var k = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            k.Children.Add(Txt(w.Kills.ToString("N0", CultureInfo.CurrentCulture) + " " + Loc.T("kills"), 12.5, TpInk, true, HorizontalAlignment.Right));
            k.Children.Add(Txt(Loc.F("{0:0}% of kills", w.Share), 11, TpMuted, false, HorizontalAlignment.Right));
            Grid.SetColumn(k, 2); g.Children.Add(k);
            sp.Children.Add(new Border { BorderBrush = B(TpLine), BorderThickness = new Thickness(0, i++ == 0 ? 0 : 1, 0, 0), Child = g });
        }
        return Flat("Top weapons", sp);
    }

    FrameworkElement RolesPanel(List<VMatch> ms, string id)
    {
        var list = Panels.Roles(ms, id);
        var sp = new StackPanel();
        int i = 0;
        foreach (var r in list)
        {
            var g = new Grid { Margin = new Thickness(12, 7, 12, 7) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            var n = new StackPanel();
            n.Children.Add(Txt(Loc.T(r.Role), 12.5, TpInk, true));
            n.Children.Add(Txt(Loc.F("{0}W {1}L", r.Wins, r.Losses), 11, TpMuted));
            g.Children.Add(n);
            var w = new StackPanel();
            w.Children.Add(Txt(N(r.WinPct, "0.0") + "%", 12.5, r.WinPct >= 50 ? TpUp : TpDown, true, HorizontalAlignment.Right));
            w.Children.Add(Txt(Loc.T("win"), 11, TpMuted, false, HorizontalAlignment.Right));
            Grid.SetColumn(w, 1); g.Children.Add(w);
            var k = new StackPanel();
            k.Children.Add(Txt(N(r.Kda, "0.00"), 12.5, TpInk, true, HorizontalAlignment.Right));
            k.Children.Add(Txt("KDA", 11, TpMuted, false, HorizontalAlignment.Right));
            Grid.SetColumn(k, 2); g.Children.Add(k);
            sp.Children.Add(new Border { BorderBrush = B(TpLine), BorderThickness = new Thickness(0, i++ == 0 ? 0 : 1, 0, 0), Child = g });
        }
        if (list.Count == 0) sp.Children.Add(Txt(Loc.T("No matches yet."), 12, TpMuted).Also(t => t.Margin = new Thickness(12)));
        return Flat("Roles", sp);
    }

    FrameworkElement MapsPanel(List<VMatch> ms, string id)
    {
        var rows = Panels.Maps(ms, id).Take(8).Select(m => new UIElement[]
        {
            new StackPanel { Orientation = Orientation.Horizontal }.Also(s =>
            {
                if (MatchArt.MapPicture(m.Map) is { } pic) s.Children.Add(new Border { Width = 40, Height = 22, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 8, 0), ClipToBounds = true, Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill } });
                s.Children.Add(Txt(m.Map, 12.5, TpInk, true));
            }),
            Txt(N(m.WinPct, "0.0") + "%", 12.5, m.WinPct >= 50 ? TpUp : TpDown, true),
            Txt($"{m.Wins}W {m.Losses}L", 12, TpMuted),
        });
        return Flat("Top maps", Table(new[] { "Map", "Win %", "W–L" }, new double[] { 0, 64, 70 }, rows));
    }

    FrameworkElement AgentsTable(List<VMatch> ms, string id)
    {
        var rows = Panels.Agents(ms, id).Take(8).Select(a =>
        {
            var name = new StackPanel { Orientation = Orientation.Horizontal };
            if (MatchArt.AgentIcon(a.Agent) is { } ai) name.Children.Add(Pic(ai, 28, 28));
            var nm = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nm.Children.Add(Txt(a.Agent, 12.5, TpInk, true));
            nm.Children.Add(Txt(Hours(a.PlayMs), 11, TpMuted));
            name.Children.Add(nm);
            var t = a.Totals;
            return new UIElement[]
            {
                name,
                Txt(a.Matches.ToString(CultureInfo.CurrentCulture)),
                Txt(N(a.WinPct, "0.0") + "%", 12.5, a.WinPct >= 50 ? TpUp : TpBody),
                Txt(N(t.KdRatio, "0.00"), 12.5, KdColor(t.KdRatio), true),
                Txt(N(t.Adr, "0.0")),
                Txt(N(t.Acs, "0.0")),
                Txt(Signed(t.DamageDelta), 12.5, SignColor(t.DamageDelta)),
                Txt(N(a.Score), 12.5, TpInk, true),
                Txt(a.BestMap.Length > 0 ? $"{a.BestMap} {N(a.BestMapWin)}%" : "–", 12, TpMuted),
            };
        });
        return Flat("Top agents", Table(new[] { "Agent", "Matches", "Win %", "K/D", "ADR", "ACS", "DDΔ", "Score", "Best map" }, new double[] { 0, 64, 62, 52, 56, 56, 52, 52, 120 }, rows));
    }

    // ───────── match history ─────────

    static string _hfMap = "", _hfAgent = "", _hfResult = "";
    static int _hfDays;

    /// <summary>Map, agent, result and date filters for the match list.</summary>
    FrameworkElement HistoryFilters(Profile p, List<VMatch> all)
    {
        var bar = new WrapPanel { Margin = new Thickness(12, 8, 12, 8) };
        ComboBox Pick(string any, IEnumerable<(string Key, string Label)> items, string sel, Action<string> set)
        {
            var b = new ComboBox { MinWidth = 130, Margin = new Thickness(0, 0, 8, 0) };
            b.Items.Add(new ComboBoxItem { Content = Loc.T(any), Tag = "" });
            foreach (var (k, l) in items) b.Items.Add(new ComboBoxItem { Content = l, Tag = k });
            b.SelectedIndex = Math.Max(0, b.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == sel));
            b.SelectionChanged += (_, _) => { if (b.SelectedItem is ComboBoxItem ci) { set((string)ci.Tag); Build(); } };
            return b;
        }
        bar.Children.Add(Pick("All maps", all.Select(m => m.Map).Where(x => !string.IsNullOrEmpty(x)).Distinct().OrderBy(x => x).Select(x => (x, x)), _hfMap, v => _hfMap = v));
        bar.Children.Add(Pick("All agents", all.Select(m => m.Players.FirstOrDefault(x => x.Id == p.Puuid)?.Agent).Where(x => !string.IsNullOrEmpty(x)).Distinct().OrderBy(x => x).Select(x => (x, x)), _hfAgent, v => _hfAgent = v));
        bar.Children.Add(Pick("Wins and losses", new[] { ("win", Loc.T("Wins")), ("loss", Loc.T("Losses")) }, _hfResult, v => _hfResult = v));
        bar.Children.Add(Pick("Any date", new[] { ("1", Loc.T("Today")), ("7", Loc.T("Last 7 days")), ("30", Loc.T("Last 30 days")), ("90", Loc.T("Last 90 days")) }, _hfDays > 0 ? _hfDays.ToString(CultureInfo.InvariantCulture) : "", v => _hfDays = v.Length > 0 ? int.Parse(v, CultureInfo.InvariantCulture) : 0));
        if (_hfMap.Length + _hfAgent.Length + _hfResult.Length > 0 || _hfDays > 0)
        {
            var clear = Btn("Clear filters");
            clear.Click += (_, _) => { _hfMap = _hfAgent = _hfResult = ""; _hfDays = 0; Build(); };
            bar.Children.Add(clear);
        }
        return bar;
    }

    static List<VMatch> HistoryFiltered(Profile p, List<VMatch> ms) => ms.Where(m =>
        (_hfMap.Length == 0 || m.Map == _hfMap)
        && (_hfAgent.Length == 0 || m.Players.FirstOrDefault(x => x.Id == p.Puuid)?.Agent == _hfAgent)
        && (_hfResult.Length == 0 || Pulse.Core.Games.Valorant.Intel.Util.Won(m, p.Puuid) == (_hfResult == "win"))
        && (_hfDays == 0 || m.StartUtc.ToLocalTime().Date > DateTime.Today.AddDays(-_hfDays))).ToList();

    FrameworkElement HistoryPanel(Profile p, List<VMatch> all)
    {
        var sp = new StackPanel();
        sp.Children.Add(HistoryFilters(p, all));
        var ms = HistoryFiltered(p, all);
        var heads = new[] { "", "Match", "Result", "", "Place", "K / D / A", "K/D", "HS%", "ADR", "ACS", "DDΔ", "Score" };
        var widths = new double[] { 44, 0, 84, 30, 92, 84, 50, 50, 56, 50, 50, 52 };
        var head = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        foreach (var w in widths) head.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
        for (int c = 0; c < heads.Length; c++)
        {
            var t = Txt(Loc.T(heads[c]), 11, TpMuted, true, c >= 4 ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            var host = new Border { Padding = new Thickness(c == 0 ? 12 : 6, 7, c == heads.Length - 1 ? 12 : 6, 7), Child = t };
            Grid.SetColumn(host, c); head.Children.Add(host);
        }
        foreach (var day in Stats.Days(ms, p.Puuid))
        {
            var d = day.Totals;
            var dayBar = new Grid { Background = B(Color.FromRgb(0x1D, 0x20, 0x27)) };
            dayBar.ColumnDefinitions.Add(new ColumnDefinition());
            dayBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var date = day.Date == DateTime.Today ? Loc.T("Today") : day.Date == DateTime.Today.AddDays(-1) ? Loc.T("Yesterday") : day.Date.ToString("ddd, d MMM", CultureInfo.CurrentCulture);
            dayBar.Children.Add(Txt(date, 12.5, TpInk, true).Also(t => t.Margin = new Thickness(12, 7, 12, 7)));
            var sum = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 7, 12, 7) };
            sum.Children.Add(Txt($"{d.Wins}W {d.Losses}L", 12, d.Wins >= d.Losses ? TpUp : TpDown, true));
            sum.Children.Add(Txt("   K/D " + N(d.Kd, "0.00"), 12, KdColor(d.Kd)));
            sum.Children.Add(Txt("   HS " + (double.IsNaN(d.HeadshotPct) ? "–" : N(d.HeadshotPct) + "%"), 12, TpBody));
            sum.Children.Add(Txt("   ADR " + N(d.DamagePerRound), 12, TpBody));
            Grid.SetColumn(sum, 1); dayBar.Children.Add(sum);
            sp.Children.Add(dayBar);
            if (sp.Children.Count == 2) sp.Children.Insert(1, head);
            foreach (var m in day.Matches) sp.Children.Add(HistoryRow(m, p.Puuid, widths));
        }
        if (ms.Count == 0) sp.Children.Add(Txt(Loc.T("No matches here. Try another playlist, act or filter."), 12, TpMuted).Also(t => t.Margin = new Thickness(12)));
        if (!p.FromYourMatches && ValorantSearch.HasMore(p))
        {
            var more = Btn(_trMoreBusy ? "Loading…" : "Load 10 more matches");
            more.IsEnabled = !_trMoreBusy;
            more.Margin = new Thickness(12, 10, 12, 12);
            more.HorizontalAlignment = HorizontalAlignment.Left;
            more.Click += async (_, _) => await LoadMore(p);
            sp.Children.Add(more);
        }
        return Flat("Match history", sp, Loc.F("{0} matches · click one for details", ms.Count));
    }

    FrameworkElement HistoryRow(VMatch m, string id, double[] widths)
    {
        var me = m.Players.FirstOrDefault(x => x.Id == id);
        var team = m.TeamOf(id);
        int us = m.Rounds.Count(r => r.Winner == team), them = m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != team);
        bool won = us > them, draw = us == them;
        var t = Analysis.Totals(m, id);
        var place = Stats.PlaceOf(m, id);
        double score = Tracker.MatchScore(m, id);
        var g = new Grid();
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
        void Put(int col, UIElement el) { var host = new Border { Padding = new Thickness(col == 0 ? 12 : 6, 6, col == widths.Length - 1 ? 12 : 6, 6), Child = el }; Grid.SetColumn(host, col); g.Children.Add(host); }
        if (MatchArt.AgentIcon(me?.Agent) is { } ai) Put(0, Pic(ai, 30, 30, 0)); else Put(0, Txt(me?.Agent ?? "", 11, TpMuted));
        var info = new StackPanel();
        info.Children.Add(Txt(m.Map, 13, TpInk, true));
        info.Children.Add(Txt(Loc.T(QueueName(m.Queue)) + " · " + Ago(m.StartUtc), 11, TpMuted));
        Put(1, info);
        var res = new StackPanel();
        res.Children.Add(Txt($"{us} : {them}", 13.5, draw ? TpBody : won ? TpUp : TpDown, true));
        res.Children.Add(Txt(Loc.T(draw ? "Draw" : won ? "Victory" : "Defeat"), 10.5, draw ? TpMuted : won ? TpUp : TpDown));
        Put(2, res);
        if (MatchArt.RankIcon(me?.Tier ?? 0) is { } ri) Put(3, Pic(ri, 22, 22, 0).Also(im => im.ToolTip = ValorantMatches.RankName(me.Tier)));
        if (place.Place > 0)
        {
            var (label, c) = place.MatchMvp ? (Loc.T("Match MVP"), TpGold) : place.TeamMvp ? (Loc.T("Team MVP"), Color.FromRgb(0xC9, 0xCF, 0xD9)) : (Loc.F("#{0}", place.Place), TpMuted);
            var pill = new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2, 7, 3), HorizontalAlignment = HorizontalAlignment.Right,
                Background = B(Color.FromArgb(place.MatchMvp || place.TeamMvp ? (byte)0x26 : (byte)0x00, c.R, c.G, c.B)), BorderBrush = B(Color.FromArgb(0x80, c.R, c.G, c.B)), BorderThickness = new Thickness(place.MatchMvp || place.TeamMvp ? 1 : 0),
                Child = Txt(label, 11, c, true),
            };
            Put(4, pill);
        }
        Put(5, Txt($"{t.Kills} / {t.Deaths} / {t.Assists}", 12.5, TpInk, true, HorizontalAlignment.Right));
        Put(6, Txt(N(t.KdRatio, "0.00"), 12.5, KdColor(t.KdRatio), true, HorizontalAlignment.Right));
        Put(7, Txt(double.IsNaN(t.HeadshotPct) ? "–" : N(t.HeadshotPct) + "%", 12.5, TpBody, false, HorizontalAlignment.Right));
        Put(8, Txt(N(t.Adr), 12.5, TpBody, false, HorizontalAlignment.Right));
        Put(9, Txt(N(t.Acs), 12.5, TpBody, false, HorizontalAlignment.Right));
        Put(10, Txt(Signed(t.DamageDelta), 12.5, SignColor(t.DamageDelta), false, HorizontalAlignment.Right));
        Put(11, Txt(N(score), 12.5, score >= 800 ? TpGold : TpInk, true, HorizontalAlignment.Right));
        var c2 = draw ? TpMuted : won ? TpUp : TpDown;
        var row = new Border
        {
            Child = g, BorderBrush = B(TpLine), BorderThickness = new Thickness(0, 1, 0, 0), Cursor = Cursors.Hand,
            Background = new LinearGradientBrush(Color.FromArgb(0x22, c2.R, c2.G, c2.B), Color.FromArgb(0x00, c2.R, c2.G, c2.B), 0),
            ToolTip = Loc.T("Open the match"),
        };
        var host = new Grid();
        host.Children.Add(row);
        host.Children.Add(new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = B(c2), IsHitTestVisible = false });
        row.MouseLeftButtonUp += (_, _) => { _trMatchId = m.Id; _trMatchTab = "scoreboard"; Build(); Scroller.ScrollToTop(); };
        return host;
    }

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

    IEnumerable<FrameworkElement> MatchPage(VMatch m, string viewer)
    {
        var b = Panels.BoardOf(m, viewer);
        // Back.
        var back = new Button { Style = (Style)FindResource("LinkButton"), Content = "‹ " + Loc.T("Back to profile"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        back.Click += (_, _) => { _trMatchId = null; Build(); };
        yield return back;

        // Banner: map picture, result, details.
        var banner = new Grid { Height = 110 };
        if (MatchArt.MapPicture(m.Map) is { } pic) banner.Children.Add(new Border { Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill, Opacity = 0.35 } });
        banner.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(0xF0, 0x12, 0x14, 0x18), Color.FromArgb(0x60, 0x12, 0x14, 0x18), 0) });
        var info = new StackPanel { Margin = new Thickness(20, 16, 20, 16), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Txt(m.Map, 22, TpInk, true));
        info.Children.Add(Txt(string.Join(" · ", new[]
        {
            Loc.T(QueueName(m.Queue)), m.StartUtc.ToLocalTime().ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture),
            TimeSpan.FromMilliseconds(m.LengthMs).ToString(@"m\:ss", CultureInfo.InvariantCulture),
            b.AvgTier >= 3 ? Loc.F("avg rank {0}", ValorantMatches.RankName((int)Math.Round(b.AvgTier))) : "",
        }.Where(x => x.Length > 0)), 12, TpMuted));
        banner.Children.Add(info);
        var score = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20) };
        bool won = b.TeamScore > b.EnemyScore;
        score.Children.Add(Txt(b.TeamScore.ToString(CultureInfo.CurrentCulture), 34, TpUp, true));
        score.Children.Add(Txt(" : ", 26, TpMuted));
        score.Children.Add(Txt(b.EnemyScore.ToString(CultureInfo.CurrentCulture), 34, TpDown, true));
        score.Children.Add(Txt("  " + Loc.T(b.TeamScore == b.EnemyScore ? "Draw" : won ? "Victory" : "Defeat"), 14, b.TeamScore == b.EnemyScore ? TpMuted : won ? TpUp : TpDown, true));
        banner.Children.Add(score);
        yield return new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = banner, Margin = new Thickness(0, 0, 0, 10), Background = B(TpBg) };

        // Tabs.
        var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (key, label) in new[] { ("scoreboard", "Scoreboard"), ("performance", "Performance"), ("economy", "Economy"), ("rounds", "Rounds"), ("duels", "Duels"), ("autopsy", "Autopsy") })
            tabs.Children.Add(Chip(Loc.T(label), "trmatchtab", _trMatchTab == key, () => { _trMatchTab = key; Build(); }));
        yield return tabs;

        yield return _trMatchTab switch
        {
            "performance" => PerformanceView(m, b, viewer),
            "economy" => EconomyView(m, b),
            "rounds" => RoundsView(m, b, viewer),
            "duels" => DuelsView(m, b, viewer),
            "autopsy" => AutopsyView(m, viewer),
            _ => ScoreboardView(m, b, viewer),
        };
    }

    /// <summary>What happened in the match (facts) kept apart from what it might mean (interpretations).</summary>
    FrameworkElement AutopsyView(VMatch m, string viewer)
    {
        var pool = _trSearched != null && _trSearched.Puuid == viewer ? _trSearched.Matches : VStore.All.Where(x => x.Players.Any(p => p.Id == viewer)).OrderByDescending(x => x.StartUtc).ToList();
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

    /// <summary>The player cell: agent, rank, party mark and Riot ID (click to open their profile).</summary>
    FrameworkElement PlayerCell(VMatch m, VPlayer p, string viewer, Dictionary<string, Color> parties)
    {
        var who = new StackPanel { Orientation = Orientation.Horizontal };
        if (MatchArt.AgentIcon(p.Agent) is { } ai) who.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 9, 0), ClipToBounds = true, Background = new ImageBrush(ai) { Stretch = Stretch.UniformToFill }, ToolTip = p.Agent });
        else who.Children.Add(new Border { Width = 30, Height = 30, Margin = new Thickness(0, 0, 9, 0), CornerRadius = new CornerRadius(5), Background = B(TpLine), ToolTip = Loc.T("Agent not known yet: saved by an older Pulse. Open VALORANT and Pulse fills it in."),
            Child = Txt("?", 13, TpMuted, true, HorizontalAlignment.Center).Also(t => t.VerticalAlignment = VerticalAlignment.Center) });
        if (MatchArt.RankIcon(p.Tier) is { } ri) who.Children.Add(Pic(ri, 20, 20, 8).Also(im => im.ToolTip = ValorantMatches.RankName(p.Tier)));
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var riotId = Stats.NameOf(m, p.Id) ?? NameBook.Get(p.Id);
        var name = riotId ?? (p.Agent.Length > 0 ? p.Agent : Loc.T("Unknown player"));
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        if (parties.TryGetValue(p.Id, out var pc)) line.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = B(pc), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("Queued together") });
        // "Name #TAG": the tag dimmer, like the game shows it.
        int hash = name.LastIndexOf('#');
        if (riotId != null && hash > 0)
        {
            line.Children.Add(Txt(name[..hash], 13, p.Id == viewer ? TpGold : TpInk, true));
            line.Children.Add(Txt(" #" + name[(hash + 1)..], 11.5, TpMuted).Also(t => t.VerticalAlignment = VerticalAlignment.Bottom));
        }
        else line.Children.Add(Txt(name, 13, p.Id == viewer ? TpGold : TpInk, true));
        names.Children.Add(line);
        var sub = new List<string>();
        if (riotId != null && p.Agent.Length > 0) sub.Add(p.Agent);
        if (p.Tier >= 3) sub.Add(ValorantMatches.RankName(p.Tier));
        if (p.Level > 0) sub.Add(Loc.F("level {0}", p.Level));
        if (sub.Count > 0) names.Children.Add(Txt(string.Join(" · ", sub), 10.5, TpMuted));
        who.Children.Add(names);
        return who;
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

    /// <summary>Opens a player's profile: from your matches when they're in them, else by searching their Riot ID.</summary>
    void OpenPlayer(string id)
    {
        if (!CoachMatches().Any(m => m.Players.Any(x => x.Id == id)))
        {
            if (NameBook.Get(id) is { } riotId && _trSearchFor != null) { _trMatchId = null; _ = _trSearchFor(riotId); }
            else if (_trSearched?.Matches.SelectMany(m => m.Players).FirstOrDefault(x => x.Id == id)?.Name is { } n && _trSearchFor != null) { _trMatchId = null; _ = _trSearchFor(n); }
            return;
        }
        _trMatchId = null;
        _trSel = id;
        _trQueue = null;
        Build();
        Scroller.ScrollToTop();
    }

    FrameworkElement ScoreboardView(VMatch m, Panels.Board b, string viewer)
    {
        var sp = new StackPanel();
        var parties = Parties(m);
        // Rounds strip.
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 12, 2) };
        foreach (var r in m.Rounds.OrderBy(r => r.N))
        {
            bool ours = r.Winner == b.Team;
            var col = ours ? TpUp : TpDown;
            var cell = new StackPanel { Width = 22, Margin = new Thickness(r.N == m.HalfLength && m.Rounds.Count > m.HalfLength ? 10 : 1, 0, 1, 0), ToolTip = $"{Loc.F("Round {0}", r.N + 1)} · {Loc.T(ours ? "Won" : "Lost")} · {Loc.T(RoundHow(r.Result))}" };
            cell.Children.Add(new Border { Height = 18, CornerRadius = new CornerRadius(3), Background = B(Color.FromArgb(0x30, col.R, col.G, col.B)), BorderBrush = B(col), BorderThickness = new Thickness(0, 0, 0, 2), Child = Txt(RoundMark(r.Result), 10, col, true, HorizontalAlignment.Center) });
            cell.Children.Add(Txt((r.N + 1).ToString(CultureInfo.CurrentCulture), 9.5, TpMuted, false, HorizontalAlignment.Center));
            strip.Children.Add(cell);
        }
        sp.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = strip });
        sp.Children.Add(Txt(Loc.T("E elimination · D spike defused · X spike detonated · T time ran out"), 10.5, TpMuted).Also(t => t.Margin = new Thickness(12, 0, 12, 4)));

        var heads = new[] { "Player", "Pulse", "ACS", "K", "D", "A", "+/−", "K/D", "ADR", "DDΔ", "HS%", "KAST", "FK", "FD" };
        var tips = new[] { "", "Pulse Score: one match's overall impact (0–1000)", "Average combat score", "Kills", "Deaths", "Assists", "Kills minus deaths", "Kills per death",
            "Damage per round", "Damage dealt minus taken, per round", "Headshot rate", "Rounds with a kill, assist, survival or trade", "First kills", "First deaths" };
        var mvp = b.Lines.OrderByDescending(l => l.Totals.Acs).FirstOrDefault()?.Player.Id;
        foreach (bool ourTeam in new[] { true, false })
        {
            var lines = b.Lines.Where(l => (l.Player.Team == b.Team) == ourTeam).OrderByDescending(l => l.Totals.Acs).ToList();
            if (lines.Count == 0) continue;
            var c = ourTeam ? TpUp : TpDown;
            int score = ourTeam ? b.TeamScore : b.EnemyScore, other = ourTeam ? b.EnemyScore : b.TeamScore;
            var teamMvp = lines[0].Player.Id;
            var ranked = lines.Where(l => l.Player.Tier >= 3).ToList();

            var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.6, GridUnitType.Star), MinWidth = 250 });
            for (int i = 1; i < heads.Length; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 44 });
            int r = 0;
            // Team bar spanning the table.
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var bar = new Grid { Background = new LinearGradientBrush(Color.FromArgb(0x30, c.R, c.G, c.B), Color.FromArgb(0x06, c.R, c.G, c.B), 0) };
            bar.Children.Add(new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = B(c) });
            var barText = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 8, 14, 8) };
            barText.Children.Add(Txt(score.ToString(CultureInfo.CurrentCulture), 20, c, true).Also(t => t.Margin = new Thickness(0, 0, 10, 0)));
            var tn = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            tn.Children.Add(Txt(Loc.T(score > other ? "Won" : score < other ? "Lost" : "Draw") + " · " + Loc.T(ourTeam ? (viewer == m.Me ? "Your team" : "Their team") : "Enemy team"), 12, TpInk, true));
            if (ranked.Count > 0) tn.Children.Add(Txt(Loc.F("avg rank {0}", ValorantMatches.RankName((int)Math.Round(ranked.Average(l => l.Player.Tier)))), 10.5, TpMuted));
            barText.Children.Add(tn);
            bar.Children.Add(barText);
            Grid.SetColumnSpan(bar, heads.Length); grid.Children.Add(bar);
            r++;
            // Column heads.
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < heads.Length; i++)
            {
                var h = Txt(Loc.T(heads[i]), 10.5, TpMuted, true, i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center);
                if (tips[i].Length > 0) h.ToolTip = Loc.T(tips[i]);
                var host = new Border { Padding = new Thickness(i == 0 ? 14 : 4, 6, 4, 6), Child = h };
                Grid.SetRow(host, r); Grid.SetColumn(host, i); grid.Children.Add(host);
            }
            r++;
            for (int li = 0; li < lines.Count; li++)
            {
                var l = lines[li];
                var t = l.Totals;
                int pm = t.Kills - t.Deaths;
                bool you = l.Player.Id == viewer;
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
                var line = new Border
                {
                    Background = you ? B(Color.FromArgb(0x1A, TpGold.R, TpGold.G, TpGold.B)) : li % 2 == 0 ? B(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent,
                    BorderBrush = B(you ? TpGold : Colors.Transparent), BorderThickness = new Thickness(3, 0, 0, 0), Cursor = Cursors.Hand,
                };
                var normal = line.Background;
                line.MouseEnter += (_, _) => line.Background = B(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
                line.MouseLeave += (_, _) => line.Background = normal;
                var id = l.Player.Id;
                line.MouseLeftButtonUp += (_, _) => OpenPlayer(id);
                Grid.SetRow(line, r); Grid.SetColumnSpan(line, heads.Length); grid.Children.Add(line);

                var cellWho = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                cellWho.Children.Add(PlayerCell(m, l.Player, viewer, parties));
                if (id == mvp || id == teamMvp)
                    cellWho.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                        Background = B(Color.FromArgb(0x26, (id == mvp ? TpGold : TpBlue).R, (id == mvp ? TpGold : TpBlue).G, (id == mvp ? TpGold : TpBlue).B)),
                        Child = Txt(id == mvp ? Loc.T("MATCH MVP") : Loc.T("TEAM MVP"), 9.5, id == mvp ? TpGold : TpBlue, true),
                    });
                var cells = new UIElement[]
                {
                    cellWho,
                    Txt(N(l.Score), 13, double.IsNaN(l.Score) ? TpMuted : l.Score >= 800 ? TpGold : TpInk, true),
                    Txt(N(t.Acs), 13, TpInk, true),
                    Txt(t.Kills.ToString(CultureInfo.CurrentCulture), 13, TpInk, true), Txt(t.Deaths.ToString(CultureInfo.CurrentCulture), 13, TpBody), Txt(t.Assists.ToString(CultureInfo.CurrentCulture), 13, TpBody),
                    Txt(Signed(pm), 13, SignColor(pm), true),
                    Txt(N(t.KdRatio, "0.00"), 13, KdColor(t.KdRatio), true),
                    Txt(N(t.Adr), 13, TpBody),
                    Txt(Signed(t.DamageDelta), 13, SignColor(t.DamageDelta)),
                    Txt(double.IsNaN(t.HeadshotPct) ? "–" : N(t.HeadshotPct) + "%", 13, TpBody),
                    Txt(N(t.KastPct) + "%", 13, TpBody),
                    Txt(t.FirstKills.ToString(CultureInfo.CurrentCulture), 13, TpBody), Txt(t.FirstDeaths.ToString(CultureInfo.CurrentCulture), 13, TpBody),
                };
                for (int i = 0; i < cells.Length; i++)
                {
                    if (cells[i] is FrameworkElement fe) { fe.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center; fe.VerticalAlignment = VerticalAlignment.Center; }
                    var host = new Border { Padding = new Thickness(i == 0 ? 11 : 4, 0, 4, 0), Child = cells[i], IsHitTestVisible = false };
                    Grid.SetRow(host, r); Grid.SetColumn(host, i); grid.Children.Add(host);
                }
                r++;
            }
            sp.Children.Add(grid);
        }
        sp.Children.Add(Txt(Loc.T("Click a player to open their profile. Dots mark players who queued together."), 11, TpMuted).Also(t => t.Margin = new Thickness(12, 8, 12, 10)));
        return Flat("Scoreboard", sp);
    }

    FrameworkElement PerformanceView(VMatch m, Panels.Board b, string viewer)
    {
        var sp = new StackPanel();
        var perf = Stats.Performance(m).ToDictionary(x => x.Player.Id);
        var parties = Parties(m);
        var heads = new[] { "Player", "2K", "3K", "4K", "5K", "Clutches", "Plants", "Defuses", "First bloods", "First deaths", "KAST", "Econ" };
        var widths = new double[] { 0, 38, 38, 38, 38, 66, 52, 58, 76, 76, 52, 52 };
        foreach (bool ourTeam in new[] { true, false })
        {
            var lines = b.Lines.Where(l => (l.Player.Team == b.Team) == ourTeam).ToList();
            if (lines.Count == 0) continue;
            var c = ourTeam ? TpUp : TpDown;
            sp.Children.Add(new Border { Background = B(Color.FromArgb(0x1C, c.R, c.G, c.B)), BorderBrush = B(c), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6), Child = Txt(Loc.T(ourTeam ? "Team A" : "Team B"), 12, TpInk, true) });
            var rows = lines.Select(l =>
            {
                var x = perf[l.Player.Id];
                TextBlock Count(int n, Color hot) => Txt(n.ToString(CultureInfo.CurrentCulture), 12.5, n > 0 ? hot : TpMuted, n > 0);
                return new UIElement[]
                {
                    PlayerCell(m, l.Player, viewer, parties),
                    Count(x.TwoK, TpInk), Count(x.ThreeK, TpInk), Count(x.FourK, TpBlue), Count(x.FiveK, TpGold),
                    Txt(x.ClutchChances > 0 ? $"{x.ClutchWins}/{x.ClutchChances}" : "–", 12.5, x.ClutchWins > 0 ? TpUp : TpBody),
                    Count(x.Plants, TpInk), Count(x.Defuses, TpInk), Count(x.FirstBloods, TpInk), Count(x.FirstDeaths, TpDown),
                    Txt(N(x.KastPct) + "%"),
                    Txt(N(x.Econ), 12.5, TpBody),
                };
            }).ToList();
            sp.Children.Add(Table(heads, widths, rows, null, i => lines[i].Player.Id == viewer, i => OpenPlayer(lines[i].Player.Id)));
        }
        sp.Children.Add(Txt(Loc.T("Econ: damage for every 1,000 credits spent. Clutches: rounds won as the last one alive, of the times they were."), 11, TpMuted).Also(t => t.Margin = new Thickness(12, 8, 12, 10)));
        return Flat("Performance", sp);
    }

    static string BuyName(Stats.Buy b) => b switch { Stats.Buy.Eco => "Eco", Stats.Buy.SemiEco => "Semi-eco", Stats.Buy.SemiBuy => "Semi-buy", _ => "Full buy" };

    FrameworkElement EconomyView(VMatch m, Panels.Board b)
    {
        var sp = new StackPanel();
        var eco = Stats.Economy(m, b.Team);
        if (eco.Count == 0) return Flat("Economy", Txt(Loc.T("Riot didn't send the economy for this match."), 12, TpMuted).Also(t => t.Margin = new Thickness(12)));
        // How each kind of buy went for the team.
        var sum = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 0) };
        foreach (var kind in new[] { Stats.Buy.FullBuy, Stats.Buy.SemiBuy, Stats.Buy.SemiEco, Stats.Buy.Eco })
        {
            var these = eco.Where(e => e.TeamBuy == kind).ToList();
            int w = these.Count(e => e.Winner == b.Team);
            var cell = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
            cell.Children.Add(Txt(Loc.T(BuyName(kind)), 11.5, TpMuted));
            cell.Children.Add(Txt(these.Count > 0 ? Loc.F("{0} of {1} won", w, these.Count) : "–", 15, these.Count == 0 ? TpMuted : w * 2 >= these.Count ? TpUp : TpDown, true));
            sum.Children.Add(cell);
        }
        sp.Children.Add(sum);
        sp.Children.Add(new Border { Height = 1, Background = B(TpLine) });
        var rows = eco.Select(e =>
        {
            bool ours = e.Winner == b.Team;
            return new UIElement[]
            {
                Txt(Loc.F("Round {0}", e.N + 1), 12.5, TpInk, true),
                Txt(Loc.T(ours ? "Won" : "Lost"), 12.5, ours ? TpUp : TpDown, true),
                Txt(Loc.T(BuyName(e.TeamBuy)), 12.5, TpBody),
                Txt(e.TeamLoadout.ToString("N0", CultureInfo.CurrentCulture), 12.5, TpInk),
                Txt(e.TeamSpent.ToString("N0", CultureInfo.CurrentCulture), 12.5, TpBody),
                Txt(e.TeamBank.ToString("N0", CultureInfo.CurrentCulture), 12.5, TpMuted),
                Txt(Loc.T(BuyName(e.EnemyBuy)), 12.5, TpBody),
                Txt(e.EnemyLoadout.ToString("N0", CultureInfo.CurrentCulture), 12.5, TpInk),
            };
        });
        sp.Children.Add(Table(new[] { "Round", "Result", "Team buy", "Loadout", "Spent", "Bank", "Enemy buy", "Enemy loadout" }, new double[] { 0, 64, 90, 76, 70, 70, 90, 104 }, rows, i => eco[i].Winner == b.Team ? TpUp : TpDown));
        return Flat("Economy", sp, Loc.T("Loadout: what the team carried into the round"));
    }

    FrameworkElement RoundsView(VMatch m, Panels.Board b, string viewer)
    {
        var sp = new StackPanel();
        foreach (var f in Stats.Feed(m))
        {
            var r = f.Round;
            bool ours = r.Winner == b.Team;
            var c = ours ? TpUp : TpDown;
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 4) };
            head.Children.Add(Txt(Loc.F("Round {0}", r.N + 1), 12.5, TpInk, true));
            head.Children.Add(Txt("   " + Loc.T(ours ? "Won" : "Lost") + " · " + Loc.T(RoundHow(r.Result)), 12, c, true));
            string cer = r.Ceremony switch { "CeremonyAce" => "Ace", "CeremonyTeamAce" => "Team ace", "CeremonyClutch" => "Clutch", "CeremonyFlawless" => "Flawless", "CeremonyThrifty" => "Thrifty", "CeremonyCloser" => "Closer", _ => null };
            if (cer != null) head.Children.Add(Txt("   " + Loc.T(cer), 12, TpGold, true));
            var extra = new List<string>();
            if (r.Planter != null && m.Players.FirstOrDefault(p => p.Id == r.Planter) is { } pl) extra.Add(Loc.F("planted by {0}", NameOrAgent(m, pl)) + (r.Site != null ? " (" + r.Site + ")" : ""));
            if (r.Defuser != null && m.Players.FirstOrDefault(p => p.Id == r.Defuser) is { } df) extra.Add(Loc.F("defused by {0}", NameOrAgent(m, df)));
            if (extra.Count > 0) head.Children.Add(Txt("   " + string.Join(" · ", extra), 11.5, TpMuted));
            sp.Children.Add(new Border { BorderBrush = B(TpLine), BorderThickness = new Thickness(0, sp.Children.Count == 0 ? 0 : 1, 0, 0), Child = head });
            foreach (var k in f.Kills)
            {
                var g = new Grid { Margin = new Thickness(24, 1, 12, 1) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.Children.Add(Txt(TimeSpan.FromMilliseconds(k.RoundMs).ToString(@"m\:ss", CultureInfo.InvariantCulture), 11.5, TpMuted));
                FrameworkElement Side(VPlayer p)
                {
                    var s = new StackPanel { Orientation = Orientation.Horizontal };
                    if (p == null) { s.Children.Add(Txt(Loc.T("Spike / fall"), 12, TpMuted)); return s; }
                    if (MatchArt.AgentIcon(p.Agent) is { } ai) s.Children.Add(Pic(ai, 20, 20, 6));
                    s.Children.Add(Txt(NameOrAgent(m, p), 12, p.Team == b.Team ? TpUp : TpDown, p.Id == viewer));
                    return s;
                }
                var killer = Side(k.Killer); Grid.SetColumn(killer, 1); g.Children.Add(killer);
                FrameworkElement how = k.DamageType.Equals("Weapon", StringComparison.OrdinalIgnoreCase) && MatchArt.WeaponIcon(k.Weapon) is { } wi
                    ? Pic(wi, 70, 20, 0).Also(im => im.ToolTip = MapGeo.Weapon(k.Weapon))
                    : Txt(Loc.T(k.DamageType switch { "Ability" => "Ability", "Bomb" => "Spike", "Melee" => "Knife", "Fall" => "Fall", _ => MapGeo.Weapon(k.Weapon) ?? k.DamageType }), 11, TpMuted);
                Grid.SetColumn(how, 2); g.Children.Add(how);
                var victim = Side(k.Victim); Grid.SetColumn(victim, 3); g.Children.Add(victim);
                sp.Children.Add(g);
            }
            sp.Children.Add(new Border { Height = 6 });
        }
        return Flat("Rounds", sp);
    }

    FrameworkElement DuelsView(VMatch m, Panels.Board b, string viewer)
    {
        var duels = Stats.Duels(m);
        int Kills(string a, string v) => duels.TryGetValue(a, out var row) ? row.GetValueOrDefault(v) : 0;
        var mine = b.Lines.Where(l => l.Player.Team == b.Team).Select(l => l.Player).ToList();
        var theirs = b.Lines.Where(l => l.Player.Team != b.Team).Select(l => l.Player).ToList();
        var g = new Grid { Margin = new Thickness(12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        foreach (var _ in theirs) g.ColumnDefinitions.Add(new ColumnDefinition());
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < theirs.Count; c++)
        {
            var e = theirs[c];
            var head = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2, 0, 2, 8) };
            if (MatchArt.AgentIcon(e.Agent) is { } ai) head.Children.Add(Pic(ai, 30, 30, 0).Also(i => i.HorizontalAlignment = HorizontalAlignment.Center));
            head.Children.Add(Txt(NameOrAgent(m, e), 11, TpDown, true, HorizontalAlignment.Center).Also(t => t.MaxWidth = 110));
            Grid.SetColumn(head, c + 1); g.Children.Add(head);
        }
        for (int r = 0; r < mine.Count; r++)
        {
            var me = mine[r];
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var who = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 8, 2) };
            if (MatchArt.AgentIcon(me.Agent) is { } ai) who.Children.Add(Pic(ai, 26, 26, 8));
            who.Children.Add(Txt(NameOrAgent(m, me), 12, me.Id == viewer ? TpGold : TpUp, true));
            Grid.SetRow(who, r + 1); g.Children.Add(who);
            for (int c = 0; c < theirs.Count; c++)
            {
                int k = Kills(me.Id, theirs[c].Id), d = Kills(theirs[c].Id, me.Id);
                var col = k > d ? TpUp : k < d ? TpDown : TpMuted;
                var cell = new Border
                {
                    Margin = new Thickness(2), CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 6, 4, 6),
                    Background = B(Color.FromArgb(k + d == 0 ? (byte)0x0A : (byte)Math.Min(0x60, 0x14 + Math.Abs(k - d) * 0x12), col.R, col.G, col.B)),
                    Child = Txt(k + d == 0 ? "–" : $"{k}–{d}", 12.5, k + d == 0 ? TpMuted : TpInk, true, HorizontalAlignment.Center),
                    ToolTip = Loc.F("{0} killed {1} {2} times; {1} killed {0} {3} times", NameOrAgent(m, me), NameOrAgent(m, theirs[c]), k, d),
                };
                Grid.SetRow(cell, r + 1); Grid.SetColumn(cell, c + 1); g.Children.Add(cell);
            }
        }
        var sp = new StackPanel();
        sp.Children.Add(g);
        sp.Children.Add(Txt(Loc.T("Each box: kills on that enemy – deaths to them. Green means the row's player won that matchup."), 11, TpMuted).Also(t => t.Margin = new Thickness(12, 0, 12, 10)));
        return Flat("Duels", sp);
    }

    static string RoundHow(string result) => result switch
    {
        "Elimination" => "Elimination",
        "Bomb detonated" or "Detonate" => "Spike detonated",
        "Bomb defused" or "Defuse" => "Spike defused",
        "Round timer expired" => "Time ran out",
        "Surrendered" => "Surrender",
        _ => result,
    };

    /// <summary>One letter for how a round ended.</summary>
    static string RoundMark(string result) => result switch
    {
        "Elimination" => "E", "Bomb defused" or "Defuse" => "D", "Bomb detonated" or "Detonate" => "X", "Round timer expired" => "T", _ => "",
    };

    static string QueueName(string q) => q switch
    {
        "competitive" => "Competitive", "unrated" => "Unrated", "premier" => "Premier", "swiftplay" => "Swiftplay", _ => q,
    };
}
