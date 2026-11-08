using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pulse.Core;
using Pulse.Core.Crosshair;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// The crosshair library: built-in styles and your saved ones in one place, searchable by name or use case, with your
/// own 1–5 ratings and a side-by-side compare that lists every setting that differs.
/// </summary>
public partial class CrosshairPage
{
    readonly StackPanel _libraryHost = new();
    static string _libSearch = "", _libUse = "";
    static readonly List<(string Name, CrosshairStyle Style)> _compare = new();

    void BuildLibrary()
    {
        _libraryHost.Children.Clear();
        var head = new Grid();
        head.Children.Add(Title("Crosshair library"));
        head.Children.Add(new TextBlock { Text = Loc.T("Click to use · ☆ to rate · ⇄ to compare"), FontSize = 11.5, Foreground = Res("TextFaintBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
        _libraryHost.Children.Add(head);

        var search = new TextBox { Style = (Style)FindResource("SearchBox"), Tag = Loc.T("Search crosshairs: name, color, what it's for"), Text = _libSearch, Margin = new Thickness(0, 0, 0, 8) };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { timer.Stop(); _libSearch = search.Text; BuildLibraryList(); };
        search.TextChanged += (_, _) => { timer.Stop(); timer.Start(); };
        _libraryHost.Children.Add(search);

        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        void ChipFor(string key, string label)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = Loc.T(label), GroupName = "xh-use", IsChecked = _libUse == key, Margin = new Thickness(0, 0, 6, 6) };
            rb.Checked += (_, _) => { _libUse = key; BuildLibraryList(); };
            chips.Children.Add(rb);
        }
        ChipFor("", "All");
        foreach (var u in CrosshairLibrary.UseCases) ChipFor(u, u);
        _libraryHost.Children.Add(chips);

        _libraryHost.Children.Add(_libList);
        _libraryHost.Children.Add(_compareHost);
        BuildLibraryList();
    }

    readonly WrapPanel _libList = new();
    readonly StackPanel _compareHost = new();

    void BuildLibraryList()
    {
        _libList.Children.Clear();
        var text = _libSearch.Trim();
        // Yours first (by your rating), then the built-in styles (by your rating, then as listed).
        var mine = S.Saved.Where(c => (_libUse.Length == 0 || c.UseCase == _libUse)
            && (text.Length == 0 || c.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || c.UseCase.Contains(text, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(c => c.Rating).ToList();
        foreach (var c in mine)
            _libList.Children.Add(LibTile(c.Name, c.Style, Loc.T("Yours") + (c.UseCase.Length > 0 ? " · " + Loc.T(c.UseCase) : ""), c.Rating,
                r => { c.Rating = r; TweakEngine.Save(); BuildLibraryList(); BuildSaved(); }));
        var built = CrosshairLibrary.Find(text, _libUse)
            .Select((e, i) => (e, i, r: S.LibraryRatings.TryGetValue(e.Name, out var r) ? r : 0))
            .OrderByDescending(x => x.r).ThenBy(x => x.i).ToList();
        foreach (var (e, _, r) in built)
            _libList.Children.Add(LibTile(e.Name, e.Style, string.Join(" · ", e.Uses.Select(Loc.T)), r,
                v => { if (v == 0) S.LibraryRatings.Remove(e.Name); else S.LibraryRatings[e.Name] = v; TweakEngine.Save(); BuildLibraryList(); }, Loc.T(e.Why)));
        if (_libList.Children.Count == 0)
            _libList.Children.Add(Dim(Loc.T("No crosshairs match. Try another word or use case.")));
        BuildCompare();
    }

    /// <summary>A library tile: the crosshair, its name, its uses, stars you can click, and a compare toggle.</summary>
    FrameworkElement LibTile(string name, CrosshairStyle style, string uses, int rating, Action<int> rate, string why = null)
    {
        var sp = new StackPanel { Width = 150 };
        sp.Children.Add(new Border { Height = 84, CornerRadius = new CornerRadius(8), Background = Backdrop(_background), ClipToBounds = true, Child = new CrosshairVisual { Crosshair = style, Zoom = 2 } });
        sp.Children.Add(new TextBlock { Text = Loc.T(name), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(2, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(new TextBlock { Text = uses, FontSize = 11, Foreground = Res("TextFaintBrush"), Margin = new Thickness(2, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = uses });
        var bottom = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        var stars = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 1; i <= 5; i++)
        {
            int v = i;
            var s = new TextBlock { Text = v <= rating ? "★" : "☆", FontSize = 13, Padding = new Thickness(1, 0, 1, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent,
                Foreground = v <= rating ? new SolidColorBrush(Color.FromRgb(0xF2, 0xC2, 0x4B)) : Res("TextFaintBrush"), ToolTip = Loc.F("Rate {0} of 5", v) };
            s.MouseLeftButtonUp += (_, e) => { e.Handled = true; rate(v == rating ? 0 : v); };
            stars.Children.Add(s);
        }
        bottom.Children.Add(stars);
        bool inCompare = _compare.Any(c => c.Name == name);
        var cmp = new TextBlock { Text = "⇄", FontSize = 14, Cursor = Cursors.Hand, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = inCompare ? Res("AccentBrush") : Res("TextDimBrush"), ToolTip = Loc.T(inCompare ? "Remove from compare" : "Compare") };
        cmp.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var at = _compare.FindIndex(c => c.Name == name);
            if (at >= 0) _compare.RemoveAt(at);
            else { if (_compare.Count == 2) _compare.RemoveAt(0); _compare.Add((name, style.Copy())); }
            BuildLibraryList();
        };
        bottom.Children.Add(cmp);
        sp.Children.Add(bottom);
        var b = new Border { Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(5), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Child = sp,
            Background = Brushes.Transparent, BorderThickness = new Thickness(1), BorderBrush = inCompare ? Res("AccentBrush") : Brushes.Transparent, ToolTip = why ?? Loc.T("Use this crosshair") };
        b.MouseEnter += (_, _) => b.Background = Res("SurfaceHoverBrush");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { if (e.Handled) return; S.Style = style.Copy(); Changed(rebuildEditor: true); };
        return b;
    }

    void BuildCompare()
    {
        _compareHost.Children.Clear();
        if (_compare.Count == 0) return;
        _compareHost.Children.Add(new Border { Height = 1, Background = Res("BorderBrush"), Margin = new Thickness(0, 6, 0, 12) });
        var head = new Grid();
        head.Children.Add(Title("Compare"));
        var clear = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Clear"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        clear.Click += (_, _) => { _compare.Clear(); BuildLibraryList(); };
        head.Children.Add(clear);
        _compareHost.Children.Add(head);
        if (_compare.Count == 1) { _compareHost.Children.Add(Dim(Loc.F("Pick one more with ⇄ to compare with {0}.", Loc.T(_compare[0].Name)))); return; }
        var (an, a) = _compare[0];
        var (bn, bs) = _compare[1];
        // Both on each background, real size and zoomed.
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        FrameworkElement Side(string name, CrosshairStyle st)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = Loc.T(name), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 0, 0, 6) });
            var bgs = new UniformGrid3();
            foreach (var bg in new[] { "dark", "sky", "busy" })
                bgs.Add(new Border { Height = 90, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 6, 0), Background = Backdrop(bg), ClipToBounds = true, Child = new CrosshairVisual { Crosshair = st, Zoom = 1 } });
            sp.Children.Add(bgs.Grid);
            sp.Children.Add(new Border { Height = 110, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 6, 6, 0), Background = Backdrop("dark"), ClipToBounds = true, Child = new CrosshairVisual { Crosshair = st, Zoom = 4 } });
            var use = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Use this one"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            use.Click += (_, _) => { S.Style = st.Copy(); Changed(rebuildEditor: true); };
            sp.Children.Add(use);
            return sp;
        }
        var l = Side(an, a); row.Children.Add(l);
        var r = Side(bn, bs); Grid.SetColumn(r, 2); row.Children.Add(r);
        _compareHost.Children.Add(row);
        var diffs = CrosshairLibrary.Differences(a, bs);
        var tbl = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        tbl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        tbl.ColumnDefinitions.Add(new ColumnDefinition());
        tbl.ColumnDefinitions.Add(new ColumnDefinition());
        int y = 0;
        void Cell(string t, int col, bool bold = false, bool dim = false)
        {
            var tb = new TextBlock { Text = t, FontSize = 12, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Res(dim ? "TextDimBrush" : "TextBrush"), Margin = new Thickness(0, 3, 8, 3) };
            Grid.SetRow(tb, y); Grid.SetColumn(tb, col); tbl.Children.Add(tb);
        }
        tbl.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(Loc.T("Setting"), 0, true, true); Cell(Loc.T(an), 1, true, true); Cell(Loc.T(bn), 2, true, true); y++;
        foreach (var (setting, va, vb) in diffs)
        {
            tbl.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(Loc.T(setting), 0, false, true); Cell(Loc.T(va), 1); Cell(Loc.T(vb), 2); y++;
        }
        _compareHost.Children.Add(diffs.Count == 0 ? Dim(Loc.T("These two have the same settings.")) : tbl);
    }

    /// <summary>Your saved tile, with a small use-case picker under it.</summary>
    FrameworkElement SavedTile(SavedCrosshair sc, FrameworkElement tile)
    {
        var sp = new StackPanel();
        sp.Children.Add(tile);
        var pick = new ComboBox { Width = 104, FontSize = 11, Margin = new Thickness(4, -6, 10, 10), ToolTip = Loc.T("What you use it for") };
        pick.Items.Add(new ComboBoxItem { Content = Loc.T("Use case…"), Tag = "" });
        foreach (var u in CrosshairLibrary.UseCases) pick.Items.Add(new ComboBoxItem { Content = Loc.T(u), Tag = u });
        pick.SelectedIndex = Math.Max(0, Array.IndexOf(CrosshairLibrary.UseCases, sc.UseCase) + 1);
        pick.SelectionChanged += (_, _) => { if (pick.SelectedItem is ComboBoxItem ci) { sc.UseCase = (string)ci.Tag; TweakEngine.Save(); BuildLibraryList(); } };
        sp.Children.Add(pick);
        return sp;
    }

    /// <summary>Three equal columns.</summary>
    sealed class UniformGrid3
    {
        public readonly Grid Grid = new();
        int _n;
        public UniformGrid3() { for (int i = 0; i < 3; i++) Grid.ColumnDefinitions.Add(new ColumnDefinition()); }
        public void Add(UIElement e) { System.Windows.Controls.Grid.SetColumn(e, _n++); Grid.Children.Add(e); }
    }
}
