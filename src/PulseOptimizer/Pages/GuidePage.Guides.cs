using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Pulse.Core;
using Pulse.Core.Guide;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// The Guides tab: a library of step-by-step guides (search, categories, progress), and a reader that shows one guide
/// chapter by chapter with steps you tick off. Progress is saved, and guides adapt to the PC: chapters for another GPU
/// maker are hidden behind a switch, and the MSI Afterburner guide shows what Pulse found on this PC.
/// </summary>
public partial class GuidePage
{
    static string _openGuide;
    static string _guideQuery = "";
    static string _guideCategory = "All";

    static readonly string MyGpu = Afterburner.GpuName;
    static readonly string MyVendor = Afterburner.Vendor(MyGpu);

    static List<string> Done(string id)
    {
        var all = TweakEngine.State.GuideDone ??= new();
        if (!all.TryGetValue(id, out var list)) all[id] = list = new();
        return list;
    }

    /// <summary>Chapters that apply here: all of them, or, for chapters tied to a GPU maker, the chosen maker's.</summary>
    static IEnumerable<GuideChapter> Visible(GuideDoc d, string vendor) => d.Chapters.Where(c => c.Only == null || c.Only == vendor);

    static string DefaultVendor(GuideDoc d)
    {
        var makers = d.Chapters.Select(c => c.Only).Where(o => o != null).Distinct().ToList();
        if (makers.Count == 0) return null;
        if (SubTab.TryGetValue("vendor-" + d.Id, out var chosen) && makers.Contains(chosen)) return chosen;
        return MyVendor != null && makers.Contains(MyVendor) ? MyVendor : makers[0];
    }

    static (int Done, int Total) Progress(GuideDoc d)
    {
        var steps = Visible(d, DefaultVendor(d)).SelectMany(c => c.Steps.Select(s => GuideDoc.Key(c, s))).ToList();
        var done = Done(d.Id);
        return (steps.Count(done.Contains), steps.Count);
    }

    /// <summary>True when a guide is about a GPU maker this PC doesn't have (shown last, not hidden).</summary>
    static bool OtherVendor(GuideDoc d) =>
        MyVendor != null && (d.Id == "nvidia-settings" && MyVendor != "nvidia" || d.Id == "amd-settings" && MyVendor != "amd");

    UIElement BuildGuides()
    {
        var doc = _openGuide == null ? null : G.Guides.FirstOrDefault(g => g.Id == _openGuide);
        return doc != null ? Reader(doc) : Library();
    }

    void OpenGuide(string id)
    {
        _openGuide = id;
        ShowTab("guides");
    }

    // ───────── the library ─────────

    UIElement Library()
    {
        var root = new StackPanel();
        root.Children.Add(Intro("Step-by-step guides", "Detailed walkthroughs for the changes that make the biggest difference. Tick steps off as you go; Pulse remembers where you got to."));
        if (G.Guides.Length == 0)
        {
            root.Children.Add(Card(Dim(T("Guides will appear here after the next guide update."), 13, 0)));
            return root;
        }

        // Search and categories.
        var search = new TextBox { Style = (Style)FindResource("SearchBox"), Tag = T("Search guides, for example undervolt, ping or XMP"), Text = _guideQuery, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left };
        var chips = new WrapPanel { Margin = new Thickness(0, 12, 0, 16) };
        var results = new StackPanel();
        var categories = new[] { "All" }.Concat(G.Guides.Select(g => g.Category).Where(c => !string.IsNullOrEmpty(c)).Distinct()).ToArray();
        if (!categories.Contains(_guideCategory)) _guideCategory = "All";
        foreach (var c in categories)
        {
            var chip = new RadioButton { Style = (Style)FindResource("Chip"), Content = T(c), GroupName = "guide-cat", IsChecked = c == _guideCategory, Margin = new Thickness(0, 0, 6, 6) };
            var cat = c;
            chip.Checked += (_, _) => { _guideCategory = cat; Fill(); };
            chips.Children.Add(chip);
        }

        void Fill()
        {
            results.Children.Clear();
            var q = _guideQuery.Trim();
            var list = G.Guides
                .Where(g => _guideCategory == "All" || g.Category == _guideCategory)
                .Where(g => q.Length == 0 || Matches(g, q))
                .OrderBy(g => OtherVendor(g)).ToList();
            if (list.Count == 0)
            {
                results.Children.Add(Card(Dim(F("No guide mentions \"{0}\".", q), 13, 0)));
                return;
            }
            var featured = q.Length == 0 && _guideCategory == "All" ? list.FirstOrDefault(g => g.Featured) : null;
            if (featured != null) results.Children.Add(FeaturedCard(featured));
            var rest = list.Where(g => g != featured).Select(g => (UIElement)GuideCard(g)).ToArray();
            if (rest.Length > 0) results.Children.Add(Columns(2, rest));
        }

        search.TextChanged += (_, _) => { _guideQuery = search.Text; Fill(); };
        root.Children.Add(search);
        root.Children.Add(chips);
        root.Children.Add(results);
        Fill();
        return root;
    }

