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
/// The card that pops up after a match, laid out like Medal's: the agent (or champion) with the result and rank badge
/// on top, then map score, K/D/A and headshots, the big plays with how many times each happened, the best clip from
/// the match, and how many clips it made. Dark and compact so it sits over a game without getting in the way.
/// </summary>
static class RecapCard
{
    const double W = 280;
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

        var body = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };

        // ── Agent, result, rank ──
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tint = MatchArt.AgentColor(m) ?? Color.FromRgb(0x3A, 0x3A, 0x42);
        var portrait = new Border
        {
            Width = 54, Height = 54, CornerRadius = new CornerRadius(10), ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(0x66, tint.R, tint.G, tint.B)),
            Child = MatchArt.Agent(m) is { } art ? new Image { Source = art, Stretch = Stretch.UniformToFill } : null,
        };
        head.Children.Add(portrait);
        var who = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(Text(Up(string.IsNullOrEmpty(m.Character) ? m.Game : m.Character), 18, White, displayFont: true));
        var result = m.Result switch { "Win" => Loc.T("Win"), "Loss" => Loc.T("Loss"), "Draw" => Loc.T("Draw"), _ => m.Mode };
        who.Children.Add(Text(Up(result), 13, m.Result == "Win" ? Win : m.Result == "Loss" ? Loss : Label).Also(t => t.Margin = new Thickness(0, 1, 0, 0)));
        Grid.SetColumn(who, 1);
        head.Children.Add(who);
        if (MatchArt.Rank(m) is { } rank)
        {
            var badge = new Image { Source = rank, Width = 38, Height = 38, VerticalAlignment = VerticalAlignment.Center, ToolTip = m.Rank };
            Grid.SetColumn(badge, 2);
            head.Children.Add(badge);
        }
        body.Children.Add(head);

        // ── Map score · K/D/A · headshots ──
        var stats = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        for (int i = 0; i < 3; i++) stats.ColumnDefinitions.Add(new ColumnDefinition());
        void Stat(int col, string label, UIElement value)
        {
            var sp = new StackPanel { HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : col == 1 ? HorizontalAlignment.Center : HorizontalAlignment.Right };
            sp.Children.Add(Text(Up(label), 10.5, Label).Also(t => t.HorizontalAlignment = sp.HorizontalAlignment));
            if (value is FrameworkElement fe) { fe.Margin = new Thickness(0, 4, 0, 0); fe.HorizontalAlignment = sp.HorizontalAlignment; }
            sp.Children.Add(value);
            Grid.SetColumn(sp, col);
            stats.Children.Add(sp);
        }
        var score = new TextBlock { FontSize = 17, FontWeight = FontWeights.Bold, FontFamily = display };
        var parts = (m.Score ?? "").Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            score.Inlines.Add(new System.Windows.Documents.Run(parts[0]) { Foreground = m.Result == "Loss" ? White : Win });
            score.Inlines.Add(new System.Windows.Documents.Run(" : ") { Foreground = Label });
            score.Inlines.Add(new System.Windows.Documents.Run(parts[1]) { Foreground = m.Result == "Win" ? White : Loss });
        }
        else score.Inlines.Add(new System.Windows.Documents.Run(string.IsNullOrEmpty(m.Score) ? "–" : m.Score) { Foreground = White });
        Stat(0, string.IsNullOrEmpty(m.Map) ? Loc.T("Score") : m.Map, score);
        Stat(1, "KDA", Text(m.Kda, 17, White, displayFont: true));
        Stat(2, m.HeadshotPercent >= 0 ? "HS%" : Loc.T("Mode"), Text(m.HeadshotPercent >= 0 ? m.HeadshotPercent + "%" : (string.IsNullOrEmpty(m.Mode) ? "–" : m.Mode), 17, White, displayFont: true));
        body.Children.Add(stats);

        // ── Big plays: one row per kind, with how many times ──
        var plays = (m.Highlights ?? new()).GroupBy(h => h).Select(g => (Label: g.Key, Count: g.Count()))
            .OrderByDescending(p => Order(p.Label)).ToList();
        if (plays.Count > 0)
        {
            var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            foreach (var (label, count) in plays.Take(4))
                list.Children.Add(RowWithCount(Up(Loc.T(label)), count, Text));
            body.Children.Add(list);
        }

        // ── Best clip ──
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
                body.Children.Add(new Border
                {
                    Height = (W - 32) * 9 / 16, CornerRadius = new CornerRadius(8), ClipToBounds = true, Margin = new Thickness(0, 12, 0, 0),
                    Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill },
                });
            }
            catch { }
        }

        // ── Clips from this match ──
        body.Children.Add(RowWithCount(Up(Loc.T("Total clips")), clips, Text).Also(r => r.Margin = new Thickness(0, 12, 0, 0)));

        // The map behind the top of the card, faded into the background.
        var map = MatchArt.Map(m);
        var backdrop = new Grid { Height = 120, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
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

    static FrameworkElement RowWithCount(string label, int count, Func<string, double, Brush, bool, bool, TextBlock> text)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = text(label, 12, White, true, false);
        l.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(l);
        var chip = new Border
        {
            MinWidth = 54, CornerRadius = new CornerRadius(5), Background = Chip, Padding = new Thickness(10, 3, 10, 4),
            Child = text(count.ToString(CultureInfo.CurrentCulture), 12.5, White, true, false).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center),
        };
        Grid.SetColumn(chip, 1);
        g.Children.Add(chip);
        return new Border { Background = Row, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 6, 6), Margin = new Thickness(0, 0, 0, 6), Child = g };
    }
}
