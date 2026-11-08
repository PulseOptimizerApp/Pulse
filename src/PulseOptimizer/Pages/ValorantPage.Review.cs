using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Clips;
using Pulse.Core.Games.Valorant;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// Death review: every one of your deaths with its cause, who killed you and with what, where everyone stood, and
/// the moment itself from your clips when there's one, played back to back like a coach's review session.
/// </summary>
public partial class ValorantPage
{
    string _reviewMatch;       // match id, or null for your recent matches
    DeathTag? _reviewFilter;
    List<(DeathInfo D, ClipFile Clip, double At)> _reviewList = new();
    int _reviewIndex = -1;
    bool _reviewAll;
    MediaElement _player;
    DispatcherTimer _playerTimer;
    double _segEnd;
    ContentControl _reviewStage;

    void StopReview()
    {
        _playerTimer?.Stop();
        try { _player?.Stop(); _player?.Close(); } catch { }
        _player = null;
        _reviewAll = false;
    }

    void BuildReview()
    {
        StopReview();
        var matches = CoachMatches();
        if (matches.Count == 0) { Root.Children.Add(CoachHeader(matches)); return; }

        var recent = matches.Take(10).ToList();
        var chosen = _reviewMatch == null ? recent : recent.Where(m => m.Id == _reviewMatch).ToList();
        if (chosen.Count == 0) { _reviewMatch = null; chosen = recent; }
        var deaths = chosen.SelectMany(m => Analysis.Deaths(m, m.Me)).OrderByDescending(d => d.Match.StartUtc).ThenBy(d => d.Kill.GameMs).ToList();

        // Filters.
        var pick = new StackPanel();
        pick.Children.Add(Label("Match"));
        var mrow = new WrapPanel();
        mrow.Children.Add(Chip(Loc.F("Last {0} matches", recent.Count), "rmatch", _reviewMatch == null, () => { _reviewMatch = null; Build(); }));
        foreach (var m in recent)
        {
            var id = m.Id;
            var score = $"{m.Rounds.Count(r => r.Winner == m.MyTeam)}–{m.Rounds.Count(r => r.Winner.Length > 0 && r.Winner != m.MyTeam)}";
            mrow.Children.Add(Chip($"{m.Map} · {m.MyAgent} · {score}", "rmatch", _reviewMatch == id, () => { _reviewMatch = id; Build(); }));
        }
        pick.Children.Add(mrow);
        pick.Children.Add(Label("Cause").Also(l => l.Margin = new Thickness(0, 8, 0, 6)));
        var crow = new WrapPanel();
        crow.Children.Add(Chip(Loc.F("All deaths ({0})", deaths.Count), "rcause", _reviewFilter == null, () => { _reviewFilter = null; Build(); }));
        foreach (var c in new[] { DeathTag.FirstDeath, DeathTag.NotTraded, DeathTag.Alone, DeathTag.Outnumbered, DeathTag.NoAbility, DeathTag.Outgunned, DeathTag.LastAlive, DeathTag.Traded })
        {
            int n = deaths.Count(d => d.Tags.Contains(c));
            if (n == 0) continue;
            var tag = c;
            crow.Children.Add(Chip($"{Loc.T(Coach.CauseName(c))} ({n})", "rcause", _reviewFilter == c, () => { _reviewFilter = tag; Build(); }));
        }
        pick.Children.Add(crow);
        Root.Children.Add(Card(pick));

        if (_reviewFilter is DeathTag f) deaths = deaths.Where(d => d.Tags.Contains(f)).ToList();
        var clips = ClipLibrary.List(S);
        _reviewList = deaths.Select(d => { var (c, at) = ClipFor(d, clips); return (d, c, at); }).ToList();
        _reviewIndex = -1;

        // The stage: video (or positions) of the selected death.
        _reviewStage = new ContentControl();
        var stageCard = Card(_reviewStage);
        Root.Children.Add(stageCard);
        int withClips = _reviewList.Count(x => x.Clip != null);
        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        var all = Btn(withClips > 0 ? Loc.F("Play all {0} clips", withClips) : "No clips of these deaths", withClips > 0);
        all.IsEnabled = withClips > 0;
        all.Click += (_, _) => { _reviewAll = true; Select(_reviewList.FindIndex(x => x.Clip != null)); };
        top.Children.Add(all);
        top.Children.Add(new TextBlock
        {
            Text = Loc.F("{0} deaths · {1} with a clip", _reviewList.Count, withClips), FontSize = 12, Foreground = Res("TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center,
        });
        Root.Children.Insert(Root.Children.IndexOf(stageCard), top);

        // The list.
        var list = new StackPanel();
        if (_reviewList.Count == 0) list.Children.Add(Dim(Loc.T("No deaths match this filter. Nice."), 12.5, 0));
        for (int i = 0; i < _reviewList.Count; i++) list.Children.Add(ReviewRow(i));
        Root.Children.Add(Card(list));
        if (clips.Count == 0 || withClips == 0)
            Root.Children.Add(Card(Faint(Loc.T("Clips show up here when you saved one around that moment (instant replay on the Clips page). Pulse matches a death to a clip using the game's own round log, so it needs to have seen the match end on this PC."), 11.5, 0)));
        Select(_reviewList.Count > 0 ? 0 : -1, autoplay: false);
    }

    /// <summary>The clip that has this death in it, and how many seconds in. Needs the match lined up with the round log.</summary>
    static (ClipFile Clip, double At) ClipFor(DeathInfo d, List<ClipFile> clips)
    {
        if (d.Match.ZeroUtc == null) return (null, 0);
        var when = d.Match.LocalTime(d.Kill.GameMs);
        foreach (var c in clips)
        {
            if (c.Seconds <= 0) continue;
            var end = ClipLibrary.LoadEnd(c.Path);
            if (end is not DateTime e) continue;
            var start = e.AddSeconds(-c.Seconds);
            if (when >= start.AddSeconds(0.5) && when <= e) return (c, (when - start).TotalSeconds);
        }
        return (null, 0);
    }

    FrameworkElement ReviewRow(int i)
    {
        var (d, clip, _) = _reviewList[i];
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (Pulse.Core.Games.MatchArt.AgentIcon(d.KillerAgent) is { } art)
            g.Children.Add(new Image { Source = art, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left }.Also(im => RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality)));
        var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var weapon = Analysis.WeaponName(d.Kill);
        who.Children.Add(new TextBlock { Text = Loc.F("Round {0}", d.Kill.Round + 1) + " · " + Loc.T(d.Attacking ? "Attack" : "Defense"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
        who.Children.Add(new TextBlock
        {
            Text = (d.KillerAgent.Length > 0 ? Loc.F("by {0}", d.KillerAgent) : "") + (weapon.Length > 0 ? " · " + Loc.T(weapon) : "") + $" · {d.Match.Map}",
            FontSize = 11.5, Foreground = Res("TextFaintBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(who, 1);
        g.Children.Add(who);
        var tags = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var t in d.Tags.OrderBy(t => t == d.Main ? 0 : 1))
            tags.Children.Add(Pill(Loc.T(Coach.CauseName(t)), Coach.IsMistake(t)));
        Grid.SetColumn(tags, 2);
        g.Children.Add(tags);
        var play = new TextBlock
        {
            Text = clip != null ? "▶  " + Loc.T("Clip") : Loc.T("No clip"), FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Res(clip != null ? "AccentBrush" : "TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(play, 3);
        g.Children.Add(play);
        var row = new Border { Background = Res("Surface2Brush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 12, 7), Child = g, Cursor = System.Windows.Input.Cursors.Hand, Tag = i };
        row.MouseLeftButtonUp += (_, _) => { _reviewAll = false; Select(i); };
        return row;
    }

    Border Pill(string text, bool warn) => new()
    {
        CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 2, 6, 2),
        Background = Res(warn ? "WarnSoftBrush" : "Surface2Brush"), BorderBrush = Res(warn ? "WarnBrush" : "BorderBrush"), BorderThickness = new Thickness(warn ? 0 : 1),
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = Res(warn ? "WarnBrush" : "TextDimBrush") },
    };

    void Select(int i, bool autoplay = true)
    {
        _reviewIndex = i;
        _playerTimer?.Stop();
        try { _player?.Stop(); } catch { }
        if (_reviewStage == null) return;
        if (i < 0 || i >= _reviewList.Count) { _reviewStage.Content = Dim(Loc.T("Pick a death below."), 12.5, 0); return; }
        var (d, clip, at) = _reviewList[i];

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        var weapon = Analysis.WeaponName(d.Kill);
        left.Children.Add(Heading(Loc.F("Round {0}", d.Kill.Round + 1) + " · " + Loc.T(Coach.CauseName(d.Main))));
        left.Children.Add(Dim(string.Join(" · ", new[]
        {
            d.KillerAgent.Length > 0 ? Loc.F("Killed by {0}", d.KillerAgent) : "",
            weapon.Length > 0 ? Loc.T(weapon) : "",
            Loc.T(d.Attacking ? "Attack" : "Defense"),
            $"{d.Match.Map}",
        }.Where(x => x.Length > 0)), 12.5, 4));

        if (clip != null)
        {
            _player ??= new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = Stretch.Uniform, IsMuted = false };
            if (_player.Parent is Panel pp) pp.Children.Remove(_player);
            if (_player.Parent is Border pb) pb.Child = null;
            var frame = new Border { Width = 560, Height = 315, CornerRadius = new CornerRadius(8), Background = Brushes.Black, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true, Child = _player };
            left.Children.Add(frame);
            double from = Math.Max(0, at - 7), to = Math.Min(clip.Seconds, at + 2.5);
            _segEnd = to;
            _player.Source = new Uri(clip.Path);
            _player.Position = TimeSpan.FromSeconds(from);
            _player.MediaOpened -= Player_Opened;
            _player.MediaOpened += Player_Opened;
            _pendingFrom = from;
            if (autoplay) _player.Play(); else { _player.Play(); _player.Pause(); }
            _playerTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _playerTimer.Tick -= PlayerTimer_Tick;
            _playerTimer.Tick += PlayerTimer_Tick;
            _playerTimer.Start();
            var controls = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            var prev = Btn("Previous"); prev.Click += (_, _) => Step(-1);
            var replay = Btn("Replay"); replay.Click += (_, _) => { _player.Position = TimeSpan.FromSeconds(from); _player.Play(); _playerTimer.Start(); };
            var next = Btn("Next"); next.Click += (_, _) => Step(1);
            var open = Btn("Open in Clips"); open.Click += (_, _) => MainWindow.Instance.Navigate("clips", "matches");
            controls.Children.Add(prev); controls.Children.Add(replay); controls.Children.Add(next); controls.Children.Add(open);
            left.Children.Add(controls);
            left.Children.Add(Faint(Loc.T("Plays the 7 seconds before your death and a moment after."), 11, 6));
        }
        else
        {
            left.Children.Add(Dim(d.Match.ZeroUtc == null
                ? Loc.T("No clip: Pulse didn't see this match end on this PC, so it can't find the moment in your clips.")
                : Loc.T("No clip of this moment. Save one with your instant replay key next time, and it'll show up here."), 12, 12));
            var controls = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            var prev = Btn("Previous"); prev.Click += (_, _) => Step(-1);
            var next = Btn("Next"); next.Click += (_, _) => Step(1);
            controls.Children.Add(prev); controls.Children.Add(next);
            left.Children.Add(controls);
        }
        left.Children.Add(WhatToDo(d));
        g.Children.Add(left);

        // Where everyone stood.
        var right = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };
        Grid.SetColumn(right, 1);
        var team = d.Match.MyTeam;
        var marks = new List<(double, double, Brush, string)>();
        foreach (var p in d.Kill.Pos)
        {
            if (p.Id == d.Match.Me) continue;
            bool mate = d.Match.TeamOf(p.Id) == team;
            var agent = d.Match.Players.FirstOrDefault(x => x.Id == p.Id)?.Agent ?? "";
            marks.Add((p.X, p.Y, p.Id == d.Kill.Killer ? Brushes.OrangeRed : mate ? new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF)) : new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x4E)),
                (p.Id == d.Kill.Killer ? Loc.T("Your killer") + ": " : mate ? Loc.T("Teammate") + ": " : Loc.T("Enemy") + ": ") + agent));
        }
        marks.Add((d.Kill.VX, d.Kill.VY, Brushes.White, Loc.T("You")));
        right.Children.Add(Heatmap(d.Match.Map, new List<Dot>(), 250, marks));
        var legend = new WrapPanel { Margin = new Thickness(0, 8, 0, 0), MaxWidth = 250 };
        void Key(Brush b, string t)
        {
            legend.Children.Add(new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = b, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(new TextBlock { Text = t, FontSize = 11, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 0, 12, 0) });
        }
        Key(Brushes.White, Loc.T("You"));
        Key(new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF)), Loc.T("Teammates"));
        Key(Brushes.OrangeRed, Loc.T("Killer"));
        Key(new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x4E)), Loc.T("Enemies"));
        right.Children.Add(legend);
        if (!double.IsNaN(d.NearestMateM))
            right.Children.Add(Faint(Loc.F("Nearest teammate: {0:0} m · enemies within 20 m: {1}", d.NearestMateM, d.EnemiesNear), 11.5, 6));
        g.Children.Add(right);
        _reviewStage.Content = g;
    }

    double _pendingFrom;

    void Player_Opened(object sender, RoutedEventArgs e)
    {
        try { _player.Position = TimeSpan.FromSeconds(_pendingFrom); } catch { }
    }

    void PlayerTimer_Tick(object sender, EventArgs e)
    {
        if (_player == null) { _playerTimer?.Stop(); return; }
        if (_player.Position.TotalSeconds < _segEnd) return;
        _player.Pause();
        _playerTimer.Stop();
        if (_reviewAll)
        {
            int next = _reviewList.FindIndex(_reviewIndex + 1, x => x.Clip != null);
            if (next >= 0) Select(next); else _reviewAll = false;
        }
    }

    void Step(int dir)
    {
        if (_reviewList.Count == 0) return;
        int i = Math.Clamp(_reviewIndex + dir, 0, _reviewList.Count - 1);
        Select(i);
    }

    /// <summary>One line on what to try next time, from the death's main cause.</summary>
    FrameworkElement WhatToDo(DeathInfo d)
    {
        string tip = d.Main switch
        {
            DeathTag.FirstDeath => "First death of the round. Next time: use utility before you peek, or let a teammate swing with you so you can be traded.",
            DeathTag.Outnumbered => "Two or more enemies were close. Next time: fight one angle at a time and use cover or a smoke to cut the others.",
            DeathTag.Alone => "No teammate within 15 m, so nobody could trade. Next time: move with someone, or fall back once you've seen them.",
            DeathTag.NoAbility => "You died with your abilities unused. Next time: use one before this fight.",
            DeathTag.NotTraded => "A teammate was alive but didn't trade you. Next time: fight where they can see your killer, and call where you died.",
            DeathTag.Outgunned => "They had a full buy against your eco. Nothing much to fix: on ecos, play for a close-range pick or to save.",
            DeathTag.LastAlive => "You were the last one alive. Clutches are hard; save the gun when the round is lost.",
            DeathTag.Traded => "You were traded: this death didn't cost the round anything extra.",
            _ => "",
        };
        if (tip.Length == 0) return new Border();
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = Loc.T("What to try").ToUpper(CultureInfo.CurrentCulture), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Res("AccentBrush") });
        sp.Children.Add(Dim(Loc.T(tip), 12, 3));
        return new Border
        {
            BorderBrush = Res("AccentBrush"), BorderThickness = new Thickness(2, 0, 0, 0), Background = Res("Surface2Brush"),
            CornerRadius = new CornerRadius(0, 6, 6, 0), Padding = new Thickness(12, 8, 12, 9), Margin = new Thickness(0, 12, 0, 0), Child = sp, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left,
        };
    }
}
