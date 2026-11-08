using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Crosshair;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// The Crosshair page: a live preview, VALORANT code import and copy, presets and saved crosshairs, an editor with
/// every VALORANT crosshair setting (plus a ring and a T-shape), and where and when the overlay shows.
/// </summary>
public partial class CrosshairPage : UserControl, IPage
{
    static CrosshairSettings S => CrosshairService.S;
    CrosshairStyle St => S.Style;

    readonly CrosshairVisual _preview = new() { Zoom = 3 };
    readonly Border _previewBox = new();
    readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(600) };
    TextBox _codeBox;
    TextBlock _codeOut, _codeMsg;
    WrapPanel _savedPanel;
    StackPanel _editor;
    static string _background = "dark";
    static double _zoom = 3;

    public CrosshairPage()
    {
        InitializeComponent();
        _save.Tick += (_, _) => { _save.Stop(); TweakEngine.Save(); };
        Action changed = () => Dispatcher.BeginInvoke(UpdateState);
        Loaded += (_, _) => CrosshairService.Changed += changed;
        Unloaded += (_, _) => { CrosshairService.Changed -= changed; if (_save.IsEnabled) { _save.Stop(); TweakEngine.Save(); } };
        SizeChanged += (_, e) => Layout(e.NewSize.Width);

        BuildLeft();
        BuildRight();
        EnabledSwitch.IsChecked = S.Enabled;
        UpdateState();
    }

    public void OnShown(object parameter) => UpdateState();

    // ───────── shared bits ─────────

    Brush Res(string k) => (Brush)FindResource(k);

    Border Card(UIElement child, double bottom = 16) => new()
    {
        Style = (Style)FindResource("Card"), Padding = new Thickness(20, 18, 20, 18), Margin = new Thickness(0, 0, 0, bottom), Child = child,
    };

    TextBlock Title(string t) => new() { Text = Loc.T(t), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 0, 0, 10) };
    TextBlock Dim(string t, double size = 12.5) => new() { Text = t, FontSize = size, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap };

    void Layout(double width)
    {
        bool narrow = width < 820;
        Columns.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 18);
        Columns.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(360);
        if (narrow && RightCol.Parent == Columns && Grid.GetColumn(RightCol) == 2)
        {
            Grid.SetColumn(RightCol, 0);
            if (Columns.RowDefinitions.Count == 0) { Columns.RowDefinitions.Add(new RowDefinition()); Columns.RowDefinitions.Add(new RowDefinition()); }
            Grid.SetRow(RightCol, 1);
        }
        else if (!narrow && Grid.GetColumn(RightCol) == 0)
        {
            Grid.SetColumn(RightCol, 2);
            Grid.SetRow(RightCol, 0);
        }
    }

    /// <summary>Something changed: redraw the preview and the overlay, save shortly after.</summary>
    void Changed(bool rebuildEditor = false)
    {
        _preview.Crosshair = St;
        _preview.InvalidateVisual();
        if (_codeOut != null) _codeOut.Text = St.Mode is "draw" or "image" ? Loc.T("Drawn and picture crosshairs don't have a VALORANT code.") : ValorantCode.Write(St);
        if (rebuildEditor) BuildEditor();
        CrosshairService.Refresh();
        _save.Stop(); _save.Start();
    }

    void UpdateState()
    {
        string text; Brush dot;
        if (!S.Enabled) { text = Loc.T("Off"); dot = Res("TextFaintBrush"); }
        else if (CrosshairService.Showing) { text = Loc.T("On screen now"); dot = Res("GoodBrush"); }
        else if (S.OnlyInGames) { text = Loc.T("Shows when you play a game"); dot = Res("InfoBrush"); }
        else { text = Loc.T("Hidden (Ctrl+Shift+X)"); dot = Res("WarnBrush"); }
        StateText.Text = text;
        StateDot.Fill = dot;
    }

    void Enabled_Click(object sender, RoutedEventArgs e)
    {
        S.Enabled = EnabledSwitch.IsChecked == true;
        TweakEngine.Save();
        CrosshairService.Refresh();
        UpdateState();
        if (S.Enabled) MainWindow.Instance.Toast(S.OnlyInGames ? Loc.T("Crosshair on. It shows when you play a game.") : Loc.T("Crosshair on."));
    }

    // ───────── left: preview, code, presets, saved ─────────

    void BuildLeft()
    {
        // Preview over a choice of backgrounds, at real size or zoomed.
        var pv = new StackPanel();
        var top = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        top.Children.Add(Title("Preview"));
        var opts = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (key, label) in new[] { ("dark", "Dark"), ("sky", "Bright"), ("busy", "Busy") })
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = Loc.T(label), GroupName = "xh-bg", IsChecked = key == _background, Margin = new Thickness(6, 0, 0, 0) };
            rb.Checked += (_, _) => { _background = key; _previewBox.Background = Backdrop(key); };
            opts.Children.Add(rb);
        }
        opts.Children.Add(new Border { Width = 1, Background = Res("BorderBrush"), Margin = new Thickness(10, 4, 4, 4) });
        foreach (var (z, label) in new[] { (1.0, "Real size"), (3.0, "Zoom 3×") })
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = Loc.T(label), GroupName = "xh-zoom", IsChecked = z == _zoom, Margin = new Thickness(6, 0, 0, 0) };
            rb.Checked += (_, _) => { _zoom = z; _preview.Zoom = z; };
            opts.Children.Add(rb);
        }
        top.Children.Add(opts);
        pv.Children.Add(top);
        _preview.Crosshair = St;
        _preview.Zoom = _zoom;
        _previewBox.Height = 300;
        _previewBox.CornerRadius = new CornerRadius(10);
        _previewBox.ClipToBounds = true;
        _previewBox.Background = Backdrop(_background);
        _previewBox.Child = _preview;
        pv.Children.Add(_previewBox);
        LeftCol.Children.Add(Card(pv));

        // VALORANT code: paste to import, or copy this crosshair's code.
        var code = new StackPanel();
        code.Children.Add(Title("Crosshair code"));
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _codeBox = new TextBox { Style = (Style)FindResource("SearchBox"), Tag = Loc.T("Paste a VALORANT code (0;P;…) or a Pulse code (PULSE1-…)") };
        _codeBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Import(); };
        row.Children.Add(_codeBox);
        var imp = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T("Import"), Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(18, 7, 18, 7) };
        imp.Click += (_, _) => Import();
        Grid.SetColumn(imp, 1);
        row.Children.Add(imp);
        code.Children.Add(row);
        _codeMsg = new TextBlock { FontSize = 12, Margin = new Thickness(2, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        code.Children.Add(_codeMsg);
        code.Children.Add(Dim(Loc.T("In VALORANT: Settings → Crosshair → Import / Export → Copy profile code. The other way round, copy this code into VALORANT:"), 12));
        ((FrameworkElement)code.Children[^1]).Margin = new Thickness(0, 12, 0, 6);
        var outRow = new Grid();
        outRow.ColumnDefinitions.Add(new ColumnDefinition());
        outRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _codeOut = new TextBlock
        {
            Text = St.Mode is "draw" or "image" ? Loc.T("Drawn and picture crosshairs don't have a VALORANT code.") : ValorantCode.Write(St), FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12, Foreground = Res("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        outRow.Children.Add(new Border { Background = Res("Surface2Brush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Child = _codeOut });
        var copy = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Copy"), Margin = new Thickness(10, 0, 0, 0) };
        copy.Click += (_, _) =>
        {
            if (St.Mode is "draw" or "image") { MainWindow.Instance.Toast(Loc.T("Drawn and picture crosshairs don't have a VALORANT code."), ToastKind.Info); return; }
            try { Clipboard.SetText(ValorantCode.Write(St)); MainWindow.Instance.Toast(Loc.T("Crosshair code copied.")); } catch { }
        };
        Grid.SetColumn(copy, 1);
        outRow.Children.Add(copy);
        code.Children.Add(outRow);
        // Pulse share codes carry everything, drawn crosshairs too.
        var share = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Copy a Pulse share code (keeps every setting, drawn ones too)"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        share.Click += (_, _) =>
        {
            var c = PulseCode.Write(St);
            if (c == null) { MainWindow.Instance.Toast(Loc.T("Picture crosshairs can't be shared as a code. Send the picture instead."), ToastKind.Info); return; }
            try { Clipboard.SetText(c); MainWindow.Instance.Toast(Loc.T("Share code copied. Friends paste it here in Pulse.")); } catch { }
        };
        code.Children.Add(share);
        LeftCol.Children.Add(Card(code));

        // The library (built-in styles plus yours): search, use cases, ratings, compare.
        LeftCol.Children.Add(Card(_libraryHost));
        BuildLibrary();

        var saved = new StackPanel();
        var sh = new Grid();
        sh.Children.Add(Title("My crosshairs"));
        var save = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Save this crosshair"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        save.Click += (_, _) =>
        {
            S.Saved.Add(new SavedCrosshair { Name = Loc.F("Crosshair {0}", S.Saved.Count + 1), Style = St.Copy() });
            TweakEngine.Save();
            BuildSaved();
        };
        sh.Children.Add(save);
        saved.Children.Add(sh);
        _savedPanel = new WrapPanel();
        saved.Children.Add(_savedPanel);
        LeftCol.Children.Add(Card(saved));
        BuildSaved();
    }

    void BuildSaved()
    {
        _savedPanel.Children.Clear();
        if (_libList.IsLoaded || _libraryHost.Children.Count > 0) BuildLibraryList();
        if (S.Saved.Count == 0) { _savedPanel.Children.Add(Dim(Loc.T("Save crosshairs here to switch between them in one click."))); return; }
        foreach (var sc in S.Saved.ToList())
            _savedPanel.Children.Add(SavedTile(sc, Tile(sc.Name, sc.Style, () => { S.Style = sc.Style.Copy(); Changed(rebuildEditor: true); },
                () => { S.Saved.Remove(sc); TweakEngine.Save(); BuildSaved(); },
                name =>
                {
                    // Keep game links pointing at it under its new name.
                    foreach (var k in S.GameCrosshairs.Where(kv => kv.Value == sc.Name).Select(kv => kv.Key).ToList()) S.GameCrosshairs[k] = name;
                    sc.Name = name; TweakEngine.Save(); BuildSaved(); if (_links != null) BuildLinks();
                })));
    }

    /// <summary>A small square with a crosshair on it and its name; click to use it.</summary>
    FrameworkElement Tile(string name, CrosshairStyle style, Action use, Action delete, Action<string> rename = null)
    {
        var g = new Grid { Width = 104 };
        var sp = new StackPanel();
        var pic = new Border { Height = 76, CornerRadius = new CornerRadius(8), Background = Backdrop("dark"), ClipToBounds = true, Child = new CrosshairVisual { Crosshair = style, Zoom = 2 } };
        sp.Children.Add(pic);
        var label = new TextBlock { Text = rename != null ? name : Loc.T(name), FontSize = 12, Foreground = Res("TextDimBrush"), Margin = new Thickness(2, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        sp.Children.Add(label);
        if (rename != null)
        {
            // Double-click the name to rename it.
            label.ToolTip = Loc.T("Double-click to rename");
            label.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2) return;
                e.Handled = true;
                var box = new TextBox { Text = name, FontSize = 12, Margin = new Thickness(0, 4, 0, 0), MaxLength = 32 };
                void Done() { var t = box.Text.Trim(); if (t.Length > 0 && t != name) rename(t); else { sp.Children.Remove(box); label.Visibility = Visibility.Visible; } }
                box.KeyDown += (_, k) => { if (k.Key == Key.Enter) Done(); else if (k.Key == Key.Escape) { box.Text = name; Done(); } };
                box.LostFocus += (_, _) => Done();
                label.Visibility = Visibility.Collapsed;
                sp.Children.Add(box);
                box.Focus(); box.SelectAll();
            };
        }
        g.Children.Add(sp);
        if (delete != null)
        {
            var x = new Button { Style = (Style)FindResource("LinkButton"), Content = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 6, 0), ToolTip = Loc.T("Delete") };
            x.Click += (_, e) => { e.Handled = true; delete(); };
            g.Children.Add(x);
        }
        var b = new Border { Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(4), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Background = Brushes.Transparent, Child = g, ToolTip = Loc.T("Use this crosshair") };
        b.MouseEnter += (_, _) => b.Background = Res("SurfaceHoverBrush");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { if (!e.Handled) use(); };
        return b;
    }

    Brush Backdrop(string key) => key switch
    {
        "sky" => new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(0x9E, 0xC9, 0xE8), 0), new(Color.FromRgb(0xD8, 0xE6, 0xEE), 0.55),
            new(Color.FromRgb(0xC2, 0xA8, 0x86), 0.56), new(Color.FromRgb(0x8C, 0x74, 0x58), 1),
        }, 90),
        "busy" => Busy(),
        _ => new RadialGradientBrush(Color.FromRgb(0x2A, 0x2E, 0x38), Color.FromRgb(0x0E, 0x10, 0x14)),
    };

    /// <summary>A cluttered, game-like background (shapes in many colors) to test how the crosshair stands out.</summary>
    static Brush Busy()
    {
        var rnd = new Random(7);
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x5A, 0x6B, 0x5E)), null, new RectangleGeometry(new Rect(0, 0, 400, 300))));
        Color[] cols = { Color.FromRgb(0xC9, 0xB7, 0x8F), Color.FromRgb(0x7A, 0x8F, 0x9E), Color.FromRgb(0x3E, 0x4A, 0x3F), Color.FromRgb(0xE8, 0xE2, 0xD0), Color.FromRgb(0x8E, 0x5B, 0x3D), Color.FromRgb(0x2B, 0x2F, 0x36) };
        for (int i = 0; i < 70; i++)
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(cols[rnd.Next(cols.Length)]), null,
                new RectangleGeometry(new Rect(rnd.Next(-40, 400), rnd.Next(-40, 300), rnd.Next(20, 120), rnd.Next(10, 90)))));
        var b = new DrawingBrush(g) { Stretch = Stretch.UniformToFill };
        b.Freeze();
        return b;
    }

    void Import()
    {
        bool pulse = PulseCode.Looks(_codeBox.Text);
        var s = pulse ? PulseCode.Parse(_codeBox.Text) : ValorantCode.Parse(_codeBox.Text);
        _codeMsg.Visibility = Visibility.Visible;
        if (s == null)
        {
            _codeMsg.Text = pulse ? Loc.T("That Pulse code is incomplete. Copy the whole line, starting with PULSE1-.") : Loc.T("That isn't a crosshair code. VALORANT codes start with 0; and Pulse codes with PULSE1-.");
            _codeMsg.Foreground = Res("WarnBrush");
            return;
        }
        S.Style = s;
        _codeMsg.Text = Loc.T("Imported. This is now your crosshair.");
        _codeMsg.Foreground = Res("GoodBrush");
        _codeBox.Text = "";
        Changed(rebuildEditor: true);
    }

    // ───────── right: editor and placement ─────────

    void BuildRight()
    {
        _editor = new StackPanel();
        RightCol.Children.Add(Card(_editor));
        BuildEditor();

        var place = new StackPanel();
        place.Children.Add(Title("Where and when"));
        place.Children.Add(Switch("Only while playing a game", "Hides it on the desktop and in other apps.", () => S.OnlyInGames, v => S.OnlyInGames = v));
        place.Children.Add(Switch("Ctrl+Shift+X shows or hides it", null, () => S.Hotkey, v => S.Hotkey = v));
        place.Children.Add(Switch("Hide it from clips and screenshots", "Recordings show the game without this crosshair.", () => S.HideFromCapture, v => S.HideFromCapture = v));

        place.Children.Add(new TextBlock { Text = Loc.T("Screen"), FontSize = 12.5, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 14, 0, 6) });
        var mon = new WrapPanel();
        foreach (var (key, label) in new[] { ("game", "The game's screen"), ("primary", "Main screen") })
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = Loc.T(label), GroupName = "xh-mon", IsChecked = S.Monitor == key };
            rb.Checked += (_, _) => { S.Monitor = key; Changed(); };
            mon.Children.Add(rb);
        }
        place.Children.Add(mon);

        // Nudge, for games whose center is off by a pixel.
        place.Children.Add(new TextBlock { Text = Loc.T("Nudge (pixels)"), FontSize = 12.5, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 10, 0, 6) });
        var nudge = new StackPanel { Orientation = Orientation.Horizontal };
        var pos = new TextBlock { FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        void ShowPos() => pos.Text = $"x {S.OffsetX:+0;-0;0}  y {S.OffsetY:+0;-0;0}";
        foreach (var (glyph, dx, dy) in new[] { ("", -1, 0), ("", 1, 0), ("", 0, -1), ("", 0, 1) })
        {
            var b = new Button { Style = (Style)FindResource("GhostButton"), Content = glyph, FontFamily = (FontFamily)FindResource("IconFont"), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (_, _) => { S.OffsetX += dx; S.OffsetY += dy; ShowPos(); Changed(); };
            nudge.Children.Add(b);
        }
        var reset = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Reset"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        reset.Click += (_, _) => { S.OffsetX = S.OffsetY = 0; ShowPos(); Changed(); };
        nudge.Children.Add(pos);
        nudge.Children.Add(reset);
        ShowPos();
        place.Children.Add(nudge);
        RightCol.Children.Add(Card(place));
        RightCol.Children.Add(Card(BuildPlay()));
    }

    StackPanel _links;

    /// <summary>Reacting to the mouse (like Crosshair X), switching crosshairs, and a crosshair per game.</summary>
    StackPanel BuildPlay()
    {
        var p = new StackPanel();
        p.Children.Add(Title("While you play"));
        p.Children.Add(Switch("Hide while aiming", "Disappears while you hold the right mouse button (scopes, aim down sights).", () => S.HideWhileAiming, v => S.HideWhileAiming = v));
        var amount = Slider("Spread", 1, 12, 1, () => S.BloomPx, v => S.BloomPx = v);
        amount.Visibility = S.Bloom ? Visibility.Visible : Visibility.Collapsed;
        p.Children.Add(Switch("Spread when shooting", "The lines open up while you hold the left mouse button, then settle.", () => S.Bloom, v => { S.Bloom = v; amount.Visibility = v ? Visibility.Visible : Visibility.Collapsed; }));
        p.Children.Add(amount);
        p.Children.Add(Switch("Ctrl+Shift+C switches crosshair", CrosshairService.CycleHotkeyWorks ? "Goes to the next one in My crosshairs." : "Another app is already using Ctrl+Shift+C.", () => S.CycleHotkey, v => S.CycleHotkey = v));

        p.Children.Add(new Border { Height = 1, Background = Res("BorderBrush"), Margin = new Thickness(0, 10, 0, 10) });
        p.Children.Add(new TextBlock { Text = Loc.T("A crosshair for each game"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
        var note = Dim(Loc.T("Pulse switches to it by itself when that game starts."), 11.5);
        note.Margin = new Thickness(0, 2, 0, 8);
        p.Children.Add(note);
        _links = new StackPanel();
        p.Children.Add(_links);
        BuildLinks();
        return p;
    }

    void BuildLinks()
    {
        _links.Children.Clear();
        foreach (var (game, name) in S.GameCrosshairs.ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var x = new Button { Style = (Style)FindResource("LinkButton"), Content = "\uE711", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, ToolTip = Loc.T("Remove"), VerticalAlignment = VerticalAlignment.Center };
            x.Click += (_, _) => { S.GameCrosshairs.Remove(game); TweakEngine.Save(); BuildLinks(); };
            DockPanel.SetDock(x, Dock.Right);
            row.Children.Add(x);
            bool missing = !S.Saved.Any(c => c.Name == name);
            row.Children.Add(new TextBlock { Text = game + "  →  " + name + (missing ? "  (" + Loc.T("deleted") + ")" : ""), FontSize = 12.5, Foreground = Res(missing ? "TextFaintBrush" : "TextBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            _links.Children.Add(row);
        }
        if (S.Saved.Count == 0)
        {
            _links.Children.Add(Dim(Loc.T("Save a crosshair first (My crosshairs), then link it to a game here."), 12));
            return;
        }
        var games = new ComboBox { IsEditable = true, Height = 32, Margin = new Thickness(0, 4, 0, 6), ToolTip = Loc.T("Game") };
        var names = new List<string>();
        if (CrosshairService.CurrentGame is { } now) names.Add(now);
        names.AddRange(Pulse.Core.Games.GameCatalog.All.Select(g => g.Name).Where(n => !names.Contains(n)).OrderBy(n => n));
        foreach (var n in names) games.Items.Add(n);
        games.Text = names.FirstOrDefault() ?? "";
        var pick = new ComboBox { Height = 32, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var c in S.Saved) pick.Items.Add(c.Name);
        pick.SelectedIndex = 0;
        var link = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Link"), HorizontalAlignment = HorizontalAlignment.Left };
        link.Click += (_, _) =>
        {
            var g = games.Text?.Trim();
            if (string.IsNullOrEmpty(g) || pick.SelectedItem is not string n) return;
            foreach (var k in S.GameCrosshairs.Keys.Where(k => k.Equals(g, StringComparison.OrdinalIgnoreCase)).ToList()) S.GameCrosshairs.Remove(k);
            S.GameCrosshairs[g] = n;
            TweakEngine.Save();
            CrosshairService.Refresh();
            BuildLinks();
        };
        _links.Children.Add(games);
        _links.Children.Add(pick);
        _links.Children.Add(link);
    }

    void BuildEditor()
    {
        _editor.Children.Clear();
        _editor.Children.Add(Title("Make your own"));
        // Three ways to make one: VALORANT-style settings, drawing pixels, or a picture.
        var modes = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (key, label, glyph) in new[] { ("build", "Build", "\uE713"), ("draw", "Draw", "\uE70F"), ("image", "Image", "\uEB9F") })
        {
            bool on = (St.Mode ?? "build") == key;
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
            sp.Children.Add(new TextBlock { Text = Loc.T(label), FontWeight = FontWeights.SemiBold });
            var b = new Border
            {
                CornerRadius = new CornerRadius(9), Padding = new Thickness(14, 7, 14, 8), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, Child = sp,
                Background = on ? Res("SurfaceHoverBrush") : Res("Surface2Brush"), BorderBrush = on ? Res("AccentBrush") : Res("BorderBrush"), BorderThickness = new Thickness(1),
            };
            if (!on) b.MouseLeftButtonUp += (_, _) => { St.Mode = key; if (key == "draw" && string.IsNullOrEmpty(St.Pixels)) St.Pixels = Seed(St); Changed(rebuildEditor: true); };
            modes.Children.Add(b);
        }
        _editor.Children.Add(modes);
        if (St.Mode == "draw") { BuildDraw(); Rotation(); return; }
        if (St.Mode == "image") { BuildImage(); Rotation(); return; }

        // Color: VALORANT's eight, plus any color.
        _editor.Children.Add(Label("Color"));
        var sw = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var hex in ValorantCode.Colors)
        {
            bool on = hex.Equals(St.Color, StringComparison.OrdinalIgnoreCase);
            var b = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
                Background = new SolidColorBrush(CrosshairVisual.ParseColor(hex)), BorderThickness = new Thickness(on ? 3 : 1),
                BorderBrush = on ? Res("TextBrush") : Res("BorderBrush"), ToolTip = hex,
            };
            var h = hex;
            b.MouseLeftButtonUp += (_, _) => { St.Color = h; Changed(rebuildEditor: true); };
            sw.Children.Add(b);
        }
        var custom = new TextBox { Style = (Style)FindResource("SearchBox"), Width = 96, Tag = "#RRGGBB", Text = Array.Exists(ValorantCode.Colors, c => c.Equals(St.Color, StringComparison.OrdinalIgnoreCase)) ? "" : St.Color, VerticalAlignment = VerticalAlignment.Top };
        custom.LostFocus += (_, _) => SetCustom(custom.Text);
        custom.KeyDown += (_, e) => { if (e.Key == Key.Enter) SetCustom(custom.Text); };
        sw.Children.Add(custom);
        _editor.Children.Add(sw);

        Section("Center dot", () => St.Dot, v => St.Dot = v,
            Slider("Size", 1, 10, 1, () => St.DotSize, v => St.DotSize = v),
            Slider("Opacity", 0, 1, 0.05, () => St.DotOpacity, v => St.DotOpacity = v, pct: true));
        Section("Inner lines", () => St.Inner.Show, v => St.Inner.Show = v,
            Slider("Length", 0, 20, 1, () => St.Inner.Length, v => St.Inner.Length = v),
            Slider("Thickness", 1, 10, 1, () => St.Inner.Thickness, v => St.Inner.Thickness = v),
            Slider("Gap", 0, 20, 1, () => St.Inner.Offset, v => St.Inner.Offset = v),
            Slider("Opacity", 0, 1, 0.05, () => St.Inner.Opacity, v => St.Inner.Opacity = v, pct: true));
        Section("Outer lines", () => St.Outer.Show, v => St.Outer.Show = v,
            Slider("Length", 0, 20, 1, () => St.Outer.Length, v => St.Outer.Length = v),
            Slider("Thickness", 1, 10, 1, () => St.Outer.Thickness, v => St.Outer.Thickness = v),
            Slider("Gap", 0, 40, 1, () => St.Outer.Offset, v => St.Outer.Offset = v),
            Slider("Opacity", 0, 1, 0.05, () => St.Outer.Opacity, v => St.Outer.Opacity = v, pct: true));
        Section("Outlines", () => St.Outlines, v => St.Outlines = v,
            Slider("Thickness", 1, 6, 1, () => St.OutlineThickness, v => St.OutlineThickness = v),
            Slider("Opacity", 0, 1, 0.05, () => St.OutlineOpacity, v => St.OutlineOpacity = v, pct: true));
        Section("Ring", () => St.Circle, v => St.Circle = v,
            Slider("Size", 2, 40, 1, () => St.CircleRadius, v => St.CircleRadius = v),
            Slider("Thickness", 1, 6, 0.5, () => St.CircleThickness, v => St.CircleThickness = v),
            Slider("Opacity", 0, 1, 0.05, () => St.CircleOpacity, v => St.CircleOpacity = v, pct: true));
        _editor.Children.Add(Switch("T-shape (no top line)", null, () => St.NoTop, v => St.NoTop = v, redraw: true));
        _editor.Children.Add(new Border { Height = 1, Background = Res("BorderBrush"), Margin = new Thickness(0, 12, 0, 12) });
        _editor.Children.Add(Slider("Overall size", 0.5, 4, 0.25, () => St.Size, v => St.Size = v, times: true));
        Rotation();
    }

    /// <summary>Turn the crosshair: 45° makes a + into an ×.</summary>
    void Rotation()
    {
        _editor.Children.Add(Slider("Rotation", 0, 90, 5, () => St.Rotation, v => St.Rotation = v));
        var quick = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (deg, label) in new[] { (0.0, "+"), (45.0, "×") })
        {
            var b = new Button { Style = (Style)FindResource("GhostButton"), Content = label, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 6, 0), ToolTip = deg + "°" };
            b.Click += (_, _) => { St.Rotation = deg; Changed(rebuildEditor: true); };
            quick.Children.Add(b);
        }
        _editor.Children.Add(quick);
    }

    // ───────── Draw: paint pixels on a grid ─────────

    static string _drawColor = "#00FFFF";
    static bool _mirror = true, _erase;

    /// <summary>Starts a drawing from the current built crosshair, so you edit rather than start from nothing.</summary>
    static string Seed(CrosshairStyle s)
    {
        int n = 21, c = n / 2;
        var cells = new string[n * n];
        var col = s.Color ?? "#FFFFFF";
        void Set(int x, int y) { if (x >= 0 && y >= 0 && x < n && y < n) cells[y * n + x] = col; }
        if (s.Inner.Show)
            for (int i = 0; i < Math.Min(8, (int)s.Inner.Length); i++)
            {
                int d = (int)s.Inner.Offset + 1 + i;
                Set(c + d, c); Set(c - d, c); Set(c, c + d); if (!s.NoTop) Set(c, c - d);
            }
        if (s.Dot || !s.Inner.Show) Set(c, c);
        return string.Join(";", cells.Select(x => x ?? ""));
    }

    void BuildDraw()
    {
        int n = St.GridSize;
        var cells = St.PixelCells();
        _editor.Children.Add(Label("Click or drag to paint. Each square is one pixel on your screen (at size 1×)."));
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var hex in ValorantCode.Colors.Append("#000000"))
        {
            bool on = !_erase && hex.Equals(_drawColor, StringComparison.OrdinalIgnoreCase);
            var sw = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, Background = new SolidColorBrush(CrosshairVisual.ParseColor(hex)), BorderThickness = new Thickness(on ? 3 : 1), BorderBrush = on ? Res("TextBrush") : Res("BorderBrush"), ToolTip = hex };
            var h = hex;
            sw.MouseLeftButtonUp += (_, _) => { _drawColor = h; _erase = false; BuildEditor(); };
            tools.Children.Add(sw);
        }
        _editor.Children.Add(tools);
        var opts = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var erase = new ToggleButton { Style = (Style)FindResource("LinkToggle"), Content = Loc.T("Eraser"), IsChecked = _erase, Margin = new Thickness(0, 0, 16, 0) };
        erase.Click += (_, _) => { _erase = erase.IsChecked == true; BuildEditor(); };
        var mirror = new ToggleButton { Style = (Style)FindResource("LinkToggle"), Content = Loc.T("Mirror (4 ways)"), IsChecked = _mirror, Margin = new Thickness(0, 0, 16, 0) };
        mirror.Click += (_, _) => _mirror = mirror.IsChecked == true;
        var clear = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Clear") };
        clear.Click += (_, _) => { St.Pixels = string.Join(";", new string[n * n].Select(_ => "")); Changed(rebuildEditor: true); };
        opts.Children.Add(erase); opts.Children.Add(mirror); opts.Children.Add(clear);
        _editor.Children.Add(opts);

        // The grid: cells sized to fit, center marked.
        const double cell = 15;
        var grid = new Canvas { Width = n * cell, Height = n * cell, Background = Backdrop("dark"), Cursor = Cursors.Pen, HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
        var rects = new Rectangle[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                var r = new Rectangle { Width = cell - 1, Height = cell - 1, Stroke = null };
                Canvas.SetLeft(r, x * cell); Canvas.SetTop(r, y * cell);
                rects[y * n + x] = r;
                grid.Children.Add(r);
            }
        void Paint(int i)
        {
            var c = cells[i];
            bool center = i == (n / 2) * n + n / 2;
            rects[i].Fill = string.IsNullOrEmpty(c) ? new SolidColorBrush(Color.FromArgb(center ? (byte)0x30 : (byte)0x14, 255, 255, 255)) : new SolidColorBrush(CrosshairVisual.ParseColor(c));
        }
        for (int i = 0; i < cells.Length; i++) Paint(i);
        bool painting = false;
        void At(Point p)
        {
            int x = (int)(p.X / cell), y = (int)(p.Y / cell);
            if (x < 0 || y < 0 || x >= n || y >= n) return;
            var value = _erase ? "" : _drawColor;
            var targets = _mirror ? new[] { (x, y), (n - 1 - x, y), (x, n - 1 - y), (n - 1 - x, n - 1 - y) } : new[] { (x, y) };
            bool changed = false;
            foreach (var (tx, ty) in targets)
            {
                int i = ty * n + tx;
                if (cells[i] == value) continue;
                cells[i] = value; Paint(i); changed = true;
            }
            if (!changed) return;
            St.Pixels = string.Join(";", cells);
            _preview.InvalidateVisual();
        }
        grid.MouseLeftButtonDown += (_, e) => { painting = true; grid.CaptureMouse(); At(e.GetPosition(grid)); };
        grid.MouseMove += (_, e) => { if (painting) At(e.GetPosition(grid)); };
        grid.MouseLeftButtonUp += (_, _) => { painting = false; grid.ReleaseMouseCapture(); Changed(); };
        _editor.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), Child = grid });
        _editor.Children.Add(new Border { Height = 1, Background = Res("BorderBrush"), Margin = new Thickness(0, 14, 0, 12) });
        _editor.Children.Add(Slider("Overall size", 1, 4, 1, () => St.Size, v => St.Size = v, times: true));
    }

    // ───────── Image: any PNG ─────────

    void BuildImage()
    {
        _editor.Children.Add(Label("Use any picture as your crosshair. A PNG with a see-through background works best (64 × 64 or smaller)."));
        var img = CrosshairVisual.LoadImage(St.ImageFile);
        if (img != null) _editor.Children.Add(Dim(Loc.F("{0} × {1} pixels", img.PixelWidth, img.PixelHeight), 12));
        var pick = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T(img == null ? "Choose a picture" : "Choose another picture"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        pick.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Pictures") + "|*.png;*.gif;*.bmp;*.jpg;*.jpeg", Title = Loc.T("Choose a crosshair picture") };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var dir = System.IO.Path.Combine(TweakEngine.DataDir, "crosshairs");
                Directory.CreateDirectory(dir);
                var src = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(dlg.FileName), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                if (src.PixelWidth > 256 || src.PixelHeight > 256)
                {
                    double f = 256.0 / Math.Max(src.PixelWidth, src.PixelHeight);
                    src = System.Windows.Media.Imaging.BitmapFrame.Create(new System.Windows.Media.Imaging.TransformedBitmap(src, new ScaleTransform(f, f)));
                }
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(src);
                var file = System.IO.Path.Combine(dir, "crosshair-" + DateTime.Now.Ticks + ".png");
                using (var fs = File.Create(file)) enc.Save(fs);
                St.ImageFile = file;
                St.Mode = "image";
                Changed(rebuildEditor: true);
            }
            catch (Exception ex) { _ = MainWindow.Instance.Alert(Loc.T("Couldn't use that picture"), ex.Message); }
        };
        _editor.Children.Add(pick);
        _editor.Children.Add(new Border { Height = 1, Background = Res("BorderBrush"), Margin = new Thickness(0, 14, 0, 12) });
        _editor.Children.Add(Slider("Overall size", 0.25, 4, 0.25, () => St.Size, v => St.Size = v, times: true));
    }

    void SetCustom(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return;
        if (!t.StartsWith('#')) t = "#" + t;
        if (System.Text.RegularExpressions.Regex.IsMatch(t, "^#[0-9a-fA-F]{6}$")) { St.Color = t.ToUpperInvariant(); Changed(rebuildEditor: true); }
    }

    TextBlock Label(string t) => new() { Text = Loc.T(t), FontSize = 12.5, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 4, 0, 8) };

    /// <summary>A heading with an on/off switch; its sliders only show while it's on.</summary>
    void Section(string title, Func<bool> get, Action<bool> set, params FrameworkElement[] rows)
    {
        var body = new StackPanel { Margin = new Thickness(0, 4, 0, 0), Visibility = get() ? Visibility.Visible : Visibility.Collapsed };
        foreach (var r in rows) body.Children.Add(r);
        var head = Switch(title, null, get, v => { set(v); body.Visibility = v ? Visibility.Visible : Visibility.Collapsed; }, redraw: true, bold: true);
        head.Margin = new Thickness(0, 10, 0, 0);
        _editor.Children.Add(new Border { Height = 1, Background = Res("BorderBrush"), Margin = new Thickness(0, 10, 0, 0) });
        _editor.Children.Add(head);
        _editor.Children.Add(body);
    }

    FrameworkElement Switch(string title, string note, Func<bool> get, Action<bool> set, bool redraw = false, bool bold = false)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 13, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Res("TextBrush"), TextWrapping = TextWrapping.Wrap });
        if (note != null) { var d = Dim(Loc.T(note), 11.5); d.Margin = new Thickness(0, 2, 0, 0); sp.Children.Add(d); }
        g.Children.Add(sp);
        var cb = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = get(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        cb.Click += (_, _) => { set(cb.IsChecked == true); Changed(); if (!redraw) TweakEngine.Save(); };
        Grid.SetColumn(cb, 1);
        g.Children.Add(cb);
        return g;
    }

    FrameworkElement Slider(string label, double min, double max, double step, Func<double> get, Action<double> set, bool pct = false, bool times = false)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
        var top = new Grid();
        top.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 12.5, Foreground = Res("TextDimBrush") });
        string Fmt(double v) => pct ? Math.Round(v * 100) + "%" : times ? v.ToString("0.##", CultureInfo.CurrentCulture) + "×" : v.ToString("0.#", CultureInfo.CurrentCulture);
        var val = new TextBlock { Text = Fmt(get()), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), HorizontalAlignment = HorizontalAlignment.Right };
        top.Children.Add(val);
        sp.Children.Add(top);
        var sl = new Slider { Style = (Style)FindResource("Seek"), Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, Value = Math.Clamp(get(), min, max), Margin = new Thickness(0, 4, 0, 0) };
        sl.ValueChanged += (_, e) => { set(Math.Round(e.NewValue / step) * step); val.Text = Fmt(get()); Changed(); };
        sp.Children.Add(sl);
        return sp;
    }
}
