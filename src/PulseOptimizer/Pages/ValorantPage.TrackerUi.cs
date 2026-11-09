using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Pulse.Core.Games;
using Pulse.Core.Games.Live;
using Pulse.Core.Games.Valorant;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// The tracker's look: glowing ring gauges, skill tiles with "plays like" rank badges, stat tiles with segment bars,
/// on cards with a soft color glow and a thin accent line.
/// </summary>
public partial class ValorantPage
{
    static readonly Color Green = Color.FromRgb(0x3D, 0xDC, 0x84), Yellow = Color.FromRgb(0xFA, 0xD6, 0x63), Red = Color.FromRgb(0xFF, 0x5A, 0x4E),
        Blue = Color.FromRgb(0x4D, 0xA3, 0xFF), Purple = Color.FromRgb(0xC9, 0xA2, 0xFF), Orange = Color.FromRgb(0xFF, 0x7A, 0x59), Cyan = Color.FromRgb(0x59, 0xD4, 0xE8),
        Grey = Color.FromRgb(0x8A, 0x8F, 0x98), Gold = Color.FromRgb(0xF5, 0xB9, 0x42);

    static Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    /// <summary>
    /// Was a soft glow (a blur effect). Effects are redrawn on every frame while scrolling and made the page lag, so it's
    /// off: the colored gradients carry the look instead.
    /// </summary>
    static DropShadowEffect Glow(Color c, double blur = 18, double opacity = 0.65) => null;

    /// <summary>The color VALORANT uses for each rank.</summary>
    static Color RankColor(int tier) => tier switch
    {
        < 3 => Grey,
        <= 5 => Color.FromRgb(0x8A, 0x8F, 0x98),   // Iron
        <= 8 => Color.FromRgb(0xC0, 0x8B, 0x55),   // Bronze
        <= 11 => Color.FromRgb(0xD3, 0xD8, 0xDE),  // Silver
        <= 14 => Color.FromRgb(0xEC, 0xC6, 0x5B),  // Gold
        <= 17 => Color.FromRgb(0x4F, 0xC9, 0xC9),  // Platinum
        <= 20 => Color.FromRgb(0xC8, 0x8E, 0xF7),  // Diamond
        <= 23 => Color.FromRgb(0x3D, 0xDC, 0x84),  // Ascendant
        <= 26 => Color.FromRgb(0xFF, 0x5A, 0x6E),  // Immortal
        _ => Color.FromRgb(0xFF, 0xF0, 0x9E),       // Radiant
    };

