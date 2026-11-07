using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Devices;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// Devices: what's plugged in, and the things Pulse can check and change for any brand.
///   Mouse: live polling rate per mouse, a DPI check with a ruler, Windows' pointer settings, button remaps via Macros.
///   Keyboard: key remaps for every game (Windows' Scancode Map), key repeat delay and speed.
///   Audio: playback and recording devices with their format, Bluetooth hands-free warning, a live mic level check.
/// Settings stored inside a device (onboard DPI steps, RGB) belong to the maker's app; the page says which one.
/// Nothing listens to the mouse or mic unless its tab is open.
/// </summary>
public partial class DevicesPage : UserControl, IPage, IReleasable
{
    static DeviceSettings S => TweakEngine.State.Devices ??= new DeviceSettings();
    static string _tab = "mouse";
    readonly Dictionary<string, (TextBlock Label, Border Line)> _tabs = new();
    List<InputDevice> _inputs = new();
    List<AudioDevice> _audio = new();

    MouseMeter _meter;
    MicMeter _mic;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    Action _onTick;

    public DevicesPage()
    {
        InitializeComponent();
        Focusable = true;
        FocusVisualStyle = null;
        foreach (var (key, title, glyph) in new[] { ("mouse", "Mouse", ""), ("keyboard", "Keyboard", ""), ("audio", "Audio", "") })
            Tabs.Children.Add(TabButton(key, title, glyph));
        _tick.Tick += (_, _) => _onTick?.Invoke();
        Unloaded += (_, _) => StopListening();
        Loaded += (_, _) => { if (_onTick == null) ShowTab(_tab); };
        PreviewKeyDown += Page_KeyDown;
    }

    public async void OnShown(object parameter)
    {
        await Scan();
        ShowTab(_tab);
    }

    public void Release() => StopListening();

    async Task Scan()
    {
        try { (_inputs, _audio) = await Task.Run(() => (InputDevices.List(), AudioDevices.List())); } catch { }
        BuildStrip();
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) { await Scan(); ShowTab(_tab); }

    void StopListening()
    {
        _tick.Stop();
        _onTick = null;
        _meter?.Dispose(); _meter = null;
        _mic?.Dispose(); _mic = null;
        _dpiArmed = _dpiRunning = false;
        _capture = null;
    }

    // ───────── building blocks ─────────

    Brush Res(string k) => (Brush)FindResource(k);
    static SolidColorBrush B(Color c, byte a = 255) => new(Color.FromArgb(a, c.R, c.G, c.B));
    static readonly Color Green = Color.FromRgb(0x34, 0xD3, 0x99), Amber = Color.FromRgb(0xFB, 0xBF, 0x24), Cyan = Color.FromRgb(0x22, 0xD3, 0xEE), Red = Color.FromRgb(0xF8, 0x71, 0x71);

    Border Card(UIElement child, double bottom = 14) => new()
    {
        Style = (Style)FindResource("Card"), Padding = new Thickness(22, 18, 22, 18), Margin = new Thickness(0, 0, 0, bottom), Child = child,
    };

