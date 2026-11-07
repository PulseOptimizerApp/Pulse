using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Pulse.Core.Guide;
using Loc = Pulse.Ui.Loc;

namespace Pulse.Pages;

/// <summary>
/// The PC guide: what helps most (with buttons into the Pulse page that does it), what GPUs and RAM cost, the gear and
/// prebuilt PCs reviewers rate best, and hardware news. Content lives in <see cref="GuideData"/>.
/// </summary>
public partial class GuidePage : UserControl
{
    readonly List<(UniformGrid Grid, int Wide, int Narrow)> _grids = new();
    readonly Dictionary<string, FrameworkElement> _sections = new();

    public GuidePage()
    {
        InitializeComponent();
        var days = (DateTime.Today - GuideData.Updated).Days;
        FreshText.Text = days <= 0 ? Loc.F("Updated {0}", GuideData.Updated.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture))
            : Loc.F("Updated {0} days ago", days);

        Section("smooth", "Make it smoother", "What actually makes games smoother", "Most impact first. Where Pulse can check or do it for you, there's a button.", BuildTips());
        Section("prices", "What things cost", "Graphics card and RAM prices right now", "AI data centers are buying most of the world's memory, so prices are far above launch. Here's what to expect in the US.", BuildPrices());
        Section("gear", "Best gear", "What reviewers rate best", "Picks from independent reviews, linked so you can read the testing.", BuildGear());
        Section("prebuilt", "Prebuilt PCs", "The best places to buy a ready-made PC", "Buying prebuilt can be the better deal while parts are expensive. These makers come up again and again in reviews.", BuildPrebuilt());
        Section("news", "News", "What's happening with PC hardware", "The stories that change what you should buy and when.", BuildNews());

        foreach (var (key, title) in new[] { ("smooth", "Make it smoother"), ("prices", "Prices"), ("gear", "Best gear"), ("prebuilt", "Prebuilt PCs"), ("news", "News") })
        {
            var chip = new RadioButton { Style = (Style)FindResource("Chip"), Content = Loc.T(title), GroupName = "guideTabs", IsChecked = key == "smooth" };
            chip.Checked += (_, _) => ScrollTo(key);
            Tabs.Children.Add(chip);
        }

