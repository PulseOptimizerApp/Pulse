using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Devices;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// The Mouse page: every connected mouse, and for each one its DPI stages, polling rate, sensor settings and button
/// bindings, written to the mouse itself (so they work in every game, with Pulse closed). The protocols come from
/// OpenMouse's open-source mouse-protocol drivers (AGPL-3.0, ~55 brands), run by <see cref="MouseDrivers"/>.
/// Mice no driver knows still get the polling-rate and DPI measurements and Windows' pointer settings.
/// </summary>
public partial class MousePage : UserControl, IPage, IReleasable
{
    static DeviceSettings S => TweakEngine.State.Devices ??= new DeviceSettings();
    static string _openKey, _tab;

    /// <summary>A mouse on the page: from a driver (settings) or only from Windows (measure only).</summary>
    sealed class Mouse
    {
        public string Key;
        public DriverDevice Driver;
        public JsonNode Status;
        public string StatusError;
        public GearDevice Raw;
        public string Name => (string)Status?["name"] is { Length: > 0 } n ? n : Driver?.ProductName is { Length: > 0 } p ? p : Raw?.Name ?? "Mouse";
        public string Brand => Driver?.Brand ?? Raw?.Maker ?? "";
        public int? Battery => Status?["batteryPercent"] is JsonValue v && v.TryGetValue<double>(out var b) ? (int)Math.Round(b) : null;
        public bool Charging => ((string)Status?["batteryState"] ?? "").StartsWith("Charging", StringComparison.Ordinal);
        public bool? Wireless => (string)Status?["connectionType"] switch { "Wireless" => true, "Wired" => false, _ => Raw?.Wireless };
        public int? Dpi => Status?["dpi"] is JsonValue d && d.TryGetValue<double>(out var x) && x > 0 ? (int)x : null;
        public int? Hz => Status?["pollingRateHz"] is JsonValue v && v.TryGetValue<double>(out var h) && h > 0 ? (int)h : null;
        public bool Can(string method) => Status?["__methods"] is JsonArray a && a.Any(x => (string)x == method);
        public string PhotoKey => string.Concat((Driver?.VendorId.ToString("X4") + Driver?.ProductId.ToString("X4") + Name).Where(char.IsLetterOrDigit));
    }

    readonly List<Mouse> _mice = new();
    bool _loading;
    string _problem;
    MouseMeter _meter;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    Action _onTick;
    static readonly DispatcherTimer StopLater = new() { Interval = TimeSpan.FromSeconds(45) };

    static readonly Color Green = Color.FromRgb(0x7D, 0xC5, 0x8E), Amber = Color.FromRgb(0xE3, 0xB0, 0x4B), Red = Color.FromRgb(0xE5, 0x67, 0x4F);

    static MousePage() => StopLater.Tick += (_, _) => { StopLater.Stop(); MouseDrivers.Stop(); };