    StackPanel Head(string title, string sub)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        sp.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), FontFamily = (FontFamily)FindResource("DisplayFont") });
        if (sub != null) sp.Children.Add(Dim(Loc.T(sub), 12.5, 3));
        return sp;
    }

    TextBlock Dim(string t, double size = 12.5, double top = 0) => new() { Text = t, FontSize = size, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
    TextBlock Strong(string t, double size = 13.5) => new() { Text = t, FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), TextWrapping = TextWrapping.Wrap };

    Border Pill(string text, Color c) => new()
    {
        CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Background = B(c, 0x22), Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = B(c) },
    };

    Button Ghost(string text, Action click)
    {
        var b = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T(text) };
        b.Click += (_, _) => click();
        return b;
    }

    Button Primary(string text, Action click)
    {
        var b = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T(text) };
        b.Click += (_, _) => click();
        return b;
    }

    static void Open(string uri) { try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { } }

    /// <summary>A row for one of Pulse's tweaks: what it does, and a switch that applies or undoes it (with backup).</summary>
    FrameworkElement TweakRow(string id)
    {
        var t = Core.TweakCatalog.All.FirstOrDefault(x => x.Id == id);
        if (t == null) return new Border();
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sp = new StackPanel();
        sp.Children.Add(Strong(Loc.T(t.Title), 13));
        sp.Children.Add(Dim(Loc.T(t.Summary), 12, 2));
        g.Children.Add(sp);
        var cb = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = t.IsOn, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        cb.Click += async (_, _) => { await TweakRunner.Set(t, cb.IsChecked == true); cb.IsChecked = t.IsOn; };
        Grid.SetColumn(cb, 1);
        g.Children.Add(cb);
        return g;
    }

    FrameworkElement TabButton(string key, string title, string glyph)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 11) };
        var icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, Margin = new Thickness(0, 1, 8, 0) };
        var label = new TextBlock { Text = Loc.T(title), FontSize = 13.5, FontWeight = FontWeights.SemiBold };
        sp.Children.Add(icon); sp.Children.Add(label);
        var line = new Border { Height = 2, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom };
        var g = new Grid { Margin = new Thickness(0, 0, 26, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        g.Children.Add(sp); g.Children.Add(line);
        g.MouseLeftButtonUp += (_, _) => ShowTab(key);
        _tabs[key] = (label, line);
        return g;
    }

    void ShowTab(string key)
    {
        StopListening();
        _tab = key;
        foreach (var (k, (label, line)) in _tabs)
        {
            var fg = Res(k == key ? "TextBrush" : "TextDimBrush");
            label.Foreground = fg;
            ((TextBlock)((StackPanel)label.Parent).Children[0]).Foreground = k == key ? Res("AccentBrush") : fg;
            line.Background = k == key ? Res("AccentBrush") : Brushes.Transparent;
        }
        Body.Children.Clear();
        Body.Children.Add(key switch { "keyboard" => BuildKeyboard(), "audio" => BuildAudio(), _ => BuildMouse() });
        Scroll.ScrollToTop();
    }

    /// <summary>One small card per connected device across the top.</summary>
    void BuildStrip()
    {
        DeviceStrip.Children.Clear();
        var items = _inputs.Select(d => (Glyph: d.Kind == DeviceKind.Mouse ? "" : "", d.Name, Sub: string.Join(" · ", new[] { d.Maker, d.Wireless ? Loc.T("Wireless") : null }.Where(x => x != null))))
            .Concat(_audio.Where(a => a.IsDefault).Select(a => (Glyph: a.Input ? "" : "", Name: string.IsNullOrEmpty(a.Adapter) ? a.Name : a.Adapter, Sub: Loc.T(a.Input ? "Default mic" : "Default speakers"))))
            .ToList();
        if (items.Count == 0) { DeviceStrip.Children.Add(Dim(Loc.T("Looking for devices…"))); return; }
        foreach (var (glyph, name, sub) in items)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Background = Res("Surface2Brush"), Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15, Foreground = Res("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190 });
            if (!string.IsNullOrEmpty(sub)) sp.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = Res("TextFaintBrush") });
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            DeviceStrip.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(12, 10, 16, 10), Margin = new Thickness(0, 0, 10, 10), Child = g, ToolTip = name });
        }
    }

    // ───────── Mouse ─────────

    bool _dpiArmed, _dpiRunning;
    double _dpiDistance = 2; // inches
    bool _metric = RegionInfo.CurrentRegion.IsMetric;
    TextBlock _dpiText;
    IntPtr _dpiDevice;

    UIElement BuildMouse()
    {
        var root = new StackPanel();
        try { _meter = new MouseMeter(); } catch { }

        // Live polling rate, one row per mouse that moves.
        var live = new StackPanel();
        live.Children.Add(Head("Polling rate", "Move your mouse in fast circles. Each mouse shows how many times a second it reports to your PC."));
        var rows = new StackPanel();
        live.Children.Add(rows);
        var rowsByDevice = new Dictionary<IntPtr, (TextBlock Rate, Border Bar, TextBlock Note)>();
        var peak = new Dictionary<IntPtr, double>();
        if (_meter == null) rows.Children.Add(Dim(Loc.T("Pulse couldn't listen to the mouse right now. Close the Measure page's test and try again.")));
        else rows.Children.Add(Dim(Loc.T("Waiting for movement…")));
        root.Children.Add(Card(live));

        // DPI check with a ruler.
        var dpi = new StackPanel();
        dpi.Children.Add(Head("DPI check", "Checks your mouse's real DPI, which can differ from what its software says."));
        var steps = new StackPanel();
        var distChips = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var (inches, label) in _metric ? new[] { (5 / 2.54, "5 cm"), (10 / 2.54, "10 cm") } : new[] { (2.0, "2 in"), (4.0, "4 in") })
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = label, GroupName = "dpi-dist", IsChecked = Math.Abs(inches - _dpiDistance) < 0.01 };
            rb.Checked += (_, _) => _dpiDistance = inches;
            distChips.Children.Add(rb);
        }
        if (Math.Abs(_dpiDistance - (_metric ? 5 / 2.54 : 2)) > 0.01 && Math.Abs(_dpiDistance - (_metric ? 10 / 2.54 : 4)) > 0.01) _dpiDistance = _metric ? 5 / 2.54 : 2;
        steps.Children.Add(Dim(Loc.T("1. Put a ruler next to your mouse and pick how far you'll move it:"), 13));
        steps.Children.Add(distChips);
        steps.Children.Add(Dim(Loc.T("2. Line the mouse's edge up with 0, press Space, move it straight to the mark, then press Space again."), 13));
        dpi.Children.Add(steps);
        _dpiText = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Res("TextBrush") };
        var arm = Primary("Get ready", () => { _dpiArmed = true; _dpiRunning = false; Focus(); _dpiText.Text = Loc.T("Ready: press Space to start."); });
        arm.HorizontalAlignment = HorizontalAlignment.Left;
        arm.Margin = new Thickness(0, 14, 0, 0);
        dpi.Children.Add(arm);
        dpi.Children.Add(_dpiText);
        root.Children.Add(Card(dpi));

        // Windows settings that change how the mouse feels.
        var win = new StackPanel();
        win.Children.Add(Head("Windows mouse settings", "For games that use the Windows cursor (and the desktop), these keep aim consistent."));
        win.Children.Add(TweakRow("mouse-accel"));
        win.Children.Add(TweakRow("pointer-1to1"));
        root.Children.Add(Card(win));

        // Buttons and on-mouse memory.
        var btn = new StackPanel();
        btn.Children.Add(Head("Buttons, DPI steps and lighting", null));
        btn.Children.Add(Dim(Loc.T("Remap any mouse button, or make it run a macro, on the Macros page. It works with every brand."), 13));
        var go = Primary("Open Macros", () => MainWindow.Instance.Navigate("macros"));
        go.HorizontalAlignment = HorizontalAlignment.Left;
        go.Margin = new Thickness(0, 10, 0, 14);
        btn.Children.Add(go);
        foreach (var d in _inputs.Where(d => d.Kind == DeviceKind.Mouse))
        {
            var line = d.MakerApp != null
                ? Loc.F("{0}: DPI steps, polling rate and lighting saved on the mouse itself are changed in {1}.", d.Name, d.MakerApp)
                : Loc.F("{0}: settings saved on the mouse itself (if it has any) are changed with its maker's software.", d.Name);
            btn.Children.Add(Dim(line, 12.5, 4));
        }
        root.Children.Add(Card(btn));

        if (_meter != null)
        {
            _onTick = () =>
            {
                double now = _meter.Now;
                foreach (var (dev, st) in _meter.Devices)
                {
                    if (!rowsByDevice.TryGetValue(dev, out var row))
                    {
                        if (rowsByDevice.Count == 0) rows.Children.Clear();
                        var name = _inputs.FirstOrDefault(i => i.Handles.Contains(dev))?.Name ?? Loc.T("Mouse");
                        var g = new Grid { Margin = new Thickness(0, 4, 0, 8) };
                        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
                        g.ColumnDefinitions.Add(new ColumnDefinition());
                        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                        var n = Strong(name, 13);
                        n.TextTrimming = TextTrimming.CharacterEllipsis;
                        n.VerticalAlignment = VerticalAlignment.Center;
                        g.Children.Add(n);
                        var track = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 16, 0) };
                        track.Children.Add(new Border { Style = (Style)FindResource("BarTrack"), Height = 8 });
                        var bar = new Border { Style = (Style)FindResource("BarFill"), Height = 8, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
                        track.Children.Add(bar);
                        Grid.SetColumn(track, 1);
                        g.Children.Add(track);
                        var rate = new TextBlock { FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), HorizontalAlignment = HorizontalAlignment.Right, FontFamily = (FontFamily)FindResource("DisplayFont") };
                        Grid.SetColumn(rate, 2);
                        g.Children.Add(rate);
                        var note = Dim("", 11.5);
                        Grid.SetRow(note, 1);
                        rows.Children.Add(g);
                        rows.Children.Add(note);
                        rowsByDevice[dev] = row = (rate, bar, note);
                    }
                    double hz = MouseMeter.Rate(st, now);
                    if (hz > 0) peak[dev] = Math.Max(peak.GetValueOrDefault(dev), hz);
                    double shown = peak.GetValueOrDefault(dev);
                    if (shown > 0)
                    {
                        int nearest = MouseMeter.Nearest(shown);
                        row.Rate.Text = nearest.ToString("#,0", CultureInfo.CurrentCulture) + " Hz";
                        var track = (FrameworkElement)row.Bar.Parent;
                        row.Bar.Width = Math.Max(4, track.ActualWidth * Math.Min(1, Math.Log(nearest / 100.0) / Math.Log(8000 / 100.0)));
                        row.Note.Text = nearest <= 125 ? Loc.T("125 Hz is the old USB default. Most gaming mice can do 1,000 Hz or more in their software.")
                            : nearest >= 2000 ? Loc.T("Very high polling uses more CPU. If games stutter, try 1,000 Hz.") : "";
                    }
                    else row.Rate.Text = "…";
                }
                // DPI check: show the count while moving.
                if (_dpiRunning && _meter.Devices.TryGetValue(_dpiDevice, out var ds))
                    _dpiText.Text = Loc.F("Moving… {0} counts so far. Press Space at the mark.", Math.Abs(ds.CountX).ToString("#,0", CultureInfo.CurrentCulture));
                else if (_dpiRunning && _dpiDevice == IntPtr.Zero && _meter.Devices.Count > 0)
                    _dpiDevice = _meter.Devices.OrderByDescending(d => d.Value.LastAt).First().Key;
            };
            _tick.Start();
        }
        return root;
    }

    void Page_KeyDown(object sender, KeyEventArgs e)
    {
        // Key capture for remaps.
        if (_capture != null)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (key == Key.LWin || key == Key.RWin) vk = key == Key.LWin ? 0x5B : 0x5C;
            var sc = KeyRemap.ScanOf(vk);
            if (sc != 0) { var c = _capture; _capture = null; c(sc); }
            return;
        }
        if (e.Key != Key.Space || !_dpiArmed || _meter == null) return;
        e.Handled = true;
        if (!_dpiRunning)
        {
            _meter.Reset();
            _dpiDevice = IntPtr.Zero;
            _dpiRunning = true;
            _dpiText.Text = Loc.T("Go: move the mouse straight to the mark, then press Space.");
            return;
        }
        _dpiRunning = _dpiArmed = false;
        if (_dpiDevice == IntPtr.Zero || !_meter.Devices.TryGetValue(_dpiDevice, out var s) || Math.Abs(s.CountX) < 50)
        {
            _dpiText.Text = Loc.T("Didn't see enough movement. Click Get ready to try again.");
            return;
        }
        double counts = Math.Sqrt((double)s.CountX * s.CountX + (double)s.CountY * s.CountY);
        double measured = counts / _dpiDistance;
        int[] common = { 400, 800, 1000, 1200, 1600, 1800, 2000, 2400, 3200, 4000, 6400 };
        int near = common.OrderBy(c => Math.Abs(c - measured)).First();
        var name = _inputs.FirstOrDefault(i => i.Handles.Contains(_dpiDevice))?.Name ?? Loc.T("Your mouse");
        _dpiText.Text = Loc.F("{0}: about {1} DPI (closest common setting: {2}). Try twice more; results within 5% are normal for a hand measurement.",
            name, Math.Round(measured).ToString("#,0", CultureInfo.CurrentCulture), near.ToString("#,0", CultureInfo.CurrentCulture));
    }

    // ───────── Keyboard ─────────

    Action<ushort> _capture;
    List<KeyRemap.Map> _maps = new();

    UIElement BuildKeyboard()
    {
        var root = new StackPanel();
        try { _maps = KeyRemap.Read(); } catch { _maps = new(); }

        // Remaps.
        var rm = new StackPanel();
        rm.Children.Add(Head("Remap keys", "Changes a key for every app and game, built into Windows (no app needs to keep running). Applies after you sign out or restart."));
        if (S.RemapPending)
        {
            var bar = new Border { Background = B(Amber, 0x1C), BorderBrush = B(Amber, 0x55), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 12) };
            bar.Child = new TextBlock { Text = Loc.T("Sign out or restart to use your new key layout."), Foreground = B(Amber), FontSize = 12.5, FontWeight = FontWeights.SemiBold };
            rm.Children.Add(bar);
        }
        var list = new StackPanel();
        rm.Children.Add(list);
        void ShowMaps()
        {
            list.Children.Clear();
            if (_maps.Count == 0) list.Children.Add(Dim(Loc.T("No keys are remapped."), 13));
            foreach (var m in _maps.ToList())
            {
                var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(KeyCap(KeyRemap.NameOf(m.From)));
                sp.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = Res("TextFaintBrush"), Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(m.To == 0 ? Pill(Loc.T("Turned off"), Red) : KeyCap(KeyRemap.NameOf(m.To)));
                g.Children.Add(sp);
                var x = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Remove"), VerticalAlignment = VerticalAlignment.Center };
                x.Click += (_, _) => { _maps.Remove(m); Save(); };
                Grid.SetColumn(x, 1);
                g.Children.Add(x);
                list.Children.Add(g);
            }
        }
        void Save()
        {
            try { KeyRemap.Write(_maps, S); TweakEngine.Save(); TweakEngine.AddLog("devices", "Key remaps: " + _maps.Count); MainWindow.Instance.Toast(Loc.T("Saved. Sign out or restart to use it.")); ShowTab("keyboard"); }
            catch (Exception ex) { _ = MainWindow.Instance.Alert(Loc.T("Couldn't save the remap"), ex.Message); }
        }
        ShowMaps();

        // Add a remap: press the key, then what it should do.
        var add = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var from = Ghost("Press the key to change", () => { });
        ushort fromCode = 0;
        var to = Ghost("Press the key it should act as", () => { });
        to.IsEnabled = false;
        var off = Ghost("Turn it off", () => { });
        off.IsEnabled = false;
        var hint = Dim("", 12, 8);
        from.Click += (_, _) => { from.Content = Loc.T("Press a key now…"); Focus(); _capture = sc => { fromCode = sc; from.Content = KeyRemap.NameOf(sc); to.IsEnabled = off.IsEnabled = true; hint.Text = Loc.T("Now choose what it should do."); }; };
        void AddMap(ushort target)
        {
            _maps.RemoveAll(m => m.From == fromCode);
            _maps.Add(new KeyRemap.Map(fromCode, target));
            Save();
        }
        to.Click += (_, _) => { to.Content = Loc.T("Press a key now…"); Focus(); _capture = sc => AddMap(sc); };
        off.Click += (_, _) => AddMap(0);
        add.Children.Add(from);
        add.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = Res("TextFaintBrush"), Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        add.Children.Add(to);
        off.Margin = new Thickness(8, 0, 0, 0);
        add.Children.Add(off);
        rm.Children.Add(add);
        rm.Children.Add(hint);

        // One-click presets and undo.
        var pre = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        foreach (var (name, maps) in KeyRemap.Presets)
        {
            bool on = maps.All(p => _maps.Contains(p));
            var chip = new System.Windows.Controls.Primitives.ToggleButton { Style = (Style)FindResource("LinkToggle"), Content = Loc.T(name), IsChecked = on, Margin = new Thickness(0, 0, 18, 6) };
            chip.Click += (_, _) =>
            {
                if (chip.IsChecked == true) { foreach (var p in maps) { _maps.RemoveAll(m => m.From == p.From); _maps.Add(p); } }
                else _maps.RemoveAll(m => maps.Contains(m));
                Save();
            };
            pre.Children.Add(chip);
        }
        rm.Children.Add(new TextBlock { Text = Loc.T("Quick picks"), FontSize = 12, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 16, 0, 2) });
        rm.Children.Add(pre);
        if (S.ScancodeBackup != null)
        {
            var undo = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Put back the keyboard layout from before Pulse"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            undo.Click += (_, _) => { try { KeyRemap.Restore(S); TweakEngine.Save(); ShowTab("keyboard"); } catch (Exception ex) { _ = MainWindow.Instance.Alert(Loc.T("Couldn't restore"), ex.Message); } };
            rm.Children.Add(undo);
        }
        root.Children.Add(Card(rm));

        // Key repeat.
        var rep = new StackPanel();
        rep.Children.Add(Head("Key repeat", "How long a held key waits before it repeats, and how fast it repeats. Changes right away."));
        int delay = KeyRepeat.Delay, speed = KeyRepeat.Speed;
        var dText = new TextBlock { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
        var sText = new TextBlock { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
        void Labels() { dText.Text = Loc.F("{0} ms", KeyRepeat.DelayMs(delay)); sText.Text = Loc.F("{0} per second", Math.Round(KeyRepeat.RepeatsPerSecond(speed))); }
        Labels();
        FrameworkElement SliderRow(string label, TextBlock val, int max, int value, Action<int> set)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
            var top = new Grid();
            top.Children.Add(new TextBlock { Text = Loc.T(label), Foreground = Res("TextDimBrush") });
            top.Children.Add(val);
            sp.Children.Add(top);
            var sl = new Slider { Style = (Style)FindResource("Seek"), Minimum = 0, Maximum = max, TickFrequency = 1, IsSnapToTickEnabled = true, Value = value, Margin = new Thickness(0, 6, 0, 0) };
            sl.ValueChanged += (_, e) => { set((int)e.NewValue); Labels(); try { KeyRepeat.Set(delay, speed, S); TweakEngine.Save(); } catch { } };
            sp.Children.Add(sl);
            return sp;
        }
        rep.Children.Add(SliderRow("Delay before repeating", dText, 3, delay, v => delay = v));
        rep.Children.Add(SliderRow("Repeat speed", sText, 31, speed, v => speed = v));
        var test = new TextBox { Style = (Style)FindResource("SearchBox"), Tag = Loc.T("Hold a key here to try it"), Margin = new Thickness(0, 8, 0, 0) };
        rep.Children.Add(test);
        if (S.OriginalKeyDelay is int od && S.OriginalKeySpeed is int os && (od != delay || os != speed))
        {
            var back = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Put back the settings from before"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            back.Click += (_, _) => { KeyRepeat.Set(od, os, S); ShowTab("keyboard"); };
            rep.Children.Add(back);
        }
        root.Children.Add(Card(rep));

        var more = new StackPanel();
        more.Children.Add(Head("Shortcuts that interrupt games", null));
        more.Children.Add(TweakRow("sticky-keys"));
        root.Children.Add(Card(more));
        foreach (var d in _inputs.Where(d => d.Kind == DeviceKind.Keyboard && d.MakerApp != null))
            root.Children.Add(Dim(Loc.F("{0}: lighting, rapid trigger and actuation saved on the keyboard are changed in {1}.", d.Name, d.MakerApp), 12, 2));
        return root;
    }

    Border KeyCap(string text) => new()
    {
        Background = Res("Surface2Brush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1, 1, 1, 3), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 4, 10, 4), Child = new TextBlock { Text = text, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12.5, FontWeight = FontWeights.SemiBold },
    };

    // ───────── Audio ─────────

    UIElement BuildAudio()
    {
        var root = new StackPanel();
        foreach (var (input, title, sub) in new[] { (false, "Playback", "Speakers and headsets Windows can play to."), (true, "Recording", "Microphones Windows can record from.") })
        {
            var sp = new StackPanel();
            sp.Children.Add(Head(title, sub));
            var list = _audio.Where(a => a.Input == input).ToList();
            if (list.Count == 0) sp.Children.Add(Dim(Loc.T("None found."), 13));
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (i > 0) sp.Children.Add(new Border { Height = 1, Background = Res("BorderBrush"), Margin = new Thickness(0, 10, 0, 10) });
                var head = new WrapPanel();
                head.Children.Add(Strong(string.IsNullOrEmpty(a.Adapter) ? a.Name : a.Adapter + " — " + a.Name, 13.5));
                if (a.IsDefault) head.Children.Add(Pill(Loc.T("Default"), Green));
                sp.Children.Add(head);
                if (a.Format.Length > 0) sp.Children.Add(Dim(a.Format, 12, 3));
                if (a.HandsFree)
                    sp.Children.Add(new TextBlock
                    {
                        Text = Loc.T("This is the Bluetooth \"Hands-Free\" mode: sound drops to low-quality mono whenever the mic is on. For games, use the stereo version of this headset for sound, and a separate mic or the headset's USB dongle."),
                        Foreground = B(Amber), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
                    });
                else if (!input && a.SampleRate > 96000)
                    sp.Children.Add(Dim(Loc.T("Above 96 kHz adds nothing you can hear in games and can make some games' audio crackle. 48 kHz is the safe choice."), 12, 4));
            }
            root.Children.Add(Card(sp));
        }

        // Mic check.
        var mic = new StackPanel();
        mic.Children.Add(Head("Mic check", "Talk normally. The bar shows how loud your default mic picks you up. Nothing is recorded or saved."));
        var track = new Grid { Height = 12, Margin = new Thickness(0, 4, 0, 0) };
        track.Children.Add(new Border { Style = (Style)FindResource("BarTrack"), Height = 12 });
        var fill = new Border { Height = 12, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Background = B(Green) };
        track.Children.Add(fill);
        mic.Children.Add(track);
        var verdict = Dim("", 12.5, 8);
        mic.Children.Add(verdict);
        var startBtn = Primary("Start mic check", () => { });
        startBtn.HorizontalAlignment = HorizontalAlignment.Left;
        startBtn.Margin = new Thickness(0, 12, 0, 0);
        double smooth = 0, loudest = 0;
        startBtn.Click += (_, _) =>
        {
            if (_mic != null) { StopListening(); startBtn.Content = Loc.T("Start mic check"); fill.Width = 0; return; }
            _mic = new MicMeter();
            if (!_mic.Ok)
            {
                _mic.Dispose(); _mic = null;
                verdict.Text = Loc.T("Couldn't open the mic. Check that a mic is plugged in and that desktop apps may use it in Windows' privacy settings.");
                return;
            }
            startBtn.Content = Loc.T("Stop");
            loudest = 0;
            _onTick = () =>
            {
                if (_mic == null) return;
                double peak = _mic.Read();
                smooth = Math.Max(peak, smooth * 0.85);
                loudest = Math.Max(loudest, peak);
                double db = 20 * Math.Log10(Math.Max(1e-4, smooth));        // -80 … 0 dBFS
                double pos = Math.Clamp((db + 60) / 60, 0, 1);
                fill.Width = Math.Max(0, track.ActualWidth * pos);
                fill.Background = B(smooth > 0.95 ? Red : smooth > 0.6 ? Amber : Green);
                verdict.Text = loudest < 0.02 ? Loc.T("Can't hear you yet. Say something.")
                    : loudest < 0.1 ? Loc.T("Quiet. Turn the mic up a little (Sound settings → your mic → Properties → Levels).")
                    : loudest > 0.98 ? Loc.T("Too loud: your voice is cutting off (clipping). Turn the mic down a little.")
                    : Loc.T("Sounds good.");
            };
            _tick.Start();
        };
        mic.Children.Add(startBtn);
        root.Children.Add(Card(mic));

        var more = new StackPanel();
        more.Children.Add(Head("Sound while you play", null));
        more.Children.Add(TweakRow("comm-ducking"));
        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var snd = Ghost("Open Sound settings", () => Open("ms-settings:sound"));
        var priv = Ghost("Mic privacy settings", () => Open("ms-settings:privacy-microphone"));
        priv.Margin = new Thickness(10, 0, 0, 0);
        links.Children.Add(snd);
        links.Children.Add(priv);
        more.Children.Add(links);
        root.Children.Add(Card(more));
        return root;
    }
}
