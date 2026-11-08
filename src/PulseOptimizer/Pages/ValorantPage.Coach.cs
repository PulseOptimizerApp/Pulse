using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pulse.Core;
using Pulse.Core.Games.Live;
using Pulse.Core.Games.Valorant;
using Pulse.Ui;
using Finding = Pulse.Core.Games.Valorant.Finding;

namespace Pulse.Pages;

/// <summary>
/// The coach: what to work on (compared with the players in your own matches), how to fix it, why you die, how you
/// use your abilities, and a weekly focus that checks whether it worked.
/// </summary>
public partial class ValorantPage
{
    static CoachState CS => TweakEngine.State.Coach ??= new CoachState();
    static bool _downloading;
    TextBlock _downloadLabel;
    static string _downloadText;

    /// <summary>Your matches for the coach: the account of your newest match (so a second account doesn't mix in).</summary>
    static List<VMatch> CoachMatches()
    {
        var all = VStore.ForCoach();
        if (all.Count == 0) return all;
        var me = all[0].Me;
        return all.Where(m => m.Me == me).Take(60).ToList();
    }

    void BuildCoach()
    {
        var matches = CoachMatches();
        Root.Children.Add(CoachHeader(matches));
        if (matches.Count < 3)
        {
            AutoDownload();
            return;
        }
        var report = Coach.Analyze(matches);
        if (CS.Focus != null) Root.Children.Add(FocusCard(matches));
        if (report.Rounds < Coach.MinRounds)
        {
            Root.Children.Add(Card(Dim(Loc.F("The coach needs about {0} rounds to say anything reliable. You have {1} so far; play a few more matches.", Coach.MinRounds, report.Rounds), 13, 0)));
            AutoDownload();
            return;
        }
        Root.Children.Add(WorkOnCard(report, matches));
        if (report.Strengths.Count > 0) Root.Children.Add(StrengthsCard(report));
        Root.Children.Add(CausesCard(report));
        if (report.Abilities.Count > 0) Root.Children.Add(AbilitiesCard(report));
        if (CS.Focus == null && CS.Past.Count > 0) Root.Children.Add(PastFocusCard());
        Root.Children.Add(Card(Faint(Loc.T("How it works: Pulse reads your own matches from Riot (the same data trackers use), including where all ten players stood at every kill, and counts every number the same way for you and for the other nine players. \"Players in your matches\" are those nine, across all your games here: same rank, same servers. Nothing is sent anywhere."), 11.5, 0)));
        AutoDownload();
    }

    FrameworkElement CoachHeader(List<VMatch> matches)
    {
        var sp = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(Heading("Your coach"));
        int rounds = matches.Sum(m => m.Rounds.Count);
        left.Children.Add(Dim(matches.Count == 0
            ? Loc.T("Pulse looks at your matches and tells you the habits that cost you rounds, how to fix them, and whether you did.")
            : Loc.F("Based on your last {0} matches ({1} rounds) in Competitive, Unrated, Premier and Swiftplay.", matches.Count, rounds), 12.5, 4));
        top.Children.Add(left);
        var get = Btn(_downloading ? "Downloading…" : "Get my recent matches", matches.Count < 3);
        get.IsEnabled = !_downloading;
        get.Margin = new Thickness(16, 0, 0, 0);
        get.VerticalAlignment = VerticalAlignment.Top;
        get.ToolTip = Loc.T("Downloads your last 20 matches from Riot (VALORANT or the Riot Client needs to be open). New matches are added by themselves after you play.");
        get.Click += async (_, _) => await Download(true);
        Grid.SetColumn(get, 1);
        top.Children.Add(get);
        sp.Children.Add(top);
        _downloadLabel = Faint(_downloadText ?? "", 11.5, 8);
        _downloadLabel.Visibility = _downloadText != null ? Visibility.Visible : Visibility.Collapsed;
        sp.Children.Add(_downloadLabel);
        if (matches.Count < 3 && !_downloading)
            sp.Children.Add(Dim(Loc.T("Open VALORANT (or the Riot Client) and press \"Get my recent matches\". Pulse also adds each match by itself when it ends, as long as Pulse is running."), 12, 10));
        return Card(sp);
    }

