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

    /// <summary>You just loaded into a match: start loading the lobby and ask whether to show it.</summary>
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
        ShowPrompt(app);
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
        sp.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo-128.png")), Width = 18, Height = 18 });
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
        _prompt?.Close();
        var body = new StackPanel { Margin = new Thickness(14, 12, 14, 14) };
        body.Children.Add(Brand(Loc.T("Live lobby")));
        body.Children.Add(T(Loc.T("Match found"), 15, White).Also(t => t.Margin = new Thickness(0, 10, 0, 0)));
        body.Children.Add(T(Loc.T("See both teams' ranks and peaks?"), 12, Dim, bold: false).Also(t => t.Margin = new Thickness(0, 2, 0, 0)));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var show = Btn(app, Loc.T("Show"), true);
        var no = Btn(app, Loc.T("Not now"), false);
        show.Click += (_, _) => { ClosePrompt(); _ = ShowPanel(app); };
        no.Click += (_, _) => ClosePrompt();
        row.Children.Add(show); row.Children.Add(no);
        body.Children.Add(row);

        var w = NewWindow(app, Card(body, 280));
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
        _promptTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        _promptTimer.Tick += (_, _) => ClosePrompt();
        _promptTimer.Start();
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

    public static async Task ShowPanel(Application app)
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
        try { lobby = _loading != null ? await _loading : await ValorantLobby.Load(); } catch { }
        lobby ??= ValorantLobby.Last;
        if (_panel != w) return;
        content.Children.Clear();
        content.Children.Add(lobby == null ? Failed(app) : Panel(app, lobby));
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

    static FrameworkElement Failed(Application app)
    {
        var sp = new StackPanel { Margin = new Thickness(16, 12, 16, 16) };
        sp.Children.Add(Header(app, Loc.T("Live lobby")));
        sp.Children.Add(T(Loc.T("Couldn't load this match. Riot may not have listed it yet; try again in a few seconds."), 12.5, Dim, bold: false).Also(t => { t.Margin = new Thickness(0, 12, 0, 0); t.TextWrapping = TextWrapping.Wrap; }));
        var again = Btn(app, Loc.T("Try again"), true);
        again.Margin = new Thickness(0, 12, 0, 0);
        again.HorizontalAlignment = HorizontalAlignment.Left;
        again.Click += (_, _) => { _loading = null; _ = ShowPanel(app); };
        sp.Children.Add(again);
        return sp;
    }

    static FrameworkElement Panel(Application app, Lobby l)
    {
        var sp = new StackPanel { Margin = new Thickness(16, 12, 16, 16) };
        sp.Children.Add(Header(app, string.IsNullOrEmpty(l.Map) ? Loc.T("Live lobby") : Loc.T("Live lobby") + " · " + l.Map));
        var cols = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        var mine = Team(Loc.T("Your team"), Ally, l.Players.Where(p => p.Ally).OrderByDescending(p => p.Tier));
        var theirs = Team(Loc.T("Enemy team"), Enemy, l.Players.Where(p => !p.Ally).OrderByDescending(p => p.Tier));
        Grid.SetColumn(theirs, 2);
        cols.Children.Add(mine); cols.Children.Add(theirs);
        sp.Children.Add(cols);
        sp.Children.Add(T(Loc.T("Peak = highest rank won at in any act. Players who hid their name stay hidden."), 10.5, Dim, bold: false).Also(t => { t.Margin = new Thickness(0, 10, 0, 0); t.TextWrapping = TextWrapping.Wrap; }));
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
        who.Children.Add(p.Hidden ? T(Loc.T("Hidden player"), 12, Dim).Also(t => t.FontStyle = FontStyles.Italic) : T(p.Name ?? p.Agent, 12, White));
        var sub = new List<string>();
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