    public MousePage()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => _onTick?.Invoke();
        PreviewKeyDown += DpiKey;
        // The drivers run in a hidden browser engine: shut it down a little after leaving this page.
        Unloaded += (_, _) => { StopMeasuring(); StopLater.Start(); };
        Loaded += (_, _) => StopLater.Stop();
    }

    public async void OnShown(object parameter) { Show(); await Load(); }
    public void Release() { StopMeasuring(); }

    async Task Load()
    {
        if (_loading) return;
        _loading = true;
        RefreshBtn.IsEnabled = false;
        _problem = null;
        Action step = () => Dispatcher.BeginInvoke(() => { if (_progress != null) _progress.Text = Loc.T(MouseDrivers.Step); });
        MouseDrivers.StepChanged += step;
        try
        {
            // 1. Mice Windows sees: on screen at once (measure only until a driver answers).
            var raw = await Task.Run(() => InputDevices.List().Where(d => d.Kind == DeviceKind.Mouse).ToList());
            var keep = _mice.Where(m => m.Driver != null).ToList();
            _mice.Clear();
            _mice.AddRange(keep);
            foreach (var r in raw)
                if (!_mice.Any(m => Same(m, r))) _mice.Add(new Mouse { Key = "raw-" + r.Key, Raw = r });
            Show();

            // 2. The drivers: which mice they can configure, then each one's settings.
            try
            {
                await MouseDrivers.Start();
                var found = await MouseDrivers.Scan();
                foreach (var d in found)
                {
                    var m = _mice.FirstOrDefault(x => x.Driver?.Key == d.Key) ?? new Mouse { Key = d.Key, Driver = d };
                    m.Driver = d;
                    _mice.RemoveAll(x => x.Driver == null && x.Raw != null && Same(m, x.Raw));
                    m.Raw ??= raw.FirstOrDefault(r => Same(m, r));
                    if (!_mice.Contains(m)) _mice.Add(m);
                    Show();
                    try { m.Status = await MouseDrivers.Status(d.Key); }
                    catch (Exception ex) { m.StatusError = ex.Message; }
                    if (m.Status?["kind"] is JsonValue k && (string)k == "keyboard") _mice.Remove(m);
                    Show();
                }
            }
            catch (Exception ex) { _problem = MouseDrivers.Problem ?? ex.Message; }
            _mice.Sort((a, b) => (a.Driver == null).CompareTo(b.Driver == null) is var c && c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            if (_openKey != null && !_mice.Any(m => m.Key == _openKey)) _openKey = null;
        }
        finally
        {
            MouseDrivers.StepChanged -= step;
            _loading = false;
            RefreshBtn.IsEnabled = true;
            Show();
        }
    }

    static bool Same(Mouse m, GearDevice r) => m.Driver != null && m.Driver.VendorId.ToString("X4") == r.Vid && m.Driver.ProductId.ToString("X4") == r.Pid;

    TextBlock _progress;

    async void Refresh_Click(object sender, RoutedEventArgs e) => await Load();
    void AllMice_Click(object sender, RoutedEventArgs e) { _openKey = null; Show(); }
    void Other_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.Navigate("devices");

    void StopMeasuring()
    {
        _tick.Stop();
        _onTick = null;
        _meter?.Dispose();
        _meter = null;
    }

    // ───────── building blocks ─────────

    Brush Res(string k) => (Brush)FindResource(k);
    static SolidColorBrush B(Color c, byte a = 255) => new(Color.FromArgb(a, c.R, c.G, c.B));
    FontFamily Icons => (FontFamily)FindResource("IconFont");

    TextBlock T(string text, double size = 13, FontWeight? w = null, string brush = "TextBrush") => new()
    {
        Text = text, FontSize = size, FontWeight = w ?? FontWeights.Normal, Foreground = Res(brush), TextWrapping = TextWrapping.Wrap,
    };

    TextBlock Icon(string glyph, double size, Brush fg = null) => new() { Text = glyph, FontFamily = Icons, FontSize = size, Foreground = fg ?? Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center };

    Border Panel(UIElement child, double bottom = 14) => new()
    {
        Background = Res("SurfaceBrush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
        Padding = new Thickness(22, 20, 22, 20), Margin = new Thickness(0, 0, 0, bottom), Child = child,
    };

    StackPanel Head(string title, string sub)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        sp.Children.Add(T(Loc.T(title), 15.5, FontWeights.SemiBold));
        if (sub != null) { var d = T(Loc.T(sub), 12.5, null, "TextDimBrush"); d.Margin = new Thickness(0, 3, 0, 0); sp.Children.Add(d); }
        return sp;
    }

    Border Pill(string text, Color c, bool dot = true) => new()
    {
        CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 8, 3), Background = B(c, 0x1F), VerticalAlignment = VerticalAlignment.Center,
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new Ellipse { Width = 6, Height = 6, Fill = B(c), Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = dot ? Visibility.Visible : Visibility.Collapsed },
                new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = B(c) },
            },
        },
    };

    FrameworkElement Battery(int pct, bool charging, double size = 12)
    {
        var col = pct <= 15 ? Red : pct <= 30 ? Amber : Green;
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var shell = new Grid { Width = size * 1.7, Height = size * 0.9, VerticalAlignment = VerticalAlignment.Center };
        shell.Children.Add(new Border { BorderBrush = Res("TextDimBrush"), BorderThickness = new Thickness(1.2), CornerRadius = new CornerRadius(2.5) });
        shell.Children.Add(new Border { Background = B(col), CornerRadius = new CornerRadius(1), Margin = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(1, (size * 1.7 - 4) * pct / 100.0) });
        sp.Children.Add(shell);
        sp.Children.Add(new Border { Width = 1.6, Height = size * 0.4, Background = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0.5, 0, 0, 0) });
        if (charging) sp.Children.Add(Icon("", size * 0.85, B(Amber)));
        sp.Children.Add(new TextBlock { Text = pct + "%", FontSize = size + 0.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    FrameworkElement Stats(Mouse m, double size = 12.5)
    {
        var sp = new WrapPanel();
        void Add(FrameworkElement e) { e.Margin = new Thickness(0, 0, 16, 4); sp.Children.Add(e); }
        StackPanel IconText(string glyph, string bold, string text)
        {
            var s = new StackPanel { Orientation = Orientation.Horizontal };
            s.Children.Add(Icon(glyph, size));
            if (bold != null) s.Children.Add(new TextBlock { Text = bold, FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            s.Children.Add(new TextBlock { Text = text, FontSize = size, Foreground = Res("TextDimBrush"), Margin = new Thickness(bold == null ? 7 : 4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return s;
        }
        if (m.Wireless is bool w) Add(IconText(w ? "" : "", null, Loc.T(w ? "Wireless" : "Wired")));
        if (m.Battery is int b) Add(Battery(b, m.Charging, size - 0.5));
        if (m.Hz is int hz) Add(IconText("", hz.ToString("#,0", CultureInfo.CurrentCulture), "Hz"));
        return sp;
    }

    /// <summary>The device's own photo (one you added), or its brand name set large and faint on the stage.</summary>
    FrameworkElement Stage(Mouse m, double height)
    {
        var g = new Grid { Height = height, ClipToBounds = true };
        g.Children.Add(new Border
        {
            Background = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.58), Center = new Point(0.5, 0.58), RadiusX = 0.6, RadiusY = 0.55,
                GradientStops = { new GradientStop(Color.FromArgb(0x2E, Green.R, Green.G, Green.B), 0), new GradientStop(Color.FromArgb(0x0E, Green.R, Green.G, Green.B), 0.5), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) },
            },
        });
        var photo = Photo(m);
        if (photo != null)
        {
            g.Children.Add(new Ellipse { Width = height * 0.5, Height = height * 0.06, Fill = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), Effect = new BlurEffect { Radius = 14 }, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, height * 0.08) });
            g.Children.Add(new Image { Source = photo, MaxHeight = height * 0.78, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, height * 0.06) });
        }
        else
        {
            var brand = (m.Brand.Length > 0 ? m.Brand : "Mouse").ToUpper(CultureInfo.InvariantCulture);
            g.Children.Add(new TextBlock
            {
                Text = brand, FontFamily = (FontFamily)FindResource("DisplayFont"), FontWeight = FontWeights.Black, FontSize = Math.Min(44, height * 0.17),
                Foreground = new LinearGradientBrush(Color.FromArgb(0x30, 255, 255, 255), Color.FromArgb(0x0C, 255, 255, 255), 90),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
            });
        }
        return g;
    }

    static ImageSource Photo(Mouse m)
    {
        if (!S.Photos.TryGetValue(m.PhotoKey, out var name)) return null;
        var f = System.IO.Path.Combine(TweakEngine.DataDir, "device-photos", name);
        if (!File.Exists(f)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            img.DecodePixelHeight = 520; img.UriSource = new Uri(f); img.EndInit(); img.Freeze();
            return img;
        }
        catch { return null; }
    }

    void PickPhoto(Mouse m)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Pictures") + "|*.png;*.jpg;*.jpeg;*.webp", Title = Loc.T("Choose a photo of this device") };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var dir = System.IO.Path.Combine(TweakEngine.DataDir, "device-photos");
            Directory.CreateDirectory(dir);
            var src = BitmapFrame.Create(new Uri(dlg.FileName), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(src);
            var name = m.PhotoKey + "-" + DateTime.Now.Ticks + ".png";
            using (var fs = File.Create(System.IO.Path.Combine(dir, name))) enc.Save(fs);
            S.Photos[m.PhotoKey] = name;
            TweakEngine.Save();
            Show();
        }
        catch (Exception ex) { _ = MainWindow.Instance.Alert(Loc.T("Couldn't use that picture"), ex.Message); }
    }

    Button MoreMenu(Mouse m)
    {
        var b = new Button { Style = (Style)FindResource("LinkButton"), Content = Icon("", 14), Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("More") };
        var menu = new ContextMenu();
        var add = new MenuItem { Header = Loc.T(Photo(m) == null ? "Use my own photo…" : "Change photo…") };
        add.Click += (_, _) => PickPhoto(m);
        menu.Items.Add(add);
        if (Photo(m) != null) { var rm = new MenuItem { Header = Loc.T("Remove photo") }; rm.Click += (_, _) => { S.Photos.Remove(m.PhotoKey); TweakEngine.Save(); Show(); }; menu.Items.Add(rm); }
        var win = new MenuItem { Header = Loc.T("Windows mouse settings") };
        win.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:mousetouchpad") { UseShellExecute = true }); } catch { } };
        menu.Items.Add(win);
        b.Click += (_, _) => { menu.PlacementTarget = b; menu.IsOpen = true; };
        return b;
    }

    // ───────── layout ─────────

    void Show()
    {
        StopMeasuring();
        BuildList();
        Main.Children.Clear();
        var m = _openKey == null ? null : _mice.FirstOrDefault(x => x.Key == _openKey);
        Main.Children.Add(m == null ? Gallery() : Detail(m));
        var tt = new TranslateTransform(0, 10);
        Main.RenderTransform = tt;
        Main.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    void Open(Mouse m) { _openKey = m.Key; _tab = null; Show(); Scroll.ScrollToTop(); }

    void BuildList()
    {
        MouseList.Children.Clear();
        if (_mice.Count == 0)
        {
            var t = T(Loc.T(_loading ? "Looking for mice…" : "No mice found."), 12.5, null, "TextDimBrush");
            t.Margin = new Thickness(8, 4, 8, 0);
            MouseList.Children.Add(t);
            return;
        }
        foreach (var m in _mice)
        {
            bool on = m.Key == _openKey;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var thumb = new Grid { Width = 36, Height = 36, Margin = new Thickness(0, 0, 10, 0) };
            thumb.Children.Add(new Border { CornerRadius = new CornerRadius(9), Background = Res("Surface2Brush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1) });
            var photo = Photo(m);
            thumb.Children.Add(photo != null ? new Image { Source = photo, Margin = new Thickness(3), Stretch = Stretch.Uniform }
                : new TextBlock { Text = (m.Brand.Length > 0 ? m.Brand[..1] : "M").ToUpperInvariant(), FontWeight = FontWeights.Bold, FontSize = 14, Foreground = Res("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            thumb.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = B(Green), Stroke = Res("SurfaceBrush"), StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -2) });
            g.Children.Add(thumb);
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = m.Name.ToUpper(CultureInfo.CurrentCulture), FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (m.Battery is int b) { var bat = Battery(b, m.Charging, 9); bat.Margin = new Thickness(0, 3, 0, 0); bat.HorizontalAlignment = HorizontalAlignment.Left; sp.Children.Add(bat); }
            else sp.Children.Add(new TextBlock { Text = m.Driver == null ? Loc.T("Measure only") : m.Brand, FontSize = 11, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            var row = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand, Child = g, ToolTip = m.Name,
                Background = on ? Res("Surface2Brush") : Brushes.Transparent, BorderBrush = on ? Res("BorderBrush") : Brushes.Transparent, BorderThickness = new Thickness(1),
            };
            if (!on) { row.MouseEnter += (_, _) => row.Background = Res("SurfaceHoverBrush"); row.MouseLeave += (_, _) => row.Background = Brushes.Transparent; }
            row.MouseLeftButtonUp += (_, _) => Open(m);
            MouseList.Children.Add(row);
        }
    }

    UIElement Gallery()
    {
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = Loc.T("Mouse"), Style = (Style)FindResource("H1") });
        var sub = T(Loc.T("Your mice. Pick one to change its DPI, polling rate and buttons. Changes are saved on the mouse, so they work in every game."), 13, null, "TextDimBrush");
        sub.Margin = new Thickness(0, 6, 0, 26);
        root.Children.Add(sub);
        if (_loading)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
            var ring = new Ellipse { Width = 14, Height = 14, StrokeThickness = 2, Stroke = Res("AccentBrush"), StrokeDashArray = new DoubleCollection { 2.5, 2 }, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(), Margin = new Thickness(0, 0, 10, 0) };
            ring.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            sp.Children.Add(ring);
            _progress = T(Loc.T(MouseDrivers.Step.Length > 0 ? MouseDrivers.Step : "Looking for mice…"), 12.5, null, "TextDimBrush");
            sp.Children.Add(_progress);
            root.Children.Add(sp);
        }
        if (_problem != null)
        {
            var warn = new Border
            {
                Background = B(Amber, 0x18), BorderBrush = B(Amber, 0x50), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 18),
            };
            var wsp = new StackPanel();
            wsp.Children.Add(new TextBlock { Text = Loc.T("Mouse settings couldn't start") + ": " + Loc.T(_problem), Foreground = B(Amber), FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
            wsp.Children.Add(T(Loc.T("Your mice are still listed below for measuring. Press Scan again to retry, or copy the details and send them to the Pulse team."), 12, null, "TextDimBrush").Also(x => x.Margin = new Thickness(0, 4, 0, 6)));
            var copy = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Copy details"), HorizontalAlignment = HorizontalAlignment.Left };
            copy.Click += (_, _) => { try { lock (MouseDrivers.Log) Clipboard.SetText(string.Join(Environment.NewLine, MouseDrivers.Log)); MainWindow.Instance.Toast(Loc.T("Copied.")); } catch { } };
            wsp.Children.Add(copy);
            warn.Child = wsp;
            root.Children.Add(warn);
        }
        var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var m in _mice) wrap.Children.Add(Card(m));
        if (_mice.Count == 0)
        {
            var e = T(Loc.T(_loading ? "" : "No mice found. Plug one in or turn it on, then press Scan again."), 13, null, "TextDimBrush");
            e.HorizontalAlignment = HorizontalAlignment.Center;
            wrap.Children.Add(e);
        }
        root.Children.Add(wrap);
        var note = T(Loc.T("Pulse supports about 55 brands, including Logitech, Razer, SteelSeries, Pulsar, Lamzu, WLmouse, VAXEE, Finalmouse, Endgame Gear, G-Wolves, Glorious, Corsair, HyperX, ASUS and ATK/VXE, using the open-source OpenMouse drivers. If your mouse isn't recognised, Pulse can still measure it."), 12, null, "TextFaintBrush");
        note.Margin = new Thickness(0, 18, 0, 0);
        note.MaxWidth = 760;
        root.Children.Add(note);
        return root;
    }

    FrameworkElement Card(Mouse m)
    {
        var dock = new DockPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new TextBlock { Text = m.Name.ToUpper(CultureInfo.CurrentCulture), FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = m.Name });
        var pill = m.Driver != null ? Pill(Loc.T("Connected"), Green) : Pill(Loc.T("Measure only"), Amber);
        pill.Margin = new Thickness(8, 0, 4, 0);
        Grid.SetColumn(pill, 1); top.Children.Add(pill);
        var more = MoreMenu(m); Grid.SetColumn(more, 2); top.Children.Add(more);
        var head = new StackPanel();
        head.Children.Add(top);
        head.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { m.Brand, Loc.T("Mouse") }.Where(x => x.Length > 0)), FontSize = 12, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 6, 0, 8) });
        head.Children.Add(Stats(m));
        if (m.Driver != null && m.Status == null)
            head.Children.Add(T(m.StatusError != null ? Loc.T("Couldn't read it") + ": " + m.StatusError : Loc.T("Reading settings…"), 11.5, null, m.StatusError != null ? "WarnBrush" : "TextFaintBrush"));
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        var btn = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T("Configure"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(0, 9, 0, 9) };
        btn.Click += (_, _) => Open(m);
        DockPanel.SetDock(btn, Dock.Bottom);
        dock.Children.Add(btn);
        dock.Children.Add(Stage(m, 250));
        var card = new Border
        {
            Width = 262, Height = 430, Margin = new Thickness(0, 0, 14, 14), Padding = new Thickness(14), CornerRadius = new CornerRadius(14),
            Background = new LinearGradientBrush(((SolidColorBrush)Res("SurfaceBrush")).Color, ((SolidColorBrush)Res("BgBrush")).Color, 90),
            BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), Child = dock,
        };
        card.MouseEnter += (_, _) => card.BorderBrush = B(Green, 0x66);
        card.MouseLeave += (_, _) => card.BorderBrush = Res("BorderBrush");
        return card;
    }

    // ───────── one mouse ─────────

    UIElement Detail(Mouse m)
    {
        var root = new StackPanel();
        var back = new Button { Style = (Style)FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        back.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { Icon("", 11, Res("AccentBrush")), new TextBlock { Text = Loc.T("All mice"), Margin = new Thickness(8, 0, 0, 0) } } };
        back.Click += (_, _) => { _openKey = null; Show(); };
        root.Children.Add(back);

        var hero = new Grid();
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        hero.ColumnDefinitions.Add(new ColumnDefinition());
        hero.Children.Add(new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, Background = Res("BgBrush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), Child = Stage(m, 230) });
        var info = new StackPanel { Margin = new Thickness(24, 6, 0, 0) };
        var nameRow = new WrapPanel();
        nameRow.Children.Add(new TextBlock { Text = m.Name.ToUpper(CultureInfo.CurrentCulture), FontSize = 24, FontWeight = FontWeights.Bold, FontFamily = (FontFamily)FindResource("DisplayFont"), Foreground = Res("TextBrush"), Margin = new Thickness(0, 0, 12, 0) });
        nameRow.Children.Add(m.Driver != null ? Pill(Loc.T("Connected"), Green) : Pill(Loc.T("Measure only"), Amber));
        nameRow.Children.Add(MoreMenu(m));
        info.Children.Add(nameRow);
        info.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { m.Brand, Loc.T("Mouse") }.Where(x => x.Length > 0)), FontSize = 13, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 6, 0, 12) });
        info.Children.Add(Stats(m, 13.5));
        if (m.Status?["ui"]?["statusNote"] is JsonValue sn) { var n = T((string)sn, 12, null, "TextFaintBrush"); n.Margin = new Thickness(0, 10, 0, 0); info.Children.Add(n); }
        Grid.SetColumn(info, 1);
        hero.Children.Add(info);
        root.Children.Add(Panel(hero));

        var tabs = m.Driver == null
            ? new[] { ("measure", "Measure", ""), ("windows", "Windows settings", "") }
            : new[] { ("performance", "Performance", ""), ("buttons", "Buttons", ""), ("measure", "Measure", ""), ("windows", "Windows settings", "") };
        if (_tab == null || !tabs.Any(t => t.Item1 == _tab)) _tab = tabs[0].Item1;
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        var barGrid = new Grid { Margin = new Thickness(0, 6, 0, 18) };
        barGrid.Children.Add(new Border { VerticalAlignment = VerticalAlignment.Bottom, Height = 1, Background = Res("BorderBrush") });
        barGrid.Children.Add(bar);
        foreach (var (key, title, glyph) in tabs)
        {
            bool on = key == _tab;
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 11) };
            sp.Children.Add(Icon(glyph, 13, on ? Res("AccentBrush") : Res("TextDimBrush")));
            sp.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res(on ? "TextBrush" : "TextDimBrush"), Margin = new Thickness(8, 0, 0, 0) });
            var g = new Grid { Margin = new Thickness(0, 0, 26, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            g.Children.Add(sp);
            g.Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom, Background = on ? Res("AccentBrush") : Brushes.Transparent });
            g.MouseLeftButtonUp += (_, _) => { _tab = key; Show(); };
            bar.Children.Add(g);
        }
        if (m.Driver == null)
        {
            var note = T(Loc.T("Pulse doesn't have a driver for this mouse yet, so its DPI, polling rate and buttons are changed in its maker's app. Pulse can still measure it, and Macros can add actions to its buttons."), 12.5, null, "TextDimBrush");
            note.Margin = new Thickness(0, 0, 0, 14);
            root.Children.Add(note);
        }
        root.Children.Add(barGrid);
        root.Children.Add(_tab switch
        {
            "performance" => Performance(m),
            "buttons" => ButtonsTab(m),
            "windows" => WindowsTab(),
            _ => Measure(m),
        });
        return root;
    }

    /// <summary>Runs a change on the mouse, then reads its status again and redraws.</summary>
    async Task Apply(Mouse m, string method, params object[] args)
    {
        try
        {
            await MouseDrivers.Invoke(m.Key, method, args);
            try { m.Status = await MouseDrivers.Status(m.Key); } catch { }
            MainWindow.Instance.Toast(Loc.T("Saved to your mouse."));
        }
        catch (Exception ex) { await MainWindow.Instance.Alert(Loc.T("The mouse didn't take the change"), ex.Message); }
        Show();
    }

    static double Num(JsonNode n, double fallback = 0) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : fallback;
    static bool? Bool(JsonNode n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    Border Chip(string text, bool on, Action click, bool enabled = true)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(16, 8, 16, 9), Margin = new Thickness(0, 0, 8, 8), Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
            Background = on ? B(Green, 0x22) : Res("Surface2Brush"), BorderBrush = on ? B(Green, 0xAA) : Res("BorderBrush"), BorderThickness = new Thickness(1),
            Opacity = enabled ? 1 : 0.5,
            Child = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = on ? B(Green) : Res("TextBrush") },
        };
        if (enabled && !on) b.MouseLeftButtonUp += (_, _) => click();
        return b;
    }

    UIElement Performance(Mouse m)
    {
        var root = new StackPanel();
        var st = m.Status;

        // ── DPI ──
        var dpi = new StackPanel();
        dpi.Children.Add(Head("Sensitivity (DPI)", "Saved on the mouse. Most players use 400 to 1,600."));
        var stages = (st?["dpiStages"] as JsonArray)?.Select(x => (int)Num(x)).Where(x => x > 0).ToList() ?? new();
        int active = (int)Num(st?["activeDpiStage"], 0);
        int current = (int)Num(st?["dpi"], stages.Count > active ? stages[active] : 800);
        var ed = st?["ui"]?["dpiStageEditor"];
        int min = (int)Num(ed?["minDpi"], 100), max = (int)Num(ed?["maxDpi"], Math.Max(26000, current)), step = Math.Max(1, (int)Num(ed?["stepDpi"], 50));
        bool stageEdit = stages.Count > 0 && m.Can("setDpiStageValue");
        int editing = Math.Clamp(active, 0, Math.Max(0, stages.Count - 1));

        var big = new TextBlock { FontSize = 40, FontWeight = FontWeights.Bold, FontFamily = (FontFamily)FindResource("DisplayFont"), Foreground = Res("TextBrush") };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { big, new TextBlock { Text = "DPI", FontSize = 14, Foreground = Res("TextDimBrush"), Margin = new Thickness(8, 0, 0, 8), VerticalAlignment = VerticalAlignment.Bottom } } };
        if (stages.Count > 1)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            for (int i = 0; i < stages.Count; i++)
            {
                int idx = i;
                var label = Loc.F("Stage {0}", i + 1) + " · " + stages[i].ToString("#,0", CultureInfo.CurrentCulture);
                chips.Children.Add(Chip(label, i == active, async () =>
                {
                    if (m.Can("setActiveDpiStage")) await Apply(m, "setActiveDpiStage", idx);
                }, m.Can("setActiveDpiStage")));
            }
            dpi.Children.Add(T(Loc.T("DPI stages (the DPI button cycles through these)"), 12, FontWeights.SemiBold, "TextFaintBrush").Also(x => x.Margin = new Thickness(0, 0, 0, 8)));
            dpi.Children.Add(chips);
        }
        int shownValue = stageEdit ? stages[editing] : current;
        big.Text = shownValue.ToString("#,0", CultureInfo.CurrentCulture);
        dpi.Children.Add(row);
        if (stageEdit || m.Can("setDpi"))
        {
            var slider = new Slider { Style = (Style)FindResource("Seek"), Minimum = min, Maximum = max, Value = Math.Clamp(shownValue, min, max), Margin = new Thickness(0, 10, 0, 6) };
            int Snap(double v) => (int)(Math.Round((v - min) / step) * step + min);
            slider.ValueChanged += (_, e) => big.Text = Snap(e.NewValue).ToString("#,0", CultureInfo.CurrentCulture);
            async Task Set(int v)
            {
                if (stageEdit) await Apply(m, "setDpiStageValue", editing, v);
                else await Apply(m, "setDpi", v);
            }
            slider.PreviewMouseLeftButtonUp += async (_, _) => await Set(Snap(slider.Value));
            dpi.Children.Add(slider);
            var scale = new Grid();
            scale.Children.Add(T(min.ToString("#,0", CultureInfo.CurrentCulture), 11, null, "TextFaintBrush"));
            scale.Children.Add(T(max.ToString("#,0", CultureInfo.CurrentCulture), 11, null, "TextFaintBrush").Also(x => x.HorizontalAlignment = HorizontalAlignment.Right));
            dpi.Children.Add(scale);
            var quick = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            foreach (var v in new[] { 400, 800, 1200, 1600, 3200 }.Where(v => v >= min && v <= max))
                quick.Children.Add(Chip(v.ToString("#,0", CultureInfo.CurrentCulture), v == shownValue, async () => await Set(v)));
            dpi.Children.Add(quick);
            if (stageEdit && stages.Count > 1)
                dpi.Children.Add(T(Loc.F("Editing stage {0}. Pick another stage above to edit it.", editing + 1), 12, null, "TextFaintBrush"));
        }
        else dpi.Children.Add(T(Loc.T("This mouse's DPI can't be changed from Pulse yet."), 12.5, null, "TextDimBrush"));
        root.Children.Add(Panel(dpi));

        // ── Polling rate ──
        var rates = (st?["supportedPollingRates"] as JsonArray)?.Select(x => (int)Num(x)).Where(x => x > 0).OrderBy(x => x).ToList() ?? new();
        if (rates.Count == 0 && m.Hz is int cur) rates.Add(cur);
        var poll = new StackPanel();
        poll.Children.Add(Head("Polling rate", "How many times a second the mouse reports to your PC. Higher is smoother, and uses a little more CPU and battery."));
        bool readOnly = Bool(st?["ui"]?["pollingReadOnly"]) == true || !m.Can("setPollingRate");
        var wrap = new WrapPanel();
        foreach (var r in rates)
            wrap.Children.Add(Chip(r >= 1000 ? (r / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) + "K Hz" : r + " Hz", r == m.Hz, async () => await Apply(m, "setPollingRate", r), !readOnly));
        poll.Children.Add(wrap);
        if (st?["ui"]?["pollingNote"] is JsonValue pn) poll.Children.Add(T((string)pn, 12, null, "TextFaintBrush"));
        else if (readOnly) poll.Children.Add(T(Loc.T("This mouse's polling rate can't be changed from Pulse yet."), 12, null, "TextFaintBrush"));
        root.Children.Add(Panel(poll));

        // ── Sensor ──
        var sensor = new StackPanel();
        sensor.Children.Add(Head("Sensor", null));
        int rows = 0;
        var lods = (st?["supportedLiftOffDistances"] as JsonArray)?.Select(x => (string)x).ToList();
        if (lods?.Count > 0 && m.Can("setLiftOffDistance"))
        {
            sensor.Children.Add(T(Loc.T("Lift-off distance (how high you can lift before the sensor stops tracking)"), 12.5, null, "TextDimBrush").Also(x => x.Margin = new Thickness(0, 0, 0, 8)));
            var w = new WrapPanel();
            foreach (var l in lods) w.Children.Add(Chip(Loc.T(l), l == (string)st?["liftOffDistance"], async () => await Apply(m, "setLiftOffDistance", l)));
            sensor.Children.Add(w);
            rows++;
        }
        void Toggle(string field, string method, string title, string note)
        {
            if (Bool(st?[field]) is not bool on || !m.Can(method)) return;
            var g = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel();
            sp.Children.Add(T(Loc.T(title), 13, FontWeights.SemiBold));
            sp.Children.Add(T(Loc.T(note), 12, null, "TextDimBrush").Also(x => x.Margin = new Thickness(0, 2, 0, 0)));
            g.Children.Add(sp);
            var cb = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            cb.Click += async (_, _) => await Apply(m, method, cb.IsChecked == true);
            Grid.SetColumn(cb, 1);
            g.Children.Add(cb);
            sensor.Children.Add(g);
            rows++;
        }
        Toggle("motionSync", "setMotionSync", "Motion Sync", "Lines sensor reads up with your PC's polling for steadier tracking. Adds about half a millisecond.");
        Toggle("angleSnapping", "setAngleSnapping", "Angle snapping", "Straightens lines you draw. Most players keep it off for aiming.");
        Toggle("rippleControl", "setRippleControl", "Ripple control", "Smooths jitter at very high DPI, with a little delay.");
        if (Num(st?["debounceMs"], -1) is var deb && deb >= 0 && m.Can("setDebounceTime"))
        {
            sensor.Children.Add(T(Loc.T("Click debounce (how long a click must settle; lower is faster, too low can double-click)"), 12.5, null, "TextDimBrush").Also(x => x.Margin = new Thickness(0, 8, 0, 8)));
            var w = new WrapPanel();
            foreach (var ms in new[] { 0, 1, 2, 4, 8, 12 })
                w.Children.Add(Chip(ms + " ms", (int)deb == ms, async () => await Apply(m, "setDebounceTime", ms)));
            sensor.Children.Add(w);
            rows++;
        }
        if (rows > 0) root.Children.Add(Panel(sensor));
        return root;
    }

    UIElement ButtonsTab(Mouse m)
    {
        var holder = new StackPanel();
        var card = new StackPanel();
        card.Children.Add(Head("Buttons", "Choose what each button does. It's saved on the mouse, so it works in every game, even with Pulse closed."));
        card.Children.Add(T(Loc.T("Reading the mouse's buttons…"), 12.5, null, "TextDimBrush"));
        holder.Children.Add(Panel(card));
        _ = LoadButtons();
        async Task LoadButtons()
        {
            JsonNode b;
            try { b = await MouseDrivers.Buttons(m.Key); }
            catch (Exception ex) { card.Children.RemoveAt(1); card.Children.Add(T(ex.Message, 12.5, null, "TextDimBrush")); return; }
            card.Children.RemoveAt(1);
            var list = b?["buttons"] as JsonArray;
            var options = (b?["options"] as JsonArray)?.Select(x => (string)x).Where(x => x != null).ToList() ?? new();
            var fixedButtons = (b?["fixed"] as JsonArray)?.Select(x => (string)x).ToHashSet() ?? new();
            if (list == null || list.Count == 0 || options.Count == 0)
            {
                card.Children.Add(T(Loc.T("This mouse's buttons can't be changed from Pulse yet. You can still add actions to them on the Macros page."), 12.5, null, "TextDimBrush"));
                var go = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Open Macros"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
                go.Click += (_, _) => MainWindow.Instance.Navigate("macros");
                card.Children.Add(go);
                return;
            }
            foreach (var btn in list)
            {
                string id = (string)btn["id"], name = (string)btn["name"], action = (string)btn["action"] ?? "";
                var g = new Grid { Margin = new Thickness(0, 4, 0, 4), MinHeight = 44 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                left.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Res("Surface2Brush"), Margin = new Thickness(0, 0, 12, 0), Child = Icon("", 13, Res("TextDimBrush")).Also(x => x.HorizontalAlignment = HorizontalAlignment.Center) });
                left.Children.Add(T(Loc.T(name), 13.5, FontWeights.SemiBold).Also(x => x.VerticalAlignment = VerticalAlignment.Center));
                g.Children.Add(left);
                bool locked = fixedButtons.Contains(name);
                var pick = new Button { Style = (Style)FindResource("GhostButton"), MinWidth = 190, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = !locked };
                pick.Content = new Grid
                {
                    Children =
                    {
                        new TextBlock { Text = Loc.T(action.Length > 0 ? action : "Not set"), FontWeight = FontWeights.SemiBold },
                        Icon(locked ? "" : "", 10).Also(x => x.HorizontalAlignment = HorizontalAlignment.Right),
                    },
                };
                var menu = new ContextMenu { MaxHeight = 420 };
                foreach (var o in options)
                {
                    var item = new MenuItem { Header = Loc.T(o), IsChecked = o == action };
                    item.Click += async (_, _) =>
                    {
                        if (o == action) return;
                        // Don't let the main click disappear without a second thought.
                        if ((name.Contains("Left", StringComparison.OrdinalIgnoreCase) || id == "0") && action == "Left click" &&
                            !await MainWindow.Instance.Confirm(Loc.T("Change the left button?"), Loc.T("Your left button won't click anymore unless another button is set to Left click. You can change it back here."), Loc.T("Change it")))
                            return;
                        try
                        {
                            await MouseDrivers.SetButton(m.Key, id, o);
                            MainWindow.Instance.Toast(Loc.T("Saved to your mouse."));
                        }
                        catch (Exception ex) { await MainWindow.Instance.Alert(Loc.T("The mouse didn't take the change"), ex.Message); }
                        Show();
                    };
                    menu.Items.Add(item);
                }
                pick.Click += (_, _) => { menu.PlacementTarget = pick; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
                Grid.SetColumn(pick, 1);
                g.Children.Add(pick);
                card.Children.Add(g);
                card.Children.Add(new Border { Height = 1, Background = Res("BorderBrush"), Margin = new Thickness(42, 2, 0, 2), Opacity = 0.6 });
            }
            card.Children.RemoveAt(card.Children.Count - 1);
            var foot = T(Loc.T("Want a button to press keys, type text or run a sequence? Use Macros."), 12, null, "TextFaintBrush");
            foot.Margin = new Thickness(0, 14, 0, 0);
            card.Children.Add(foot);
        }
        return holder;
    }

    UIElement Measure(Mouse m)
    {
        var root = new StackPanel();
        try { _meter = new MouseMeter(); } catch { }
        var card = new StackPanel();
        card.Children.Add(Head("Polling rate, measured", "Move the mouse in fast circles. This is what actually reaches your PC."));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var gauge = new RateGauge { Width = 180, Height = 180 };
        grid.Children.Add(gauge);
        var note = T("", 12.5, null, "TextDimBrush");
        note.Margin = new Thickness(26, 0, 0, 0);
        note.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(note, 1);
        grid.Children.Add(note);
        card.Children.Add(grid);
        root.Children.Add(Panel(card));
        root.Children.Add(Panel(DpiCheck(m)));
        root.Children.Add(Panel(ClickTest()));
        root.Children.Add(Panel(SensConverter(m)));
        if (_meter == null) { note.Text = Loc.T("Pulse couldn't listen to the mouse right now. Close the Measure page's test and try again."); return root; }
        double peak = 0;
        var handles = m.Raw?.Handles ?? new List<IntPtr>();
        _onTick = () =>
        {
            double now = _meter.Now;
            var mine = handles.Count > 0 ? _meter.Devices.Where(x => handles.Contains(x.Key)).Select(x => x.Value) : _meter.Devices.Values;
            foreach (var s in mine) { double hz = MouseMeter.Rate(s, now); if (hz > 0) peak = Math.Max(peak, hz); }
            if (_dpiRunning && _dpiText != null)
            {
                if (_dpiDevice == IntPtr.Zero && _meter.Devices.Count > 0) _dpiDevice = _meter.Devices.OrderByDescending(x => x.Value.LastAt).First().Key;
                if (_meter.Devices.TryGetValue(_dpiDevice, out var ds))
                    _dpiText.Text = Loc.F("Moving… {0} counts so far. Press Space at the mark.", Math.Abs(ds.CountX).ToString("#,0", CultureInfo.CurrentCulture));
            }
            if (peak <= 0) { note.Text = Loc.T("Waiting for movement…"); return; }
            int nearest = MouseMeter.Nearest(peak);
            gauge.Value = nearest;
            note.Text = m.Hz is int set && set > nearest * 1.3
                ? Loc.F("The mouse is set to {0} Hz but only {1} Hz reaches your PC. Plug the receiver into a USB port on the back of the PC, away from other wireless devices.", set.ToString("#,0", CultureInfo.CurrentCulture), nearest.ToString("#,0", CultureInfo.CurrentCulture))
                : Loc.T("Measured while you move the mouse.");
        };
        _tick.Start();
        return root;
    }

    // ───────── DPI check: move a known distance, count what the mouse reports ─────────

    bool _dpiArmed, _dpiRunning;
    double _dpiDistance = RegionInfo.CurrentRegion.IsMetric ? 5 / 2.54 : 2;
    TextBlock _dpiText;
    IntPtr _dpiDevice;
    int? _dpiSet;

    StackPanel DpiCheck(Mouse m)
    {
        _dpiSet = m.Dpi;
        var sp = Head("DPI, measured", "Check that the mouse really moves at the DPI it's set to. Sensors are often a few percent off.");
        bool metric = RegionInfo.CurrentRegion.IsMetric;
        sp.Children.Add(T(Loc.T("1. Put a ruler next to your mouse and pick how far you'll move it:"), 12.5, null, "TextDimBrush"));
        var dist = new WrapPanel { Margin = new Thickness(0, 8, 0, 10) };
        foreach (var (inches, label) in metric ? new[] { (5 / 2.54, "5 cm"), (10 / 2.54, "10 cm") } : new[] { (2.0, "2 in"), (4.0, "4 in") })
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = label, GroupName = "dpi-dist", IsChecked = Math.Abs(inches - _dpiDistance) < 0.01, Margin = new Thickness(0, 0, 6, 0) };
            rb.Checked += (_, _) => _dpiDistance = inches;
            dist.Children.Add(rb);
        }
        sp.Children.Add(dist);
        sp.Children.Add(T(Loc.T("2. Line the mouse's edge up with 0, press Space, move it straight to the mark, then press Space again."), 12.5, null, "TextDimBrush"));
        var go = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Get ready"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        _dpiText = T("", 13.5, FontWeights.SemiBold);
        _dpiText.Margin = new Thickness(0, 10, 0, 0);
        _dpiText.TextWrapping = TextWrapping.Wrap;
        go.Click += (_, _) => { _dpiArmed = true; _dpiRunning = false; Focusable = true; Focus(); _dpiText.Text = Loc.T("Ready: press Space to start."); };
        sp.Children.Add(go);
        sp.Children.Add(_dpiText);
        return sp;
    }

    void DpiKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !_dpiArmed || _meter == null || _dpiText == null) return;
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
        if (_dpiSet is int set && set > 0)
        {
            double off = (measured - set) / set * 100;
            _dpiText.Text = Loc.F("About {0} DPI, set to {1} ({2:+0;-0;0}%). Try twice more; within 5% is normal for a hand measurement.",
                Math.Round(measured).ToString("#,0", CultureInfo.CurrentCulture), set.ToString("#,0", CultureInfo.CurrentCulture), off);
        }
        else
        {
            int[] common = { 400, 800, 1000, 1200, 1600, 1800, 2000, 2400, 3200, 4000, 6400 };
            int near = common.OrderBy(c => Math.Abs(c - measured)).First();
            _dpiText.Text = Loc.F("About {0} DPI (closest common setting: {1}). Try twice more; within 5% is normal for a hand measurement.",
                Math.Round(measured).ToString("#,0", CultureInfo.CurrentCulture), near.ToString("#,0", CultureInfo.CurrentCulture));
        }
    }

    // ───────── click test: worn switches double-click ─────────

    StackPanel ClickTest()
    {
        var sp = Head("Click test", "Click here as fast as you like. A worn switch sends two clicks for one press (\"double-clicking\"); Pulse spots clicks closer together than a finger can make.");
        var pad = new Border { Height = 120, CornerRadius = new CornerRadius(10), Background = Res("Surface2Brush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 10), Cursor = Cursors.Hand };
        var inner = new TextBlock { Text = Loc.T("Click with any button"), FontSize = 14, Foreground = Res("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        pad.Child = inner;
        var result = T("", 12.5, null, "TextDimBrush");
        result.TextWrapping = TextWrapping.Wrap;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var last = new Dictionary<MouseButton, double>();
        var counts = new Dictionary<MouseButton, int>();
        var doubles = new Dictionary<MouseButton, int>();
        var recent = new Queue<double>();
        double fastest = double.MaxValue;
        string Name(MouseButton b) => b switch { MouseButton.Left => Loc.T("Left"), MouseButton.Right => Loc.T("Right"), MouseButton.Middle => Loc.T("Middle"), MouseButton.XButton1 => Loc.T("Back"), _ => Loc.T("Forward") };
        pad.PreviewMouseDown += (_, e) =>
        {
            e.Handled = true;
            double now = sw.Elapsed.TotalMilliseconds;
            var b = e.ChangedButton;
            counts[b] = counts.GetValueOrDefault(b) + 1;
            if (last.TryGetValue(b, out var prev))
            {
                double gap = now - prev;
                // Under 20 ms is faster than any finger (even butterfly clicking is ~40 ms): that's the switch bouncing.
                if (gap < 20) doubles[b] = doubles.GetValueOrDefault(b) + 1;
                else fastest = Math.Min(fastest, gap);
            }
            last[b] = now;
            recent.Enqueue(now);
            while (recent.Count > 0 && now - recent.Peek() > 1000) recent.Dequeue();
            inner.Text = Loc.F("{0} clicks per second", recent.Count);
            var parts = counts.Select(kv => $"{Name(kv.Key)} {kv.Value}").ToList();
            string text = string.Join(" · ", parts);
            if (fastest < double.MaxValue) text += "  ·  " + Loc.F("fastest {0} ms apart", Math.Round(fastest));
            var bad = doubles.Where(kv => kv.Value > 0).ToList();
            if (bad.Count > 0)
            {
                text += "\n" + Loc.F("{0} button: {1} double-clicks from one press. The switch is wearing out; cleaning or replacing it, or the maker's debounce setting, can fix it.",
                    string.Join(", ", bad.Select(kv => Name(kv.Key))), bad.Sum(kv => kv.Value));
                result.Foreground = Res("WarnBrush");
            }
            else result.Foreground = Res("TextDimBrush");
            result.Text = text;
        };
        pad.PreviewMouseUp += (_, e) => e.Handled = true;
        pad.ContextMenuOpening += (_, e) => e.Handled = true;
        sp.Children.Add(pad);
        sp.Children.Add(result);
        return sp;
    }

    // ───────── sensitivity converter ─────────

    /// <summary>Degrees the camera turns per mouse count at sensitivity 1 (well known for these engines).</summary>
    internal static readonly (string Game, double Yaw)[] SensGames =
    {
        ("Counter-Strike 2", 0.022), ("VALORANT", 0.07), ("Apex Legends", 0.022), ("Overwatch 2", 0.0066),
        ("Call of Duty", 0.0066), ("Destiny 2", 0.0066), ("Team Fortress 2", 0.022),
    };

    /// <summary>Centimeters of mouse movement for one full turn.</summary>
    internal static double Cm360(double yaw, double sens, double dpi) => sens <= 0 || dpi <= 0 ? 0 : 360 / (yaw * sens * dpi) * 2.54;

    /// <summary>The sensitivity in another game that turns the same amount for the same movement.</summary>
    internal static double ConvertSens(double sens, double fromYaw, double toYaw) => sens * fromYaw / toYaw;

    StackPanel SensConverter(Mouse m)
    {
        var sp = Head("Sensitivity converter", "Keep the same aim in every game: Pulse turns your sensitivity into the matching one for each game.");
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        var game = new ComboBox { Height = 34 };
        foreach (var (name, _) in SensGames) game.Items.Add(name);
        game.SelectedIndex = 1;
        var sens = new TextBox { Style = (Style)FindResource("SearchBox"), Text = "0.35", Tag = Loc.T("Sensitivity") };
        var dpi = new TextBox { Style = (Style)FindResource("SearchBox"), Text = (m.Dpi ?? 800).ToString(CultureInfo.InvariantCulture), Tag = "DPI" };
        Grid.SetColumn(sens, 2); Grid.SetColumn(dpi, 4);
        g.Children.Add(game); g.Children.Add(sens); g.Children.Add(dpi);
        sp.Children.Add(g);
        var table = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        sp.Children.Add(table);
        void Update()
        {
            table.Children.Clear();
            if (game.SelectedIndex < 0 || !double.TryParse(sens.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var sv) || sv <= 0
                || !double.TryParse(dpi.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv) || dv <= 0)
            {
                table.Children.Add(T(Loc.T("Type your sensitivity and DPI."), 12.5, null, "TextFaintBrush"));
                return;
            }
            var from = SensGames[game.SelectedIndex];
            table.Children.Add(T(Loc.F("{0} cm for a full turn ({1} in)", Cm360(from.Yaw, sv, dv).ToString("0.#", CultureInfo.CurrentCulture), (Cm360(from.Yaw, sv, dv) / 2.54).ToString("0.#", CultureInfo.CurrentCulture)), 13, FontWeights.SemiBold));
            foreach (var (name, yaw) in SensGames)
            {
                if (name == from.Game) continue;
                var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                row.Children.Add(T(name, 12.5, null, "TextDimBrush"));
                var v = T(ConvertSens(sv, from.Yaw, yaw).ToString("0.###", CultureInfo.InvariantCulture), 12.5, FontWeights.SemiBold);
                v.HorizontalAlignment = HorizontalAlignment.Right;
                row.Children.Add(v);
                table.Children.Add(row);
            }
            table.Children.Add(T(Loc.T("Same DPI in every game. Turn off mouse acceleration in each game for this to match."), 11.5, null, "TextFaintBrush").Also(x => x.Margin = new Thickness(0, 10, 0, 0)));
        }
        game.SelectionChanged += (_, _) => Update();
        sens.TextChanged += (_, _) => Update();
        dpi.TextChanged += (_, _) => Update();
        Update();
        return sp;
    }

    UIElement WindowsTab()
    {
        var sp = new StackPanel();
        sp.Children.Add(Head("Windows mouse settings", "For games that use the Windows cursor (and the desktop), these keep aim consistent."));
        foreach (var id in new[] { "mouse-accel", "pointer-1to1" })
        {
            var t = TweakCatalog.All.FirstOrDefault(x => x.Id == id);
            if (t == null) continue;
            var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var s = new StackPanel();
            s.Children.Add(T(Loc.T(t.Title), 13, FontWeights.SemiBold));
            s.Children.Add(T(Loc.T(t.Summary), 12, null, "TextDimBrush").Also(x => x.Margin = new Thickness(0, 2, 0, 0)));
            g.Children.Add(s);
            var cb = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = t.IsOn, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            cb.Click += async (_, _) => { await TweakRunner.Set(t, cb.IsChecked == true); cb.IsChecked = t.IsOn; };
            Grid.SetColumn(cb, 1);
            g.Children.Add(cb);
            sp.Children.Add(g);
        }
        return Panel(sp);
    }
}
