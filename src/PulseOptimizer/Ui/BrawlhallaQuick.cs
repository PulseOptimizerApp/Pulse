using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Pulse.Core;
using Pulse.Core.Games.Brawlhalla;
using Pulse.Pages;

namespace Pulse.Ui;

/// <summary>
/// Ctrl+Shift+B in Brawlhalla: a small box in the top right corner. Type the opponent's name from the loading screen
/// and it shows your chance of winning, their best legend and their best weapon. Esc (or ×) closes it and gives the
/// game its focus back. Brawlhalla doesn't tell other apps who you're playing, and Pulse doesn't read the game, so the
/// name is typed.
/// </summary>
public static class BrawlhallaQuick
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    static Window _w;
    static IntPtr _game;
    static StackPanel _out;
    static TextBlock _status;
    static TextBox _box;
    static CancellationTokenSource _cts;
    static BhProfile _me;
    static DateTime _meAt;

    static BrawlhallaSettings S => TweakEngine.State.Brawlhalla ??= new BrawlhallaSettings();

    static readonly Color Bg = Color.FromArgb(0xF5, 0x12, 0x12, 0x16), Gold = Color.FromRgb(0xF5, 0xB9, 0x42),
        Green = Color.FromRgb(0x3D, 0xDC, 0x84), Red = Color.FromRgb(0xFF, 0x5A, 0x4E), Row = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
    static readonly Brush White = new SolidColorBrush(Color.FromRgb(0xF4, 0xF2, 0xEE)), Dim = new SolidColorBrush(Color.FromRgb(0x9A, 0x97, 0x91));

    public static void Toggle()
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(Toggle); return; }
        if (_w != null) { Close(); return; }
        Open(app);
    }

    static void Open(Application app)
    {
        _game = GetForegroundWindow();
        var body = new StackPanel { Margin = new Thickness(16, 12, 16, 16) };

        var head = new Grid();
        var brand = new StackPanel { Orientation = Orientation.Horizontal };
        brand.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Pulse;component/Assets/logo-128.png")), Width = 18, Height = 18 });
        brand.Children.Add(T("PULSE", 11.5, White, true).Also(t => { t.Margin = new Thickness(7, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; }));
        brand.Children.Add(T("  ·  " + Loc.T("Matchup").ToUpper(CultureInfo.CurrentCulture), 10.5, Dim, true).Also(t => t.VerticalAlignment = VerticalAlignment.Center));
        head.Children.Add(brand);
        var x = T("×", 18, Dim, true).Also(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.Cursor = Cursors.Hand; t.Margin = new Thickness(0, -4, 0, 0); });
        x.MouseLeftButtonUp += (_, _) => Close();
        head.Children.Add(x);
        body.Children.Add(head);

        _box = new TextBox
        {
            Style = (Style)app.FindResource("SearchBox"), Tag = Loc.T("Opponent's name, ID or Steam link"), FontSize = 14, Margin = new Thickness(0, 12, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; _ = Run(_box.Text); }
            else if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
        body.Children.Add(_box);
        _status = T(Loc.T("Type their name from the loading screen, then Enter. Esc closes."), 11, Dim, false).Also(t => { t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 7, 0, 0); });
        body.Children.Add(_status);
        _out = new StackPanel();
        body.Children.Add(_out);

        var card = new Border
        {
            Width = 340, CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Bg), Child = body, Margin = new Thickness(18),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, Gold.R, Gold.G, Gold.B)), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.45, Color = Colors.Black },
        };
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, Content = card, FontFamily = (FontFamily)app.FindResource("UiFont"),
        };
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        var area = SystemParameters.WorkArea;
        w.Loaded += (_, _) => { w.Left = area.Right - w.ActualWidth - 2; w.Top = area.Top + 2; };
        w.SizeChanged += (_, _) => w.Left = area.Right - w.ActualWidth - 2;
        w.Closed += (_, _) => { if (_w == w) _w = null; };
        _w = w;
        w.Show();
        w.Activate();
        _box.Focus();
        Keyboard.Focus(_box);

        // Your own numbers, ready by the time you've typed (your rating moves every ranked match).
        if (S.MyId > 0 && (_me == null || DateTime.Now - _meAt > TimeSpan.FromMinutes(2)))
            _ = Task.Run(async () =>
            {
                try { if (await Brawlhalla.Profile(S.MyId, lookUpTier: false) is { SavedAt: null } me) { _me = me; _meAt = DateTime.Now; } } catch { }
            });
        _ = Task.Run(() => Brawlhalla.Legends());
    }

    static void Close()
    {
        _cts?.Cancel();
        var w = _w;
        _w = null;
        w?.Close();
        // Back to the game.
        if (_game != IntPtr.Zero) SetForegroundWindow(_game);
    }

    static void Say(string text, bool bad = false)
    {
        _status.Text = text;
        _status.Visibility = Visibility.Visible;
        _status.Foreground = bad ? new SolidColorBrush(Red) : Dim;
    }

    static async Task Run(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _out.Children.Clear();
        try
        {
            bool steamId = System.Text.RegularExpressions.Regex.IsMatch(text, @"^7656119\d{10}$");
            if (!steamId && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0) { await Show(id, ct); return; }
            if (text.Any(c => c > 127)) { Say(Loc.T("Brawlhalla's search doesn't take accented letters or symbols. Use their Brawlhalla ID instead (in game: Inventory, Profile)."), true); return; }
            Say(Loc.T("Searching…"));
            bool steam = steamId || text.Contains("steamcommunity.com", StringComparison.OrdinalIgnoreCase);
            var rows = steam ? new List<BhRanking>() : await Task.Run(() => Brawlhalla.Find(text, ct));
            if (rows.Count == 0 && await Task.Run(() => Brawlhalla.Steam(text, ct)) is { } s && !s.Name.Any(c => c > 127) && (steam || !string.Equals(s.Name, text, StringComparison.OrdinalIgnoreCase)))
                rows = await Task.Run(() => Brawlhalla.Find(s.Name, ct));
            if (ct.IsCancellationRequested) return;
            if (rows.Count == 0) { Say(Loc.T("No ranked player with that name. Try their Brawlhalla ID instead."), true); return; }
            var exact = rows.Where(r => string.Equals(r.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1 || rows.Count == 1) { await Show((exact.Count == 1 ? exact[0] : rows[0]).Id, ct); return; }
            Say(Loc.T("Which one?"));
            foreach (var r in rows.Take(5))
            {
                var row = new Border { Background = new SolidColorBrush(Row), CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 6, 0, 0), Cursor = Cursors.Hand };
                var g = new Grid();
                g.Children.Add(T(r.Name, 13, White, true));
                g.Children.Add(T($"{r.Tier} · {r.Rating:N0} · {r.Region}", 11.5, Dim, false).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right));
                row.Child = g;
                long pick = r.Id;
                row.MouseLeftButtonUp += (_, _) => _ = Show(pick, ct);
                _out.Children.Add(row);
            }
        }
        catch (OperationCanceledException) { }
        catch { Say(Loc.T("Couldn't reach Brawlhalla's stats. Try again in a moment."), true); }
    }

    static async Task Show(long id, CancellationToken ct)
    {
        _out.Children.Clear();
        Say(Loc.T("Loading their stats…"));
        var legendsTask = Task.Run(() => Brawlhalla.Legends(ct));
        var foe = await Task.Run(() => Brawlhalla.Profile(id, ct, lookUpTier: false));
        var legends = await legendsTask;
        if (ct.IsCancellationRequested || _w == null) return;
        if (foe == null) { Say(Loc.T("Brawlhalla's servers sent no stats for that player. If the ID is right, try again in a moment."), true); return; }
        Say(foe.SavedAt is { } at ? Loc.F("Saved stats from {0}: Brawlhalla's servers aren't answering for them right now.", at.ToString("g", CultureInfo.CurrentCulture)) : "");
        _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Visibility.Collapsed : Visibility.Visible;
        string Name(int legend) => legends.TryGetValue(legend, out var l) ? l.Name : Loc.F("Legend {0}", legend);

        // Who.
        var main = foe.Legends.Where(l => l.Games > 0).OrderByDescending(l => l.Games).FirstOrDefault();
        var who = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        who.Children.Add(Pic(main != null ? BrawlhallaArt.Legend(Name(main.LegendId)) : null, 40));
        var whoText = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        whoText.Children.Add(T(foe.Name, 15, White, true));
        whoText.Children.Add(T(foe.OneVsOne is { } one ? $"{one.Tier} · {one.Rating:N0}" : Loc.T("No ranked 1v1 this season"), 11.5, Dim, false));
        who.Children.Add(whoText);
        _out.Children.Add(who);

        // Chance of winning.
        var odds = BrawlhallaMatchup.Odds(S.MyId > 0 ? _me : null, foe);
        if (S.MyId > 0 && _me == null)
        {
            try { if (await Brawlhalla.Profile(S.MyId, ct, lookUpTier: false) is { } me) { _me = me; _meAt = DateTime.Now; odds = BrawlhallaMatchup.Odds(me, foe); } } catch { }
            if (ct.IsCancellationRequested || _w == null) return;
        }
        var chance = new Border { Background = new SolidColorBrush(Row), CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, 10, 0, 0) };
        var cs = new StackPanel();
        if (odds == null)
            cs.Children.Add(T(S.MyId > 0 ? Loc.T("Not enough games to tell.") : Loc.T("Set \"This is me\" on your Brawlhalla profile in Pulse to see your chance of winning."), 11.5, Dim, false).Also(t => t.TextWrapping = TextWrapping.Wrap));
        else
        {
            double pct = odds.Chance * 100;
            var c = pct >= 55 ? Green : pct <= 45 ? Red : Gold;
            var line = new Grid();
            line.Children.Add(T(Loc.T("Your chance of winning"), 12, Dim, false).Also(t => t.VerticalAlignment = VerticalAlignment.Center));
            line.Children.Add(T($"{pct:0}%", 24, new SolidColorBrush(c), true).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right));
            cs.Children.Add(line);
            var bar = new Grid { Height = 6, Margin = new Thickness(0, 4, 0, 0) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.01, odds.Chance), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.01, 1 - odds.Chance), GridUnitType.Star) });
            bar.Children.Add(new Border { Background = new SolidColorBrush(Green), CornerRadius = new CornerRadius(3, 0, 0, 3), Margin = new Thickness(0, 0, 1, 0) });
            bar.Children.Add(new Border { Background = new SolidColorBrush(Red), CornerRadius = new CornerRadius(0, 3, 3, 0) }.Also(b => Grid.SetColumn(b, 1)));
            cs.Children.Add(bar);
            cs.Children.Add(T(odds.Basis == "ranked" ? Loc.F("From ranked 1v1 ratings: {0} against {1}", odds.MyRating.ToString("N0", CultureInfo.CurrentCulture), odds.TheirRating.ToString("N0", CultureInfo.CurrentCulture))
                : Loc.T("Rough: from both overall win rates (one of you has no ranked 1v1 this season)"), 10.5, Dim, false).Also(t => { t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 5, 0, 0); }));
        }
        chance.Child = cs;
        _out.Children.Add(chance);

        // Their best legend and best weapon.
        if (BrawlhallaMatchup.BestLegend(foe) is { } best)
            _out.Children.Add(Fact(Loc.T("Best legend"), BrawlhallaArt.Legend(Name(best.LegendId)), Name(best.LegendId),
                Loc.F("{0:0}% wins in {1} games", 100.0 * best.Wins / best.Games, best.Games.ToString("N0", CultureInfo.CurrentCulture))
                + (main != null && main.LegendId != best.LegendId ? " · " + Loc.F("mostly plays {0}", Name(main.LegendId)) : "")));
        var weapons = BrawlhallaMatchup.Weapons(foe, legends);
        long held = Math.Max(1, weapons.Sum(w => w.Time));
        var bestWeapon = weapons.Where(w => w.Time * 100 / held >= 8).OrderByDescending(w => w.Kos / (w.Time / 3600.0)).FirstOrDefault();
        if (bestWeapon.Weapon != null)
            _out.Children.Add(Fact(Loc.T("Best weapon"), BrawlhallaArt.Weapon(bestWeapon.Weapon), WeaponName(bestWeapon.Weapon),
                Loc.F("{0:0.0} KOs an hour · held {1:0}% of the time", bestWeapon.Kos / (bestWeapon.Time / 3600.0), 100.0 * bestWeapon.Time / held)));
        if (BrawlhallaMatches.Smurf(foe) is { Score: >= 35 } sm)
            _out.Children.Add(T(Loc.T(sm.Label) + (sm.Signs.Count > 0 ? ": " + Loc.F(sm.Signs[0].Format, sm.Signs[0].Args) : ""), 11.5, new SolidColorBrush(Red), false)
                .Also(t => { t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 10, 0, 0); }));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var app = Application.Current;
        var full = new Button { Style = (Style)app.FindResource("PrimaryButton"), Content = Loc.T("Full matchup"), Padding = new Thickness(14, 5, 14, 6), Margin = new Thickness(0, 0, 8, 0), Focusable = false };
        full.Click += (_, _) =>
        {
            _w?.Close();
            _w = null;
            if (app.MainWindow is MainWindow mw) { mw.ShowFromTray(); mw.Navigate("brawlhalla", "matchup:" + id); }
        };
        var close = new Button { Style = (Style)app.FindResource("GhostButton"), Content = Loc.T("Close"), Padding = new Thickness(14, 5, 14, 6), Focusable = false };
        close.Click += (_, _) => Close();
        buttons.Children.Add(full);
        buttons.Children.Add(close);
        _out.Children.Add(buttons);
    }

    static string WeaponName(string w) => w switch
    {
        "Fists" => "Gauntlets", "Pistol" => "Blasters", "Hammer" => "Grapple Hammer", "Katar" => "Katars", "Boots" => "Battle Boots",
        _ => System.Text.RegularExpressions.Regex.Replace(w, "(?<=[a-z])(?=[A-Z])", " "),
    };

    static FrameworkElement Fact(string label, ImageSource pic, string name, string detail)
    {
        var g = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        g.Children.Add(Pic(pic, 34));
        var t = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(T(label.ToUpper(CultureInfo.CurrentCulture), 9.5, Dim, true));
        t.Children.Add(T(name, 13.5, White, true));
        t.Children.Add(T(detail, 11, Dim, false).Also(x => { x.TextWrapping = TextWrapping.Wrap; x.MaxWidth = 250; }));
        g.Children.Add(t);
        return g;
    }

    static FrameworkElement Pic(ImageSource src, double size)
    {
        var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Row), VerticalAlignment = VerticalAlignment.Top };
        if (src != null)
        {
            var img = new Image { Source = src, Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            b.Child = img;
            b.SizeChanged += (_, e) => b.Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);
        }
        return b;
    }

    static TextBlock T(string s, double size, Brush c, bool bold) => new()
    {
        Text = s, FontSize = size, Foreground = c, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis,
    };
}
