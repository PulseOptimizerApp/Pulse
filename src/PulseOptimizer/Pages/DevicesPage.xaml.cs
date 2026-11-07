using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
/// Devices, laid out like a device hub: a list of your devices with battery on the left, a gallery of device cards
/// (name, Connected, wireless / battery / polling rate, Configure), and a settings screen per device.
///   Mouse: DPI and polling rate (set on Logitech mice over HID++; a measured check for any mouse), buttons, Windows settings.
///   Keyboard: remaps for every game (Windows' Scancode Map), key repeat, shortcuts.
///   Headset / speakers / mic: format, Bluetooth hands-free warning, a live mic check.
/// Nothing listens to the mouse or mic unless a screen that needs it is open.
/// </summary>
public partial class DevicesPage : UserControl, IPage, IReleasable
{
    static DeviceSettings S => TweakEngine.State.Devices ??= new DeviceSettings();
    static string _openKey;          // device being configured (null = gallery)
    static string _section;           // tab inside the device screen
    List<GearDevice> _inputs = new();
    List<AudioDevice> _audio = new();
    bool _scanning;

    MouseMeter _meter;
    MicMeter _mic;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    Action _onTick;

    static readonly Color Green = Color.FromRgb(0x34, 0xD3, 0x99), Amber = Color.FromRgb(0xFB, 0xBF, 0x24), Cyan = Color.FromRgb(0x22, 0xD3, 0xEE),
        Red = Color.FromRgb(0xF8, 0x71, 0x71), Violet = Color.FromRgb(0x8B, 0x5C, 0xF6);

    public DevicesPage()
    {
        InitializeComponent();
        Focusable = true;
        FocusVisualStyle = null;
        _tick.Tick += (_, _) => _onTick?.Invoke();
        Unloaded += (_, _) => StopListening();
        PreviewKeyDown += Page_KeyDown;
    }

    public async void OnShown(object parameter)
    {
        ShowCurrent();
        await Scan();
    }

    public void Release() => StopListening();

