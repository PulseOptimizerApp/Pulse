using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Pulse.Core.Games;
using Pulse.Core.Games.Live;
using Pulse.Core.Games.Valorant;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// The tracker's detail panels in a plain, dense style: Pulse Score, the stat grid, accuracy, weapons (with the real
/// weapon icons), roles, maps, agents, and a full scoreboard for any recent match.
/// </summary>
public partial class ValorantPage
{
    static readonly Color TpBg = Color.FromRgb(0x18, 0x1B, 0x21), TpLine = Color.FromRgb(0x22, 0x25, 0x2C), TpMuted = Color.FromRgb(0x7D, 0x81, 0x8B),
        TpUp = Color.FromRgb(0x4F, 0xC3, 0x8A), TpDown = Color.FromRgb(0xE5, 0x60, 0x5A), TpInk = Color.FromRgb(0xF1, 0xF2, 0xF4), TpBody = Color.FromRgb(0xD9, 0xDB, 0xE0);
    static string _trOpenMatch;

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

    static string N(double v, string f = "0") => double.IsNaN(v) ? "–" : v.ToString(f, CultureInfo.CurrentCulture);
    static string Signed(double v) => double.IsNaN(v) ? "–" : (v > 0 ? "+" : v < 0 ? "−" : "") + Math.Abs(v).ToString("0", CultureInfo.CurrentCulture);

    /// <summary>A row of panels side by side with set widths (0 = share the rest).</summary>
    static Grid PanelRow(params (UIElement El, double W)[] items)
    {
        var g = new Grid { Margin = new Thickness(0, 0, -10, 0) };
        for (int i = 0; i < items.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = items[i].W > 0 && items[i].W < 10 ? new GridLength(items[i].W, GridUnitType.Star) : items[i].W >= 10 ? new GridLength(items[i].W) : new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(items[i].El, i);
            g.Children.Add(items[i].El);
        }
        return g;
    }

    /// <summary>A plain table: header row, then rows; widths in pixels, 0 = take the rest. Column 0 is left-aligned.</summary>
    Grid Table(string[] heads, double[] widths, IEnumerable<UIElement[]> rows, Func<int, Color?> accent = null)
    {
        var g = new Grid();
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
        int r = 0;
        void AddRow(UIElement[] cells, bool head)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (!head)
            {
                var line = new Border { BorderBrush = B(TpLine), BorderThickness = new Thickness(0, 1, 0, 0) };
                if (accent?.Invoke(r - 1) is Color a) line.Child = new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = B(a) };
                Grid.SetRow(line, r); Grid.SetColumnSpan(line, widths.Length); g.Children.Add(line);
            }
            for (int c = 0; c < cells.Length && c < widths.Length; c++)
            {
                var host = new Border { Padding = new Thickness(c == 0 ? 12 : 6, head ? 7 : 6, c == widths.Length - 1 ? 12 : 6, head ? 7 : 6), Child = cells[c] };
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

    // ───────── Pulse Score and the stat grid ─────────

    FrameworkElement ScorePanel(Profile p)
    {
        var sp = new StackPanel { Margin = new Thickness(12, 10, 12, 12) };
        bool known = !double.IsNaN(p.Score);
        sp.Children.Add(Txt(known ? N(p.Score) : "–", 36, TpInk, true));
        sp.Children.Add(new TextBlock
        {
            Text = known ? Loc.F("Better than {0:0}% of the players Pulse has seen", p.Score / 10) : Loc.T("Needs about 10 matches with other players to compare with."),
            FontSize = 11.5, Foreground = B(TpMuted), TextWrapping = TextWrapping.Wrap,
        });
        var hist = p.ScoreHistory.Take(12).Reverse().ToList();
        if (hist.Any(x => !double.IsNaN(x)))
        {
            var bars = new UniformGrid { Rows = 1, Height = 34, Margin = new Thickness(0, 12, 0, 0) };
            foreach (var v in hist)
                bars.Children.Add(new Border
                {
                    Height = double.IsNaN(v) ? 2 : Math.Max(2, v / 1000 * 34), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1, 0, 1, 0),
                    Background = B(double.IsNaN(v) ? TpLine : v >= 500 ? Color.FromRgb(0x8A, 0x93, 0xA6) : TpDown), ToolTip = double.IsNaN(v) ? null : N(v),
                });
            sp.Children.Add(bars);
            sp.Children.Add(Txt(Loc.F("Last {0} matches · 500 = average", hist.Count), 11, TpMuted).Also(t => t.Margin = new Thickness(0, 4, 0, 0)));
        }
        sp.Children.Add(new TextBlock
        {
            Text = Loc.T("Combat score, KAST, damage delta, damage, opening duels and trades, against every player-match Pulse has seen."),
            FontSize = 11, Foreground = B(TpMuted), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        });
        return Flat("Pulse score", sp);
    }