    /// <summary>Tops up recent matches once every few hours when the Riot Client is running.</summary>
    void AutoDownload()
    {
        if (_downloading || DateTime.UtcNow - CS.LastBackfill < TimeSpan.FromHours(6)) return;
        _ = Download(false);
    }

    async Task Download(bool manual)
    {
        if (_downloading) return;
        _downloading = true;
        _downloadText = manual ? Loc.T("Connecting to the Riot Client…") : null;
        if (manual && _tab == "coach") Build();
        var progress = new Progress<(string Step, int Done, int Total)>(p =>
        {
            if (!manual) return;
            _downloadText = p.Step == "list" ? Loc.T("Reading your match list…")
                : p.Done >= p.Total ? Loc.T("Saving…")
                : Loc.F("Downloading match {0} of {1}…", p.Done + 1, p.Total);
            // Only the status line changes while it downloads; the page is rebuilt once at the end.
            if (_downloadLabel != null) { _downloadLabel.Text = _downloadText; _downloadLabel.Visibility = Visibility.Visible; }
        });
        ValorantMatches.BackfillResult r;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { r = await ValorantMatches.Backfill(30, progress, cts.Token); }
        catch (OperationCanceledException) { r = new ValorantMatches.BackfillResult(0, 0, "It took too long"); }
        catch (Exception ex) { r = new ValorantMatches.BackfillResult(0, 0, ex.Message); }
        _downloading = false;
        if (r.Error == null) { CS.LastBackfill = DateTime.UtcNow; TweakEngine.Save(); }
        _downloadText = r.Error == "no-client" ? (manual ? Loc.T("Open VALORANT or the Riot Client first, then try again.") : null)
            : r.Error != null ? (manual ? Loc.F("{0}. Try again in a minute.", Loc.T(r.Error)) : null)
            : r.Added > 0 ? Loc.F("Added {0} matches.", r.Added)
            : manual ? Loc.T("You're up to date: every recent match is already here.") : null;
        if (_tab is "coach" or "maps" or "review" && (manual || r.Added > 0)) Build();
    }

    // ───────── what to work on ─────────