    async Task Scan()
    {
        if (_scanning) return;
        _scanning = true;
        RefreshBtn.IsEnabled = false;
        try
        {
            var (inputs, audio) = await Task.Run(() => (InputDevices.List(), AudioDevices.List()));
            _inputs = inputs; _audio = audio;
            BuildList();
            ShowCurrent();
            // Logitech devices answer over HID++ with their real name, battery, DPI and report rate.
            var logi = await LogitechHidpp.Scan();
            if (logi.Count > 0)
            {
                _inputs = InputDevices.Merge(_inputs, logi);
                BuildList();
                ShowCurrent();
            }
        }
        catch { }
        finally { _scanning = false; RefreshBtn.IsEnabled = true; }
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await Scan();
    void AllDevices_Click(object sender, RoutedEventArgs e) { _openKey = null; ShowCurrent(); }

    void StopListening()
    {
        _tick.Stop();
        _onTick = null;
        _meter?.Dispose(); _meter = null;
        _mic?.Dispose(); _mic = null;
        _dpiArmed = _dpiRunning = false;
        _capture = null;
    }

    // ───────── what's shown ─────────

    /// <summary>A device on screen: an input device, or an audio device.</summary>
    sealed record Item(string Key, string Name, string Sub, DeviceKind? Input, AudioDevice Audio, GearDevice Gear)
    {
        public string Glyph => Input switch
        {
            DeviceKind.Mouse => "",
            DeviceKind.Keyboard => "",
            _ => Audio?.Input == true ? "" : Audio != null && (Audio.Name + Audio.Adapter).Contains("head", StringComparison.OrdinalIgnoreCase) ? "" : "",
        };
    }

    List<Item> Items()
    {
        var list = _inputs.Select(d => new Item(d.Key, d.Name,
            string.Join(" · ", new[] { d.Maker, Loc.T(d.Kind == DeviceKind.Mouse ? "Mouse" : "Keyboard") }.Where(x => !string.IsNullOrEmpty(x))), d.Kind, null, d)).ToList();
        foreach (var a in _audio.Where(a => a.IsDefault))
            list.Add(new Item((a.Input ? "mic-" : "out-") + a.Name + a.Adapter, string.IsNullOrEmpty(a.Adapter) ? a.Name : a.Adapter,
                Loc.T(a.Input ? "Microphone" : a.Name.Contains("head", StringComparison.OrdinalIgnoreCase) ? "Headset" : "Speakers"), null, a, null));
        return list;
    }

    void ShowCurrent()
    {
        StopListening();
        Main.Children.Clear();
        var item = _openKey == null ? null : Items().FirstOrDefault(i => i.Key == _openKey);
        Main.Children.Add(item == null ? Gallery() : Detail(item));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var tt = new TranslateTransform(0, 10);
        Main.RenderTransform = tt;
        Main.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        BuildList();
    }

    void Open(Item i) { _openKey = i.Key; _section = null; ShowCurrent(); Scroll.ScrollToTop(); }

    // ───────── building blocks ─────────

    Brush Res(string k) => (Brush)FindResource(k);
    static SolidColorBrush B(Color c, byte a = 255) => new(Color.FromArgb(a, c.R, c.G, c.B));
    FontFamily Icons => (FontFamily)FindResource("IconFont");

    TextBlock T(string text, double size = 13, FontWeight? w = null, string brush = "TextBrush") => new()
    {
        Text = text, FontSize = size, FontWeight = w ?? FontWeights.Normal, Foreground = Res(brush), TextWrapping = TextWrapping.Wrap,
    };

    TextBlock Icon(string glyph, double size, Brush fg = null) => new() { Text = glyph, FontFamily = Icons, FontSize = size, Foreground = fg ?? Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center };

    Border Panel(UIElement child, Thickness? pad = null, double bottom = 14) => new()
    {
        Background = Res("SurfaceBrush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
        Padding = pad ?? new Thickness(22, 20, 22, 20), Margin = new Thickness(0, 0, 0, bottom), Child = child,
    };

    StackPanel Head(string title, string sub)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        sp.Children.Add(T(Loc.T(title), 15.5, FontWeights.SemiBold));
        if (sub != null) { var d = T(Loc.T(sub), 12.5, null, "TextDimBrush"); d.Margin = new Thickness(0, 3, 0, 0); sp.Children.Add(d); }
        return sp;
    }

    Border Connected() => new()
    {
        CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 8, 3), Background = B(Green, 0x1F), VerticalAlignment = VerticalAlignment.Center,
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new Ellipse { Width = 6, Height = 6, Fill = B(Green), Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = Loc.T("Connected"), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = B(Green) },
            },
        },
    };

    /// <summary>A small battery shape filled to the level, with the percentage.</summary>
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

    /// <summary>Stats row: Wireless / battery / polling rate (for audio: the sound format).</summary>
    FrameworkElement Stats(Item i)
    {
        var sp = new WrapPanel();
        void Add(UIElement e) { if (e is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 16, 4); sp.Children.Add(e); }
        StackPanel IconText(string glyph, string text, string bold = null)
        {
            var s = new StackPanel { Orientation = Orientation.Horizontal };
            s.Children.Add(Icon(glyph, 12.5));
            if (bold != null) s.Children.Add(new TextBlock { Text = bold, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            s.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = Res("TextDimBrush"), Margin = new Thickness(bold == null ? 7 : 4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return s;
        }
        if (i.Gear is { } g)
        {
            Add(IconText(g.Wireless ? "" : "", Loc.T(g.Wireless ? "Wireless" : "Wired")));
            if (g.Battery is int b) Add(Battery(b, g.Charging));
            if (g.Logi?.RateHz is int hz) Add(IconText("", "Hz", hz.ToString("#,0", CultureInfo.CurrentCulture)));
            else if (g.Logi?.Dpi is int dpi) Add(IconText("", "DPI", dpi.ToString("#,0", CultureInfo.CurrentCulture)));
        }
        else if (i.Audio is { } a)
        {
            if (a.SampleRate > 0) Add(IconText("", a.Format));
            if (a.HandsFree) Add(IconText("", Loc.T("Hands-Free mode")));
        }
        return sp;
    }

    /// <summary>The lit stage with the device's photo (one you added) or its icon.</summary>
    FrameworkElement Stage(Item i, double height, double iconSize)
    {
        var g = new Grid { Height = height, ClipToBounds = true };
        g.Children.Add(new Border
        {
            Background = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.58), Center = new Point(0.5, 0.58), RadiusX = 0.6, RadiusY = 0.55,
                GradientStops = { new GradientStop(Color.FromArgb(0x30, Green.R, Green.G, Green.B), 0), new GradientStop(Color.FromArgb(0x10, Green.R, Green.G, Green.B), 0.5), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) },
            },
        });
        // floor shadow
        g.Children.Add(new Ellipse
        {
            Width = iconSize * 1.1, Height = iconSize * 0.12, Fill = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), Effect = new BlurEffect { Radius = 14 },
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, height * 0.1),
        });
        var photo = Photo(i);
        if (photo != null)
            g.Children.Add(new Image { Source = photo, MaxHeight = height * 0.8, MaxWidth = height * 1.1, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, height * 0.08), Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.6, Direction = 270 } });
        else
            g.Children.Add(new TextBlock
            {
                Text = i.Glyph, FontFamily = Icons, FontSize = iconSize, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, height * 0.08),
                Foreground = new LinearGradientBrush(Color.FromRgb(0xF2, 0xF4, 0xF7), Color.FromRgb(0x8A, 0x90, 0x9C), 90),
                Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 0, Opacity = 0.45, Color = Green },
            });
        return g;
    }

    // ───────── your own device photo (kept on this PC only) ─────────

    static string PhotoDir => System.IO.Path.Combine(TweakEngine.DataDir, "device-photos");
    static string PhotoKey(Item i) => string.Concat((i.Gear?.Vid + i.Gear?.Pid + i.Name).Where(char.IsLetterOrDigit));

    static ImageSource Photo(Item i)
    {
        if (!S.Photos.TryGetValue(PhotoKey(i), out var name)) return null;
        var f = System.IO.Path.Combine(PhotoDir, name);
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

    void PickPhoto(Item i)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Pictures") + "|*.png;*.jpg;*.jpeg;*.webp", Title = Loc.T("Choose a photo of this device") };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Directory.CreateDirectory(PhotoDir);
            var src = BitmapFrame.Create(new Uri(dlg.FileName), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(src);
            var name = PhotoKey(i) + "-" + DateTime.Now.Ticks + ".png";
            using (var fs = File.Create(System.IO.Path.Combine(PhotoDir, name))) enc.Save(fs);
            S.Photos[PhotoKey(i)] = name;
            TweakEngine.Save();
            ShowCurrent();
        }
        catch (Exception ex) { _ = MainWindow.Instance.Alert(Loc.T("Couldn't use that picture"), ex.Message); }
    }

    void RemovePhoto(Item i)
    {
        S.Photos.Remove(PhotoKey(i));
        TweakEngine.Save();
        ShowCurrent();
    }

    /// <summary>The "⋯" menu on a device.</summary>
    Button MoreMenu(Item i)
    {
        var b = new Button { Style = (Style)FindResource("LinkButton"), Content = Icon("", 14), Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("More") };
        var menu = new ContextMenu();
        var add = new MenuItem { Header = Loc.T(Photo(i) == null ? "Use my own photo…" : "Change photo…") };
        add.Click += (_, _) => PickPhoto(i);
        menu.Items.Add(add);
        if (Photo(i) != null) { var rm = new MenuItem { Header = Loc.T("Remove photo") }; rm.Click += (_, _) => RemovePhoto(i); menu.Items.Add(rm); }
        var win = new MenuItem { Header = Loc.T(i.Audio != null ? "Windows sound settings" : "Windows device settings") };
        win.Click += (_, _) => Launch(i.Audio != null ? "ms-settings:sound" : i.Input == DeviceKind.Mouse ? "ms-settings:mousetouchpad" : "ms-settings:typing");
        menu.Items.Add(win);
        b.Click += (_, _) => { menu.PlacementTarget = b; menu.IsOpen = true; };
        return b;
    }

    static void Launch(string uri) { try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { } }

    // ───────── left list ─────────

    void BuildList()
    {
        DeviceList.Children.Clear();
        var items = Items();
        if (items.Count == 0)
        {
            var t = T(Loc.T(_scanning ? "Looking for devices…" : "No devices found."), 12.5, null, "TextDimBrush");
            t.Margin = new Thickness(8, 4, 8, 0);
            DeviceList.Children.Add(t);
            return;
        }
        foreach (var i in items)
        {
            bool on = i.Key == _openKey;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var thumb = new Grid { Width = 36, Height = 36, Margin = new Thickness(0, 0, 10, 0) };
            thumb.Children.Add(new Border { CornerRadius = new CornerRadius(9), Background = Res("Surface2Brush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1) });
            var photo = Photo(i);
            thumb.Children.Add(photo != null ? new Image { Source = photo, Margin = new Thickness(4), Stretch = Stretch.Uniform } : Icon(i.Glyph, 16, Res("TextBrush")).Also(x => x.HorizontalAlignment = HorizontalAlignment.Center));
            thumb.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = B(Green), Stroke = Res("SurfaceBrush"), StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -2) });
            g.Children.Add(thumb);
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = i.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (i.Gear?.Battery is int b) { var bat = Battery(b, i.Gear.Charging, 9); bat.Margin = new Thickness(0, 3, 0, 0); bat.HorizontalAlignment = HorizontalAlignment.Left; sp.Children.Add(bat); }
            else sp.Children.Add(new TextBlock { Text = i.Sub, FontSize = 11, Foreground = Res("TextFaintBrush"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            var row = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand, Child = g, ToolTip = i.Name,
                Background = on ? Res("Surface2Brush") : Brushes.Transparent, BorderBrush = on ? Res("BorderBrush") : Brushes.Transparent, BorderThickness = new Thickness(1),
            };
            if (!on) { row.MouseEnter += (_, _) => row.Background = Res("SurfaceHoverBrush"); row.MouseLeave += (_, _) => row.Background = Brushes.Transparent; }
            row.MouseLeftButtonUp += (_, _) => Open(i);
            DeviceList.Children.Add(row);
        }
    }

    // ───────── gallery ─────────

    UIElement Gallery()
    {
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = Loc.T("Devices"), Style = (Style)FindResource("H1") });
        var sub = T(Loc.T("Your mice, keyboards, headsets and mics. Pick one to change its settings."), 13, null, "TextDimBrush");
        sub.Margin = new Thickness(0, 6, 0, 26);
        root.Children.Add(sub);
        var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var items = Items();
        foreach (var i in items) wrap.Children.Add(Card(i));
        if (items.Count == 0)
        {
            var empty = T(Loc.T(_scanning ? "Looking for devices…" : "No devices found. Plug one in and press Scan again."), 13, null, "TextDimBrush");
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            wrap.Children.Add(empty);
        }
        root.Children.Add(wrap);
        var note = T(Loc.T("Logitech mice and keyboards show their real name, battery, DPI and polling rate. For other brands, Pulse measures polling rate and DPI and handles remaps, key repeat and sound; settings stored inside the device (lighting, onboard DPI steps) stay in its maker's app."), 12, null, "TextFaintBrush");
        note.Margin = new Thickness(0, 18, 0, 0);
        note.MaxWidth = 760;
        root.Children.Add(note);
        return root;
    }

    FrameworkElement Card(Item i)
    {
        var sp = new DockPanel { LastChildFill = true };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock { Text = i.Name.ToUpper(CultureInfo.CurrentCulture), FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = i.Name };
        top.Children.Add(name);
        var con = Connected(); con.Margin = new Thickness(8, 0, 4, 0); Grid.SetColumn(con, 1); top.Children.Add(con);
        var more = MoreMenu(i); Grid.SetColumn(more, 2); top.Children.Add(more);
        var head = new StackPanel();
        head.Children.Add(top);
        head.Children.Add(new TextBlock { Text = i.Sub, FontSize = 12, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 6, 0, 8) });
        head.Children.Add(Stats(i));
        DockPanel.SetDock(head, Dock.Top);
        sp.Children.Add(head);
        var btn = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T("Configure"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(0, 9, 0, 9) };
        btn.Click += (_, _) => Open(i);
        DockPanel.SetDock(btn, Dock.Bottom);
        sp.Children.Add(btn);
        sp.Children.Add(Stage(i, 250, 96));
        var card = new Border
        {
            Width = 262, Height = 430, Margin = new Thickness(0, 0, 14, 14), Padding = new Thickness(14, 14, 14, 14), CornerRadius = new CornerRadius(14),
            Background = new LinearGradientBrush(((SolidColorBrush)Res("SurfaceBrush")).Color, ((SolidColorBrush)Res("BgBrush")).Color, 90),
            BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), Child = sp,
        };
        card.MouseEnter += (_, _) => card.BorderBrush = B(Green, 0x66);
        card.MouseLeave += (_, _) => card.BorderBrush = Res("BorderBrush");
        return card;
    }

    // ───────── one device ─────────

    UIElement Detail(Item i)
    {
        var root = new StackPanel();
        var back = new Button { Style = (Style)FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        back.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { Icon("", 11, Res("AccentBrush")), new TextBlock { Text = Loc.T("All devices"), Margin = new Thickness(8, 0, 0, 0) } } };
        back.Click += (_, _) => { _openKey = null; ShowCurrent(); };
        root.Children.Add(back);

        // Header: stage on the left, name and stats on the right.
        var hero = new Grid();
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        hero.ColumnDefinitions.Add(new ColumnDefinition());
        var stage = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, Background = Res("BgBrush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), Child = Stage(i, 230, 110) };
        hero.Children.Add(stage);
        var info = new StackPanel { Margin = new Thickness(24, 6, 0, 0) };
        var nameRow = new WrapPanel();
        nameRow.Children.Add(new TextBlock { Text = i.Name.ToUpper(CultureInfo.CurrentCulture), FontSize = 24, FontWeight = FontWeights.Bold, FontFamily = (FontFamily)FindResource("DisplayFont"), Foreground = Res("TextBrush"), Margin = new Thickness(0, 0, 12, 0) });
        nameRow.Children.Add(Connected());
        nameRow.Children.Add(MoreMenu(i));
        info.Children.Add(nameRow);
        info.Children.Add(new TextBlock { Text = i.Sub, FontSize = 13, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 6, 0, 12) });
        info.Children.Add(Stats(i));
        Grid.SetColumn(info, 1);
        hero.Children.Add(info);
        root.Children.Add(Panel(hero, new Thickness(14)));

        // Sections for this kind of device.
        var sections = i.Input switch
        {
            DeviceKind.Mouse => new[] { ("performance", "Performance", ""), ("buttons", "Buttons", ""), ("windows", "Windows settings", "") },
            DeviceKind.Keyboard => new[] { ("remap", "Remap keys", ""), ("repeat", "Key repeat", ""), ("shortcuts", "Shortcuts", "") },
            _ => i.Audio?.Input == true ? new[] { ("mic", "Mic check", ""), ("sound", "Sound", "") } : new[] { ("sound", "Sound", "") },
        };
        if (_section == null || !sections.Any(s => s.Item1 == _section)) _section = sections[0].Item1;
        var body = new StackPanel();
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        var tabBar = new Grid { Margin = new Thickness(0, 6, 0, 18) };
        tabBar.Children.Add(new Border { VerticalAlignment = VerticalAlignment.Bottom, Height = 1, Background = Res("BorderBrush") });
        tabBar.Children.Add(tabs);
        foreach (var (key, title, glyph) in sections)
        {
            bool on = key == _section;
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 11) };
            sp.Children.Add(Icon(glyph, 13, on ? Res("AccentBrush") : Res("TextDimBrush")));
            sp.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res(on ? "TextBrush" : "TextDimBrush"), Margin = new Thickness(8, 0, 0, 0) });
            var g = new Grid { Margin = new Thickness(0, 0, 26, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            g.Children.Add(sp);
            g.Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom, Background = on ? Res("AccentBrush") : Brushes.Transparent });
            g.MouseLeftButtonUp += (_, _) => { _section = key; ShowCurrent(); };
            tabs.Children.Add(g);
        }
        root.Children.Add(tabBar);
        body.Children.Add(_section switch
        {
            "performance" => MousePerformance(i.Gear),
            "buttons" => MouseButtons(i.Gear),
            "windows" => MouseWindows(),
            "remap" => KeyboardRemap(),
            "repeat" => KeyboardRepeat(),
            "shortcuts" => KeyboardShortcuts(i.Gear),
            "mic" => MicCheck(),
            _ => Sound(i.Audio),
        });
        root.Children.Add(body);
        return root;
    }

    /// <summary>A row for one of Pulse's tweaks: what it does, and a switch that applies or undoes it (with backup).</summary>
    FrameworkElement TweakRow(string id)
    {
        var t = Core.TweakCatalog.All.FirstOrDefault(x => x.Id == id);
        if (t == null) return new Border();
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sp = new StackPanel();
        sp.Children.Add(T(Loc.T(t.Title), 13, FontWeights.SemiBold));
        var d = T(Loc.T(t.Summary), 12, null, "TextDimBrush"); d.Margin = new Thickness(0, 2, 0, 0); sp.Children.Add(d);
        g.Children.Add(sp);
        var cb = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = t.IsOn, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        cb.Click += async (_, _) => { await TweakRunner.Set(t, cb.IsChecked == true); cb.IsChecked = t.IsOn; };
        Grid.SetColumn(cb, 1);
        g.Children.Add(cb);
        return g;
    }

    // ───────── mouse: performance ─────────

    bool _dpiArmed, _dpiRunning;
    double _dpiDistance = RegionInfo.CurrentRegion.IsMetric ? 5 / 2.54 : 2;
    TextBlock _dpiText;
    IntPtr _dpiDevice;

    UIElement MousePerformance(GearDevice d)
    {
        var root = new StackPanel();
        try { _meter = new MouseMeter(); } catch { }
        var logi = d?.Logi;

        // ── DPI ──
        var dpiCard = new StackPanel();
        dpiCard.Children.Add(Head("Sensitivity (DPI)", logi?.Dpi != null ? "Set on the mouse itself. Games feel faster at higher DPI; most players use 400 to 1,600." : "Check your mouse's real DPI with a ruler. To change it, use the DPI button or your mouse's own app."));
        if (logi?.Dpi is int cur && logi.DpiList.Count > 1)
        {
            var big = new TextBlock { FontSize = 40, FontWeight = FontWeights.Bold, FontFamily = (FontFamily)FindResource("DisplayFont"), Foreground = Res("TextBrush") };
            var unit = new TextBlock { Text = "DPI", FontSize = 14, Foreground = Res("TextDimBrush"), Margin = new Thickness(8, 0, 0, 8), VerticalAlignment = VerticalAlignment.Bottom };
            var status = T("", 12.5, null, "TextDimBrush");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { big, unit } };
            dpiCard.Children.Add(row);
            var list = logi.DpiList;
            int Snap(double v) => list.OrderBy(x => Math.Abs(x - v)).First();
            var slider = new Slider { Style = (Style)FindResource("Seek"), Minimum = list.Min(), Maximum = list.Max(), Value = cur, Margin = new Thickness(0, 10, 0, 6) };
            big.Text = cur.ToString("#,0", CultureInfo.CurrentCulture);
            slider.ValueChanged += (_, e) => big.Text = Snap(e.NewValue).ToString("#,0", CultureInfo.CurrentCulture);
            async Task Apply(int dpi)
            {
                status.Text = Loc.T("Setting…");
                bool ok = await LogitechHidpp.SetDpi(logi, dpi);
                big.Text = (logi.Dpi ?? dpi).ToString("#,0", CultureInfo.CurrentCulture);
                status.Text = ok ? Loc.F("Set to {0} DPI.", dpi.ToString("#,0", CultureInfo.CurrentCulture))
                    : Loc.T("The mouse kept its own setting. Its onboard profile is in charge: switch the mouse to software mode in its maker's app, then try again.");
                status.Foreground = ok ? B(Green) : B(Amber);
                BuildList();
            }
            slider.PreviewMouseLeftButtonUp += async (_, _) => await Apply(Snap(slider.Value));
            dpiCard.Children.Add(slider);
            var scale = new Grid();
            scale.Children.Add(T(list.Min().ToString("#,0", CultureInfo.CurrentCulture), 11, null, "TextFaintBrush"));
            var maxT = T(list.Max().ToString("#,0", CultureInfo.CurrentCulture), 11, null, "TextFaintBrush"); maxT.HorizontalAlignment = HorizontalAlignment.Right; scale.Children.Add(maxT);
            dpiCard.Children.Add(scale);
            var chips = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            foreach (var v in new[] { 400, 800, 1200, 1600, 3200 }.Where(v => v >= list.Min() && v <= list.Max()))
            {
                var c = new Button { Style = (Style)FindResource("GhostButton"), Content = v.ToString("#,0", CultureInfo.CurrentCulture), Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(14, 6, 14, 6) };
                c.Click += async (_, _) => { slider.Value = Snap(v); await Apply(Snap(v)); };
                chips.Children.Add(c);
            }
            dpiCard.Children.Add(chips);
            dpiCard.Children.Add(status);
            var checkBody = DpiCheck();
            checkBody.Visibility = Visibility.Collapsed;
            var toggle = new System.Windows.Controls.Primitives.ToggleButton { Style = (Style)FindResource("LinkToggle"), Content = Loc.T("Check it with a ruler"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
            toggle.Click += (_, _) => checkBody.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            dpiCard.Children.Add(toggle);
            dpiCard.Children.Add(checkBody);
        }
        else dpiCard.Children.Add(DpiCheck());

        // ── Polling rate ──
        var rate = new StackPanel();
        rate.Children.Add(Head("Polling rate", "How many times a second the mouse reports to your PC. Move it in fast circles to measure."));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var gauge = new RateGauge { Width = 170, Height = 170 };
        grid.Children.Add(gauge);
        var right = new StackPanel { Margin = new Thickness(26, 6, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var setting = T("", 13, null, "TextDimBrush");
        var note = T("", 12.5, null, "TextDimBrush"); note.Margin = new Thickness(0, 10, 0, 0);
        if (logi?.Rates.Count > 1)
        {
            right.Children.Add(T(Loc.T("Set on the mouse"), 12, FontWeights.SemiBold, "TextFaintBrush"));
            var opts = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
            foreach (var hz in logi.Rates)
            {
                var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = hz >= 1000 ? (hz / 1000) + "K" : hz.ToString(CultureInfo.InvariantCulture), GroupName = "devrate", IsChecked = logi.RateHz == hz, ToolTip = hz + " Hz" };
                rb.Checked += async (_, _) =>
                {
                    if (logi.RateHz == hz) return;
                    bool ok = await LogitechHidpp.SetRate(logi, hz);
                    setting.Text = ok ? Loc.F("Set to {0} Hz.", hz.ToString("#,0", CultureInfo.CurrentCulture)) : Loc.T("The mouse kept its own setting (its onboard profile is in charge).");
                    BuildList();
                };
                opts.Children.Add(rb);
            }
            right.Children.Add(opts);
        }
        right.Children.Add(setting);
        right.Children.Add(note);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        rate.Children.Add(grid);

        root.Children.Add(Panel(dpiCard));
        root.Children.Add(Panel(rate));

        if (_meter != null)
        {
            double peak = 0;
            _onTick = () =>
            {
                double now = _meter.Now;
                // only this device's reports (all mice when Windows didn't tell which handle is which)
                var mine = d?.Handles.Count > 0 ? _meter.Devices.Where(x => d.Handles.Contains(x.Key)).Select(x => x.Value) : _meter.Devices.Values;
                foreach (var st in mine)
                {
                    double hz = MouseMeter.Rate(st, now);
                    if (hz > 0) peak = Math.Max(peak, hz);
                }
                if (peak > 0)
                {
                    int nearest = MouseMeter.Nearest(peak);
                    gauge.Value = nearest;
                    note.Text = nearest <= 125 ? Loc.T("125 Hz is the old USB default. Most gaming mice can do 1,000 Hz or more.")
                        : nearest >= 2000 ? Loc.T("Very high polling uses more CPU. If games stutter, try 1,000 Hz.") : Loc.T("Measured while you move the mouse.");
                }
                if (_dpiRunning && _meter.Devices.TryGetValue(_dpiDevice, out var ds) && _dpiText != null)
                    _dpiText.Text = Loc.F("Moving… {0} counts so far. Press Space at the mark.", Math.Abs(ds.CountX).ToString("#,0", CultureInfo.CurrentCulture));
                else if (_dpiRunning && _dpiDevice == IntPtr.Zero && _meter.Devices.Count > 0)
                    _dpiDevice = _meter.Devices.OrderByDescending(x => x.Value.LastAt).First().Key;
            };
            _tick.Start();
        }
        else note.Text = Loc.T("Pulse couldn't listen to the mouse right now. Close the Measure page's test and try again.");
        return root;
    }

    FrameworkElement DpiCheck()
    {
        var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        bool metric = RegionInfo.CurrentRegion.IsMetric;
        sp.Children.Add(T(Loc.T("1. Put a ruler next to your mouse and pick how far you'll move it:"), 13, null, "TextDimBrush"));
        var dist = new WrapPanel { Margin = new Thickness(0, 8, 0, 10) };
        foreach (var (inches, label) in metric ? new[] { (5 / 2.54, "5 cm"), (10 / 2.54, "10 cm") } : new[] { (2.0, "2 in"), (4.0, "4 in") })
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = label, GroupName = "dpi-dist", IsChecked = Math.Abs(inches - _dpiDistance) < 0.01 };
            rb.Checked += (_, _) => _dpiDistance = inches;
            dist.Children.Add(rb);
        }
        sp.Children.Add(dist);
        sp.Children.Add(T(Loc.T("2. Line the mouse's edge up with 0, press Space, move it straight to the mark, then press Space again."), 13, null, "TextDimBrush"));
        var go = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Get ready"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        _dpiText = T("", 13.5, FontWeights.SemiBold);
        _dpiText.Margin = new Thickness(0, 10, 0, 0);
        go.Click += (_, _) => { _dpiArmed = true; _dpiRunning = false; Focus(); _dpiText.Text = Loc.T("Ready: press Space to start."); };
        sp.Children.Add(go);
        sp.Children.Add(_dpiText);
        return sp;
    }

    UIElement MouseButtons(GearDevice d)
    {
        var sp = new StackPanel();
        sp.Children.Add(Head("Buttons", "Make any mouse button do something else, or run a macro. Works with every brand, in every game that allows it."));
        var go = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T("Open Macros"), HorizontalAlignment = HorizontalAlignment.Left };
        go.Click += (_, _) => MainWindow.Instance.Navigate("macros");
        sp.Children.Add(go);
        if (d?.MakerApp != null)
        {
            var t = T(Loc.F("Lighting and the DPI steps saved inside the mouse are changed in {0}.", d.MakerApp), 12.5, null, "TextDimBrush");
            t.Margin = new Thickness(0, 14, 0, 0);
            sp.Children.Add(t);
        }
        return Panel(sp);
    }

    UIElement MouseWindows()
    {
        var sp = new StackPanel();
        sp.Children.Add(Head("Windows mouse settings", "For games that use the Windows cursor (and the desktop), these keep aim consistent."));
        sp.Children.Add(TweakRow("mouse-accel"));
        sp.Children.Add(TweakRow("pointer-1to1"));
        return Panel(sp);
    }

    void Page_KeyDown(object sender, KeyEventArgs e)
    {
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
        int[] common = { 400, 800, 1000, 1200, 1600, 1800, 2000, 2400, 3200, 4000, 6400 };
        int near = common.OrderBy(c => Math.Abs(c - measured)).First();
        var name = _inputs.FirstOrDefault(i => i.Handles.Contains(_dpiDevice))?.Name ?? Loc.T("Your mouse");
        _dpiText.Text = Loc.F("{0}: about {1} DPI (closest common setting: {2}). Try twice more; results within 5% are normal for a hand measurement.",
            name, Math.Round(measured).ToString("#,0", CultureInfo.CurrentCulture), near.ToString("#,0", CultureInfo.CurrentCulture));
    }

    // ───────── keyboard ─────────

    Action<ushort> _capture;
    List<KeyRemap.Map> _maps = new();

    Border KeyCap(string text) => new()
    {
        Background = Res("Surface2Brush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1, 1, 1, 3), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 4, 10, 4), Child = new TextBlock { Text = text, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") },
    };

    UIElement KeyboardRemap()
    {
        try { _maps = KeyRemap.Read(); } catch { _maps = new(); }
        var rm = new StackPanel();
        rm.Children.Add(Head("Remap keys", "Changes a key for every app and game, built into Windows (no app needs to keep running). Applies after you sign out or restart."));
        if (S.RemapPending)
            rm.Children.Add(new Border
            {
                Background = B(Amber, 0x1C), BorderBrush = B(Amber, 0x55), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 12),
                Child = new TextBlock { Text = Loc.T("Sign out or restart to use your new key layout."), Foreground = B(Amber), FontSize = 12.5, FontWeight = FontWeights.SemiBold },
            });
        if (_maps.Count == 0) rm.Children.Add(T(Loc.T("No keys are remapped."), 13, null, "TextDimBrush"));
        void Save()
        {
            try { KeyRemap.Write(_maps, S); TweakEngine.Save(); TweakEngine.AddLog("devices", "Key remaps: " + _maps.Count); MainWindow.Instance.Toast(Loc.T("Saved. Sign out or restart to use it.")); ShowCurrent(); }
            catch (Exception ex) { _ = MainWindow.Instance.Alert(Loc.T("Couldn't save the remap"), ex.Message); }
        }
        foreach (var m in _maps.ToList())
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(KeyCap(KeyRemap.NameOf(m.From)));
            sp.Children.Add(Icon("", 12).Also(x => x.Margin = new Thickness(12, 0, 12, 0)));
            sp.Children.Add(m.To == 0
                ? new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3, 8, 4), Background = B(Red, 0x22), Child = new TextBlock { Text = Loc.T("Turned off"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B(Red) } }
                : KeyCap(KeyRemap.NameOf(m.To)));
            g.Children.Add(sp);
            var x = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Remove"), VerticalAlignment = VerticalAlignment.Center };
            x.Click += (_, _) => { _maps.Remove(m); Save(); };
            Grid.SetColumn(x, 1);
            g.Children.Add(x);
            rm.Children.Add(g);
        }

        var add = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var from = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Press the key to change") };
        var to = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Press the key it should act as"), IsEnabled = false };
        var off = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Turn it off"), IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
        var hint = T("", 12, null, "TextDimBrush"); hint.Margin = new Thickness(0, 8, 0, 0);
        ushort fromCode = 0;
        from.Click += (_, _) => { from.Content = Loc.T("Press a key now…"); Focus(); _capture = sc => { fromCode = sc; from.Content = KeyRemap.NameOf(sc); to.IsEnabled = off.IsEnabled = true; hint.Text = Loc.T("Now choose what it should do."); }; };
        void AddMap(ushort target) { _maps.RemoveAll(m => m.From == fromCode); _maps.Add(new KeyRemap.Map(fromCode, target)); Save(); }
        to.Click += (_, _) => { to.Content = Loc.T("Press a key now…"); Focus(); _capture = sc => AddMap(sc); };
        off.Click += (_, _) => AddMap(0);
        add.Children.Add(from);
        add.Children.Add(Icon("", 12).Also(x => x.Margin = new Thickness(12, 0, 12, 0)));
        add.Children.Add(to);
        add.Children.Add(off);
        rm.Children.Add(add);
        rm.Children.Add(hint);

        rm.Children.Add(new TextBlock { Text = Loc.T("Quick picks"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 18, 0, 8) });
        var pre = new WrapPanel();
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
        rm.Children.Add(pre);
        if (S.ScancodeBackup != null)
        {
            var undo = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Put back the keyboard layout from before Pulse"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
            undo.Click += (_, _) => { try { KeyRemap.Restore(S); TweakEngine.Save(); ShowCurrent(); } catch (Exception ex) { _ = MainWindow.Instance.Alert(Loc.T("Couldn't restore"), ex.Message); } };
            rm.Children.Add(undo);
        }
        return Panel(rm);
    }

    UIElement KeyboardRepeat()
    {
        var rep = new StackPanel();
        rep.Children.Add(Head("Key repeat", "How long a held key waits before it repeats, and how fast it repeats. Changes right away."));
        int delay = KeyRepeat.Delay, speed = KeyRepeat.Speed;
        var dText = new TextBlock { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Res("TextBrush") };
        var sText = new TextBlock { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Res("TextBrush") };
        void Labels() { dText.Text = Loc.F("{0} ms", KeyRepeat.DelayMs(delay)); sText.Text = Loc.F("{0} per second", Math.Round(KeyRepeat.RepeatsPerSecond(speed))); }
        Labels();
        FrameworkElement Row(string label, TextBlock val, int max, int value, Action<int> set)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };
            var top = new Grid();
            top.Children.Add(new TextBlock { Text = Loc.T(label), Foreground = Res("TextDimBrush") });
            top.Children.Add(val);
            sp.Children.Add(top);
            var sl = new Slider { Style = (Style)FindResource("Seek"), Minimum = 0, Maximum = max, TickFrequency = 1, IsSnapToTickEnabled = true, Value = value, Margin = new Thickness(0, 8, 0, 0) };
            sl.ValueChanged += (_, e) => { set((int)e.NewValue); Labels(); try { KeyRepeat.Set(delay, speed, S); TweakEngine.Save(); } catch { } };
            sp.Children.Add(sl);
            return sp;
        }
        rep.Children.Add(Row("Delay before repeating", dText, 3, delay, v => delay = v));
        rep.Children.Add(Row("Repeat speed", sText, 31, speed, v => speed = v));
        rep.Children.Add(new TextBox { Style = (Style)FindResource("SearchBox"), Tag = Loc.T("Hold a key here to try it"), Margin = new Thickness(0, 8, 0, 0) });
        if (S.OriginalKeyDelay is int od && S.OriginalKeySpeed is int os && (od != delay || os != speed))
        {
            var back = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T("Put back the settings from before"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
            back.Click += (_, _) => { KeyRepeat.Set(od, os, S); ShowCurrent(); };
            rep.Children.Add(back);
        }
        return Panel(rep);
    }

    UIElement KeyboardShortcuts(GearDevice d)
    {
        var sp = new StackPanel();
        sp.Children.Add(Head("Shortcuts that interrupt games", null));
        sp.Children.Add(TweakRow("sticky-keys"));
        if (d?.MakerApp != null)
        {
            var t = T(Loc.F("Lighting, rapid trigger and actuation saved on the keyboard are changed in {0}.", d.MakerApp), 12.5, null, "TextDimBrush");
            t.Margin = new Thickness(0, 12, 0, 0);
            sp.Children.Add(t);
        }
        return Panel(sp);
    }

    // ───────── audio ─────────

    UIElement Sound(AudioDevice a)
    {
        var root = new StackPanel();
        var sp = new StackPanel();
        sp.Children.Add(Head(a?.Input == true ? "Microphone" : "Sound", null));
        if (a != null)
        {
            void Line(string label, string value)
            {
                var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.Children.Add(T(Loc.T(label), 13, null, "TextDimBrush"));
                var v = T(value, 13, FontWeights.SemiBold); Grid.SetColumn(v, 1); g.Children.Add(v);
                sp.Children.Add(g);
            }
            Line("Device", string.IsNullOrEmpty(a.Adapter) ? a.Name : a.Adapter);
            Line("Shows in Windows as", a.Name);
            if (a.SampleRate > 0) Line("Format", a.Format);
            if (a.HandsFree)
            {
                var w = T(Loc.T("This is the Bluetooth \"Hands-Free\" mode: sound drops to low-quality mono whenever the mic is on. For games, use the stereo version of this headset for sound, and a separate mic or the headset's USB dongle."), 12.5);
                w.Foreground = B(Amber); w.Margin = new Thickness(0, 10, 0, 0);
                sp.Children.Add(w);
            }
            else if (!a.Input && a.SampleRate > 96000)
            {
                var w = T(Loc.T("Above 96 kHz adds nothing you can hear in games and can make some games' audio crackle. 48 kHz is the safe choice."), 12.5, null, "TextDimBrush");
                w.Margin = new Thickness(0, 10, 0, 0);
                sp.Children.Add(w);
            }
        }
        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var snd = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Open Sound settings") };
        snd.Click += (_, _) => Launch("ms-settings:sound");
        links.Children.Add(snd);
        if (a?.Input == true)
        {
            var priv = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Mic privacy settings"), Margin = new Thickness(10, 0, 0, 0) };
            priv.Click += (_, _) => Launch("ms-settings:privacy-microphone");
            links.Children.Add(priv);
        }
        sp.Children.Add(links);
        root.Children.Add(Panel(sp));
        var more = new StackPanel();
        more.Children.Add(Head("Sound while you play", null));
        more.Children.Add(TweakRow("comm-ducking"));
        root.Children.Add(Panel(more));
        return root;
    }

    UIElement MicCheck()
    {
        var mic = new StackPanel();
        mic.Children.Add(Head("Mic check", "Talk normally. The bar shows how loud your mic picks you up. Nothing is recorded or saved."));
        var track = new Grid { Height = 14, Margin = new Thickness(0, 4, 0, 0) };
        track.Children.Add(new Border { Style = (Style)FindResource("BarTrack"), Height = 14 });
        var fill = new Border { Height = 14, CornerRadius = new CornerRadius(7), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Background = B(Green) };
        track.Children.Add(fill);
        // the good zone
        var zone = new Grid { Height = 14, Margin = new Thickness(0, -14, 0, 0), IsHitTestVisible = false };
        zone.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.55, GridUnitType.Star) });
        zone.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.35, GridUnitType.Star) });
        zone.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.10, GridUnitType.Star) });
        var good = new Border { BorderBrush = B(Green, 0x80), BorderThickness = new Thickness(1, 0, 1, 0) };
        Grid.SetColumn(good, 1);
        zone.Children.Add(good);
        mic.Children.Add(track);
        mic.Children.Add(zone);
        var verdict = T("", 13, FontWeights.SemiBold); verdict.Margin = new Thickness(0, 10, 0, 0);
        mic.Children.Add(verdict);
        var startBtn = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T("Start mic check"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
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
                double db = 20 * Math.Log10(Math.Max(1e-4, smooth));
                fill.Width = Math.Max(0, track.ActualWidth * Math.Clamp((db + 60) / 60, 0, 1));
                fill.Background = B(smooth > 0.95 ? Red : smooth > 0.6 ? Amber : Green);
                verdict.Text = loudest < 0.02 ? Loc.T("Can't hear you yet. Say something.")
                    : loudest < 0.1 ? Loc.T("Quiet. Turn the mic up a little (Sound settings → your mic → Properties → Levels).")
                    : loudest > 0.98 ? Loc.T("Too loud: your voice is cutting off (clipping). Turn the mic down a little.")
                    : Loc.T("Sounds good.");
            };
            _tick.Start();
        };
        mic.Children.Add(startBtn);
        return Panel(mic);
    }
}