    /// <summary>Searches the guide in the language Pulse is showing, and in English.</summary>
    static bool Matches(GuideDoc g, string q)
    {
        bool Has(string s) => !string.IsNullOrEmpty(s) && (s.Contains(q, StringComparison.CurrentCultureIgnoreCase) || T(s).Contains(q, StringComparison.CurrentCultureIgnoreCase));
        return Has(g.Title) || Has(g.Summary) || Has(g.Category) || g.Chapters.Any(c => Has(c.Title) || Has(c.Intro) || c.Steps.Any(s => Has(s.Text) || Has(s.Detail) || Has(s.Tip)));
    }

    FrameworkElement Meta(GuideDoc g)
    {
        var (done, total) = Progress(g);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(Faint(F("{0} min", g.Minutes) + "  ·  " + T(g.Level ?? "Easy") + "  ·  " + F("{0} steps", total), 11.5));
        if (done > 0)
        {
            row.Children.Add(new Border { Width = 70, Height = 4, CornerRadius = new CornerRadius(2), Background = Res("BorderBrush"), Margin = new Thickness(14, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 70.0 * done / Math.Max(1, total), CornerRadius = new CornerRadius(2), Background = Res(done == total ? "GoodBrush" : "AccentBrush") } });
            row.Children.Add(Faint(done == total ? T("Done") : F("{0} of {1}", done, total), 11.5));
        }
        return row;
    }

    Border Clickable(Border card, Action click)
    {
        card.Cursor = Cursors.Hand;
        card.MouseEnter += (_, _) => card.Background = Res("SurfaceHoverBrush");
        card.MouseLeave += (_, _) => card.ClearValue(Border.BackgroundProperty);
        card.MouseLeftButtonUp += (_, _) => click();
        return card;
    }