    FrameworkElement StatGrid(Profile p)
    {
        var grid = new UniformGrid { Columns = 4 };
        int i = 0;
        foreach (var s in p.Stats)
        {
            bool known = !double.IsNaN(s.Value);
            var cell = new StackPanel();
            cell.Children.Add(Txt(Loc.T(s.Skill.Name), 11.5, TpMuted));
            var v = Tracker.Shown(s);
            string unit = s.Skill.Unit switch { "%" => "%", "/100 rounds" => " " + Loc.T("/ 100 rds"), _ => "" };
            cell.Children.Add(Txt(known ? N(v, s.Skill.Unit == "%" || s.Skill.Id is "abil" ? "0.0" : "0") + unit : "–", 18, TpInk, true).Also(t => t.Margin = new Thickness(0, 2, 0, 2)));
            var foot = new StackPanel { Orientation = Orientation.Horizontal };
            if (known && !double.IsNaN(s.TopPercent)) foot.Children.Add(Txt(Loc.F("Top {0:0}%", s.TopPercent), 11, s.TopPercent <= 30 ? TpUp : TpMuted, s.TopPercent <= 30));
            if (known && s.PlaysLikeTier > 0) foot.Children.Add(Txt("  " + Loc.F("like {0}", ValorantMatches.RankName(s.PlaysLikeTier)), 11, TpMuted));
            if (!known) foot.Children.Add(Txt(Loc.T("Not enough data yet"), 11, TpMuted));
            cell.Children.Add(foot);
            grid.Children.Add(new Border
            {
                Padding = new Thickness(12, 10, 12, 10), Child = cell, ToolTip = Loc.T(s.Skill.Explains),
                BorderBrush = B(TpLine), BorderThickness = new Thickness(i % 4 == 0 ? 0 : 1, i >= 4 ? 1 : 0, 0, 0),
            });
            i++;
        }
        return Flat("Stats", grid, Loc.F("last {0} matches · {1:N0} rounds", p.Matches.Count, p.Totals.Rounds));
    }

    // ───────── accuracy, weapons, roles ─────────