/// <summary>A round dial for the polling rate (125 Hz to 8 kHz on a log scale), drawn with an accent arc.</summary>
sealed class RateGauge : FrameworkElement
{
    double _value, _shown;
    public double Value
    {
        get => _value;
        set
        {
            if (Math.Abs(value - _value) < 0.5) return;
            _value = value;
            BeginAnimation(ShownProperty, new DoubleAnimation(_shown, value, TimeSpan.FromMilliseconds(500)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    static readonly DependencyProperty ShownProperty = DependencyProperty.Register("Shown", typeof(double), typeof(RateGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((RateGauge)d)._shown = (double)e.NewValue));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, r = Math.Min(w, h) / 2 - 10;
        var c = new Point(w / 2, h / 2);
        const double start = 135, sweep = 270;
        Point At(double deg, double rad) => new(c.X + rad * Math.Cos(deg * Math.PI / 180), c.Y + rad * Math.Sin(deg * Math.PI / 180));
        Geometry Arc(double from, double to)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(At(from, r), false, false);
                ctx.ArcTo(At(to, r), new Size(r, r), 0, to - from > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }
        var track = new Pen(TryFindResource("BorderBrush") as Brush ?? Brushes.DimGray, 10) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, track, Arc(start, start + sweep));
        double frac = _shown <= 0 ? 0 : Math.Clamp(Math.Log(_shown / 100.0) / Math.Log(8000 / 100.0), 0, 1);
        if (frac > 0.005)
        {
            var accent = new LinearGradientBrush(Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0x34, 0xD3, 0x99), 0);
            dc.DrawGeometry(null, new Pen(accent, 10) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(start, start + sweep * frac));
        }
        var text = TryFindResource("TextBrush") as Brush ?? Brushes.White;
        var dim = TryFindResource("TextDimBrush") as Brush ?? Brushes.Gray;
        var ff = (TryFindResource("DisplayFont") as FontFamily) ?? new FontFamily("Segoe UI");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var big = new FormattedText(_shown > 0 ? Math.Round(_shown).ToString("#,0", CultureInfo.CurrentCulture) : "—", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(ff, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 30, text, dpi);
        dc.DrawText(big, new Point(c.X - big.Width / 2, c.Y - big.Height / 2 - 6));
        var unit = new FormattedText(_shown > 0 ? "Hz" : Pulse.Ui.Loc.T("move the mouse"), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, dim, dpi);
        dc.DrawText(unit, new Point(c.X - unit.Width / 2, c.Y + big.Height / 2 - 4));
    }
}

static class UiExt
{
    public static T Also<T>(this T x, Action<T> a) { a(x); return x; }
}