    /// <summary>A card with a soft glow of its color and a thin accent line along the top.</summary>
    Border Card2(UIElement child, Color accent, double pad = 16, bool glow = false)
    {
        var g = new Grid();
        if (glow)
            g.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(14), IsHitTestVisible = false,
                Background = new RadialGradientBrush(A(accent, 0x22), A(accent, 0)) { Center = new Point(0.5, 0.42), GradientOrigin = new Point(0.5, 0.42), RadiusX = 0.6, RadiusY = 0.55 },
            });
        g.Children.Add(new Border
        {
            Height = 1, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(60, 0, 60, 0), IsHitTestVisible = false,
            Background = new LinearGradientBrush(new GradientStopCollection { new(A(accent, 0), 0), new(A(accent, 0xCC), 0.5), new(A(accent, 0), 1) }, 0),
        });
        var inner = new Border { Padding = new Thickness(pad), Child = child };
        g.Children.Add(inner);
        return new Border
        {
            CornerRadius = new CornerRadius(14), BorderBrush = B(Color.FromRgb(0x24, 0x26, 0x2C)), BorderThickness = new Thickness(1),
            Background = new LinearGradientBrush(Color.FromRgb(0x17, 0x18, 0x1C), Color.FromRgb(0x12, 0x13, 0x16), 90),
            Margin = new Thickness(0, 0, 0, 10), Child = g, ClipToBounds = true,
        };
    }

    TextBlock Caps(string t, double size = 11, Brush b = null, double spacing = 0) => new()
    {
        Text = Loc.T(t).ToUpper(CultureInfo.CurrentCulture), FontSize = size, FontWeight = FontWeights.ExtraBold, Foreground = b ?? Res("TextDimBrush"),
    };

    Border Pill2(string text, Color c, double size = 11) => new()
    {
        CornerRadius = new CornerRadius(999), Padding = new Thickness(11, 4, 11, 5), BorderThickness = new Thickness(1),
        BorderBrush = B(A(c, 0x73)), Background = B(A(c, 0x1F)), HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock { Text = text.ToUpper(CultureInfo.CurrentCulture), FontSize = size, FontWeight = FontWeights.ExtraBold, Foreground = B(c) },
    };

    // ───────── icons (24×24 line icons) ─────────

    const string IconShield = "M12 2.5 L19.5 5.5 V11.5 C19.5 16.1 16.3 19.9 12 21.5 C7.7 19.9 4.5 16.1 4.5 11.5 V5.5 Z M8.5 12 L10.9 14.4 L15.5 9.6";
    const string IconMask = "M3 8.5 C6 6.9 9 6.5 12 6.5 C15 6.5 18 6.9 21 8.5 C21 13.5 18.4 17 15.6 17 C13.9 17 13.2 15.4 12 15.4 C10.8 15.4 10.1 17 8.4 17 C5.6 17 3 13.5 3 8.5 Z M7 10.8 A1.3 1.3 0 1 0 9.6 10.8 A1.3 1.3 0 1 0 7 10.8 M14.4 10.8 A1.3 1.3 0 1 0 17 10.8 A1.3 1.3 0 1 0 14.4 10.8";
    const string IconTrophy = "M7 4 H17 V8.5 A5 5 0 0 1 7 8.5 Z M7 6 H4.5 A2.5 2.5 0 0 0 7.3 9.6 M17 6 H19.5 A2.5 2.5 0 0 1 16.7 9.6 M12 13.5 V17 M8.5 20 H15.5 L14.7 17 H9.3 Z";
    const string IconCross = "M5 12 A7 7 0 1 0 19 12 A7 7 0 1 0 5 12 M12 2.5 V7.5 M12 16.5 V21.5 M2.5 12 H7.5 M16.5 12 H21.5";

    static FrameworkElement Icon(string data, Color c, double size = 15) => new System.Windows.Shapes.Path
    {
        Data = Geometry.Parse(data), Stroke = B(c), StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        Width = size, Height = size, Stretch = Stretch.Uniform,
    };

    // ───────── the ring gauge (style A) ─────────

    static System.Windows.Shapes.Path Ring(double size, double stroke, double frac, Brush brush)
    {
        double r = (size - stroke) / 2 - 4, cx = size / 2, cy = size / 2;
        double sweep = 270 * Math.Clamp(frac, 0.002, 0.9995);
        Point P(double deg) => new(cx + r * Math.Cos(deg * Math.PI / 180), cy + r * Math.Sin(deg * Math.PI / 180));
        var fig = new PathFigure { StartPoint = P(135), IsClosed = false };
        fig.Segments.Add(new ArcSegment(P(135 + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
        return new System.Windows.Shapes.Path
        {
            Data = new PathGeometry(new[] { fig }), Stroke = brush, StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = size, Height = size,
        };
    }

    /// <summary><paramref name="baseColor"/> with <paramref name="amount"/> of <paramref name="tint"/> blended in (both opaque).</summary>
    static Color Mix(Color baseColor, Color tint, double amount) => Color.FromRgb(
        (byte)(baseColor.R + (tint.R - baseColor.R) * amount), (byte)(baseColor.G + (tint.G - baseColor.G) * amount), (byte)(baseColor.B + (tint.B - baseColor.B) * amount));

    /// <summary>Spreads capitals out a little (WPF has no letter spacing): "WIN RATE" → "W I N  R A T E" with hair spaces.</summary>
    static string Spaced(string caps) => string.Join("\u200A", caps.ToCharArray());

    /// <summary>
    /// A stat gauge: a plain dark card, the title in spaced capitals, a thick 270° arc on a faint track of its own colour
    /// with the number and an icon inside, then the verdict, a short line and why. No glows or tinted cards: the colour
    /// is only on the arc, the number and the verdict.
    /// </summary>
    FrameworkElement GaugeA(string title, string icon, double frac, string value, string unit, Color c, string word, string sub, string why)
    {
        const double size = 124, stroke = 15;
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = Spaced(Loc.T(title).ToUpper(CultureInfo.CurrentCulture)), FontSize = 12, FontWeight = FontWeights.Bold,
            Foreground = B(Color.FromRgb(0xE4, 0xE8, 0xEE)), HorizontalAlignment = HorizontalAlignment.Center,
        });

        // The gauge sits in a soft halo of its own colour (only around the ring, not the whole card).
        var stage = new Grid { Height = size + 8, Margin = new Thickness(0, 14, 0, 0) };
        // A canvas doesn't clip, so the halo can spread past the gauge's own box.
        var halo = new Canvas { Width = 0, Height = 0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var glow = new Ellipse
        {
            Width = size * 1.9, Height = size * 1.9,
            Fill = new RadialGradientBrush(new GradientStopCollection { new(A(c, 0x30), 0), new(A(c, 0x14), 0.45), new(A(c, 0x00), 1) }),
        };
        Canvas.SetLeft(glow, -size * 0.95);
        Canvas.SetTop(glow, -size * 0.95);
        halo.Children.Add(glow);
        stage.Children.Add(halo);
        var ring = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        ring.Children.Add(Ring(size, stroke, 1, B(Mix(Color.FromRgb(0x0D, 0x16, 0x20), c, 0.28))));
        if (!double.IsNaN(frac) && frac > 0) ring.Children.Add(Ring(size, stroke, frac, B(c)));
        var inner = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        var num = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        num.Children.Add(new TextBlock { Text = value, FontSize = 28, FontWeight = FontWeights.Bold, Foreground = B(c), FontFamily = (FontFamily)FindResource("DisplayFont") });
        if (unit.Length > 0) num.Children.Add(new TextBlock { Text = unit, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = B(c), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1, 0, 0, 5) });
        inner.Children.Add(num);
        inner.Children.Add(new Border { Margin = new Thickness(0, 1, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Child = Icon(icon, Color.FromRgb(0xEE, 0xF0, 0xF4), 27) });
        ring.Children.Add(inner);
        stage.Children.Add(ring);
        sp.Children.Add(stage);

        sp.Children.Add(new TextBlock { Text = Spaced(Loc.T(word).ToUpper(CultureInfo.CurrentCulture)), FontSize = 14, FontWeight = FontWeights.Bold, Foreground = B(c), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });
        if (sub.Length > 0)
            sp.Children.Add(new TextBlock { Text = Spaced(sub.ToUpper(CultureInfo.CurrentCulture)), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = B(Color.FromRgb(0x84, 0x8D, 0x99)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) });
        sp.Children.Add(new TextBlock
        {
            Text = why, FontSize = 12, Foreground = B(Color.FromRgb(0xB9, 0xAE, 0x98)), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Margin = new Thickness(8, 10, 8, 0), LineHeight = 17, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = why,
        });
        return new Border
        {
            Child = sp, Padding = new Thickness(16, 18, 16, 18), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(12), ClipToBounds = true,
            BorderBrush = B(Color.FromRgb(0x1E, 0x2A, 0x37)), BorderThickness = new Thickness(1),
            Background = B(Color.FromRgb(0x0D, 0x16, 0x20)),
        };
    }

    // ───────── header ─────────

    FrameworkElement Header(Profile p, string mainAgent, Tracker.MmrEstimate mmr)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var face = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 16, 0), BorderBrush = B(Color.FromRgb(0x2C, 0x2E, 0x36)), BorderThickness = new Thickness(2) };
        face.Background = MatchArt.AgentIcon(mainAgent) is { } art ? new ImageBrush(art) { Stretch = Stretch.UniformToFill } : B(Color.FromRgb(0x22, 0x22, 0x26));
        g.Children.Add(face);

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(mid, 1);
        var nameRow = new WrapPanel();
        nameRow.Children.Add(new TextBlock { Text = p.Name.Length > 0 ? p.Name : mainAgent ?? Loc.T("Unknown player"), FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        Border Tag(string t, Color c) => new()
        {
            CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = B(A(c, 0x80)), BorderThickness = new Thickness(1), Background = B(A(c, 0x14)),
            Child = new TextBlock { Text = Loc.T(t).ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.ExtraBold, Foreground = B(c) },
        };
        if (p.CheatChecks >= 2) nameRow.Children.Add(p.Cheat.Score >= 60 ? Tag("Repeated anomalies", Red) : p.Cheat.Score >= 30 ? Tag("Statistical outlier", Yellow) : Tag("● No anomalies", Green));
        if (p.Smurf.Score >= 60) nameRow.Children.Add(Tag("Likely smurf", Yellow));
        if (mmr?.Side == Tracker.MmrSide.Above) nameRow.Children.Add(Tag("MMR above rank", Purple));
        else if (mmr?.Side == Tracker.MmrSide.Below) nameRow.Children.Add(Tag("MMR below rank", Orange));
        mid.Children.Add(nameRow);
        var sub = new List<string>();
        if (mainAgent != null) sub.Add(Loc.F("Mostly {0}", mainAgent) + (Analysis.RoleOf(mainAgent) is { Length: > 0 } role ? " · " + Loc.T(role) : ""));
        if (p.Matches.Count > 0) sub.Add(Loc.F("last played {0}", p.Matches[0].StartUtc.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture)));
        if (p.FromYourMatches && !p.IsYou)
        {
            if (p.With > 0) sub.Add(Loc.F("{0}–{1} together", p.WithWins, p.With - p.WithWins));
            if (p.Against > 0) sub.Add(Loc.F("you went {0}–{1} against them", p.AgainstWins, p.Against - p.AgainstWins));
        }
        mid.Children.Add(new TextBlock { Text = string.Join(" · ", sub), FontSize = 12, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 2, 0, 0) });
        if (p.Name.Length == 0 && p.FromYourMatches && p.Matches.Any(m => VStore.Incomplete(m.Id)))
            mid.Children.Add(new TextBlock { Text = Loc.T(mainAgent == null
                    ? "Saved by an older Pulse that didn't keep Riot IDs or know this agent yet. Open VALORANT and Pulse downloads the match again with both."
                    : "Saved by an older Pulse that didn't keep Riot IDs. Open VALORANT and Pulse downloads the match again with their name."),
                FontSize = 11.5, Foreground = Res("TextFaintBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void Chip2(string label, string value)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 12, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 0, 6, 0) });
            sp.Children.Add(new TextBlock { Text = value, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush") });
            chips.Children.Add(new Border { Background = B(Color.FromRgb(0x1D, 0x1F, 0x25)), BorderBrush = B(Color.FromRgb(0x2A, 0x2D, 0x35)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 4), Margin = new Thickness(0, 0, 6, 0), Child = sp });
        }
        Chip2("Matches", p.Matches.Count.ToString(CultureInfo.CurrentCulture));
        Chip2("Rounds", p.Totals.Rounds.ToString("N0", CultureInfo.CurrentCulture));
        if (p.Level > 0) Chip2("Level", p.Level.ToString(CultureInfo.CurrentCulture));
        if (p.PlayMs > 0) Chip2("Playtime", TimeSpan.FromMilliseconds(p.PlayMs).TotalHours >= 1 ? Loc.F("{0:0} h", TimeSpan.FromMilliseconds(p.PlayMs).TotalHours) : Loc.F("{0:0} min", TimeSpan.FromMilliseconds(p.PlayMs).TotalMinutes));
        mid.Children.Add(chips);
        g.Children.Add(mid);

        var ranks = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(ranks, 2);
        bool unrated = p.Tier < 3;
        string curLabel = p.FromYourMatches && !p.IsYou ? "Last seen at" : unrated && p.LastTier >= 3 ? "Last ranked" : "Current";
        int curTier = unrated && p.LastTier >= 3 ? p.LastTier : p.Tier;
        string curSub = !unrated
            ? string.Join(" · ", new[] { p.Rr > 0 || p.Tier >= 3 ? $"{p.Rr} RR" : "", p.ActGames > 0 ? Loc.F("{0} games this act", p.ActGames) : "" }.Where(x => x.Length > 0))
            : p.FromYourMatches && !p.IsYou ? "" : Loc.T("No ranked games this act");
        ranks.Children.Add(RankBox(curLabel, curTier, curSub));
        if (p.Peak >= 3) ranks.Children.Add(RankBox("Peak", p.Peak, p.PeakAct));
        g.Children.Add(ranks);

        var card = Card2(g, Purple, 14);
        // A purple wash from the top left, like light on the portrait.
        if (card.Child is Grid cg)
            cg.Children.Insert(0, new Border { IsHitTestVisible = false, CornerRadius = new CornerRadius(14), Background = new RadialGradientBrush(A(Color.FromRgb(0x78, 0x5A, 0xFF), 0x33), A(Color.FromRgb(0x78, 0x5A, 0xFF), 0)) { Center = new Point(0, 0), GradientOrigin = new Point(0, 0), RadiusX = 0.55, RadiusY = 1.2 } });
        return card;
    }

    FrameworkElement RankBox(string label, int tier, string sub)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var c = RankColor(tier);
        if (MatchArt.RankIcon(tier) is { } icon)
            row.Children.Add(new Image { Source = icon, Width = 36, Height = 36, Margin = new Thickness(0, 0, 10, 0), Effect = Glow(c, 14, 0.45) }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(Caps(label, 10, B(Color.FromRgb(0x7C, 0x80, 0x8A))));
        t.Children.Add(new TextBlock { Text = tier >= 3 ? ValorantMatches.RankName(tier) : Loc.T("Unrated"), FontSize = 14.5, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 1, 0, 0) });
        if (!string.IsNullOrEmpty(sub)) t.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = Res("TextDimBrush") });
        row.Children.Add(t);
        return new Border
        {
            CornerRadius = new CornerRadius(12), Background = B(Color.FromRgb(0x1A, 0x1B, 0x20)), BorderBrush = B(A(c, 0x40)), BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0), MinWidth = 180, Child = row,
        };
    }

    // ───────── skills ─────────

    static string Fmt(SkillScore s)
    {
        var v = Tracker.Shown(s);
        if (double.IsNaN(v)) return "–";
        return s.Skill.Unit == "%" ? v.ToString("0.#", CultureInfo.CurrentCulture) : v < 10 ? v.ToString("0.0", CultureInfo.CurrentCulture) : v.ToString("0", CultureInfo.CurrentCulture);
    }

    static string UnitOf(SkillScore s) => s.Skill.Unit switch { "%" => "%", "/100" => "/100", "" => s.Skill.Id is "utility" or "abil" ? "/round" : "", var u => u };

    FrameworkElement SkillTile2(SkillScore s, int compareTier, string likeWord)
    {
        bool known = !double.IsNaN(s.Value);
        var c = known && s.PlaysLikeTier > 0 ? RankColor(s.PlaysLikeTier) : Grey;
        var sp = new StackPanel();
        var head = new Grid();
        head.Children.Add(new TextBlock { Text = Loc.T(s.Skill.Name).ToUpper(CultureInfo.CurrentCulture), FontSize = 12, FontWeight = FontWeights.ExtraBold, Foreground = B(Color.FromRgb(0xC3, 0xC6, 0xCD)), ToolTip = Loc.T(s.Skill.Explains), VerticalAlignment = VerticalAlignment.Center });
        if (known && !double.IsNaN(s.TopPercent))
            head.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right, CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 3), BorderThickness = new Thickness(1),
                BorderBrush = B(A(c, 0x66)), Background = B(A(c, 0x1F)),
                Child = new TextBlock { Text = Loc.F("TOP {0:0}%", s.TopPercent), FontSize = 10.5, FontWeight = FontWeights.ExtraBold, Foreground = B(c) },
            });
        sp.Children.Add(head);
        var val = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        val.Children.Add(new TextBlock { Text = Fmt(s), FontSize = 22, FontWeight = FontWeights.ExtraBold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont") });
        if (known) val.Children.Add(new TextBlock { Text = UnitOf(s), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B(Color.FromRgb(0x8E, 0x92, 0x9B)), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 0, 4) });
        sp.Children.Add(val);

        // The bar, with a mark where players at the compared rank usually are.
        double max = Tracker.BarMax(s.Skill.Id, s.Value);
        var bar = new Grid { Height = 18 };
        bar.Children.Add(new Border { Height = 6, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(4), Background = B(Color.FromRgb(0x24, 0x26, 0x2D)) });
        var fill = new Border { Height = 6, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(4), Width = 0,
            Background = new LinearGradientBrush(A(c, 0x59), c, 0), Effect = Glow(c, 12, 0.5) };
        bar.Children.Add(fill);
        var mark = new Border { Width = 2, Height = 16, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, -4, 0, 0), Background = B(Color.FromRgb(0xEC, 0xED, 0xEF)), CornerRadius = new CornerRadius(1), Visibility = Visibility.Collapsed };
        var markText = new TextBlock { Text = Loc.T("rank avg"), FontSize = 9, Foreground = B(Color.FromRgb(0x6B, 0x6F, 0x78)), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        bar.Children.Add(mark);
        bar.Children.Add(markText);
        double avg = compareTier >= 3 ? Tracker.MedianAt(s.Skill.Id, compareTier) : double.NaN;
        bar.SizeChanged += (_, e) =>
        {
            double w = e.NewSize.Width;
            if (known) fill.Width = Math.Max(6, w * Math.Clamp(s.Value / max, 0, 1));
            if (!double.IsNaN(avg))
            {
                double x = w * Math.Clamp(avg / max, 0, 1);
                mark.Margin = new Thickness(x - 1, -4, 0, 0); mark.Visibility = Visibility.Visible;
                markText.Margin = new Thickness(Math.Max(0, x - 20), 0, 0, 0); markText.Visibility = Visibility.Visible;
            }
        };
        sp.Children.Add(bar);

        // Plays like.
        var like = new StackPanel { Orientation = Orientation.Horizontal };
        if (known && s.PlaysLikeTier > 0 && MatchArt.RankIcon(s.PlaysLikeTier) is { } ri)
            like.Children.Add(new Image { Source = ri, Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0), Effect = Glow(c, 10, 0.6) }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality)));
        var lt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        lt.Children.Add(new TextBlock { Text = Loc.T(likeWord).ToUpper(CultureInfo.CurrentCulture), FontSize = 9.5, FontWeight = FontWeights.ExtraBold, Foreground = B(Color.FromRgb(0x8E, 0x92, 0x9B)) });
        lt.Children.Add(new TextBlock
        {
            Text = !known ? Loc.T("Not enough data yet") : s.PlaysLikeTier > 0 ? ValorantMatches.RankName(s.PlaysLikeTier) : Loc.T("Needs more ranked players"),
            FontSize = known && s.PlaysLikeTier > 0 ? 12.5 : 11, FontWeight = FontWeights.ExtraBold, Foreground = known && s.PlaysLikeTier > 0 ? B(c) : Res("TextFaintBrush"),
        });
        like.Children.Add(lt);
        sp.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 4, 0, 0), BorderThickness = new Thickness(1),
            BorderBrush = B(A(c, 0x47)), Background = new LinearGradientBrush(A(c, 0x29), A(c, 0x08), 0), Child = like,
        });

        var tile = new Grid();
        tile.Children.Add(new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = B(c), Effect = Glow(c, 14, 0.7) });
        tile.Children.Add(new Border { Padding = new Thickness(14, 10, 14, 10), Child = sp });
        return new Border
        {
            CornerRadius = new CornerRadius(12), Background = B(Color.FromRgb(0x17, 0x18, 0x1D)), BorderBrush = B(Color.FromRgb(0x24, 0x26, 0x2D)), BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 10, 10), ClipToBounds = true, Child = tile,
        };
    }

    FrameworkElement RatingPanel(Rating r)
    {
        var sp = new StackPanel();
        sp.Children.Add(Caps("Impact", 10.5, B(Purple)));
        sp.Children.Add(new TextBlock
        {
            Text = r == null ? "–" : r.Overall.ToString("0.00", CultureInfo.CurrentCulture), FontSize = 42, FontWeight = FontWeights.ExtraBold, Foreground = Brushes.White,
            FontFamily = (FontFamily)FindResource("DisplayFont"), Effect = Glow(Purple, 22, 0.55), Margin = new Thickness(0, 0, 0, 0),
        });
        sp.Children.Add(new TextBlock
        {
            Text = (r != null && !double.IsNaN(r.TopPercent) ? Loc.F("Top {0:0}%", r.TopPercent) + " · " : "") + Loc.T("1.00 is average for their own lobbies; Top % also weighs how strong those lobbies are"),
            FontSize = 11.5, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });
        void Side(string label, double v)
        {
            var g = new Grid { Margin = new Thickness(0, 5, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            g.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 12, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center });
            var track = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = B(Color.FromRgb(0x26, 0x28, 0x2F)) });
            var f = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Background = new LinearGradientBrush(Color.FromRgb(0x7C, 0x5C, 0xFF), Purple, 0) };
            track.Children.Add(f);
            track.SizeChanged += (_, e) => { if (!double.IsNaN(v)) f.Width = e.NewSize.Width * Math.Clamp(v / 2, 0, 1); };
            Grid.SetColumn(track, 1); g.Children.Add(track);
            var t = new TextBlock { Text = double.IsNaN(v) ? "–" : v.ToString("0.00", CultureInfo.CurrentCulture), FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(t, 2); g.Children.Add(t);
            sp.Children.Add(g);
        }
        Side("Attack", r?.Attack ?? double.NaN);
        Side("Defense", r?.Defense ?? double.NaN);
        var g2 = new Grid();
        g2.Children.Add(new Border { CornerRadius = new CornerRadius(12), Background = new RadialGradientBrush(A(Purple, 0x38), A(Purple, 0)) { Center = new Point(0.3, 0), GradientOrigin = new Point(0.3, 0), RadiusX = 0.8, RadiusY = 0.9 } });
        g2.Children.Add(new Border { Padding = new Thickness(14), Child = sp });
        return new Border { CornerRadius = new CornerRadius(12), Background = B(Color.FromRgb(0x17, 0x18, 0x1D)), BorderBrush = B(Color.FromRgb(0x2E, 0x2A, 0x3D)), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 10), Child = g2, ClipToBounds = true };
    }

    // ───────── stat tiles ─────────

    FrameworkElement StatTile(SkillScore s, Color c, int compareTier, bool workOn)
    {
        bool known = !double.IsNaN(s.Value);
        var sp = new StackPanel();
        sp.Children.Add(Caps(s.Skill.Name, 10.5, B(Color.FromRgb(0x9A, 0x9D, 0xA6))));
        var val = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 10) };
        val.Children.Add(new TextBlock { Text = Fmt(s), FontSize = 21, FontWeight = FontWeights.ExtraBold, Foreground = known ? B(c) : Res("TextFaintBrush"), FontFamily = (FontFamily)FindResource("DisplayFont") });
        if (known && s.Skill.Unit.Length > 0) val.Children.Add(new TextBlock { Text = s.Skill.Unit, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = B(A(c, 0xBF)), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 0, 5) });
        sp.Children.Add(val);
        var segs = new UniformGrid { Columns = 10, Height = 5 };
        int lit = known && !double.IsNaN(s.TopPercent) ? (int)Math.Round((100 - s.TopPercent) / 10) : 0;
        for (int i = 0; i < 10; i++)
            segs.Children.Add(new Border { Margin = new Thickness(0, 0, 3, 0), CornerRadius = new CornerRadius(2), Background = i < lit ? B(c) : B(Color.FromRgb(0x26, 0x28, 0x2F)) });
        sp.Children.Add(segs);
        var tags = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        Border Tag(string t, Color col) => new()
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 0, 6, 0), BorderThickness = new Thickness(1), BorderBrush = B(A(col, 0x80)),
            Child = new TextBlock { Text = t, FontSize = 10, FontWeight = FontWeights.ExtraBold, Foreground = B(col) },
        };
        if (!known) tags.Children.Add(new TextBlock { Text = Loc.T("Not enough data yet"), FontSize = 11, Foreground = Res("TextFaintBrush") });
        else
        {
            if (!double.IsNaN(s.TopPercent)) tags.Children.Add(Tag(Loc.F("TOP {0:0}%", s.TopPercent), c));
            if (s.PlaysLikeTier > 0) tags.Children.Add(Tag("// " + Loc.F("{0} LEVEL", ValorantMatches.RankName(s.PlaysLikeTier).Split(' ')[0].ToUpper(CultureInfo.CurrentCulture)), c));
            if (workOn) tags.Children.Add(Tag(Loc.T("COACH: WORK ON THIS"), Red));
        }
        sp.Children.Add(tags);
        var g = new Grid();
        g.Children.Add(new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, Background = new LinearGradientBrush(c, A(c, 0), 0) });
        g.Children.Add(new Border { Padding = new Thickness(13, 10, 13, 10), Child = sp });
        return new Border { CornerRadius = new CornerRadius(12), Background = B(Color.FromRgb(0x17, 0x18, 0x1D)), BorderBrush = B(Color.FromRgb(0x24, 0x26, 0x2D)), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 10), ClipToBounds = true, Child = g, ToolTip = Loc.T(s.Skill.Explains) };
    }

    /// <summary>A section title in the cards: a heading on the left, a faint note on the right.</summary>
    FrameworkElement SectionHead(string title, string note, Color? accentWord = null, string accent = null)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        var t = new TextBlock { FontSize = 15.5, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont") };
        t.Inlines.Add(new System.Windows.Documents.Run(Loc.T(title)));
        if (accent != null) t.Inlines.Add(new System.Windows.Documents.Run(" " + Loc.T(accent)) { Foreground = B(accentWord ?? Blue) });
        g.Children.Add(t);
        if (!string.IsNullOrEmpty(note)) g.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = Res("TextFaintBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
        return g;
    }

    // ───────── the whole profile ─────────

    /// <summary>
    /// A profile, top to bottom: the header and tracker.gg's tab bar; on Overview, Pulse's four gauges, the acts and
    /// skills, then the tracker (playlist and act buttons, side rail, act overview, top agents, last matches). The
    /// other tabs show one thing each (TrackerBody), so nothing needs a long scroll.
    /// </summary>
    IEnumerable<FrameworkElement> ProfileView(Profile p)
    {
        var mainAgent = p.Matches.Select(m => m.Players.FirstOrDefault(x => x.Id == p.Puuid)?.Agent).Where(a => !string.IsNullOrEmpty(a))
            .GroupBy(a => a).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var mine = p.IsYou ? CoachMatches() : null;
        var mmr = p.IsYou ? Tracker.Mmr(mine, _trRr) : null;
        if (_trView == "insights" && !p.IsYou) _trView = "overview";
        yield return Header(p, mainAgent, mmr);
        yield return ProfileNav(p);

        if (p.Matches.Count == 0 && ProfileMatches(p).Count == 0)
        {
            yield return Card2(Dim(Loc.T("No competitive, unrated, premier or swiftplay matches to measure yet."), 12.5, 0), Grey);
            yield break;
        }

        if (_trView == "overview" && p.Matches.Count > 0)
        {
            // Gauges.
            var t = p.Totals;
            var gauges = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -10, 0) };
            var cheatC = p.Cheat.Score >= 60 ? Red : p.Cheat.Score >= 30 ? Yellow : Green;
            gauges.Children.Add(GaugeA("Cheating signals", IconShield, p.CheatChecks == 0 ? double.NaN : p.Cheat.Score / 100.0, p.CheatChecks == 0 ? "–" : $"{p.Cheat.Score}", p.CheatChecks == 0 ? "" : "%", cheatC,
                p.CheatChecks < 2 ? "Insufficient evidence" : p.Cheat.Score >= 60 ? "Repeated anomalies" : p.Cheat.Score >= 30 ? "Statistical outlier" : p.Cheat.Score > 0 ? "Unusual performance" : "No anomalies",
                Loc.F("{0} of {1} signals", p.Cheat.Signs.Count, Math.Max(p.CheatChecks, 4)),
                p.Cheat.Signs.Count > 0 ? string.Join(" · ", p.Cheat.Signs) : p.CheatChecks == 0 ? Loc.T("Needs about 5 matches to check.") : Loc.F("Nothing unusual for {0}.", p.CompareTier >= 3 ? ValorantMatches.RankName(p.CompareTier) : Loc.T("their rank"))));
            var smC = p.Smurf.Score >= 60 ? Red : p.Smurf.Score >= 35 ? Yellow : Green;
            gauges.Children.Add(GaugeA("Smurf chance", IconMask, p.Smurf.Score / 100.0, $"{p.Smurf.Score}", "%", smC,
                p.Smurf.Score >= 60 ? "Likely smurf" : p.Smurf.Score >= 35 ? "Possible smurf" : p.Smurf.Score >= 15 ? "Unusual, inconclusive" : "Consistent with rank",
                Loc.F("{0} signs", p.Smurf.Signs.Count),
                p.Smurf.Signs.Count > 0 ? string.Join(" · ", p.Smurf.Signs) : Loc.T("Nothing unusual about this account")));
            var maps = p.Matches.GroupBy(m => m.Map).Where(g => g.Count() >= 2).Select(g => (Map: g.Key, Win: 100.0 * g.Count(m => WonFor(m, p.Puuid)) / g.Count())).OrderByDescending(x => x.Win).ToList();
            var winC = t.WinPct >= 55 ? Green : t.WinPct >= 45 ? Yellow : Red;
            gauges.Children.Add(GaugeA("Win rate", IconTrophy, t.WinPct / 100, $"{t.WinPct:0}", "%", winC,
                t.WinPct >= 55 ? "Climbing" : t.WinPct <= 45 ? "Struggling" : "Even", Loc.F("{0} won of {1}", t.Wins, t.Matches),
                maps.Count >= 2 ? Loc.F("Best map {0} {1:0}% · weakest {2} {3:0}%", maps[0].Map, maps[0].Win, maps[^1].Map, maps[^1].Win) : Loc.T("Best and worst maps show after two games on each.")));
            var kdC = t.KdRatio >= 1.4 ? Gold : t.KdRatio >= 1.2 ? Blue : t.KdRatio >= 1.0 ? Green : t.KdRatio >= 0.8 ? Grey : Red;
            var acsTop = p.Stats.FirstOrDefault(x => x.Skill.Id == "acs");
            gauges.Children.Add(GaugeA("K/D", IconCross, t.KdRatio / 2, t.KdRatio.ToString("0.00", CultureInfo.CurrentCulture), "", kdC,
                t.KdRatio >= 1.4 ? "Elite" : t.KdRatio >= 1.2 ? "Strong" : t.KdRatio >= 1.0 ? "Positive" : t.KdRatio >= 0.8 ? "Average" : "Below average", Loc.F("{0} kills · {1} deaths", t.Kills, t.Deaths),
                (acsTop != null && !double.IsNaN(acsTop.TopPercent) ? Loc.F("Top {0:0}% in your matches", acsTop.TopPercent) + " · " : "") + Loc.F("{0:0} ACS", t.Acs)));
            yield return gauges;
            yield return ActsStrip(p);

            // Skills.
            var sk = new StackPanel();
            int scaleN = VStore.All.Sum(m => m.Players.Count);
            sk.Children.Add(SectionHead("Skills", Loc.F("{0} matches · compared with {1:N0} players in your games", p.Matches.Count, scaleN)));
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(RatingPanel(p.Rating));
            var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -10, 0) };
            var words = new Dictionary<string, string> { ["aim"] = "Aims like", ["positioning"] = "Positions like", ["utility"] = "Utility like", ["opening"] = "Duels like", ["clutch"] = "Clutches like", ["trading"] = "Trades like" };
            foreach (var id in new[] { "aim", "positioning", "utility", "opening", "clutch", "trading" })
                if (p.Skills.FirstOrDefault(x => x.Skill.Id == id) is { } s) grid.Children.Add(SkillTile2(s, p.CompareTier, words[id]));
            Grid.SetColumn(grid, 1);
            row.Children.Add(grid);
            sk.Children.Add(row);
            yield return Card2(sk, Purple);
        }

        foreach (var e in TrackerBody(p, mmr)) yield return e;
    }

    /// <summary>A section that can't show yet: what it needs, and (when Pulse has few of your matches) a button to fetch them.</summary>
    FrameworkElement Waiting(string title, string text)
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading(title));
        sp.Children.Add(Dim(Loc.T(text), 12.5, 6));
        int have = CoachMatches().Count;
        if (have < 20)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var get = Btn(_downloading ? "Downloading…" : "Get my recent matches", true);
            get.IsEnabled = !_downloading;
            get.Click += (_, _) => _ = Download(true);
            row.Children.Add(get);
            row.Children.Add(Faint(_downloadText ?? Loc.F("Pulse has {0} of your matches.", have), 11.5, 0).Also(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(10, 0, 0, 0); }));
            sp.Children.Add(row);
        }
        return sp;
    }
}