        Body.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Body"), FontSize = 12, Margin = new Thickness(2, 6, 0, 0), Foreground = Res("TextFaintBrush"),
            Text = Loc.T("Prices are US prices from public trackers and change daily. Pulse isn't paid by any brand in this guide, and links have no affiliate codes."),
        });
        SizeChanged += (_, _) => Fit();
    }

    // ───────── layout helpers ─────────

    Brush Res(string key) => (Brush)FindResource(key);

    void Fit()
    {
        bool narrow = ActualWidth < 980;
        foreach (var (g, wide, nar) in _grids) g.Columns = narrow ? nar : wide;
    }

    UniformGrid Cols(int wide, int narrow)
    {
        var g = new UniformGrid { Columns = wide, Margin = new Thickness(-6, 0, -6, 0) };
        _grids.Add((g, wide, narrow));
        return g;
    }

    void ScrollTo(string key)
    {
        if (!_sections.TryGetValue(key, out var el)) return;
        var y = el.TranslatePoint(new Point(0, 0), Body).Y;
        Scroller.ScrollToVerticalOffset(Math.Max(0, y - 4));
    }

    void Section(string key, string eyebrow, string title, string sub, UIElement content)
    {
        var sp = new StackPanel { Margin = new Thickness(0, _sections.Count == 0 ? 4 : 34, 0, 0) };
        sp.Children.Add(new TextBlock { Text = Loc.T(eyebrow).ToUpper(CultureInfo.CurrentCulture), FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = Res("AccentBrush"), Margin = new Thickness(0, 0, 0, 6) });
        sp.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 21, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)FindResource("DisplayFont"), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Style = (Style)FindResource("Body"), Text = Loc.T(sub), Margin = new Thickness(0, 4, 0, 14), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left });
        sp.Children.Add(content);
        _sections[key] = sp;
        Body.Children.Add(sp);
    }

    Border Card(UIElement child, double pad = 18) => new()
    {
        Style = (Style)FindResource("HoverCard"), Padding = new Thickness(pad), Margin = new Thickness(6, 0, 6, 12), Child = child,
    };

    TextBlock Title(string text, double size = 14.5) => new() { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextBrush") };
    TextBlock Text(string text, double top = 4) => new() { Style = (Style)FindResource("Body"), Text = text, Margin = new Thickness(0, top, 0, 0) };

    Border Pill(string text, Color c)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 8, 3), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromArgb(0x24, c.R, c.G, c.B)),
            Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(c) },
        };
    }

    static readonly Color Cyan = Color.FromRgb(0x22, 0xD3, 0xEE), Blue = Color.FromRgb(0x5B, 0x8C, 0xFF), Violet = Color.FromRgb(0x8B, 0x5C, 0xF6),
        Green = Color.FromRgb(0x34, 0xD3, 0x99), Amber = Color.FromRgb(0xFB, 0xBF, 0x24), Red = Color.FromRgb(0xF8, 0x71, 0x71), Grey = Color.FromRgb(0x8A, 0x8A, 0x93);

    /// <summary>A small "Source ↗" link that opens the article in the browser.</summary>
    FrameworkElement Source(string name, string url)
    {
        if (string.IsNullOrEmpty(url)) return new Border();
        var b = new Button { Style = (Style)FindResource("LinkButton"), Content = name + " ↗", FontSize = 12, ToolTip = url, HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => Open(url);
        return b;
    }

    static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ───────── 1. What helps most ─────────

    UIElement BuildTips()
    {
        var root = new StackPanel();
        var grid = Cols(2, 1);
        int n = 0;
        foreach (var t in GuideData.Tips)
        {
            n++;
            var impact = t.Impact switch { "Huge" => Green, "High" => Cyan, "Medium" => Blue, _ => Grey };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var num = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Background = Res("Surface2Brush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = n.ToString(CultureInfo.InvariantCulture), FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0),
            };
            head.Children.Add(num);
            var title = Title(Loc.T(t.Title), 15);
            title.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(title, 1);
            head.Children.Add(title);
            var pill = Pill(Loc.F("{0} impact", Loc.T(t.Impact)), impact);
            pill.Margin = new Thickness(10, 0, 0, 0);
            pill.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(pill, 2);
            head.Children.Add(pill);

            var sp = new StackPanel();
            sp.Children.Add(head);
            var body = Text(Loc.T(t.Body), 10);
            body.Margin = new Thickness(42, 8, 0, 0);
            sp.Children.Add(body);
            var actions = new WrapPanel { Margin = new Thickness(42, 12, 0, 0) };
            if (t.PulsePage != null)
            {
                var go = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T(t.PulseAction), Padding = new Thickness(12, 5, 12, 5), FontSize = 12.5, Margin = new Thickness(0, 0, 12, 0) };
                var page = t.PulsePage;
                go.Click += (_, _) => MainWindow.Instance.Navigate(page);
                actions.Children.Add(go);
            }
            if (t.SourceUrl != null)
            {
                var src = Source(t.SourceName, t.SourceUrl);
                src.VerticalAlignment = VerticalAlignment.Center;
                actions.Children.Add(src);
            }
            if (actions.Children.Count > 0) sp.Children.Add(actions);
            grid.Children.Add(Card(sp));
        }
        root.Children.Add(grid);

        // Popular "boosts" that don't help.
        root.Children.Add(new TextBlock { Text = Loc.T("Skip these"), FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 10) });
        var skip = Cols(3, 1);
        foreach (var (title, body) in GuideData.Skip)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(0x24, Red.R, Red.G, Red.B)),
                Child = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = new SolidColorBrush(Red), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            var sp = new StackPanel();
            sp.Children.Add(Title(Loc.T(title), 14));
            sp.Children.Add(Text(Loc.T(body)));
            Grid.SetColumn(sp, 1);
            row.Children.Add(sp);
            skip.Children.Add(Card(row, 16));
        }
        root.Children.Add(skip);
        return root;
    }

    // ───────── 2. Prices ─────────

    UIElement BuildPrices()
    {
        var root = new StackPanel();

        // Three headline numbers.
        var stats = Cols(3, 1);
        void Stat(string label, string now, string was, Color c)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 12, Foreground = Res("TextDimBrush") });
            sp.Children.Add(new TextBlock { Text = now, FontSize = 26, FontWeight = FontWeights.Bold, FontFamily = (FontFamily)FindResource("DisplayFont"), Foreground = new SolidColorBrush(c), Margin = new Thickness(0, 4, 0, 0) });
            sp.Children.Add(new TextBlock { Text = was, FontSize = 12, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 2, 0, 0) });
            stats.Children.Add(Card(sp));
        }
        Stat("32 GB DDR5 kit", "$429 – $600", Loc.T("About $95 before the shortage"), Red);
        Stat("Radeon RX 9070 XT", "$919", Loc.F("Launched at {0}", "$599"), Amber);
        Stat("GeForce RTX 5070 Ti", "$1,399", Loc.F("Launched at {0}", "$749"), Amber);
        root.Children.Add(stats);

        // Graphics cards: launch price vs now, with a bar showing how far above launch each one sells.
        var gpu = new StackPanel();
        var hdr = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.6, GridUnitType.Star) });
        string[] heads = { "Graphics card", "Launch price", "Best price now", "Above launch price" };
        for (int i = 0; i < heads.Length; i++)
        {
            var h = new TextBlock { Text = Loc.T(heads[i]).ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Res("TextFaintBrush") };
            Grid.SetColumn(h, i);
            hdr.Children.Add(h);
        }
        gpu.Children.Add(hdr);
        int maxPct = GuideData.Gpus.Max(g => (g.Now - g.Msrp) * 100 / g.Msrp);
        foreach (var g in GuideData.Gpus)
        {
            int pct = (g.Now - g.Msrp) * 100 / g.Msrp;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 0), MinHeight = 40 };
            foreach (var w in new[] { 2.2, 1, 1, 2.6 }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Text = g.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
            name.Children.Add(new TextBlock { Text = g.Tier, FontSize = 11.5, Foreground = Res("TextFaintBrush") });
            row.Children.Add(name);
            var msrp = new TextBlock { Text = Money(g.Msrp), FontSize = 13.5, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(msrp, 1);
            row.Children.Add(msrp);
            var now = new TextBlock { Text = Money(g.Now), FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(now, 2);
            row.Children.Add(now);
            var c = pct <= 10 ? Green : pct <= 50 ? Amber : Red;
            var bar = new Grid { VerticalAlignment = VerticalAlignment.Center };
            bar.ColumnDefinitions.Add(new ColumnDefinition());
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            var track = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Res("Surface2Brush") });
            track.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(c),
                Tag = Math.Max(0.03, pct / (double)Math.Max(1, maxPct)),
            });
            track.SizeChanged += (s, _) =>
            {
                var t = (Grid)s;
                if (t.Children[1] is Border fill && fill.Tag is double share) fill.Width = t.ActualWidth * share;
            };
            bar.Children.Add(track);
            var pctText = new TextBlock { Text = pct <= 0 ? Loc.T("At launch") : "+" + pct + "%", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(c), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(pctText, 1);
            bar.Children.Add(pctText);
            Grid.SetColumn(bar, 3);
            row.Children.Add(bar);
            row.ToolTip = Loc.F("Lowest price ever seen: {0}", Money(g.Lowest));
            gpu.Children.Add(row);
            gpu.Children.Add(new Border { Height = 1, Background = Res("BorderBrush"), Opacity = 0.6 });
        }
        var gpuFoot = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        gpuFoot.Children.Add(new TextBlock { Text = Loc.T("Hover a card for its lowest price ever. ") + Loc.T("Source:") + " ", FontSize = 12, Foreground = Res("TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center });
        gpuFoot.Children.Add(Source(GuideData.GpuSource, GuideData.GpuSourceUrl));
        gpu.Children.Add(gpuFoot);
        var gpuCard = Card(gpu, 20);
        gpuCard.Margin = new Thickness(0, 0, 0, 12);
        root.Children.Add(gpuCard);

        // RAM kits.
        var ram = Cols(3, 2);
        foreach (var r in GuideData.Ram)
        {
            var sp = new StackPanel();
            var top = new Grid();
            top.Children.Add(new TextBlock { Text = r.Kit, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
            var tag = Pill(r.Ddr5 ? "DDR5" : "DDR4", r.Ddr5 ? Violet : Blue);
            tag.HorizontalAlignment = HorizontalAlignment.Right;
            top.Children.Add(tag);
            sp.Children.Add(top);
            sp.Children.Add(new TextBlock { Text = r.Now, FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 0) });
            if (!string.IsNullOrEmpty(r.Before))
                sp.Children.Add(new TextBlock { Text = Loc.F("Was {0} before the shortage", r.Before), FontSize = 12, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 2, 0, 0) });
            ram.Children.Add(Card(sp, 16));
        }
        root.Children.Add(ram);
        var ramFoot = new WrapPanel { Margin = new Thickness(0, 0, 0, 0) };
        ramFoot.Children.Add(new TextBlock { Text = Loc.T("Source:") + " ", FontSize = 12, Foreground = Res("TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center });
        ramFoot.Children.Add(Source(GuideData.RamSource, GuideData.RamSourceUrl));
        root.Children.Add(ramFoot);

        // How to upgrade without paying shortage prices.
        var tips = new StackPanel();
        tips.Children.Add(Title(Loc.T("Upgrading without paying shortage prices"), 15));
        foreach (var t in GuideData.UpgradeTips) tips.Children.Add(Check(Loc.T(t)));
        var tipsCard = Card(tips, 20);
        tipsCard.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(tipsCard);
        return root;
    }

    static string Money(int v) => "$" + v.ToString("N0", CultureInfo.InvariantCulture);

    FrameworkElement Check(string text)
    {
        var g = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, Foreground = new SolidColorBrush(Green), Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top });
        var t = Text(text, 0);
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        return g;
    }

    // ───────── 3. Best gear ─────────

    UIElement BuildGear()
    {
        var root = new StackPanel();
        foreach (var group in GuideData.Gear)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, root.Children.Count == 0 ? 0 : 10, 0, 10) };
            head.Children.Add(new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Background = Res("Surface2Brush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = group.Glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            head.Children.Add(new TextBlock { Text = Loc.T(group.Title), FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
            root.Children.Add(head);
            var grid = Cols(4, 2);
            foreach (var p in group.Picks)
            {
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = Loc.T(p.Label).ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Res("AccentBrush") });
                sp.Children.Add(Title(p.Name, 14));
                sp.Children[1].SetValue(MarginProperty, new Thickness(0, 6, 0, 0));
                sp.Children.Add(Text(Loc.T(p.Why), 4));
                var src = Source(p.SourceName, p.SourceUrl);
                src.Margin = new Thickness(0, 10, 0, 0);
                sp.Children.Add(src);
                grid.Children.Add(Card(sp, 16));
            }
            root.Children.Add(grid);
        }
        return root;
    }

    // ───────── 4. Prebuilt PCs ─────────

    UIElement BuildPrebuilt()
    {
        var root = new StackPanel();
        var grid = Cols(3, 1);
        foreach (var b in GuideData.Builders)
        {
            var sp = new StackPanel();
            sp.Children.Add(Title(b.Name, 15));
            var good = Pill(Loc.T(b.GoodFor), Green);
            good.Margin = new Thickness(0, 8, 0, 0);
            sp.Children.Add(good);
            sp.Children.Add(Text(Loc.T(b.Example), 10));
            var watch = new TextBlock { Text = Loc.F("Watch for: {0}", Loc.T(b.Watch)), FontSize = 12, Foreground = new SolidColorBrush(Amber), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            sp.Children.Add(watch);
            var src = Source(b.SourceName, b.SourceUrl);
            src.Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(src);
            grid.Children.Add(Card(sp));
        }
        root.Children.Add(grid);

        var checks = new StackPanel();
        checks.Children.Add(Title(Loc.T("Before you buy a prebuilt, check for"), 15));
        foreach (var c in GuideData.PrebuiltChecks) checks.Children.Add(Check(Loc.T(c)));
        var card = Card(checks, 20);
        card.Margin = new Thickness(0, 2, 0, 0);
        root.Children.Add(card);
        return root;
    }

    // ───────── 5. News ─────────

    UIElement BuildNews()
    {
        var grid = Cols(2, 1);
        foreach (var n in GuideData.News)
        {
            var sp = new StackPanel();
            sp.Children.Add(Pill(Loc.T(n.Tag).ToUpper(CultureInfo.CurrentCulture), n.Hot ? Red : Grey));
            var t = Title(Loc.T(n.Title), 15);
            t.Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(t);
            sp.Children.Add(Text(Loc.T(n.Body), 6));
            var src = Source(n.SourceName, n.SourceUrl);
            src.Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(src);
            grid.Children.Add(Card(sp));
        }
        return grid;
    }
}