    FrameworkElement AccuracyPanel(Profile p)
    {
        var a = Panels.AccuracyOf(p.Matches, p.Puuid);
        var g = new Grid { Margin = new Thickness(12, 10, 12, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        // A plain figure: head, torso, legs, each shaded by how often shots land there.
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
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            r.Children.Add(Txt(Loc.T(name), 12.5, TpMuted));
            var pct = Txt(a.Hits > 0 ? N(a.Pct(n), "0.0") + "%" : "–", 12.5, c, true, HorizontalAlignment.Right); Grid.SetColumn(pct, 1); r.Children.Add(pct);
            var hits = Txt(Loc.F("{0:N0} hits", n), 11.5, TpMuted, false, HorizontalAlignment.Right); Grid.SetColumn(hits, 2); r.Children.Add(hits);
            rows.Children.Add(r);
        }
        Line3("Head", a.Head, TpUp);
        Line3("Body", a.Body, TpInk);
        Line3("Legs", a.Legs, TpInk);
        // Headshot rate match by match.
        if (a.HsPerMatch.Count >= 3)
        {
            double w = 200, h = 30, mx = Math.Max(10, a.HsPerMatch.Max()), mn = Math.Min(a.HsPerMatch.Min(), mx - 10);
            var pts = new PointCollection(a.HsPerMatch.Select((v, i) => new Point(i * w / (a.HsPerMatch.Count - 1), h - (v - mn) / (mx - mn) * h)));
            var line = new System.Windows.Shapes.Polyline { Points = pts, Stroke = B(TpUp), StrokeThickness = 1.5, Stretch = Stretch.Fill, Height = h, Margin = new Thickness(0, 8, 0, 0) };
            rows.Children.Add(line);
            rows.Children.Add(Txt(Loc.F("Headshot rate, last {0} matches", a.HsPerMatch.Count), 11, TpMuted));
        }
        Grid.SetColumn(rows, 1);
        g.Children.Add(rows);
        return Flat("Accuracy", g, Loc.F("last {0} matches", Math.Min(20, p.Matches.Count)));
    }

    FrameworkElement WeaponsPanel(Profile p)
    {
        var list = Panels.Weapons(p.Matches, p.Puuid).Take(4).ToList();
        var sp = new StackPanel();
        if (list.Count == 0) sp.Children.Add(Txt(Loc.T("No weapon kills yet."), 12, TpMuted).Also(t => t.Margin = new Thickness(12)));
        int i = 0;
        foreach (var w in list)
        {
            var g = new Grid { Margin = new Thickness(12, 8, 12, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (MatchArt.WeaponIcon(w.Id) is { } icon) g.Children.Add(Pic(icon, 84, 26, 0).Also(im => im.HorizontalAlignment = HorizontalAlignment.Left));
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(Txt(w.Name, 12.5, TpInk, true));
            var sub = new List<string>();
            if (w.Category.Length > 0) sub.Add(Loc.T(w.Category));
            if (!double.IsNaN(w.AvgMeters)) sub.Add(Loc.F("avg {0:0} m", w.AvgMeters));
            name.Children.Add(Txt(string.Join(" · ", sub), 11, TpMuted));
            Grid.SetColumn(name, 1); g.Children.Add(name);
            var k = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            k.Children.Add(Txt(w.Kills.ToString("N0", CultureInfo.CurrentCulture), 13, TpInk, true, HorizontalAlignment.Right));
            k.Children.Add(Txt(Loc.F("{0:0}% of kills", w.Share), 11, TpMuted, false, HorizontalAlignment.Right));
            Grid.SetColumn(k, 2); g.Children.Add(k);
            sp.Children.Add(new Border { BorderBrush = B(TpLine), BorderThickness = new Thickness(0, i++ == 0 ? 0 : 1, 0, 0), Child = g });
        }
        return Flat("Top weapons", sp);
    }

    FrameworkElement RolesPanel(Profile p)
    {
        var list = Panels.Roles(p.Matches, p.Puuid);
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

    // ───────── maps and agents ─────────

    FrameworkElement MapsPanel(Profile p)
    {
        var rows = Panels.Maps(p.Matches, p.Puuid).Take(7).Select(m => new UIElement[]
        {
            new StackPanel { Orientation = Orientation.Horizontal }.Also(s =>
            {
                if (MatchArt.MapPicture(m.Map) is { } pic) s.Children.Add(new Border { Width = 40, Height = 22, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 8, 0), ClipToBounds = true, Background = new ImageBrush(pic) { Stretch = Stretch.UniformToFill } });
                s.Children.Add(Txt(m.Map, 12.5, TpInk, true));
            }),
            Txt(N(m.WinPct, "0.0") + "%", 12.5, m.WinPct >= 50 ? TpUp : TpDown, true),
            Txt($"{m.Wins}–{m.Losses}", 12, TpMuted),
        });
        return Flat("Maps", Table(new[] { "Map", "Win %", "W–L" }, new double[] { 0, 64, 56 }, rows));
    }

    FrameworkElement AgentsTable(Profile p)
    {
        var rows = Panels.Agents(p.Matches, p.Puuid).Take(6).Select(a =>
        {
            var name = new StackPanel { Orientation = Orientation.Horizontal };
            if (MatchArt.AgentIcon(a.Agent) is { } ai) name.Children.Add(Pic(ai, 26, 26));
            var nm = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nm.Children.Add(Txt(a.Agent, 12.5, TpInk, true));
            var hours = TimeSpan.FromMilliseconds(a.PlayMs).TotalHours;
            nm.Children.Add(Txt(hours >= 1 ? Loc.F("{0:0} h", hours) : Loc.F("{0:0} min", TimeSpan.FromMilliseconds(a.PlayMs).TotalMinutes), 11, TpMuted));
            name.Children.Add(nm);
            var t = a.Totals;
            return new UIElement[]
            {
                name,
                Txt(a.Matches.ToString(CultureInfo.CurrentCulture)),
                Txt(N(a.WinPct, "0.0") + "%", 12.5, a.WinPct >= 50 ? TpUp : TpBody),
                Txt(N(t.KdRatio, "0.00")),
                Txt(N(t.Adr, "0.0")),
                Txt(N(t.Acs, "0.0")),
                Txt(Signed(t.DamageDelta), 12.5, t.DamageDelta >= 0 ? TpUp : TpDown),
                Txt(N(a.Score), 12.5, TpInk, true),
                Txt(a.BestMap.Length > 0 ? $"{a.BestMap} {N(a.BestMapWin)}%" : "–", 12, TpMuted),
            };
        });
        return Flat("Agents", Table(new[] { "Agent", "Matches", "Win %", "K/D", "ADR", "ACS", "DDΔ", "Score", "Best map" }, new double[] { 0, 64, 62, 52, 56, 56, 52, 52, 120 }, rows));
    }

    // ───────── a full scoreboard for one match ─────────

    FrameworkElement ScoreboardView(VMatch m, string me)
    {
        var b = Panels.BoardOf(m, me);
        var sp = new StackPanel();
        // Rounds: who won each, and how.
        var tl = new UniformGrid { Rows = 1, Margin = new Thickness(12, 10, 12, 4) };
        foreach (var r in m.Rounds.OrderBy(r => r.N))
        {
            bool ours = r.Winner == b.Team;
            var cell = new StackPanel { Margin = new Thickness(1, 0, 1, 0), ToolTip = $"{Loc.F("Round {0}", r.N + 1)} · {Loc.T(RoundHow(r.Result))}" };
            cell.Children.Add(new Border { Height = 6, Background = B(ours ? TpUp : TpDown), Opacity = ours ? 1 : 0.75 });
            cell.Children.Add(Txt((r.N + 1).ToString(CultureInfo.CurrentCulture), 9.5, TpMuted, false, HorizontalAlignment.Center));
            tl.Children.Add(cell);
        }
        sp.Children.Add(tl);
        var lines = b.Lines;
        var rows = lines.Select(l =>
        {
            var t = l.Totals;
            var who = new StackPanel { Orientation = Orientation.Horizontal };
            if (MatchArt.AgentIcon(l.Player.Agent) is { } ai) who.Children.Add(Pic(ai, 22, 22, 6));
            if (MatchArt.RankIcon(l.Player.Tier) is { } ri) who.Children.Add(Pic(ri, 18, 18, 6).Also(im => im.ToolTip = ValorantMatches.RankName(l.Player.Tier)));
            string label = l.Player.Id == me ? Loc.T("You") : _trNames.GetValueOrDefault(l.Player.Id) is { Length: > 0 } nm ? nm : l.Player.Agent;
            who.Children.Add(Txt(label, 12.5, TpInk, l.Player.Id == me));
            int pm = t.Kills - t.Deaths;
            return new UIElement[]
            {
                who,
                Txt(N(l.Score), 12.5, TpInk, true),
                Txt(N(t.Acs)),
                Txt(t.Kills.ToString(CultureInfo.CurrentCulture)), Txt(t.Deaths.ToString(CultureInfo.CurrentCulture)), Txt(l.Player.Assists.ToString(CultureInfo.CurrentCulture)),
                Txt(Signed(pm), 12.5, pm >= 0 ? TpUp : TpDown),
                Txt(N(t.KdRatio, "0.0")),
                Txt(Signed(t.DamageDelta), 12.5, t.DamageDelta >= 0 ? TpUp : TpDown),
                Txt(N(t.Adr, "0.0")),
                Txt(double.IsNaN(t.HeadshotPct) ? "–" : N(t.HeadshotPct) + "%"),
                Txt(N(t.KastPct) + "%"),
                Txt(t.FirstKills.ToString(CultureInfo.CurrentCulture)), Txt(t.FirstDeaths.ToString(CultureInfo.CurrentCulture)),
            };
        });
        sp.Children.Add(Table(new[] { "Player", "Score", "ACS", "K", "D", "A", "+/−", "K/D", "DDΔ", "ADR", "HS%", "KAST", "FK", "FD" },
            new double[] { 0, 52, 46, 34, 34, 34, 44, 42, 48, 50, 46, 50, 34, 34 }, rows, i => lines[i].Player.Team == b.Team ? TpUp : TpDown));
        var head = Loc.F("{0} · {1}–{2}", m.Map, b.TeamScore, b.EnemyScore) + "   " + string.Join(" · ", new[]
        {
            Loc.T(QueueName(m.Queue)), TimeSpan.FromMilliseconds(m.LengthMs).ToString(@"m\:ss", CultureInfo.InvariantCulture),
            b.AvgTier >= 3 ? Loc.F("avg rank {0}", ValorantMatches.RankName((int)Math.Round(b.AvgTier))) : "",
        }.Where(x => x.Length > 0));
        return Flat(head, sp).Also(f => f.Margin = new Thickness(0, 0, 0, 8));
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

    static string QueueName(string q) => q switch
    {
        "competitive" => "Competitive", "unrated" => "Unrated", "premier" => "Premier", "swiftplay" => "Swiftplay", _ => q,
    };

    /// <summary>Recent match rows open their scoreboard when clicked.</summary>
    FrameworkElement MatchRowWithBoard(VMatch m, string puuid)
    {
        var row = MatchRow(m, puuid);
        var sp = new StackPanel();
        row.Cursor = Cursors.Hand;
        row.ToolTip = Loc.T(_trOpenMatch == m.Id ? "Hide the scoreboard" : "Show the full scoreboard");
        row.MouseLeftButtonUp += (_, _) => { _trOpenMatch = _trOpenMatch == m.Id ? null : m.Id; Build(); };
        sp.Children.Add(row);
        if (_trOpenMatch == m.Id) sp.Children.Add(ScoreboardView(m, puuid));
        return sp;
    }
}
