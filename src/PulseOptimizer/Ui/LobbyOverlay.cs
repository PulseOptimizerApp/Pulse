using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pulse.Core.Games;
using Pulse.Core.Games.Live;
using Pulse.Pages;

namespace Pulse.Ui;

/// <summary>
/// The VALORANT live lobby on screen: when you load into a match, a small Pulse popup asks whether to show both teams;
/// "Show" opens a panel with every player's agent, name (or "Hidden"), level, current rank and RR, peak rank and win
/// rate this act. Both windows never take focus from the game (clicks work, keys stay with the game). Games in
/// exclusive fullscreen draw over them; borderless or windowed shows them.
/// </summary>
public static class LobbyOverlay
{
    static Window _prompt, _panel;
    static Task<Lobby> _loading;
    static DispatcherTimer _promptTimer;

    static readonly Color Bg = Color.FromArgb(0xF4, 0x12, 0x12, 0x16);
    static readonly Brush White = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF2, 0xEE)));
    static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromRgb(0x8F, 0x8C, 0x86)));
    static readonly Brush Ally = Frozen(new SolidColorBrush(Color.FromRgb(0x4F, 0xD1, 0x8B)));
    static readonly Brush Enemy = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A)));
    static readonly Brush RowBg = Frozen(new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)));
    static readonly Brush MeBg = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xF2, 0xEF, 0xE8)));
    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    /// <summary>
    /// Live smurf warnings (opt-in, see its risk): when you load into a match, read the lobby in the background and,
    /// only if someone on either team shows smurf signs, show a small warning. Nothing shows when nobody does.
    /// </summary>
    public static void MatchFound()
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(MatchFound); return; }
        // Give the match a few seconds to be listed, then load it in the background.
        _loading = Task.Run(async () =>
        {
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(3000);
                try { if (await ValorantLobby.Load() is { } l) return l; } catch { }
            }
            return null;
        });
        var loading = _loading;
        _ = loading.ContinueWith(t => app.Dispatcher.BeginInvoke(() =>
        {
            if (t.Status != TaskStatus.RanToCompletion || t.Result is not { } lobby) return;
            var flagged = lobby.Players.Where(p => !p.Me && !p.Hidden && p.Smurf?.Score >= 35).OrderByDescending(p => p.Smurf.Score).ToList();
            if (flagged.Count > 0) ShowWarning(app, flagged);
        }));
    }

    /// <summary>
    /// A match you played just ended: a summary (result, your numbers, the one thing that mattered most) with buttons to
    /// open the match in Pulse and, when the after-match lobby is on, to see both teams (no risk, it's over).
    /// </summary>
    public static void AfterMatch(Pulse.Core.Games.Valorant.VMatch vm, bool summary = true, bool lobby = true)
    {
        var app = Application.Current;
        if (app == null || vm == null || (!summary && !lobby)) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(() => AfterMatch(vm, summary, lobby)); return; }
        _after = vm;
        if (lobby) _afterLoading = Task.Run(() => ValorantLobby.AfterMatch(vm));
        var sum = summary ? Pulse.Core.Games.Valorant.PostMatch.For(vm) : null;
        var body = new StackPanel { Margin = new Thickness(14, 12, 14, 14) };
        var buttons = new List<(string, bool, Action)>();
        if (sum == null)
        {
            body.Children.Add(Brand(Loc.T("After the match")));
            body.Children.Add(T(Loc.T("Match over"), 15, White).Also(t => t.Margin = new Thickness(0, 10, 0, 0)));
            body.Children.Add(T(Loc.T("See both teams: ranks, peaks, smurf signs and unusual stats?"), 12, Dim, bold: false).Also(t => { t.Margin = new Thickness(0, 2, 0, 0); t.TextWrapping = TextWrapping.Wrap; }));
            buttons.Add((Loc.T("Show"), true, () => _ = ShowAfter(app)));
        }
        else
        {
            SummaryBody(body, sum);
            buttons.Add((Loc.T("Open match"), true, () => OpenInPulse(app, vm.Id)));
            if (lobby) buttons.Add((Loc.T("Both teams"), false, () => _ = ShowAfter(app)));
        }
        buttons.Add((Loc.T("Close"), false, null));
        Prompt(app, body, buttons, sum == null ? 25 : 40, width: sum == null ? 300 : 340);
    }

    static readonly Brush Win = Frozen(new SolidColorBrush(Color.FromRgb(0x3F, 0xD6, 0xB0)));
    static readonly Brush Loss = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A)));
    static readonly Brush Tip = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x57)));
    static readonly Brush TipBg = Frozen(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xC8, 0x57)));
    static readonly Brush GoodBg = Frozen(new SolidColorBrush(Color.FromArgb(0x1A, 0x3F, 0xD6, 0xB0)));

    /// <summary>The summary: result and map, your agent and place, your numbers, and one point.</summary>
    static void SummaryBody(StackPanel body, Pulse.Core.Games.Valorant.PostMatch.Summary s)
    {
        body.Children.Add(Brand(Loc.T("Match summary")));
        var result = s.Result > 0 ? Loc.T("Victory") : s.Result < 0 ? Loc.T("Defeat") : Loc.T("Draw");
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        head.Children.Add(T(result, 17, s.Result > 0 ? Win : s.Result < 0 ? Loss : White));
        head.Children.Add(T($"  {s.Us}–{s.Them}", 17, White));
        if (!string.IsNullOrEmpty(s.Map)) head.Children.Add(T("  ·  " + s.Map, 12.5, Dim, bold: false).Also(t => t.VerticalAlignment = VerticalAlignment.Center));
        body.Children.Add(head);

        var who = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        who.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        who.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        who.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (MatchArt.AgentIcon(s.Agent) is { } art)
            who.Children.Add(new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = RowBg, Child = new Image { Source = art }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)) });
        var mid = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(T($"{s.Agent} · {s.Kills} / {s.Deaths} / {s.Assists}", 13, White));
        mid.Children.Add(T(Loc.F("#{0} of {1} · ACS {2} · ADR {3} · HS {4}%", s.Place, s.Match.Players.Count, Math.Round(s.Acs), Math.Round(s.Adr), double.IsNaN(s.HsPct) ? "–" : Math.Round(s.HsPct).ToString(CultureInfo.CurrentCulture)), 11, Dim, bold: false).Also(t => t.Margin = new Thickness(0, 2, 0, 0)));
        Grid.SetColumn(mid, 1);
        who.Children.Add(mid);
        if (!double.IsNaN(s.Score))
        {
            var score = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            score.Children.Add(T(Math.Round(s.Score).ToString(CultureInfo.CurrentCulture), 18, White).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right));
            score.Children.Add(T(Loc.T("Pulse score"), 9.5, Dim, bold: false).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right));
            Grid.SetColumn(score, 2);
            who.Children.Add(score);
        }
        body.Children.Add(who);

        if (s.Main != null)
        {
            var text = T(Loc.F(s.Main.Format, s.Main.Args), 11.5, s.Main.Problem ? Tip : Win, bold: false).Also(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; });
            body.Children.Add(new Border
            {
                Background = s.Main.Problem ? TipBg : GoodBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 12, 0, 0), Child = text,
            });
        }
    }

    /// <summary>Brings Pulse up on this match's scoreboard.</summary>
    static void OpenInPulse(Application app, string id)
    {
        if (app.MainWindow is not MainWindow mw) return;
        mw.ShowFromTray();
        mw.Navigate("valorant", "match:" + id);
    }

    static Pulse.Core.Games.Valorant.VMatch _after;
    static Task<Lobby> _afterLoading;

    /// <summary>The last match's lobby (from the VALORANT page), loading it again if needed.</summary>
    public static Task ShowAfter(Application app, Pulse.Core.Games.Valorant.VMatch vm = null)
    {
        if (vm != null && vm.Id != _after?.Id) { _after = vm; _afterLoading = null; }
        if (_after == null) return Task.CompletedTask;
        _afterLoading ??= Task.Run(() => ValorantLobby.AfterMatch(_after));
        var map = _after.Map;
        return ShowPanel(app, _afterLoading, Loc.T("After the match") + (string.IsNullOrEmpty(map) ? "" : " · " + map), after: true);
    }

    static Window NewWindow(Application app, UIElement content) => new Window
    {
        WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
        ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
        SizeToContent = SizeToContent.WidthAndHeight, Content = content, Opacity = 0, FontFamily = (FontFamily)app.FindResource("UiFont"),
    }.Also(w => w.SourceInitialized += (s, _) =>
    {
        var h = new WindowInteropHelper((Window)s).Handle;
        SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST);
    });

    static TextBlock T(string s, double size, Brush c, bool bold = true, FontFamily font = null) => new()
    {
        Text = s, FontSize = size, Foreground = c, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis,
        FontFamily = font ?? new FontFamily("Segoe UI Variable Text, Segoe UI"),
    };

    static Border Card(UIElement child, double width) => new()
    {
        Width = width, CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Bg),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), Child = child,
        Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.45, Color = Colors.Black }, Margin = new Thickness(18),
    };

    static FrameworkElement Brand(string what)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Pulse;component/Assets/logo-128.png")), Width = 18, Height = 18 });
        sp.Children.Add(T("PULSE", 11.5, White).Also(t => { t.Margin = new Thickness(7, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; }));
        sp.Children.Add(T("  ·  " + what.ToUpper(CultureInfo.CurrentCulture), 10.5, Dim).Also(t => t.VerticalAlignment = VerticalAlignment.Center));
        return sp;
    }

    static Button Btn(Application app, string text, bool primary) => new()
    {
        Style = (Style)app.FindResource(primary ? "PrimaryButton" : "GhostButton"), Content = text, Padding = new Thickness(14, 5, 14, 6),
        Margin = new Thickness(0, 0, 8, 0), Focusable = false,
    };

    // ───────── the prompt ─────────

    static void ShowPrompt(Application app)
    {
        var body = new StackPanel { Margin = new Thickness(14, 12, 14, 14) };
        body.Children.Add(Brand(Loc.T("Live lobby")));
        body.Children.Add(T(Loc.T("Match found"), 15, White).Also(t => t.Margin = new Thickness(0, 10, 0, 0)));
        body.Children.Add(T(Loc.T("See both teams' ranks and peaks?"), 12, Dim, bold: false).Also(t => t.Margin = new Thickness(0, 2, 0, 0)));
        Prompt(app, body, () => _ = ShowPanel(app));
    }

    /// <summary>A small corner popup with Show / Not now, gone by itself after 25 seconds.</summary>
    static void Prompt(Application app, StackPanel body, Action show, string showText = null) =>
        Prompt(app, body, new List<(string, bool, Action)> { (showText ?? Loc.T("Show"), true, show), (Loc.T("Not now"), false, null) });

    /// <summary>A small corner popup with buttons (an action of null just closes it), gone by itself after a while.</summary>
    static void Prompt(Application app, StackPanel body, List<(string Text, bool Primary, Action Act)> buttons, int seconds = 25, double width = 300)
    {
        _prompt?.Close();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (text, primary, act) in buttons)
        {
            var b = Btn(app, text, primary);
            b.Click += (_, _) => { ClosePrompt(); act?.Invoke(); };
            row.Children.Add(b);
        }
        body.Children.Add(row);

        var w = NewWindow(app, Card(body, width));
        var area = SystemParameters.WorkArea;
        w.Loaded += (_, _) =>
        {
            w.Left = area.Right - w.ActualWidth - 2;
            w.Top = area.Top + 2;
            w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        };
        _prompt = w;
        w.Show();
        _promptTimer?.Stop();
        _promptTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _promptTimer.Tick += (_, _) => ClosePrompt();
        _promptTimer.Start();
    }

    /// <summary>Live smurf warning: who on which team, and the main sign.</summary>
    static void ShowWarning(Application app, List<LobbyPlayer> flagged)
    {
        var body = new StackPanel { Margin = new Thickness(14, 12, 14, 14) };
        body.Children.Add(Brand(Loc.T("Smurf warning")));
        bool any = flagged.Any(p => p.Smurf.Score >= 60);
        body.Children.Add(T(Loc.T(any ? "Likely smurf in this match" : "Possible smurf in this match"), 15, White).Also(t => t.Margin = new Thickness(0, 10, 0, 6)));
        foreach (var p in flagged.Take(3))
        {
            var c = p.Smurf.Score >= 60 ? Color.FromRgb(0xE0, 0x77, 0x3F) : Color.FromRgb(0xE5, 0xB8, 0x4F);
            var row = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            if (MatchArt.AgentIcon(p.Agent) is { } art) line.Children.Add(new Image { Source = art, Width = 22, Height = 22, Margin = new Thickness(0, 0, 6, 0) }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
            line.Children.Add(T(p.Agent + " · " + Loc.T(p.Ally ? "your team" : "enemy team"), 12.5, p.Ally ? Ally : Enemy).Also(t => t.VerticalAlignment = VerticalAlignment.Center));
            line.Children.Add(T("  " + Loc.T(p.Smurf.Label).ToUpper(CultureInfo.CurrentCulture), 10, new SolidColorBrush(c)).Also(t => t.VerticalAlignment = VerticalAlignment.Center));
            row.Children.Add(line);
            if (p.Smurf.Signs.Count > 0) row.Children.Add(T(p.Smurf.Signs[0], 11, Dim, bold: false).Also(t => { t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(28, 1, 0, 0); }));
            body.Children.Add(row);
        }
        body.Children.Add(T(Loc.T("Signs, not proof. Cheating signals need the match's stats, so they show when it ends."), 10.5, Dim, bold: false).Also(t => { t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 8, 0, 0); }));
        Prompt(app, body, () => _ = ShowPanel(app), Loc.T("Show both teams"));
    }

    static void ClosePrompt()
    {
        _promptTimer?.Stop();
        var w = _prompt;
        _prompt = null;
        if (w == null) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => w.Close();
        w.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // ───────── the lobby panel ─────────

    public static Task ShowPanel(Application app) => ShowPanel(app, null, Loc.T("Live lobby"), after: false);

    static async Task ShowPanel(Application app, Task<Lobby> source, string title, bool after)
    {
        _panel?.Close();
        var content = new Grid();
        var card = Card(content, 640);
        var w = NewWindow(app, card);
        var area = SystemParameters.WorkArea;
        w.Loaded += (_, _) =>
        {
            w.Left = area.Left + (area.Width - w.ActualWidth) / 2;
            w.Top = area.Top + 40;
            w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        };
        w.SizeChanged += (_, _) => w.Left = area.Left + (area.Width - w.ActualWidth) / 2;
        _panel = w;
        content.Children.Add(Loading());
        w.Show();

        Lobby lobby = null;
        if (after) { try { lobby = await source; } catch { } }
        else
        {
            try { lobby = _loading != null ? await _loading : await ValorantLobby.Load(); } catch { }
            lobby ??= ValorantLobby.Last;
        }
        if (_panel != w) return;
        content.Children.Clear();
        content.Children.Add(lobby == null ? Failed(app, after) : Panel(app, lobby, title, after));
    }

    static FrameworkElement Header(Application app, string title)
    {
        var g = new Grid();
        g.Children.Add(Brand(title));
        var close = new Button { Style = (Style)app.FindResource("GhostButton"), Content = "✕", Padding = new Thickness(9, 2, 9, 3), HorizontalAlignment = HorizontalAlignment.Right, Focusable = false, ToolTip = Loc.T("Close") };
        close.Click += (_, _) => { _panel?.Close(); _panel = null; };
        g.Children.Add(close);
        return g;
    }

    static FrameworkElement Loading() => new StackPanel { Margin = new Thickness(16, 14, 16, 18), Children = { Brand(Loc.T("Live lobby")), T(Loc.T("Loading both teams…"), 13, Dim, bold: false).Also(t => t.Margin = new Thickness(0, 12, 0, 0)) } };

    static FrameworkElement Failed(Application app, bool after = false)
    {
        var sp = new StackPanel { Margin = new Thickness(16, 12, 16, 16) };
        sp.Children.Add(Header(app, Loc.T("Live lobby")));
        sp.Children.Add(T(Loc.T("Couldn't load this match. Riot may not have listed it yet; try again in a few seconds."), 12.5, Dim, bold: false).Also(t => { t.Margin = new Thickness(0, 12, 0, 0); t.TextWrapping = TextWrapping.Wrap; }));
        var again = Btn(app, Loc.T("Try again"), true);
        again.Margin = new Thickness(0, 12, 0, 0);
        again.HorizontalAlignment = HorizontalAlignment.Left;
        again.Click += (_, _) => { if (after) { _afterLoading = null; _ = ShowAfter(app); } else { _loading = null; _ = ShowPanel(app); } };
        sp.Children.Add(again);
        return sp;
    }

    static FrameworkElement Panel(Application app, Lobby l, string title, bool after)
    {
        var sp = new StackPanel { Margin = new Thickness(16, 12, 16, 16) };
        sp.Children.Add(Header(app, after ? title : string.IsNullOrEmpty(l.Map) ? Loc.T("Live lobby") : Loc.T("Live lobby") + " · " + l.Map));
        var cols = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        var mine = Team(Loc.T("Your team"), Ally, l.Players.Where(p => p.Ally).OrderByDescending(p => p.Tier));
        var theirs = Team(Loc.T("Enemy team"), Enemy, l.Players.Where(p => !p.Ally).OrderByDescending(p => p.Tier));
        Grid.SetColumn(theirs, 2);
        cols.Children.Add(mine); cols.Children.Add(theirs);
        sp.Children.Add(cols);
        sp.Children.Add(T(after
            ? Loc.T("Peak = highest rank won at in any act. Smurf tags and unusual stats are signs, not proof: hover one to see why. Read after the match ended, the way Riot's own match history shows it.")
            : Loc.T("Peak = highest rank won at in any act. Smurf tags are signs, not proof: hover one to see why. Players who hid their name stay hidden."), 10.5, Dim, bold: false).Also(t => { t.Margin = new Thickness(0, 10, 0, 0); t.TextWrapping = TextWrapping.Wrap; }));
        return sp;
    }

    static FrameworkElement Team(string title, Brush color, IEnumerable<LobbyPlayer> players)
    {
        var sp = new StackPanel();
        sp.Children.Add(T(title.ToUpper(CultureInfo.CurrentCulture), 10.5, color).Also(t => t.Margin = new Thickness(2, 0, 0, 6)));
        var list = players.ToList();
        if (list.Count == 0) sp.Children.Add(T("–", 12, Dim));
        foreach (var p in list) sp.Children.Add(PlayerRow(p));
        return sp;
    }

    static FrameworkElement PlayerRow(LobbyPlayer p)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var face = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(7), ClipToBounds = true, Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center };
        if (MatchArt.AgentIcon(p.Agent) is { } art) face.Child = new Image { Source = art, Stretch = Stretch.Uniform }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality));
        face.ToolTip = p.Agent;
        g.Children.Add(face);

        var who = new StackPanel { Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(p.Hidden ? T(Loc.T("Hidden player"), 12, Dim).Also(t => t.FontStyle = FontStyles.Italic) : T(p.Name ?? (string.IsNullOrEmpty(p.Agent) ? Loc.T("Unknown agent") : p.Agent), 12, White));
        if (p.Smurf?.Label is { } label)
        {
            // Smurf signs: amber for "possible", orange for "likely"; hover for the reasons.
            var c = p.Smurf.Score >= 60 ? Color.FromRgb(0xE0, 0x77, 0x3F) : Color.FromRgb(0xE5, 0xB8, 0x4F);
            nameRow.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(999), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B)),
                Child = T(Loc.T(label).ToUpper(CultureInfo.CurrentCulture), 9, new SolidColorBrush(c)),
                ToolTip = Loc.T("Signs, not proof:") + "\n• " + string.Join("\n• ", p.Smurf.Signs),
            });
        }
        if (p.Unusual is { Score: > 0 } u)
            nameRow.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(999), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0xF0, 0x5A, 0x5A)),
                Child = T(Loc.T("Unusual stats").ToUpper(CultureInfo.CurrentCulture), 9, Enemy),
                ToolTip = Loc.T("Signs, not proof:") + "\n• " + string.Join("\n• ", u.Signs),
            });
        who.Children.Add(nameRow);
        var sub = new List<string>();
        if (!string.IsNullOrEmpty(p.Extra)) sub.Add(p.Extra);
        if (p.Level > 0) sub.Add(Loc.F("Lvl {0}", p.Level));
        if (p.WinPercent >= 0) sub.Add(Loc.F("{0}% WR · {1} games", p.WinPercent, p.Games));
        who.Children.Add(T(sub.Count > 0 ? string.Join("  ·  ", sub) : p.Agent, 10.5, Dim, bold: false));
        Grid.SetColumn(who, 1);
        g.Children.Add(who);

        // Current rank (with RR) and peak, each with its badge.
        var ranks = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        ranks.Children.Add(Rank(p.Tier, p.Tier >= 3 ? p.Rr + " RR" : Loc.T("Unranked"), 24));
        if (p.PeakTier >= 3 && p.PeakTier != p.Tier) ranks.Children.Add(Rank(p.PeakTier, Loc.T("Peak"), 20).Also(r => { r.Margin = new Thickness(8, 0, 0, 0); r.Opacity = 0.85; }));
        Grid.SetColumn(ranks, 2);
        g.Children.Add(ranks);

        return new Border { Background = p.Me ? MeBg : RowBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 5, 8, 5), Margin = new Thickness(0, 0, 0, 4), Child = g };
    }

    static FrameworkElement Rank(int tier, string note, double size)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = ValorantMatches.RankName(tier) };
        if (MatchArt.RankIcon(tier) is { } icon) sp.Children.Add(new Image { Source = icon, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
        var text = new StackPanel { Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = ValorantMatches.RankName(tier);
        if (!string.IsNullOrEmpty(name)) text.Children.Add(T(name, 10.5, White));
        text.Children.Add(T(note, 9.5, Dim, bold: false));
        sp.Children.Add(text);
        return sp;
    }
}