    FrameworkElement WorkOnCard(CoachReport r, List<VMatch> matches)
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("What to work on"));
        if (r.Weaknesses.Count == 0)
        {
            sp.Children.Add(Dim(Loc.T("Nothing stands out: you're at or above the players in your matches on everything Pulse tracks. To climb, pick the number you'd most like to push and make it this week's focus."), 12.5, 6));
            var pick = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var f in r.All.OrderBy(f => f.Gap).Take(3))
            {
                var b = Btn(f.Metric.Name);
                b.Margin = new Thickness(0, 0, 8, 8);
                b.Click += (_, _) => StartFocus(f.Metric.Id, matches);
                pick.Children.Add(b);
            }
            sp.Children.Add(pick);
            return Card(sp);
        }
        sp.Children.Add(Dim(Loc.T("The habits that cost you the most, compared with players at your level. Fix one at a time."), 12.5, 4));
        int i = 1;
        foreach (var f in r.Weaknesses) sp.Children.Add(WeaknessBlock(i++, f, matches));
        return Card(sp);
    }

    FrameworkElement WeaknessBlock(int n, Finding f, List<VMatch> matches)
    {
        var m = f.Metric;
        var sp = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Res("WarnSoftBrush"), Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock { Text = n.ToString(CultureInfo.CurrentCulture), FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Res("WarnBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        head.Children.Add(new TextBlock { Text = Loc.T(m.Name), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(head);
        sp.Children.Add(CompareBars(f));
        sp.Children.Add(Dim(Loc.T(m.Tip.Why), 12.5, 10));
        sp.Children.Add(Label("How to fix it").Also(l => l.Margin = new Thickness(0, 12, 0, 4)));
        foreach (var step in m.Tip.Steps)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = Res("AccentBrush"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 7, 0, 0) });
            var t = new TextBlock { Text = Loc.T(step), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextBrush"), LineHeight = 18 };
            Grid.SetColumn(t, 1);
            row.Children.Add(t);
            sp.Children.Add(row);
        }
        var drill = new StackPanel();
        drill.Children.Add(new TextBlock { Text = Loc.T("Drill").ToUpper(CultureInfo.CurrentCulture), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Res("AccentBrush") });
        drill.Children.Add(Dim(Loc.T(m.Tip.Drill), 12, 3));
        sp.Children.Add(new Border
        {
            BorderBrush = Res("AccentBrush"), BorderThickness = new Thickness(2, 0, 0, 0), Background = Res("Surface2Brush"),
            CornerRadius = new CornerRadius(0, 6, 6, 0), Padding = new Thickness(12, 8, 12, 9), Margin = new Thickness(0, 10, 0, 0), Child = drill,
        });
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        bool isFocus = CS.Focus?.Metric == m.Id;
        var focus = Btn(isFocus ? "This week's focus" : "Make this my focus for the week", !isFocus);
        focus.IsEnabled = !isFocus;
        focus.Click += (_, _) => StartFocus(m.Id, matches);
        buttons.Children.Add(focus);
        if (m.Tip.Watch is DeathTag tag)
        {
            var watch = Btn("Watch these deaths");
            watch.Click += (_, _) => { _reviewFilter = tag; _reviewMatch = null; ShowTab("review"); };
            buttons.Children.Add(watch);
        }
        if (m.Id is "alone" or "outnumbered" or "firstdeath" or "untraded")
        {
            var map = Btn("See where it happens");
            map.Click += (_, _) => { _mapLayer = "deaths"; ShowTab("maps"); };
            buttons.Children.Add(map);
        }
        sp.Children.Add(buttons);
        return sp;
    }

    /// <summary>Two bars: you and the players in your matches.</summary>
    FrameworkElement CompareBars(Finding f)
    {
        var g = new Grid { Margin = new Thickness(32, 10, 0, 0), MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        double max = Math.Max(f.Mine, f.Base) * 1.15;
        bool good = f.Gap >= 0;
        void Row(int i, string who, double v, Brush fill)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = who, FontSize = 12, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 10, 3), TextTrimming = TextTrimming.CharacterEllipsis };
            var track = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = Res("Surface2Brush"), VerticalAlignment = VerticalAlignment.Center };
            var bar = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = fill, HorizontalAlignment = HorizontalAlignment.Left, Width = max > 0 ? Math.Max(4, 220 * v / max) : 4 };
            var cell = new Grid(); cell.Children.Add(track); cell.Children.Add(bar);
            var val = new TextBlock { Text = f.Metric.Format(v), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(l, i); Grid.SetRow(cell, i); Grid.SetRow(val, i);
            Grid.SetColumn(cell, 1); Grid.SetColumn(val, 2);
            g.Children.Add(l); g.Children.Add(cell); g.Children.Add(val);
        }
        Row(0, Loc.T("You"), f.Mine, Res(good ? "GoodBrush" : "WarnBrush"));
        Row(1, f.Against, f.Base, Res("TextFaintBrush"));
        return g;
    }

    FrameworkElement StrengthsCard(CoachReport r)
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("What you're doing well"));
        foreach (var f in r.Strengths)
        {
            sp.Children.Add(new TextBlock { Text = Loc.T(f.Metric.Name), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 14, 0, 0) });
            sp.Children.Add(CompareBars(f).Also(e => ((Grid)e).Margin = new Thickness(0, 6, 0, 0)));
        }
        sp.Children.Add(Faint(Loc.T("Keep doing these: they're winning you rounds."), 11.5, 10));
        return Card(sp);
    }

    // ───────── why you die ─────────

    FrameworkElement CausesCard(CoachReport r)
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Why you die"));
        sp.Children.Add(Dim(Loc.F("Every one of your {0} deaths, filed under its main cause. Pulse works it out from where all ten players stood at that moment.", r.DeathsCounted), 12.5, 4));
        var order = new[] { DeathTag.FirstDeath, DeathTag.Outnumbered, DeathTag.Alone, DeathTag.NoAbility, DeathTag.NotTraded, DeathTag.Outgunned, DeathTag.LastAlive, DeathTag.Traded };
        int max = r.Causes.Values.DefaultIfEmpty(1).Max();
        var g = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        int row = 0;
        foreach (var c in order)
        {
            if (!r.Causes.TryGetValue(c, out var n) || n == 0) continue;
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = Loc.T(Coach.CauseName(c)), FontSize = 12.5, Foreground = Res(Coach.IsMistake(c) ? "TextBrush" : "TextDimBrush"), Margin = new Thickness(0, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center };
            var bar = new Border
            {
                Height = 10, CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Background = Res(Coach.IsMistake(c) ? "WarnBrush" : "TextFaintBrush"), Width = 8,
            };
            var cell = new Grid { VerticalAlignment = VerticalAlignment.Center };
            cell.Children.Add(bar);
            cell.SizeChanged += (_, e) => bar.Width = Math.Max(8, e.NewSize.Width * n / max);
            var count = new TextBlock { Text = $"{n}  ·  {100.0 * n / Math.Max(1, r.DeathsCounted):0}%", FontSize = 12, Foreground = Res("TextDimBrush"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var link = new Button { Style = (Style)FindResource("LinkButton"), Content = name, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = Loc.T("Watch these deaths") };
            var tag = c;
            link.Click += (_, _) => { _reviewFilter = tag; _reviewMatch = null; ShowTab("review"); };
            Grid.SetRow(link, row); Grid.SetRow(cell, row); Grid.SetRow(count, row);
            Grid.SetColumn(cell, 1); Grid.SetColumn(count, 2);
            g.Children.Add(link); g.Children.Add(cell); g.Children.Add(count);
            row++;
        }
        sp.Children.Add(g);
        if (!r.HasAbilityRounds)
            sp.Children.Add(Faint(Loc.T("\"No ability used yet\" needs per-round ability data, which Riot doesn't include in these matches, so it isn't counted."), 11.5, 10));
        sp.Children.Add(Faint(Loc.T("Orange: something you can change. Grey: the situation (a trade, a lost eco, being the last one alive)."), 11.5, 6));
        return Card(sp);
    }

    // ───────── ability usage ─────────

    FrameworkElement AbilitiesCard(CoachReport r)
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Ability usage"));
        sp.Children.Add(Dim(Loc.T("How often you use each ability per round, against other players on the same agent in your matches. Unused utility is the cheapest thing to fix."), 12.5, 4));
        string[] slots = { "C", "Q", "E", "X" };
        foreach (var a in r.Abilities.Take(4))
        {
            var block = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            if (Pulse.Core.Games.MatchArt.AgentIcon(a.Agent) is { } art)
                head.Children.Add(new Image { Source = art, Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 0) }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
            head.Children.Add(new TextBlock { Text = a.Agent, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = "  ·  " + Loc.F("{0} rounds", a.Rounds), FontSize = 12, Foreground = Res("TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center });
            block.Children.Add(head);
            var g = new Grid { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            for (int i = 0; i < 4; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            for (int rI = 0; rI < 3; rI++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            void Cell(int row, int col, string text, Brush b, bool bold = false)
            {
                var t = new TextBlock { Text = text, FontSize = 12.5, Foreground = b, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 2, 0, 2) };
                Grid.SetRow(t, row); Grid.SetColumn(t, col); g.Children.Add(t);
            }
            Cell(0, 0, "", Res("TextFaintBrush"));
            for (int i = 0; i < 4; i++) Cell(0, i + 1, slots[i], Res("TextFaintBrush"), true);
            Cell(1, 0, Loc.T("You, per round"), Res("TextDimBrush"));
            Cell(2, 0, Loc.F("Other {0} players", a.Agent), Res("TextDimBrush"));
            for (int i = 0; i < 4; i++)
            {
                double mine = a.Mine[i];
                double? other = a.Others?[i];
                bool low = other is > 0.15 && mine < other * 0.7;
                Cell(1, i + 1, mine.ToString("0.00", CultureInfo.CurrentCulture), Res(low ? "WarnBrush" : "TextBrush"), true);
                Cell(2, i + 1, other?.ToString("0.00", CultureInfo.CurrentCulture) ?? "–", Res("TextDimBrush"));
            }
            block.Children.Add(g);
            if (a.Others == null)
                block.Children.Add(Faint(Loc.T("Not enough other players on this agent in your matches to compare yet."), 11.5, 4));
            else
            {
                var low = Enumerable.Range(0, 4).Where(i => a.Others[i] > 0.15 && a.Mine[i] < a.Others[i] * 0.7).Select(i => slots[i]).ToList();
                if (low.Count > 0)
                    block.Children.Add(Dim(Loc.F("You use {0} about a third less than other {1} players. In the buy phase, decide where it goes this round.", string.Join(", ", low), a.Agent), 12, 6));
            }
            sp.Children.Add(block);
        }
        sp.Children.Add(Faint(Loc.T("C, Q, E and X are VALORANT's default keys for the four abilities. X is the ultimate, which you get less often by design."), 11.5, 12));
        return Card(sp);
    }

    // ───────── the weekly focus, and proof ─────────

    void StartFocus(string metric, List<VMatch> matches)
    {
        Coach.StartFocus(CS, metric, matches);
        TweakEngine.AddLog("valorant", "Coach focus: " + metric);
        TweakEngine.Save();
        Build();
        Scroller.ScrollToTop();
    }

    FrameworkElement FocusCard(List<VMatch> matches)
    {
        var f = CS.Focus;
        var m = Coach.Get(f.Metric);
        var sp = new StackPanel();
        if (m == null) return new Border();
        var after = matches.Where(x => x.StartUtc >= f.Start).OrderBy(x => x.StartUtc).ToList();
        double now = Coach.ValueOver(after, f.Metric);
        var verdict = Coach.Judge(f, now, after.Count);

        sp.Children.Add(Label("This week's focus"));
        sp.Children.Add(Heading(m.Name));
        var days = (int)(DateTime.UtcNow - f.Start).TotalDays;
        sp.Children.Add(Dim(Loc.F("Started {0} days ago. Before: {1} (your last {2} matches).", days, m.Format(f.Baseline), f.BaselineMatches), 12.5, 4));

        var big = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        void Stat(string label, string value, Brush b)
        {
            var s = new StackPanel { Margin = new Thickness(0, 0, 36, 0) };
            s.Children.Add(new TextBlock { Text = value, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = b, FontFamily = (FontFamily)FindResource("DisplayFont") });
            s.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = Res("TextFaintBrush") });
            big.Children.Add(s);
        }
        Stat(Loc.T("Before"), m.Format(f.Baseline), Res("TextDimBrush"));
        Stat(Loc.F("Since ({0} matches)", after.Count), after.Count > 0 ? m.Format(now) : "–",
            Res(verdict == Coach.Verdict.Better ? "GoodBrush" : verdict == Coach.Verdict.Worse ? "WarnBrush" : "TextBrush"));
        sp.Children.Add(big);

        if (after.Count > 0) sp.Children.Add(FocusGraph(f, m, after));
        sp.Children.Add(Dim(verdict switch
        {
            Coach.Verdict.Better => Loc.T("It's working: this is clearly better than before. Keep it up until it's a habit, then pick the next thing."),
            Coach.Verdict.Worse => Loc.T("It's gone the wrong way so far. Watch a few of these deaths in Death review and look for the moment it went wrong."),
            Coach.Verdict.Same => Loc.T("About the same as before so far. Changes this small can be chance; give it a few more matches."),
            _ => Loc.F("Play {0} more matches for a first answer. Every match you play is checked automatically.", Math.Max(1, 5 - after.Count)),
        }, 12.5, 10));
        if (after.Count is > 0 and < 10)
            sp.Children.Add(Faint(Loc.T("A handful of matches can swing on luck. The answer gets surer the more you play."), 11.5, 4));
        sp.Children.Add(Dim(Loc.T(m.Tip.Drill), 12, 10).Also(t => t.Foreground = Res("TextBrush")));

        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var end = Btn("End focus");
        end.Click += (_, _) => { Coach.EndFocus(CS, matches); TweakEngine.Save(); Build(); };
        buttons.Children.Add(end);
        if (m.Tip.Watch is DeathTag tag)
        {
            var watch = Btn("Watch these deaths");
            watch.Click += (_, _) => { _reviewFilter = tag; _reviewMatch = null; ShowTab("review"); };
            buttons.Children.Add(watch);
        }
        sp.Children.Add(buttons);
        var card = Card(sp);
        card.BorderBrush = Res("AccentBrush");
        return card;
    }

    /// <summary>Each match since the focus started as a dot, with the "before" level as a dashed line.</summary>
    FrameworkElement FocusGraph(CoachFocus f, Metric m, List<VMatch> after)
    {
        const double W = 460, H = 110;
        var values = after.Select(x => Coach.ValueIn(x, f.Metric)).ToList();
        var known = values.Where(v => !double.IsNaN(v)).ToList();
        var c = new Canvas { Width = W, Height = H, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = false };
        if (known.Count == 0) return c;
        double hi = Math.Max(known.Max(), f.Baseline) * 1.1, lo = Math.Min(known.Min(), f.Baseline) * 0.9;
        if (hi - lo < 1e-6) { hi += 1; lo = Math.Max(0, lo - 1); }
        double Y(double v) => H - 12 - (v - lo) / (hi - lo) * (H - 24);
        double X(int i) => values.Count == 1 ? W / 2 : 10 + i * (W - 20) / (values.Count - 1);
        c.Children.Add(new Line { X1 = 0, X2 = W, Y1 = Y(f.Baseline), Y2 = Y(f.Baseline), Stroke = Res("TextFaintBrush"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 4 } });
        var lbl = new TextBlock { Text = Loc.T("before"), FontSize = 10.5, Foreground = Res("TextFaintBrush") };
        Canvas.SetLeft(lbl, W + 6); Canvas.SetTop(lbl, Y(f.Baseline) - 8);
        c.Children.Add(lbl);
        var line = new Polyline { Stroke = Res("AccentBrush"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        for (int i = 0; i < values.Count; i++)
        {
            if (double.IsNaN(values[i])) continue;
            line.Points.Add(new Point(X(i), Y(values[i])));
        }
        c.Children.Add(line);
        for (int i = 0; i < values.Count; i++)
        {
            if (double.IsNaN(values[i])) continue;
            bool better = m.HigherBetter ? values[i] >= f.Baseline : values[i] <= f.Baseline;
            var dot = new Ellipse { Width = 8, Height = 8, Fill = Res(better ? "GoodBrush" : "WarnBrush"), ToolTip = $"{after[i].Map} · {after[i].MyAgent}: {m.Format(values[i])}" };
            Canvas.SetLeft(dot, X(i) - 4); Canvas.SetTop(dot, Y(values[i]) - 4);
            c.Children.Add(dot);
        }
        return c;
    }

    FrameworkElement PastFocusCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Past focuses"));
        foreach (var f in CS.Past.Take(6))
        {
            var m = Coach.Get(f.Metric);
            if (m == null) continue;
            var v = Coach.Judge(f, f.After, f.AfterMatches);
            var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = Loc.T(m.Name) + "  ·  " + f.Start.ToLocalTime().ToString("d", CultureInfo.CurrentCulture), FontSize = 12.5, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            var res = new TextBlock
            {
                Text = $"{m.Format(f.Baseline)} → {(f.AfterMatches > 0 ? m.Format(f.After) : "–")}   " + Loc.T(v switch { Coach.Verdict.Better => "Better", Coach.Verdict.Worse => "Worse", Coach.Verdict.Same => "About the same", _ => "Too few matches" }),
                FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                Foreground = Res(v == Coach.Verdict.Better ? "GoodBrush" : v == Coach.Verdict.Worse ? "WarnBrush" : "TextDimBrush"),
            };
            Grid.SetColumn(res, 1);
            row.Children.Add(res);
            sp.Children.Add(row);
        }
        return Card(sp);
    }
}
