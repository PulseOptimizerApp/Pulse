using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pulse.Core;
using Pulse.Core.Games;
using Pulse.Core.Games.Live;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// Everything Pulse does for VALORANT in one place (like the Roblox page): the opt-in live lobby with its risk, your
/// recent matches, and shortcuts to clip markers, the crosshair and game settings.
/// </summary>
public partial class ValorantPage : UserControl, IPage
{
    static Core.Clips.ClipSettings S => TweakEngine.State.Clips;

    readonly Dictionary<string, (TextBlock Label, Border Line)> _tabs = new();
    string _tab = "coach";

    public ValorantPage()
    {
        InitializeComponent();
        foreach (var (key, title) in new[] { ("coach", "Coach"), ("tracker", "Tracker"), ("maps", "Heatmaps"), ("review", "Death review"), ("store", "Store"), ("overview", "Live lobby & matches") })
            Tabs.Children.Add(TabButton(key, title));
        Unloaded += (_, _) => { StopReview(); StopRoundPlay(); };
    }

    public void OnShown(object parameter)
    {
        if (parameter is string key && _tabs.ContainsKey(key)) _tab = key;
        ShowTab(_tab, false);
    }

    Brush Res(string k) => (Brush)FindResource(k);
    Border Card(UIElement child) => new() { Style = (Style)FindResource("Card"), Padding = new Thickness(22, 18, 22, 18), Margin = new Thickness(0, 0, 0, 14), Child = child };
    TextBlock Heading(string t) => new() { Text = Loc.T(t), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont"), TextWrapping = TextWrapping.Wrap };
    TextBlock Dim(string t, double size = 12.5, double top = 4) => new() { Text = t, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, top, 0, 0), LineHeight = size * 1.45 };
    TextBlock Faint(string t, double size = 11.5, double top = 4) => new() { Text = t, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, top, 0, 0) };
    TextBlock Label(string t) => new() { Text = Loc.T(t).ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 0, 0, 6) };

    Button Btn(string text, bool primary = false) => new() { Style = (Style)FindResource(primary ? "PrimaryButton" : "GhostButton"), Content = Loc.T(text), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 10, 0) };

    RadioButton Chip(string text, string group, bool on, Action pick)
    {
        var c = new RadioButton { Style = (Style)FindResource("Chip"), Content = text, GroupName = group, IsChecked = on, Margin = new Thickness(0, 0, 6, 6) };
        c.Checked += (_, _) => pick();
        return c;
    }

    FrameworkElement TabButton(string key, string title)
    {
        var label = new TextBlock { Text = Loc.T(title), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 11) };
        var line = new Border { Height = 2, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom };
        var g = new Grid { Margin = new Thickness(0, 0, 26, 0), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        g.Children.Add(label);
        g.Children.Add(line);
        g.MouseLeftButtonUp += (_, _) => ShowTab(key);
        g.MouseEnter += (_, _) => { if (_tab != key) label.Foreground = Res("TextBrush"); };
        g.MouseLeave += (_, _) => { if (_tab != key) label.Foreground = Res("TextDimBrush"); };
        _tabs[key] = (label, line);
        return g;
    }

    void ShowTab(string key, bool animate = true)
    {
        if (_tab == "review" && key != "review") StopReview();
        StopRoundPlay();
        _tab = key;
        foreach (var (k, (label, line)) in _tabs)
        {
            label.Foreground = Res(k == key ? "TextBrush" : "TextDimBrush");
            line.Background = k == key ? Res("AccentBrush") : Brushes.Transparent;
        }
        Build();
        Scroller.ScrollToTop();
        if (!animate) return;
        Root.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    void Build()
    {
        Root.Children.Clear();
        switch (_tab)
        {
            case "coach": BuildCoach(); break;
            case "maps": BuildMaps(); break;
            case "review": BuildReview(); break;
            case "store": BuildStore(); break;
            case "tracker": BuildTracker(); break;
            default:
                Root.Children.Add(LobbyCard());
                Root.Children.Add(MatchesCard());
                Root.Children.Add(MoreCard());
                break;
        }
    }

    // ───────── live lobby ─────────

    FrameworkElement LobbyCard()
    {
        var sp = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(Heading("Live lobby"));
        left.Children.Add(Dim(Loc.T("When you load into a match, a popup offers to show both teams: agent, name, level, current rank and RR, peak rank, win rate this act, and smurf signs, like tracker apps. Players who hid their name stay hidden."), 12.5, 4));
        top.Children.Add(left);
        bool on = S.LiveLobby && S.LiveLobbyRisksAccepted;
        var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = on, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 2, 0, 0) };
        Grid.SetColumn(sw, 1);
        top.Children.Add(sw);
        sp.Children.Add(top);

        // The risk, always visible on this card.
        var risk = new StackPanel();
        risk.Children.Add(new TextBlock { Text = Loc.T("The risk").ToUpper(CultureInfo.CurrentCulture), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Res("WarnBrush") });
        risk.Children.Add(Dim(Loc.T("Riot treats ranks shown during a match as hidden information and has warned that apps showing hidden information can lead to bans; some users of tracker apps have reported suspensions. Pulse never reveals players who hid their name and only reads data through the Riot Client's own login on your PC, but it can't promise Riot won't act on your account."), 12, 3));
        sp.Children.Add(new Border
        {
            BorderBrush = Res("WarnBrush"), BorderThickness = new Thickness(2, 0, 0, 0), Background = Res("Surface2Brush"),
            CornerRadius = new CornerRadius(0, 6, 6, 0), Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, 14, 0, 0), Child = risk,
        });

        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var show = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Show the current lobby"), Padding = new Thickness(14, 6, 14, 6), IsEnabled = on };
        show.ToolTip = Loc.T("Opens the lobby panel for the match you're in now.");
        show.Click += (_, _) => _ = LobbyOverlay.ShowPanel(Application.Current);
        row.Children.Add(show);
        sp.Children.Add(row);
        sp.Children.Add(Dim(Loc.T("Smurf signs come from RR per win (Riot gives more when a player's hidden skill is above their rank), win rate this act, account level and peak rank. They're signs, not proof, and Pulse always shows why. Pulse doesn't guess who's cheating: nothing it can see tells a cheater from a very good player."), 11.5, 10));

        sw.Click += async (_, _) =>
        {
            if (sw.IsChecked == true && !S.LiveLobbyRisksAccepted)
            {
                bool ok = await MainWindow.Instance.Confirm(Loc.T("Turn on the live lobby?"),
                    Loc.T("Riot treats ranks shown during a match as hidden information. Tracker apps that do this are widely used, but Riot has warned that apps showing hidden information can lead to bans, and some users of such apps have reported suspensions. Pulse lowers the risk by never revealing players who hid their name and by only reading data through the Riot Client's own login on your PC, but it can't promise Riot won't act on your account.\n\nTurn it on anyway?"),
                    Loc.T("Turn on"), Loc.T("Cancel"));
                if (!ok) { sw.IsChecked = false; return; }
                S.LiveLobbyRisksAccepted = true;
                TweakEngine.AddLog("valorant", "VALORANT live lobby turned on (risk accepted)");
            }
            S.LiveLobby = sw.IsChecked == true;
            TweakEngine.Save();
            show.IsEnabled = S.LiveLobby;
        };
        return Card(sp);
    }

    // ───────── recent matches ─────────

    FrameworkElement MatchesCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Recent matches"));
        var list = MatchStore.All.Where(m => m.Game == "VALORANT" && !m.Session).OrderByDescending(m => m.End).Take(8).ToList();
        if (list.Count == 0)
        {
            sp.Children.Add(Dim(S.MatchRecaps
                ? Loc.T("Your VALORANT matches show up here after you play, with agent, result, score, K/D/A, headshots and rank.")
                : Loc.T("Turn on match recaps on the Clips page to see your VALORANT matches here."), 12.5, 6));
            return Card(sp);
        }
        var rows = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var m in list) rows.Children.Add(MatchRow(m));
        sp.Children.Add(rows);
        var all = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("All match recaps"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        all.Click += (_, _) => MainWindow.Instance.Navigate("clips", "matches");
        sp.Children.Add(all);
        return Card(sp);
    }

    FrameworkElement MatchRow(MatchRecord m)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var w in new[] { new GridLength(40), new GridLength(1, GridUnitType.Star), new GridLength(90), new GridLength(90), new GridLength(70), new GridLength(40) })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        var face = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(7), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };
        if (MatchArt.AgentColor(m) is Color c) face.Background = new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B));
        if (MatchArt.Agent(m) is { } art) face.Child = new Image { Source = art, Stretch = Stretch.Uniform }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality));
        g.Children.Add(face);

        var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { m.Map, m.Character }.Where(x => !string.IsNullOrEmpty(x))), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        who.Children.Add(new TextBlock { Text = Loc.T(m.Mode) + " · " + m.End.ToString("g", CultureInfo.CurrentCulture), FontSize = 11, Foreground = Res("TextFaintBrush") });
        Grid.SetColumn(who, 1);
        g.Children.Add(who);

        TextBlock Cell(string t, Brush b, int col)
        {
            var x = new TextBlock { Text = t, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = b, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(x, col);
            g.Children.Add(x);
            return x;
        }
        Cell(Loc.T(string.IsNullOrEmpty(m.Result) ? "–" : m.Result) + "  " + m.Score, Res(m.Result == "Win" ? "GoodBrush" : m.Result == "Loss" ? "BadBrush" : "TextDimBrush"), 2);
        Cell(m.Kda, Res("TextBrush"), 3);
        Cell(m.HeadshotPercent >= 0 ? m.HeadshotPercent + "% HS" : "", Res("TextDimBrush"), 4);
        if (MatchArt.Rank(m) is { } rank)
        {
            var img = new Image { Source = rank, Width = 28, Height = 28, ToolTip = m.Rank, HorizontalAlignment = HorizontalAlignment.Right };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            Grid.SetColumn(img, 5);
            g.Children.Add(img);
        }
        return new Border { Background = Res("Surface2Brush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 6, 10, 6), Child = g };
    }

    // ───────── shortcuts ─────────

    FrameworkElement MoreCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Also for VALORANT"));
        var wrap = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        void Go(string text, string page, object arg = null)
        {
            var b = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T(text), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 10, 10) };
            b.Click += (_, _) => MainWindow.Instance.Navigate(page, arg);
            wrap.Children.Add(b);
        }
        Go("Match recaps and kill markers on clips", "clips");
        Go("Crosshair", "crosshair");
        Go("Optimize VALORANT", "games");
        Go("GPU tuning", "gpu");
        Go("Fix stutter guide", "guide");
        sp.Children.Add(wrap);
        return Card(sp);
    }
}