    FrameworkElement FeaturedCard(GuideDoc g)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = T("Featured guide").ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Res("AccentBrush") });
        sp.Children.Add(Heading(T(g.Title), 21).Also(h => h.Margin = new Thickness(0, 6, 0, 0)));
        sp.Children.Add(Dim(T(g.Summary), 13.5, 6));
        if (g.Tool == "afterburner")
        {
            var found = new List<string>();
            if (MyGpu != null) found.Add(MyGpu);
            var v = Afterburner.Version;
            found.Add(v == null ? T("Afterburner not installed yet") : F("Afterburner {0} installed", v.ToString(3)));
            sp.Children.Add(Faint(T("On this PC:") + " " + string.Join("  ·  ", found), 12, 10));
        }
        sp.Children.Add(Meta(g));
        var open = new Button { Style = (Style)FindResource("PrimaryButton"), Content = T(Progress(g).Done > 0 ? "Continue" : "Start the guide"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(16, 7, 16, 7) };
        open.Click += (_, _) => OpenGuide(g.Id);
        sp.Children.Add(open);
        var card = Card(sp, new Thickness(26, 22, 26, 22));
        card.BorderBrush = Res("AccentBrush");
        return Clickable(card, () => OpenGuide(g.Id));
    }

    FrameworkElement GuideCard(GuideDoc g)
    {
        var sp = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(Faint(T(g.Category ?? "").ToUpper(CultureInfo.CurrentCulture), 10.5));
        if (OtherVendor(g)) top.Children.Add(Faint("  ·  " + T("For another graphics card brand"), 10.5));
        sp.Children.Add(top);
        sp.Children.Add(Heading(T(g.Title), 15.5).Also(h => h.Margin = new Thickness(0, 6, 0, 0)));
        sp.Children.Add(Dim(T(g.Summary), 12.5, 5));
        sp.Children.Add(Meta(g));
        var card = Card(sp, new Thickness(20, 16, 20, 18), 0);
        card.VerticalAlignment = VerticalAlignment.Stretch;
        return Clickable(card, () => OpenGuide(g.Id));
    }

    // ───────── reading a guide ─────────

    UIElement Reader(GuideDoc d)
    {
        var root = new StackPanel();
        var back = new Button { Style = (Style)FindResource("LinkButton"), Content = "‹  " + T("All guides"), FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 12) };
        back.Click += (_, _) => { _openGuide = null; ShowTab("guides"); };
        root.Children.Add(back);

        // Title and progress.
        var head = new StackPanel { Margin = new Thickness(2, 0, 0, 16) };
        head.Children.Add(Faint(T(d.Category ?? "").ToUpper(CultureInfo.CurrentCulture), 10.5));
        head.Children.Add(Heading(T(d.Title), 22).Also(h => h.Margin = new Thickness(0, 6, 0, 0)));
        head.Children.Add(Dim(T(d.Summary), 13.5, 6));
        var progressText = Faint("", 11.5);
        var bar = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
        var track = new Border { Width = 220, Height = 4, CornerRadius = new CornerRadius(2), Background = Res("BorderBrush"), Child = bar, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var metaRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        metaRow.Children.Add(Faint(F("{0} min", d.Minutes) + "  ·  " + T(d.Level ?? "Easy") + "      ", 11.5));
        metaRow.Children.Add(track);
        metaRow.Children.Add(progressText);
        var reset = new Button { Style = (Style)FindResource("LinkButton"), Content = T("Start over"), FontSize = 11.5, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        metaRow.Children.Add(reset);
        head.Children.Add(metaRow);
        root.Children.Add(head);

        if (d.Tool == "afterburner") root.Children.Add(AfterburnerPanel());

        // Chapters, filtered by GPU maker when the guide has maker-specific chapters.
        var body = new StackPanel();
        var vendors = d.Chapters.Select(c => c.Only).Where(o => o != null).Distinct().ToList();
        string vendor = DefaultVendor(d);

        void Update()
        {
            var steps = Visible(d, vendor).SelectMany(c => c.Steps.Select(s => GuideDoc.Key(c, s))).ToList();
            var done = Done(d.Id);
            int n = steps.Count(done.Contains);
            bar.Width = 220.0 * n / Math.Max(1, steps.Count);
            bar.Background = Res(n == steps.Count && n > 0 ? "GoodBrush" : "AccentBrush");
            progressText.Text = n == steps.Count && n > 0 ? T("All done") : F("{0} of {1} steps done", n, steps.Count);
            reset.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Draw()
        {
            body.Children.Clear();
            var chapters = Visible(d, vendor).ToList();
            var holders = new List<FrameworkElement>();
            // Contents: every chapter, click to jump to it.
            var toc = new WrapPanel();
            for (int i = 0; i < chapters.Count; i++)
            {
                var link = new Button { Style = (Style)FindResource("LinkButton"), Content = $"{i + 1:00}  {T(chapters[i].Title)}", FontSize = 12.5, Margin = new Thickness(0, 0, 22, 6) };
                int at = i;
                link.Click += (_, _) => { try { Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + holders[at].TranslatePoint(new Point(0, 0), Scroller).Y - 8); } catch { } };
                toc.Children.Add(link);
            }
            var tocBox = new StackPanel();
            tocBox.Children.Add(Faint(T("In this guide").ToUpper(CultureInfo.CurrentCulture), 10.5).Also(t => t.Margin = new Thickness(0, 0, 0, 10)));
            tocBox.Children.Add(toc);
            body.Children.Add(Card(tocBox, new Thickness(22, 16, 22, 12)));

            for (int i = 0; i < chapters.Count; i++)
            {
                var c = ChapterCard(d, chapters[i], i + 1, Update);
                holders.Add(c);
                body.Children.Add(c);
            }
            Update();
        }

        if (vendors.Count > 1)
        {
            var pick = new StackPanel();
            pick.Children.Add(Faint(MyVendor != null ? T("Steps for your graphics card are shown. Switch if you're setting up a different one:") : T("Pick your graphics card brand:"), 12));
            root.Children.Add(pick);
            var seg = Segmented("vendor-" + d.Id, vendors.Select(v => (v, v switch { "nvidia" => "NVIDIA", "amd" => "AMD", "intel" => "Intel", _ => v })).ToArray(), key => { vendor = key; Draw(); });
            seg.Margin = new Thickness(0, 8, 0, 16);
            root.Children.Add(seg);
        }
        else Draw();

        reset.Click += async (_, _) =>
        {
            if (!await MainWindow.Instance.Confirm(Loc.T("Start this guide over?"), Loc.T("Every ticked step in this guide is cleared."), Loc.T("Start over"), Loc.T("Cancel"))) return;
            Done(d.Id).Clear();
            TweakEngine.Save();
            Draw();
        };

        root.Children.Add(body);

        if (d.Sources.Length > 0)
        {
            var src = new StackPanel();
            src.Children.Add(Heading(T("Sources"), 14.5));
            src.Children.Add(Faint(T("Where the steps and numbers in this guide come from."), 12, 2));
            var links = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var s in d.Sources)
            {
                var l = Source(s.Name, s.Url);
                l.HorizontalAlignment = HorizontalAlignment.Left;
                l.Margin = new Thickness(0, 0, 0, 4);
                links.Children.Add(l);
            }
            src.Children.Add(links);
            root.Children.Add(Card(src));
        }
        return root;
    }

    FrameworkElement ChapterCard(GuideDoc d, GuideChapter c, int number, Action changed)
    {
        var sp = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = number.ToString("00", CultureInfo.InvariantCulture), FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Res("TextFaintBrush"), Width = 34, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(Heading(T(c.Title), 17));
        sp.Children.Add(title);
        if (!string.IsNullOrEmpty(c.Intro)) sp.Children.Add(Dim(T(c.Intro), 13, 8).Also(t => t.Margin = new Thickness(34, 8, 0, 0)));

        var steps = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var s in c.Steps) steps.Children.Add(StepRow(d, c, s, changed));
        sp.Children.Add(steps);
        return Card(sp, new Thickness(22, 20, 22, 14));
    }

    FrameworkElement StepRow(GuideDoc d, GuideChapter c, GuideStep s, Action changed)
    {
        var key = GuideDoc.Key(c, s);
        var done = Done(d.Id);
        bool isDone = done.Contains(key);

        var g = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        g.ColumnDefinitions.Add(new ColumnDefinition());

        // The tick box.
        var tick = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, Foreground = Res("OnAccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var box = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1.5), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0), Cursor = Cursors.Hand, Child = tick, ToolTip = T("Mark as done") };
        var text = new TextBlock { Text = T(s.Text), FontSize = 13.5, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Foreground = Res("TextBrush"), Cursor = Cursors.Hand };

        void Paint()
        {
            box.Background = isDone ? Res("AccentBrush") : Brushes.Transparent;
            box.BorderBrush = isDone ? Res("AccentBrush") : Res("TextFaintBrush");
            tick.Visibility = isDone ? Visibility.Visible : Visibility.Hidden;
            text.Foreground = Res(isDone ? "TextDimBrush" : "TextBrush");
        }
        void Toggle()
        {
            isDone = !isDone;
            if (isDone) { if (!done.Contains(key)) done.Add(key); }
            else done.Remove(key);
            TweakEngine.Save();
            Paint();
            if (isDone) box.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(160)));
            changed();
        }
        box.MouseLeftButtonUp += (_, _) => Toggle();
        text.MouseLeftButtonUp += (_, _) => Toggle();
        Paint();
        g.Children.Add(box);

        var right = new StackPanel();
        Grid.SetColumn(right, 1);
        right.Children.Add(text);
        if (!string.IsNullOrEmpty(s.Detail)) right.Children.Add(Dim(T(s.Detail), 12.5, 4));
        if (!string.IsNullOrEmpty(s.Tip)) right.Children.Add(Callout(T("Tip"), T(s.Tip), "AccentBrush"));
        if (!string.IsNullOrEmpty(s.Warn)) right.Children.Add(Callout(T("Careful"), T(s.Warn), "WarnBrush"));
        if (!string.IsNullOrEmpty(s.Button) && (!string.IsNullOrEmpty(s.Url) || !string.IsNullOrEmpty(s.Page)))
        {
            var b = new Button { Style = (Style)FindResource("GhostButton"), Content = T(s.Button) + (s.Url != null ? "  ↗" : ""), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 9, 0, 0), Padding = new Thickness(12, 5, 12, 5), FontSize = 12.5 };
            if (s.Url != null) b.ToolTip = s.Url;
            b.Click += (_, _) => { if (!string.IsNullOrEmpty(s.Page)) MainWindow.Instance.Navigate(s.Page); else Open(s.Url); };
            right.Children.Add(b);
        }
        g.Children.Add(right);
        return g;
    }

    /// <summary>A tip or a warning: a short label and the text, set off by a line on the left.</summary>
    FrameworkElement Callout(string label, string body, string brush)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label.ToUpper(CultureInfo.CurrentCulture), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Res(brush) });
        sp.Children.Add(Dim(body, 12.5, 3));
        return new Border
        {
            BorderBrush = Res(brush), BorderThickness = new Thickness(2, 0, 0, 0), Background = Res("Surface2Brush"),
            CornerRadius = new CornerRadius(0, 6, 6, 0), Padding = new Thickness(12, 8, 12, 9), Margin = new Thickness(0, 9, 0, 0), Child = sp,
        };
    }

    // ───────── what Pulse found, for the Afterburner guide ─────────

    FrameworkElement AfterburnerPanel()
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading(T("On this PC"), 14.5));
        var v = Afterburner.Version;
        bool old = v != null && v < Afterburner.Needed && MyGpu != null && System.Text.RegularExpressions.Regex.IsMatch(MyGpu, @"RTX\s*50|RX\s*9\d\d\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var start = Afterburner.StartingPoint(MyGpu);
        var rows = new (string Label, string Value, string Brush)[]
        {
            (T("Graphics card"), MyGpu ?? T("Not found"), "TextBrush"),
            ("MSI Afterburner", v == null ? T("Not installed") : old ? F("{0}: too old for your card, update to 4.6.6 or newer", v.ToString(3)) : F("{0} installed", v.ToString(3)), v == null || old ? "WarnBrush" : "GoodBrush"),
            ("RivaTuner Statistics Server", Afterburner.RtssExe != null ? T("Installed") : T("Not installed (needed for the overlay and FPS cap)"), Afterburner.RtssExe != null ? "GoodBrush" : "WarnBrush"),
            (T("Starting point for your card"), start != null ? T(start) : T("Use the general steps below"), "TextBrush"),
        };
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Faint(rows[i].Label, 12.5).Also(t => t.Margin = new Thickness(0, 0, 12, 8));
            var val = new TextBlock { Text = rows[i].Value, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Res(rows[i].Brush), Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(l, i); Grid.SetRow(val, i); Grid.SetColumn(val, 1);
            grid.Children.Add(l); grid.Children.Add(val);
        }
        sp.Children.Add(grid);

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (v != null)
        {
            var open = new Button { Style = (Style)FindResource("PrimaryButton"), Content = T(Afterburner.Running ? "Show Afterburner" : "Open Afterburner"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 10, 0) };
            open.Click += (_, _) => Afterburner.Open();
            buttons.Children.Add(open);
        }
        if (v == null || old)
        {
            var get = new Button { Style = (Style)FindResource(v == null ? "PrimaryButton" : "GhostButton"), Content = T("Get Afterburner from MSI") + "  ↗", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 10, 0) };
            get.Click += (_, _) => Open(Afterburner.Download);
            buttons.Children.Add(get);
        }
        sp.Children.Add(buttons);
        sp.Children.Add(Faint(T("These are common starting points testers report, not guarantees: every chip is different, which is why the guide tests each step."), 11.5, 10));
        return Card(sp);
    }
}
