using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Pulse.Core.Games;
using Pulse.Pages;

namespace Pulse.Ui;

/// <summary>
/// The card that pops up after a match: Pulse's header, the agent (or champion) portrait on its own color with the
/// result and rank badge, then map score, K/D/A and headshots as tiles, the big plays as chips, and the best clip with
/// how many clips the match made. Dark and compact so it sits over a game without getting in the way.
/// </summary>
static class RecapCard
{
    const double W = 290;
    static readonly Color Bg = Color.FromArgb(0xF4, 0x12, 0x12, 0x16);
    static readonly Brush White = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF2, 0xEE)));
    static readonly Brush Label = Frozen(new SolidColorBrush(Color.FromRgb(0x8F, 0x8C, 0x86)));
    static readonly Brush Win = Frozen(new SolidColorBrush(Color.FromRgb(0x4F, 0xD1, 0x8B)));
    static readonly Brush Loss = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A)));
    static readonly Brush Row = Frozen(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)));
    static readonly Brush Chip = Frozen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    public static FrameworkElement Build(Application app, MatchRecord m, int clips, string bestClip)
    {
        var display = (FontFamily)app.FindResource("DisplayFont");
        var scope = new Grid();
        NameScope.SetNameScope(scope, new NameScope());

        TextBlock Text(string s, double size, Brush color, bool bold = true, bool displayFont = false) => new()
        {
            Text = s, FontSize = size, Foreground = color, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            FontFamily = displayFont ? display : (FontFamily)app.FindResource("UiFont"), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        string Up(string s) => (s ?? "").ToUpper(CultureInfo.CurrentCulture);

        var body = new StackPanel { Margin = new Thickness(14, 12, 14, 14) };

        // ── Pulse header: logo, "Match recap", the mode ──
        var top = new Grid();
        var brand = new StackPanel { Orientation = Orientation.Horizontal };
        brand.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Pulse;component/Assets/logo-128.png")), Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center });
        brand.Children.Add(Text("PULSE", 11.5, White, displayFont: true).Also(t => { t.Margin = new Thickness(7, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; }));
        brand.Children.Add(Text("  ·  " + Up(Loc.T("Match recap")), 10.5, Label).Also(t => t.VerticalAlignment = VerticalAlignment.Center));
        top.Children.Add(brand);
        if (!string.IsNullOrEmpty(m.Mode))
            top.Children.Add(Text(Up(Loc.T(m.Mode)), 10.5, Label).Also(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; }));
        body.Children.Add(top);

        // ── Agent, result, rank: a strip in the agent's color that fades out to the right, the portrait at its left ──
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tint = MatchArt.AgentColor(m) ?? Color.FromRgb(0x4A, 0x4A, 0x55);
        var strip = new Border
        {
            CornerRadius = new CornerRadius(10), ClipToBounds = true, Margin = new Thickness(0, 12, 0, 0),
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0xB0, tint.R, tint.G, tint.B), 0),
                new GradientStop(Color.FromArgb(0x40, tint.R, tint.G, tint.B), 0.45),
                new GradientStop(Color.FromArgb(0x00, tint.R, tint.G, tint.B), 1),
            }, 0),
            BorderBrush = new LinearGradientBrush(Color.FromArgb(0x70, tint.R, tint.G, tint.B), Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0),
            BorderThickness = new Thickness(1),
            Child = head,
        };
        // The whole portrait (Riot's square agent icon, transparent around the agent) standing on the strip, not cropped.
        var portrait = new Border
        {
            Width = 64, Height = 64, ClipToBounds = true,
            Child = MatchArt.Agent(m) is { } art
                ? new Image { Source = art, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 3, 3, 0) }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality))
                : null,
        };
        head.Children.Add(portrait);
        var who = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(Text(Up(string.IsNullOrEmpty(m.Character) ? m.Game : m.Character), 20, White, displayFont: true));
        var result = m.Result switch { "Win" => Loc.T("Win"), "Loss" => Loc.T("Loss"), "Draw" => Loc.T("Draw"), _ => "" };
        if (result.Length > 0)
        {
            var c = m.Result == "Win" ? Win : m.Result == "Loss" ? Loss : Label;
            var cc = ((SolidColorBrush)c).Color;
            who.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 2, 9, 3), Margin = new Thickness(0, 5, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromArgb(0x2A, cc.R, cc.G, cc.B)),
                Child = Text(Up(result), 11.5, c),
            });
        }
        Grid.SetColumn(who, 1);
        head.Children.Add(who);
        if (MatchArt.Rank(m) is { } rank)
        {
            // Just the badge and its name: no box around it.
            var r = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            r.Children.Add(new Image { Source = rank, Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
            if (!string.IsNullOrEmpty(m.Rank)) r.Children.Add(Text(Up(m.Rank), 9.5, Label).Also(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 3, 0, 0); }));
            Grid.SetColumn(r, 2);
            head.Children.Add(r);
        }
        body.Children.Add(strip);

        // ── Three tiles: map score · K/D/A · headshots ──
        var stats = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        for (int i = 0; i < 3; i++) stats.ColumnDefinitions.Add(new ColumnDefinition());
        void Stat(int col, string label, UIElement value)
        {
            var sp = new StackPanel();
            sp.Children.Add(Text(Up(label), 9.5, Label));
            if (value is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 0, 0);
            sp.Children.Add(value);
            var tile = new Border { Background = Row, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 8, 8), Margin = new Thickness(col == 0 ? 0 : 3, 0, col == 2 ? 0 : 3, 0), Child = sp };
            Grid.SetColumn(tile, col);
            stats.Children.Add(tile);
        }
        var score = new TextBlock { FontSize = 16, FontWeight = FontWeights.Bold, FontFamily = display };
        var parts = (m.Score ?? "").Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            score.Inlines.Add(new System.Windows.Documents.Run(parts[0]) { Foreground = m.Result == "Loss" ? White : Win });
            score.Inlines.Add(new System.Windows.Documents.Run(" : ") { Foreground = Label });
            score.Inlines.Add(new System.Windows.Documents.Run(parts[1]) { Foreground = m.Result == "Win" ? White : Loss });
        }
        else score.Inlines.Add(new System.Windows.Documents.Run(string.IsNullOrEmpty(m.Score) ? "–" : m.Score) { Foreground = White });
        Stat(0, string.IsNullOrEmpty(m.Map) ? Loc.T("Score") : m.Map, score);
        Stat(1, "K / D / A", Text(m.Kda.Replace(" / ", "/"), 16, White, displayFont: true));
        Stat(2, m.HeadshotPercent >= 0 ? Loc.T("Headshots") : Loc.T("Mode"), Text(m.HeadshotPercent >= 0 ? m.HeadshotPercent + "%" : (string.IsNullOrEmpty(m.Mode) ? "–" : m.Mode), 16, White, displayFont: true));
        body.Children.Add(stats);

        // ── Big plays, as chips ("3K ×2") ──
        var plays = (m.Highlights ?? new()).GroupBy(h => h).Select(g => (Label: g.Key, Count: g.Count()))
            .OrderByDescending(p => Order(p.Label)).ToList();
        if (plays.Count > 0)
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var (label, count) in plays.Take(4))
            {
                var chip = new TextBlock { FontSize = 12, FontWeight = FontWeights.Bold, FontFamily = display };
                chip.Inlines.Add(new System.Windows.Documents.Run(Up(Loc.T(label))) { Foreground = White });
                if (count > 1) chip.Inlines.Add(new System.Windows.Documents.Run("  ×" + count) { Foreground = Label });
                wrap.Children.Add(new Border { Background = Chip, CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 6, 0), Child = chip });
            }
            body.Children.Add(wrap);
        }

        // ── Best clip, with how many clips the match made ──
        ImageSource thumb = null;
        if (bestClip != null && File.Exists(Core.Clips.ClipLibrary.ThumbFor(bestClip)))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 520;
                bmp.UriSource = new Uri(Core.Clips.ClipLibrary.ThumbFor(bestClip));
                bmp.EndInit();
                thumb = bmp;
            }
            catch { }
        }
        string clipsText = clips == 1 ? Loc.T("1 clip") : Loc.F("{0} clips", clips);
        if (thumb != null)
        {
            var pic = new Grid { Height = (W - 28) * 9 / 16 };
            pic.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill } });
            pic.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(0, 0, 8, 8), Height = 40, VerticalAlignment = VerticalAlignment.Bottom,
                Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0xC0, 0, 0, 0), 90),
            });
            var tags = new Grid { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 10, 8) };
            tags.Children.Add(Text(Up(Loc.T("Best clip")), 10.5, White));
            tags.Children.Add(Text(Up(clipsText), 10.5, White).Also(t => t.HorizontalAlignment = HorizontalAlignment.Right));
            pic.Children.Add(tags);
            body.Children.Add(new Border { Margin = new Thickness(0, 12, 0, 0), Child = pic });
        }
        else body.Children.Add(Text(clips == 0 ? Loc.T("No clips this match") : clipsText, 11.5, Label).Also(t => t.Margin = new Thickness(0, 12, 0, 0)));

        // The map behind the top of the card, faded into the background.
        var map = MatchArt.Map(m);
        var backdrop = new Grid { Height = 150, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        if (map != null)
        {
            backdrop.Children.Add(new Image { Source = map, Stretch = Stretch.UniformToFill, Opacity = 0.32 });
            backdrop.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(0x30, Bg.R, Bg.G, Bg.B), Bg, 90) });
        }

        var drain = new Rectangle
        {
            Height = 2, Fill = m.Result == "Loss" ? Loss : Win, Opacity = 0.9, VerticalAlignment = VerticalAlignment.Bottom,
            RenderTransformOrigin = new Point(0, 0.5), RenderTransform = new ScaleTransform(1, 1),
        };
        scope.RegisterName("Drain", drain);

        scope.Children.Add(new Border
        {
            Width = W, CornerRadius = new CornerRadius(12), ClipToBounds = true, Background = new SolidColorBrush(Bg),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Child = new Grid { Children = { backdrop, body, drain } },
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.45, Color = Colors.Black },
        });
        return scope;
    }

    static int Order(string label) => label switch
    {
        "ACE" or "Penta kill" => 5, "4K" or "Quadra kill" => 4, "3K" or "Triple kill" => 3, _ => 0,
    };
}
