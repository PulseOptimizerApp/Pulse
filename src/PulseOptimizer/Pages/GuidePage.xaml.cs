using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Pulse.Core.Guide;
using Pulse.Ui;
using Loc = Pulse.Ui.Loc;

namespace Pulse.Pages;

/// <summary>
/// The PC guide, one tab at a time: step-by-step guides, hardware lookup and a PC builder with expected frame rates, what makes games smoother, what hardware costs (with charts), the gear reviewers
/// rate best, prebuilt PC makers, and hardware news. Content comes from <see cref="GuideLive"/> (Pulse's website),
/// so prices and news stay current between Pulse releases.
/// </summary>
public partial class GuidePage : UserControl, IPage
{
    static string _tab = "guides";
    static readonly Dictionary<string, string> SubTab = new();
    readonly Dictionary<string, (TextBlock Label, Border Line)> _tabs = new();

    static GuideContent G => GuideLive.Current;
    static string T(string s) => G.T(s);
    static string F(string pattern, params object[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, T(pattern), args); } catch { return string.Format(pattern, args); }
    }

    static readonly Color Green = GuideChart.Green, Amber = GuideChart.Amber, Red = GuideChart.Red, Cyan = GuideChart.Cyan, Violet = GuideChart.Violet,
        Blue = Color.FromRgb(0xE0, 0x77, 0x3F), Grey = Color.FromRgb(0x9C, 0x98, 0x8F);

    public GuidePage()
    {
        InitializeComponent();
        foreach (var (key, title) in new[] { ("guides", "Guides"), ("hardware", "Hardware"), ("smooth", "Make it smoother"), ("prices", "Prices"), ("gear", "Best gear"), ("prebuilt", "Prebuilt PCs"), ("news", "News") })
            Tabs.Children.Add(TabButton(key, title));
        ShowTab(_tab, animate: false);
        UpdateFresh();

        Action changed = () => Dispatcher.BeginInvoke(() => { UpdateFresh(); ShowTab(_tab, animate: false); });
        Loaded += async (_, _) =>
        {
            GuideLive.Changed += changed;
            await GuideLive.Refresh();
            UpdateFresh();
        };
        Unloaded += (_, _) => GuideLive.Changed -= changed;
    }

    /// <summary>Opened with a guide's id (like "clean-driver") from elsewhere in Pulse: open that guide.</summary>
    public void OnShown(object parameter)
    {
        if (parameter is string id && G.Guides.Any(g => g.Id == id)) OpenGuide(id);
    }

    // ───────── header ─────────

    void UpdateFresh()
    {
        var days = (DateTime.Today - G.Updated.Date).Days;
        string when = days <= 0 ? T("today") : days == 1 ? T("yesterday") : F("{0} days ago", days);
        bool live = GuideLive.FreshThisRun;
        FreshText.Text = live ? F("Live · updated {0}", when) : F("Offline copy · updated {0}", when);
        FreshDot.Fill = new SolidColorBrush(live && days <= 7 ? Green : live ? Amber : Grey);
        FreshText.ToolTip = T("Pulse downloads the latest prices and news from its website each time you open the guide. Prices are US prices from public trackers. No brand pays to be in this guide, and links have no affiliate codes.");
    }

    FrameworkElement TabButton(string key, string title)
    {
        var label = new TextBlock { Text = T(title), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 11) };
        var line = new Border { Height = 2, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom };
        var g = new Grid { Margin = new Thickness(0, 0, 26, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
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
        _tab = key;
        foreach (var (k, (label, line)) in _tabs)
        {
            label.Foreground = Res(k == key ? "TextBrush" : "TextDimBrush");
            line.Background = k == key ? Res("AccentBrush") : Brushes.Transparent;
        }
        Body.Children.Clear();
        Body.Children.Add(key switch
        {
            "prices" => BuildPrices(),
            "gear" => BuildGear(),
            "prebuilt" => BuildPrebuilt(),
            "news" => BuildNews(),
            "guides" => BuildGuides(),
            "hardware" => BuildHardware(),
            _ => BuildTips(),
        });
        Scroller.ScrollToTop();
        if (!animate) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var tt = new TranslateTransform(0, 8);
        Body.RenderTransform = tt;
        Body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    // ───────── building blocks ─────────

    Brush Res(string key) => (Brush)FindResource(key);
    static SolidColorBrush B(Color c, byte a = 255) => new(Color.FromArgb(a, c.R, c.G, c.B));

    Border Card(UIElement child, Thickness? pad = null, double bottom = 14) => new()
    {
        Style = (Style)FindResource("Card"), Padding = pad ?? new Thickness(22, 18, 22, 18), Margin = new Thickness(0, 0, 0, bottom), Child = child,
    };

    TextBlock Heading(string text, double size = 16) => new()
    {
        Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextBrush"),
        FontFamily = (FontFamily)FindResource("DisplayFont"),
    };

    TextBlock Dim(string text, double size = 12.5, double top = 3) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, top, 0, 0), LineHeight = size * 1.45,
    };

    TextBlock Faint(string text, double size = 11.5, double top = 0) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, top, 0, 0),
    };

    /// <summary>A section title with a one-line explanation under it.</summary>
    StackPanel Intro(string title, string sub)
    {
        var sp = new StackPanel { Margin = new Thickness(2, 0, 0, 16) };
        sp.Children.Add(Heading(T(title), 19));
        if (sub != null) sp.Children.Add(Dim(T(sub), 13, 4));
        return sp;
    }

    Border Pill(string text, Color c) => new()
    {
        CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 2, 9, 3), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
        Background = B(c, 0x22),
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = B(c) },
    };

    Border Divider() => new() { Height = 1, Background = Res("BorderBrush"), Margin = new Thickness(0, 14, 0, 14) };

    FrameworkElement Source(string name, string url)
    {
        if (string.IsNullOrEmpty(url)) return new Border();
        var b = new Button { Style = (Style)FindResource("LinkButton"), Content = (name ?? T("Source")) + " ↗", FontSize = 12, ToolTip = url };
        b.Click += (_, _) => Open(url);
        return b;
    }

    static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Pill-shaped switch for the views inside a tab. Remembers the choice while Pulse runs.</summary>
    FrameworkElement Segmented(string id, (string Key, string Title)[] items, Action<string> show)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        var outer = new Border
        {
            Background = Res("Surface2Brush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16), Child = bar,
        };
        var parts = new List<(string Key, Border Box, TextBlock Text)>();
        void Select(string key)
        {
            SubTab[id] = key;
            foreach (var (k, box, text) in parts)
            {
                box.Background = k == key ? Res("SurfaceHoverBrush") : Brushes.Transparent;
                text.Foreground = Res(k == key ? "TextBrush" : "TextDimBrush");
            }
            show(key);
        }
        foreach (var (key, title) in items)
        {
            var text = new TextBlock { Text = T(title), FontSize = 12.5, FontWeight = FontWeights.SemiBold };
            var box = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(13, 6, 13, 7), Cursor = Cursors.Hand, Child = text };
            box.MouseLeftButtonUp += (_, _) => Select(key);
            parts.Add((key, box, text));
            bar.Children.Add(box);
        }
        var start = SubTab.TryGetValue(id, out var s) && items.Any(i => i.Key == s) ? s : items[0].Key;
        Select(start);
        return outer;
    }

    /// <summary>A small number tile: label, big value, note.</summary>
    Border Stat(string label, string value, string note, Color c)
    {
        var sp = new StackPanel();
        sp.Children.Add(Faint(label.ToUpper(CultureInfo.CurrentCulture), 10.5));
        sp.Children.Add(new TextBlock { Text = value, FontSize = 24, FontWeight = FontWeights.Bold, Foreground = B(c), Margin = new Thickness(0, 6, 0, 0), FontFamily = (FontFamily)FindResource("DisplayFont") });
        if (note != null) sp.Children.Add(Faint(note, 11.5, 2));
        return new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(18, 14, 18, 14), Child = sp };
    }

    /// <summary>Lays children out in equal columns with a gap (fewer columns on a narrow window).</summary>
    Grid Columns(int count, params UIElement[] children)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        void Lay()
        {
            int cols = ActualWidth > 0 && ActualWidth < 900 ? Math.Min(count, 1 + (count > 2 ? 1 : 0)) : count;
            g.ColumnDefinitions.Clear(); g.RowDefinitions.Clear();
            for (int c = 0; c < cols; c++) g.ColumnDefinitions.Add(new ColumnDefinition());
            for (int r = 0; r < (children.Length + cols - 1) / cols; r++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i] is FrameworkElement fe)
                {
                    int c = i % cols;
                    fe.Margin = new Thickness(c == 0 ? 0 : 6, i / cols == 0 ? 0 : 12, c == cols - 1 ? 0 : 6, 0);
                }
                Grid.SetColumn(children[i], i % cols);
                Grid.SetRow(children[i], i / cols);
            }
        }
        foreach (var c in children) g.Children.Add(c);
        Lay();
        SizeChangedEventHandler h = (_, e) => { if ((e.PreviousSize.Width < 900) != (e.NewSize.Width < 900)) Lay(); };
        SizeChanged += h;
        g.Unloaded += (_, _) => SizeChanged -= h;
        return g;
    }

    // ───────── 1. Make it smoother ─────────

    UIElement BuildTips()
    {
        var root = new StackPanel();
        root.Children.Add(Intro("What actually makes games smoother", "Biggest difference first. Click a tip to see why and how."));

        var list = new StackPanel();
        int n = 0;
        foreach (var t in G.Tips)
        {
            if (n > 0) list.Children.Add(new Border { Height = 1, Background = Res("BorderBrush") });
            list.Children.Add(TipRow(t, ++n, open: n == 1));
        }
        root.Children.Add(Card(list, new Thickness(0)));

        // What not to bother with.
        var skip = new StackPanel();
        skip.Children.Add(Heading(T("Skip these"), 14.5));
        skip.Children.Add(Faint(T("Popular advice that doesn't help on a modern PC."), 12, 2));
        foreach (var s in G.Skip)
        {
            var g = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = B(Red), Margin = new Thickness(0, 3, 12, 0), VerticalAlignment = VerticalAlignment.Top });
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = T(s.Title), FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = Res("TextBrush") });
            sp.Children.Add(Dim(T(s.Body), 12.5, 2));
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            skip.Children.Add(g);
        }
        root.Children.Add(Card(skip));
        return root;
    }

    /// <summary>One tip: a row with its impact; click to open the explanation, source and Pulse button.</summary>
    FrameworkElement TipRow(Tip t, int n, bool open)
    {
        int level = t.Impact switch { "Huge" => 4, "High" => 3, "Medium" => 2, _ => 1 };
        var color = level switch { 4 => Green, 3 => Cyan, 2 => Blue, _ => Grey };

        var head = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        var num = new TextBlock { Text = n.ToString("00", CultureInfo.InvariantCulture), FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Res("TextFaintBrush"), Width = 30, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = T(t.Title), FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        Grid.SetColumn(title, 1);

        // Impact as signal bars, with the word next to it.
        var meter = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, ToolTip = F("{0} impact", T(t.Impact)) };
        var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 8, 1) };
        for (int i = 1; i <= 4; i++)
            bars.Children.Add(new Border { Width = 4, Height = 4 + i * 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(1.5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom, Background = i <= level ? B(color) : Res("BorderBrush") });
        meter.Children.Add(bars);
        meter.Children.Add(new TextBlock { Text = T(t.Impact), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B(color), Width = 64, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(meter, 2);
        var chev = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = Res("TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(open ? 180 : 0) };
        Grid.SetColumn(chev, 3);
        head.Children.Add(num); head.Children.Add(title); head.Children.Add(meter); head.Children.Add(chev);

        var detail = new StackPanel { Margin = new Thickness(30, 10, 40, 0), Visibility = open ? Visibility.Visible : Visibility.Collapsed };
        detail.Children.Add(Dim(T(t.Body), 13, 0));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        if (!string.IsNullOrEmpty(t.PulsePage))
        {
            var go = new Button { Style = (Style)FindResource("PrimaryButton"), Content = T(t.PulseAction ?? "Open in Pulse"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 14, 0) };
            var page = t.PulsePage;
            go.Click += (_, _) => MainWindow.Instance.Navigate(page);
            actions.Children.Add(go);
        }
        if (!string.IsNullOrEmpty(t.SourceUrl))
        {
            var src = Source(t.SourceName, t.SourceUrl);
            src.VerticalAlignment = VerticalAlignment.Center;
            actions.Children.Add(src);
        }
        if (actions.Children.Count > 0) detail.Children.Add(actions);

        var sp = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        sp.Children.Add(head);
        sp.Children.Add(detail);
        var row = new Border { Background = Brushes.Transparent, Child = sp };
        row.MouseEnter += (_, _) => row.Background = Res("SurfaceHoverBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        head.MouseLeftButtonUp += (_, _) =>
        {
            bool show = detail.Visibility != Visibility.Visible;
            detail.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            ((RotateTransform)chev.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(show ? 180 : 0, TimeSpan.FromMilliseconds(160)));
            if (show)
            {
                detail.Opacity = 0;
                detail.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            }
        };
        return row;
    }

    // ───────── 2. Prices ─────────

    UIElement BuildPrices()
    {
        var root = new StackPanel();
        root.Children.Add(Intro("What hardware costs right now", "AI data centers are buying most of the world's memory, so prices are far above launch. US prices."));

        // Three headline numbers.
        var gpus = G.Gpus.Where(g => g.Msrp > 0).ToArray();
        double avg = gpus.Length > 0 ? gpus.Average(g => (g.Now - g.Msrp) / (double)g.Msrp) : 0;
        var best = gpus.OrderBy(g => (g.Now - g.Msrp) / (double)g.Msrp).FirstOrDefault();
        var hist = G.RamHistory.OrderBy(p => p.Date).ToArray();
        var tiles = new List<UIElement>
        {
            Stat(T("Graphics cards"), "+" + Math.Round(avg * 100) + "%", T("Average above launch price"), GuideChart.ForMarkup(avg)),
        };
        if (best != null)
            tiles.Add(Stat(T("Best deal now"), "$" + best.Now.ToString("#,0", CultureInfo.InvariantCulture), F("{0}, {1} above launch", best.Name, "+" + Math.Round((best.Now - best.Msrp) * 100.0 / best.Msrp) + "%"), Green));
        if (hist.Length >= 2 && hist[0].Cheapest > 0)
            tiles.Add(Stat(T("32 GB DDR5 kit"), "$" + hist[^1].Cheapest.ToString("#,0", CultureInfo.InvariantCulture),
                F("{0}× the {1} price", (hist[^1].Cheapest / (double)hist[0].Cheapest).ToString("0.#", CultureInfo.CurrentCulture), hist[0].Date.ToString("MMMM yyyy", CultureInfo.CurrentCulture)), Red));
        root.Children.Add(Columns(tiles.Count, tiles.ToArray()));

        var view = new StackPanel();
        root.Children.Add(Segmented("prices", new[] { ("gpu", "Graphics cards"), ("ram", "Memory"), ("tips", "Upgrade smart") }, key =>
        {
            view.Children.Clear();
            view.Children.Add(key switch { "ram" => RamView(), "tips" => UpgradeView(), _ => GpuView() });
        }));
        root.Children.Add(view);
        return root;
    }

    UIElement GpuView()
    {
        var sp = new StackPanel();
        var chartHolder = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        string current = SubTab.TryGetValue("gpuTier", out var f) ? f : "all";
        void Draw()
        {
            var list = current == "all" ? G.Gpus : G.Gpus.Where(g => (g.Tier ?? "").Contains(current, StringComparison.OrdinalIgnoreCase)).ToArray();
            chartHolder.Content = list.Length == 0 ? Faint(T("No cards in this range.")) : new GpuPriceChart(list, T);
        }
        foreach (var (key, title) in new[] { ("all", "All"), ("1080p", "1080p"), ("1440p", "1440p"), ("4K", "4K") })
        {
            var chip = new RadioButton { Style = (Style)FindResource("Chip"), Content = T(title), GroupName = "gpuTier", IsChecked = key == current };
            chip.Checked += (_, _) => { current = key; SubTab["gpuTier"] = key; Draw(); };
            filters.Children.Add(chip);
        }
        Draw();

        var legend = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        legend.Children.Add(new Ellipse { Width = 9, Height = 9, Stroke = Res("TextDimBrush"), StrokeThickness = 2, Margin = new Thickness(0, 0, 6, 0) });
        legend.Children.Add(Faint(T("Launch price"), 11.5));
        legend.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = B(Amber), Margin = new Thickness(16, 0, 6, 0) });
        legend.Children.Add(Faint(T("Best price now"), 11.5));
        legend.Children.Add(new Rectangle { Width = 8, Height = 8, Fill = B(Cyan), RenderTransform = new RotateTransform(45, 4, 4), Margin = new Thickness(16, 0, 6, 0) });
        legend.Children.Add(Faint(T("Lowest ever (hover)"), 11.5));

        var top = new Grid();
        top.Children.Add(filters);
        top.Children.Add(legend);
        sp.Children.Add(top);

        var inner = new StackPanel();
        inner.Children.Add(chartHolder);
        var foot = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foot.Children.Add(Faint(T("Source:") + " ", 11.5));
        foot.Children.Add(Source(G.GpuSource, G.GpuSourceUrl));
        inner.Children.Add(foot);
        sp.Children.Add(Card(inner, new Thickness(22, 16, 22, 14)));
        return sp;
    }

    UIElement RamView()
    {
        var sp = new StackPanel();
        if (G.RamHistory.Length >= 2)
        {
            var chart = new StackPanel();
            var head = new Grid();
            var ht = new StackPanel();
            ht.Children.Add(Heading(T("32 GB DDR5 kit price"), 15));
            ht.Children.Add(Faint(T("2×16 GB DDR5-6000, cheapest listing and average listing in the US"), 12, 2));
            head.Children.Add(ht);
            var legend = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            legend.Children.Add(new Border { Width = 14, Height = 3, CornerRadius = new CornerRadius(2), Background = B(Cyan), Margin = new Thickness(0, 0, 6, 0) });
            legend.Children.Add(Faint(T("Cheapest"), 11.5));
            legend.Children.Add(new Border { Width = 14, Height = 3, CornerRadius = new CornerRadius(2), Background = B(Violet, 0xC0), Margin = new Thickness(16, 0, 6, 0) });
            legend.Children.Add(Faint(T("Average"), 11.5));
            head.Children.Add(legend);
            chart.Children.Add(head);
            chart.Children.Add(new RamHistoryChart(G.RamHistory, T) { Margin = new Thickness(0, 14, 0, 0) });
            chart.Children.Add(Faint(T("Hover the chart to see any date.") + "  " + T("Source:") + " " + G.RamHistorySource, 11.5, 8));
            sp.Children.Add(Card(chart, new Thickness(22, 18, 22, 14)));
        }

        // Current kit prices: DDR5 and DDR4 side by side.
        UIElement List(bool ddr5)
        {
            var l = new StackPanel();
            var h = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            h.Children.Add(Heading(ddr5 ? "DDR5" : "DDR4", 15));
            h.Children.Add(Faint(ddr5 ? T("Newer PCs (AM5, Intel 12th gen and up)") : T("Older PCs (AM4, Intel up to 14th gen)"), 11.5));
            ((TextBlock)h.Children[1]).Margin = new Thickness(10, 3, 0, 0);
            l.Children.Add(h);
            foreach (var r in G.Ram.Where(r => r.Ddr5 == ddr5))
            {
                var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var left = new StackPanel();
                left.Children.Add(new TextBlock { Text = T(r.Kit), FontSize = 13, Foreground = Res("TextBrush") });
                if (!string.IsNullOrEmpty(r.Before)) left.Children.Add(Faint(F("Was {0} before the shortage", r.Before), 11, 1));
                row.Children.Add(left);
                var price = new TextBlock { Text = r.Now, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(price, 1);
                row.Children.Add(price);
                l.Children.Add(row);
            }
            return new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(22, 16, 22, 18), Child = l };
        }
        sp.Children.Add(Columns(2, List(true), List(false)));
        var foot = new StackPanel { Orientation = Orientation.Horizontal };
        foot.Children.Add(Faint(T("Source:") + " ", 11.5));
        foot.Children.Add(Source(G.RamSource, G.RamSourceUrl));
        sp.Children.Add(foot);
        return sp;
    }

    UIElement UpgradeView()
    {
        var l = new StackPanel();
        l.Children.Add(Heading(T("Upgrading without paying shortage prices"), 15));
        int i = 0;
        foreach (var tip in G.UpgradeTips)
        {
            var g = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = B(Cyan, 0x22), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = (++i).ToString(CultureInfo.InvariantCulture), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = B(Cyan), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            var t = Dim(T(tip), 13, 1);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            l.Children.Add(g);
        }
        return Card(l);
    }

    // ───────── 3. Best gear ─────────

    UIElement BuildGear()
    {
        var root = new StackPanel();
        root.Children.Add(Intro("What reviewers rate best", "Picks from independent reviews. Each one links to the testing."));
        if (G.Gear.Length == 0) return root;
        var view = new StackPanel();
        root.Children.Add(Segmented("gear", G.Gear.Select(g => (g.Title, g.Title)).ToArray(), key =>
        {
            view.Children.Clear();
            var group = G.Gear.FirstOrDefault(g => g.Title == key) ?? G.Gear[0];
            var list = new StackPanel();
            for (int i = 0; i < group.Picks.Length; i++)
            {
                if (i > 0) list.Children.Add(Divider());
                list.Children.Add(PickRow(group.Picks[i]));
            }
            view.Children.Add(Card(list, new Thickness(22, 18, 22, 18)));
        }));
        root.Children.Add(view);
        return root;
    }

    FrameworkElement PickRow(Pick p)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = T(p.Label).ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Res("AccentBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 16, 0) };
        var mid = new StackPanel();
        mid.Children.Add(new TextBlock { Text = p.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), TextWrapping = TextWrapping.Wrap });
        mid.Children.Add(Dim(T(p.Why), 12.5, 3));
        Grid.SetColumn(mid, 1);
        var src = Source(p.SourceName, p.SourceUrl);
        src.VerticalAlignment = VerticalAlignment.Center;
        src.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(src, 2);
        g.Children.Add(label); g.Children.Add(mid); g.Children.Add(src);
        return g;
    }

    // ───────── 4. Prebuilt PCs ─────────

    UIElement BuildPrebuilt()
    {
        var root = new StackPanel();
        root.Children.Add(Intro("Where to buy a ready-made PC", "While parts are expensive, a prebuilt can be the better deal. These makers come up again and again in reviews."));
        var view = new StackPanel();
        root.Children.Add(Segmented("prebuilt", new[] { ("makers", "Makers"), ("checks", "Before you buy") }, key =>
        {
            view.Children.Clear();
            if (key == "checks")
            {
                var l = new StackPanel();
                l.Children.Add(Heading(T("Before you buy a prebuilt, check for"), 15));
                foreach (var c in G.PrebuiltChecks)
                {
                    var g = new Grid { Margin = new Thickness(0, 12, 0, 0) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    g.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, Foreground = B(Green), Margin = new Thickness(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Top });
                    var t = Dim(T(c), 13, 0);
                    Grid.SetColumn(t, 1);
                    g.Children.Add(t);
                    l.Children.Add(g);
                }
                view.Children.Add(Card(l));
                return;
            }
            var list = new StackPanel();
            for (int i = 0; i < G.Builders.Length; i++)
            {
                if (i > 0) list.Children.Add(Divider());
                list.Children.Add(BuilderRow(G.Builders[i]));
            }
            view.Children.Add(Card(list, new Thickness(22, 18, 22, 18)));
        }));
        root.Children.Add(view);
        return root;
    }

    FrameworkElement BuilderRow(Builder b)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sp = new StackPanel();
        var title = new WrapPanel();
        title.Children.Add(new TextBlock { Text = b.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(Pill(T(b.GoodFor), Cyan));
        sp.Children.Add(title);
        sp.Children.Add(Dim(T(b.Example), 12.5, 5));
        var watch = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        watch.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = B(Amber), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        watch.Children.Add(Faint(F("Watch for: {0}", T(b.Watch)), 12));
        sp.Children.Add(watch);
        g.Children.Add(sp);
        var src = Source(b.SourceName, b.SourceUrl);
        src.VerticalAlignment = VerticalAlignment.Center;
        src.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(src, 1);
        g.Children.Add(src);
        return g;
    }

    // ───────── 5. News ─────────

    UIElement BuildNews()
    {
        var root = new StackPanel();
        root.Children.Add(Intro("What's happening with PC hardware", "The stories that change what to buy and when."));
        var tags = new[] { "All" }.Concat(G.News.Select(n => n.Tag).Distinct()).ToArray();
        var view = new StackPanel();
        root.Children.Add(Segmented("news", tags.Select(t => (t, t)).ToArray(), key =>
        {
            view.Children.Clear();
            var items = key == "All" ? G.News : G.News.Where(n => n.Tag == key).ToArray();
            var list = new StackPanel();
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0) list.Children.Add(Divider());
                list.Children.Add(NewsRow(items[i]));
            }
            view.Children.Add(Card(list, new Thickness(22, 18, 22, 18)));
        }));
        root.Children.Add(view);
        return root;
    }

    FrameworkElement NewsRow(NewsItem n)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var tagColor = n.Tag switch { "RAM" => Violet, "GPU" => Green, "Storage" => Amber, "Coming" => Cyan, _ => Blue };
        var tag = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        tag.Children.Add(Pill(T(n.Tag), tagColor));
        if (n.Hot)
        {
            var hot = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 7, 0, 0) };
            hot.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = B(Red), Margin = new Thickness(0, 0, 6, 0) });
            hot.Children.Add(Faint(T("Big story"), 11));
            tag.Children.Add(hot);
        }
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = T(n.Title), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(Dim(T(n.Body), 13, 4));
        var src = Source(n.SourceName, n.SourceUrl);
        src.HorizontalAlignment = HorizontalAlignment.Left;
        src.Margin = new Thickness(0, 6, 0, 0);
        sp.Children.Add(src);
        Grid.SetColumn(sp, 1);
        g.Children.Add(tag); g.Children.Add(sp);
        return g;
    }
}
