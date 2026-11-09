using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Pulse.Core.Mouse;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// The Devices view, laid out like OpenMouse: a card per device, then a device screen with its picture on the left and
/// tabs on the right (Overview, Performance, Buttons, Lighting, Profiles, Advanced). Only what the device's driver
/// supports is shown, and every change is read back from the device.
/// </summary>
public partial class MousePage
{
    sealed record Found(string Key, int VendorId, int ProductId, string Name, double Score, string Image = null)
    {
        /// <summary>What OpenMouse calls it on its card: receivers count as the mouse they talk to.</summary>
        public string Label => Kind is "Keyboard" or "Headset" ? Kind : "Mouse";

        public string Kind => Name switch
        {
            var n when System.Text.RegularExpressions.Regex.IsMatch(n, "keyboard|60he|80he|wooting|keychron|\\bk\\d{2}", System.Text.RegularExpressions.RegexOptions.IgnoreCase) => "Keyboard",
            var n when System.Text.RegularExpressions.Regex.IsMatch(n, "headset|g733|g435|g535|arctis|cloud", System.Text.RegularExpressions.RegexOptions.IgnoreCase) => "Headset",
            var n when System.Text.RegularExpressions.Regex.IsMatch(n, "receiver|dongle", System.Text.RegularExpressions.RegexOptions.IgnoreCase) => "Receiver",
            _ => "Mouse",
        };
    }

    // OpenMouse's green, for the things you act on.
    static readonly SolidColorBrush Green = Frozen(Color.FromRgb(0x3D, 0xDC, 0x84));
    static readonly SolidColorBrush GreenDim = Frozen(Color.FromArgb(0x24, 0x3D, 0xDC, 0x84));
    static readonly SolidColorBrush CardBg = Frozen(Color.FromRgb(0x17, 0x18, 0x1B));
    static readonly SolidColorBrush CardLine = Frozen(Color.FromRgb(0x26, 0x28, 0x2D));
    static readonly SolidColorBrush Inset = Frozen(Color.FromRgb(0x1E, 0x20, 0x24));
    static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    List<Found> _found = new();
    Found _current;
    int _clientId;
    // Bumped whenever the page starts over (a new scan, a device opened, the page closed): an answer that arrives for an
    // older one is ignored instead of drawing over what's on screen now.
    int _gen;
    string _openKey;   // the device _clientId is connected to: it stays connected while the page is open
    // What each device said when it was read (name, battery, polling…), its brand and its picture, for its card.
    readonly Dictionary<string, (JsonElement Status, string Brand, string Image)> _known = new();
    string _brand = "";
    HashSet<string> _methods = new();
    JsonElement _status;
    bool _busy;
    string _tab = "overview";
    string _error;
    readonly StackPanel _tabBody = new();

    // ───────── page views ─────────

    void View_Checked(object sender, RoutedEventArgs e)
    {
        if (DevicesView == null) return;
        bool devices = DevicesTab.IsChecked == true;
        DevicesView.Visibility = devices ? Visibility.Visible : Visibility.Collapsed;
        ConverterView.Visibility = devices ? Visibility.Collapsed : Visibility.Visible;
    }

    // ───────── the device list ─────────

    async void ScanMice()
    {
        int gen = ++_gen;
        _busy = false;
        _current = null;
        if (_clientId != 0 && MouseHost.Instance is { } open) { try { await open.Run("close", _clientId).WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
        _clientId = 0; _openKey = null; _known.Clear();
        var host = MouseHost.Instance;
        if (host == null) { ShowList(Loc.T("Mouse support isn't included in this build of Pulse.")); return; }
        ShowList(Loc.T("Looking for devices…"));
        try
        {
            var json = await host.Run("scan").WaitAsync(TimeSpan.FromSeconds(20));
            if (gen != _gen) return;
            // One card per device: a mouse and its receiver can show up several times; keep the best match of each.
            _found = JsonSerializer.Deserialize<List<JsonElement>>(json)!
                .Select(e => new Found(e.GetProperty("key").GetString(), e.GetProperty("vendorId").GetInt32(), e.GetProperty("productId").GetInt32(),
                    e.GetProperty("productName").GetString() ?? "", e.GetProperty("score").GetDouble(),
                    e.TryGetProperty("image", out var img) ? img.GetString() : null))
                .GroupBy(f => (f.VendorId, f.ProductId)).Select(g => g.OrderByDescending(f => f.Score).First())
                .OrderBy(f => f.Kind is "Mouse" or "Receiver" ? 0 : 1).ToList();
            ShowList(null);
        }
        catch (Exception ex) { if (gen == _gen) ShowList(Loc.F("Couldn't look for devices: {0}", ex.Message)); }
    }

    void ShowList(string message)
    {
        DevicesView.Children.Clear();
        DevicesView.Children.Add(new TextBlock { Text = Loc.T("Devices"), FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush") });
        DevicesView.Children.Add(new TextBlock
        {
            Text = message ?? (_found.Count == 0
                ? Loc.T("No supported device found. Plug in your mouse (or its receiver) and scan again. Close your mouse's own app if it's open: it can hold on to the mouse.")
                : Loc.T("Devices Pulse can talk to. Pick one to change its settings.")),
            FontSize = 12.5, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 4, 0, 18), TextWrapping = TextWrapping.Wrap,
        });
        var grid = new WrapPanel();
        foreach (var f in _found) grid.Children.Add(DeviceCard(f));
        grid.Children.Add(ScanCard());
        DevicesView.Children.Add(grid);
        DevicesView.Children.Add(new TextBlock
        {
            Text = Loc.T("Device support comes from OpenMouse's open-source drivers (about 60 brands). Changes are saved on the device itself, so they work everywhere, even without Pulse running."),
            FontSize = 11, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left,
        });
    }

    // OpenMouse's showcase picture on the "Add device" card.
    const string ShowcaseArt = "https://img.openmouse.app/logitech-g502-x-plus.png";

    Border DeviceCard(Found f)
    {
        bool connected = f.Key == _openKey && _known.ContainsKey(f.Key);
        var known = connected ? _known[f.Key] : default;
        string Get(string name) => connected && known.Status.ValueKind == JsonValueKind.Object && known.Status.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v.ToString() : null;

        var sp = new DockPanel { LastChildFill = true };
        var top = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = Pretty(Get("name") ?? f.Name), FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var pill = connected ? Pill("●  " + Loc.T("Connected"), Green, GreenDim) : Pill(Loc.T("Not connected"), Res("TextDimBrush") as SolidColorBrush, Inset);
        pill.Margin = new Thickness(0);
        Grid.SetColumn(pill, 1);
        head.Children.Add(pill);
        top.Children.Add(head);
        top.Children.Add(new TextBlock
        {
            Text = connected && !string.IsNullOrEmpty(known.Brand) ? $"{Pretty(known.Brand)} · {Loc.T(f.Label)}" : Loc.T(f.Label),
            FontSize = 11.5, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 6, 0, 0),
        });
        if (connected)
        {
            var facts = new TextBlock { FontSize = 12, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 8, 0, 0) };
            void Add(string text, bool bold)
            {
                if (facts.Inlines.Count > 0) facts.Inlines.Add(new System.Windows.Documents.Run("    "));
                facts.Inlines.Add(new System.Windows.Documents.Run(text) { FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, Foreground = bold ? Res("TextBrush") : Res("TextDimBrush") });
            }
            if (Get("connectionType") is { } conn) Add(Loc.T(conn), false);
            if (double.TryParse(Get("batteryPercent"), NumberStyles.Float, CultureInfo.InvariantCulture, out var bat)) Add(Loc.F("{0}%", Math.Round(bat)), true);
            if (double.TryParse(Get("pollingRateHz"), NumberStyles.Float, CultureInfo.InvariantCulture, out var hz))
            {
                Add(hz.ToString("N0", CultureInfo.CurrentCulture), true);
                facts.Inlines.Add(new System.Windows.Documents.Run(" " + Loc.T("Hz")));
            }
            top.Children.Add(facts);
        }
        else top.Children.Add(new TextBlock { Text = Loc.T("Open it to read its settings"), FontSize = 12, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 8, 0, 0) });
        DockPanel.SetDock(top, Dock.Top);
        sp.Children.Add(top);

        var go = connected
            ? new Button { Content = Loc.T("Configure"), Style = (Style)FindResource("GhostButton"), Background = Green, Foreground = Brushes.Black, FontWeight = FontWeights.SemiBold }
            : new Button { Content = Loc.T("Open"), Style = (Style)FindResource("GhostButton") };
        go.HorizontalAlignment = HorizontalAlignment.Stretch;
        go.Padding = new Thickness(10, 7, 10, 7);
        go.Click += (_, _) => OpenMouse(f);
        DockPanel.SetDock(go, Dock.Bottom);
        sp.Children.Add(go);
        sp.Children.Add(Stage(Art(connected ? known.Image : f.Image, f.Kind, 190), connected));

        var card = Card(sp, 246);
        card.Height = 430;
        if (connected) card.BorderBrush = Frozen(Color.FromArgb(0x90, 0x3D, 0xDC, 0x84));
        return card;
    }

    Border ScanCard()
    {
        var sp = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = Loc.T("Add device"), FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush") });
        top.Children.Add(new TextBlock { Text = Loc.T("Plug in your mouse (or its receiver), then scan to adjust its onboard settings."), FontSize = 11.5, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        DockPanel.SetDock(top, Dock.Top);
        sp.Children.Add(top);
        var scan = new Button { Content = "+  " + Loc.T("Add device"), Style = (Style)FindResource("GhostButton"), HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 7, 10, 7) };
        scan.Click += (_, _) => ScanMice();
        DockPanel.SetDock(scan, Dock.Bottom);
        sp.Children.Add(scan);
        sp.Children.Add(Stage(Art(ShowcaseArt, "Mouse", 150), false));
        var card = Card(sp, 246);
        card.Height = 430;
        return card;
    }

    // ───────── a device ─────────

    async void OpenMouse(Found f)
    {
        var host = MouseHost.Instance;
        if (host == null || _busy) return;
        int gen = ++_gen;
        _busy = true;
        _current = f;
        _tab = "overview";
        _error = null;
        // Already connected (back from its screen): show what was read, then read it again.
        if (_clientId != 0 && _openKey == f.Key && _known.TryGetValue(f.Key, out var had))
        {
            _status = had.Status;
            ShowDevice(null);
            try { await ReadStatus(gen); }
            catch (Exception ex) when (gen == _gen) { _error = Loc.F("Couldn't connect: {0}", ex is TimeoutException ? Loc.T("no answer") : ex.Message); ShowDevice(null); }
            catch { }
            finally { if (gen == _gen) _busy = false; }
            return;
        }
        _status = default;
        MouseHost.ClearLog();
        ShowDevice(Loc.T("Connecting…"));
        try
        {
            if (_clientId != 0) { try { await host.Run("close", _clientId).WaitAsync(TimeSpan.FromSeconds(5)); } catch { } _clientId = 0; }
            if (_openKey != null) _known.Remove(_openKey);
            _openKey = null;
            var openedJson = await host.Run("open", f.Key).WaitAsync(TimeSpan.FromSeconds(15));
            if (gen != _gen) return;
            var opened = JsonSerializer.Deserialize<JsonElement>(openedJson);
            _clientId = opened.GetProperty("id").GetInt32();
            _openKey = f.Key;
            _brand = opened.TryGetProperty("brand", out var brand) ? brand.GetString() ?? "" : "";
            _methods = opened.GetProperty("methods").EnumerateArray().Select(m => m.GetString()).ToHashSet();
            Profiles.Device = MouseSettings.DeviceId(f.VendorId, f.ProductId);
            Core.TweakEngine.Save();
            ShowDevice(Loc.T("Reading its settings… Some wireless mice take up to 30 seconds the first time."));
            await ReadStatus(gen);
        }
        catch (Exception) when (gen != _gen) { }   // the page moved on (closed, rescanned, another device opened)
        catch (TimeoutException)
        {
            _error = f.Kind == "Receiver" || f.Name.Contains("receiver", StringComparison.OrdinalIgnoreCase)
                ? Loc.T("The device didn't answer. If you have more than one receiver, pick the one your mouse is paired with, and wake the mouse by moving it.")
                : Loc.T("The device didn't answer. Close its own app if it's open, wake it by moving it, and try again.");
            ShowDevice(null);
        }
        catch (Exception ex) { _error = Loc.F("Couldn't connect: {0}", ex.Message); ShowDevice(null); }
        finally { if (gen == _gen) _busy = false; }
    }

    async Task ReadStatus(int gen)
    {
        var host = MouseHost.Instance!;
        var json = await host.Run("status", _clientId).WaitAsync(TimeSpan.FromSeconds(30));
        if (gen != _gen) return;
        _status = JsonSerializer.Deserialize<JsonElement>(json);
        if (_current != null)
        {
            // The picture comes from the name the device gives itself (a receiver only knows its mouse once asked).
            string image = _current.Image;
            try
            {
                var byName = await host.Run("image", Str("name") ?? _current.Name, _current.Kind == "Keyboard" ? "keyboard" : "mouse").WaitAsync(TimeSpan.FromSeconds(5));
                if (!string.IsNullOrEmpty(byName)) image = byName;
            }
            catch { }
            if (gen != _gen || _current == null) return;
            _known[_current.Key] = (_status, _brand, image);
        }
        ShowDevice(null);
    }

    /// <summary>Changes one setting on the device, then reads everything back so what's shown is what it has.</summary>
    async void Set(string method, params object[] args)
    {
        if (_busy || _clientId == 0) return;
        int gen = _gen;
        _busy = true;
        try
        {
            await MouseHost.Instance!.Run("call", _clientId, method, JsonSerializer.Serialize(args)).WaitAsync(TimeSpan.FromSeconds(20));
            if (gen != _gen) return;
            await ReadStatus(gen);
        }
        catch (Exception) when (gen != _gen) { }
        catch (Exception ex) { _error = Loc.F("The device didn't take that: {0}", ex is TimeoutException ? Loc.T("no answer") : ex.Message); ShowDevice(null); }
        finally { if (gen == _gen) _busy = false; }
    }

    // ───────── reading the status ─────────

    bool Has(string name) => _status.ValueKind == JsonValueKind.Object && _status.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    double Num(string name) => Has(name) && _status.GetProperty(name).ValueKind == JsonValueKind.Number ? _status.GetProperty(name).GetDouble() : double.NaN;
    string Str(string name) => Has(name) ? _status.GetProperty(name).ToString() : null;
    bool? Bool(string name) => Has(name) && _status.GetProperty(name).ValueKind is JsonValueKind.True or JsonValueKind.False ? _status.GetProperty(name).GetBoolean() : null;
    List<double> Nums(string name) => Has(name) && _status.GetProperty(name).ValueKind == JsonValueKind.Array
        ? _status.GetProperty(name).EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToList() : new();
    bool Hint(string name) => Has("ui") && _status.GetProperty("ui").TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // ───────── the device screen ─────────

    void ShowDevice(string working)
    {
        var f = _current;
        if (f == null) { ShowList(null); return; }   // never leave the page empty
        DevicesView.Children.Clear();

        // Breadcrumb: Devices / name
        var crumb = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        var back = new Button { Content = Loc.T("Devices"), Style = (Style)FindResource("GhostButton"), Padding = new Thickness(10, 4, 10, 4) };
        back.Click += (_, _) =>
        {
            // The device stays connected (its card shows it), like OpenMouse; it's closed when another is opened or the page closes.
            _current = null;
            ShowList(null);
        };
        crumb.Children.Add(back);
        crumb.Children.Add(new TextBlock { Text = "  /  " + Pretty(Str("name") ?? f.Name), FontSize = 12.5, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center });
        DevicesView.Children.Add(crumb);

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());

        // Left: the device.
        var left = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        left.Children.Add(new TextBlock { Text = Pretty(Str("name") ?? f.Name), FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), TextWrapping = TextWrapping.Wrap });
        var facts = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        facts.Children.Add(Pill(working == null && _error == null && _status.ValueKind == JsonValueKind.Object ? Loc.T("Connected") : working != null ? Loc.T("Connecting") : Loc.T("No answer"),
            _error == null ? Green : Res("BadBrush") as SolidColorBrush, _error == null ? GreenDim : Frozen(Color.FromArgb(0x24, 0xE5, 0x67, 0x4F))));
        if (Str("connectionType") is { } conn) facts.Children.Add(Pill(Loc.T(conn), Res("TextDimBrush") as SolidColorBrush, Inset));
        if (!double.IsNaN(Num("batteryPercent"))) facts.Children.Add(Pill(Loc.F("{0}%", Num("batteryPercent")), Res("TextDimBrush") as SolidColorBrush, Inset));
        left.Children.Add(facts);
        left.Children.Add(new Border { Height = 280, Margin = new Thickness(0, 10, 0, 0), Child = Stage(Art(_known.TryGetValue(f.Key, out var k) ? k.Image : f.Image, f.Kind, 220), _error == null && _status.ValueKind == JsonValueKind.Object) });
        layout.Children.Add(left);

        // Right: tabs and their content.
        var right = new StackPanel();
        Grid.SetColumn(right, 1);
        if (working != null || _status.ValueKind != JsonValueKind.Object)
        {
            right.Children.Add(Panel(null, new TextBlock { Text = working ?? _error ?? "", FontSize = 13, Foreground = Res(_error != null ? "BadBrush" : "TextDimBrush"), TextWrapping = TextWrapping.Wrap }));
            if (_error != null) right.Children.Add(Panel(Loc.T("Details"), Diagnostics()));
        }
        else
        {
            var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            foreach (var (id, label, show) in new[]
            {
                ("overview", "Overview", true),
                ("performance", "Performance", true),
                ("buttons", "Buttons", HasButtons),
                ("lighting", "Lighting", HasLighting),
                ("profiles", "Profiles", CanProfile),
                ("advanced", "Advanced", true),
            })
            {
                if (!show) continue;
                var t = new RadioButton { Style = (Style)FindResource("Chip"), Content = Loc.T(label), IsChecked = _tab == id, Margin = new Thickness(0, 0, 6, 0), GroupName = "mouseTabs" };
                t.Checked += (_, _) => { _tab = id; ShowDevice(null); };
                tabs.Children.Add(t);
            }
            right.Children.Add(tabs);
            if (_error != null) right.Children.Add(new TextBlock { Text = _error, FontSize = 12.5, Foreground = Res("BadBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            switch (_tab)
            {
                case "performance": PerformanceTab(right); break;
                case "buttons": right.Children.Add(Panel(Loc.T("Buttons"), ButtonsPanel())); break;
                case "lighting": right.Children.Add(Panel(Loc.T("Lighting"), LightingPanel())); break;
                case "profiles": right.Children.Add(Panel(Loc.T("Game profiles"), ProfilesPanel())); break;
                case "advanced": AdvancedTab(right); break;
                default: OverviewTab(right); break;
            }
        }
        layout.Children.Add(right);
        DevicesView.Children.Add(layout);
    }

    // ───────── Overview ─────────

    void OverviewTab(StackPanel to)
    {
        // Current setup: the numbers that matter, big.
        var nums = new UniformGrid { Columns = 4 };
        void Big(string label, string value, string unit, string sub)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            sp.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 11.5, Foreground = Res("TextDimBrush") });
            var v = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            v.Children.Add(new TextBlock { Text = value, FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush") });
            if (unit != null) v.Children.Add(new TextBlock { Text = " " + unit, FontSize = 13, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5) });
            sp.Children.Add(v);
            if (sub != null) sp.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 2, 0, 0) });
            nums.Children.Add(sp);
        }
        var stages = Nums("dpiStages");
        int active = double.IsNaN(Num("activeDpiStage")) ? -1 : (int)Num("activeDpiStage");
        Big("Sensitivity", double.IsNaN(Num("dpi")) ? "–" : Num("dpi").ToString("N0", CultureInfo.CurrentCulture), Loc.T("DPI"), stages.Count > 0 && active >= 0 ? Loc.F("Stage {0} of {1}", active + 1, stages.Count) : null);
        double hz = Num("pollingRateHz");
        Big("Polling rate", double.IsNaN(hz) ? "–" : hz.ToString("N0", CultureInfo.CurrentCulture), Loc.T("Hz"), double.IsNaN(hz) ? null : Loc.F("{0} ms per report", (1000 / hz).ToString("0.###", CultureInfo.CurrentCulture)));
        double bat = Num("batteryPercent");
        Big("Battery", double.IsNaN(bat) ? "–" : bat.ToString("0", CultureInfo.CurrentCulture), double.IsNaN(bat) ? null : "%", Str("batteryState") is { } bs && bs != "Unknown" ? Loc.T(bs) : null);
        double prof = Num("activeProfile");
        Big("Onboard profile", double.IsNaN(prof) ? "–" : (prof + (prof == 0 ? 1 : 0)).ToString("0", CultureInfo.CurrentCulture), null, null);
        var setup = new StackPanel();
        setup.Children.Add(nums);
        if (stages.Count > 0) setup.Children.Add(Field(Loc.T("DPI stages"), DpiStages(stages, active), Loc.T("Click a stage to use it; type a new value and press Enter to change it.")));
        to.Children.Add(Panel(Loc.T("Current setup"), setup));

        var info = new StackPanel();
        if (Has("firmware") && _status.GetProperty("firmware").ValueKind == JsonValueKind.Array)
            foreach (var fw in _status.GetProperty("firmware").EnumerateArray().Select(x => x.ToString()).Where(x => x.Length > 0))
                info.Children.Add(new TextBlock { Text = fw, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 2, 0, 0) });
        if (Str("connectionDetail") is { Length: > 0 } d) info.Children.Add(new TextBlock { Text = d, FontSize = 12, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 6, 0, 0) });
        if (info.Children.Count > 0) to.Children.Add(Panel(Loc.T("Firmware"), info));
        if (Has("ui") && _status.GetProperty("ui").TryGetProperty("statusNote", out var note) && note.ValueKind == JsonValueKind.String)
            to.Children.Add(new TextBlock { Text = note.GetString(), FontSize = 12, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap });
    }

    FrameworkElement DpiStages(List<double> stages, int active)
    {
        var row = new WrapPanel();
        if (!_methods.Contains("setDpiStageValue"))
        {
            for (int i = 0; i < stages.Count; i++) row.Children.Add(Pill(stages[i].ToString("0", CultureInfo.CurrentCulture), i == active ? Green : Res("TextDimBrush") as SolidColorBrush, i == active ? GreenDim : Inset));
            return row;
        }
        for (int i = 0; i < stages.Count; i++)
        {
            int stage = i;
            var box = new TextBox { Style = (Style)FindResource("SearchBox"), Width = 78, Text = stages[i].ToString("0", CultureInfo.CurrentCulture), ToolTip = Loc.F("Stage {0}", i + 1), FontWeight = i == active ? FontWeights.Bold : FontWeights.Normal };
            box.GotKeyboardFocus += (_, _) => { if (stage != active && _methods.Contains("setActiveDpiStage")) Set("setActiveDpiStage", stage); };
            box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && TryNum(box.Text, out var v)) Set("setDpiStageValue", stage, (int)v); };
            var cell = new StackPanel { Margin = new Thickness(0, 0, 6, 6) };
            cell.Children.Add(box);
            cell.Children.Add(new Border { Height = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(12, 4, 12, 0), Background = i == active ? Green : Brushes.Transparent });
            row.Children.Add(cell);
        }
        return row;
    }

    // ───────── Performance ─────────

    void PerformanceTab(StackPanel to)
    {
        var sp = new StackPanel();
        if (Nums("dpiStages").Count == 0 && !double.IsNaN(Num("dpi")) && _methods.Contains("setDpi"))
        {
            var box = new TextBox { Style = (Style)FindResource("SearchBox"), Width = 100, Text = Num("dpi").ToString("0", CultureInfo.CurrentCulture) };
            box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && TryNum(box.Text, out var v)) Set("setDpi", (int)v); };
            sp.Children.Add(Field(Loc.T("DPI"), box, Loc.T("Type a value and press Enter.")));
        }
        if (!double.IsNaN(Num("pollingRateHz")))
        {
            var rates = Rates();
            bool writable = _methods.Contains("setPollingRate") && !Hint("pollingReadOnly");
            var row = new WrapPanel();
            foreach (var hz in rates) row.Children.Add(Choice(hz >= 1000 ? Loc.F("{0}K Hz", hz / 1000) : Loc.F("{0} Hz", hz), hz == Num("pollingRateHz"), writable, () => Set("setPollingRate", (int)hz)));
            sp.Children.Add(Field(Loc.T("Polling rate"), row, writable ? Loc.T("Higher sends your movement more often: smoother and a little quicker, for more CPU and battery.") : Loc.T("This device reports its polling rate but can't change it from here.")));
        }
        if (Has("liftOffDistance") && _methods.Contains("setLiftOffDistance"))
        {
            var row = new WrapPanel();
            foreach (var lod in new[] { "Low", "Medium", "High" })
                if (!(lod == "Low" && Hint("hideLodLow"))) row.Children.Add(Choice(Loc.T(lod), Str("liftOffDistance") == lod, true, () => Set("setLiftOffDistance", lod)));
            sp.Children.Add(Field(Loc.T("Lift-off distance"), row, Loc.T("How high you can lift the mouse before it stops tracking. Low suits low-sens players who lift and reset often.")));
        }
        if (!Hint("hideProcessingCard"))
        {
            var toggles = new StackPanel();
            void Toggle(string field, string setter, string label, string hide, string tip)
            {
                if (Hint(hide) || Bool(field) is not { } on || !_methods.Contains(setter)) return;
                toggles.Children.Add(SwitchRow(Loc.T(label), Loc.T(tip), on, v => Set(setter, v)));
            }
            Toggle("motionSync", "setMotionSync", "Motion sync", "hideMotionSync", "Lines sensor reads up with USB polls: smoother tracking for about half a millisecond of delay.");
            Toggle("angleSnapping", "setAngleSnapping", "Angle snapping", "hideAngleSnapping", "Straightens lines you draw. Usually off for aiming: it bends small corrections.");
            Toggle("rippleControl", "setRippleControl", "Ripple control", "hideRippleControl", "Smooths jitter at high DPI, at the cost of a little delay.");
            if (toggles.Children.Count > 0) sp.Children.Add(Field(Loc.T("Sensor"), toggles, null));
        }
        to.Children.Add(Panel(Loc.T("Performance"), sp.Children.Count > 0 ? sp : Note(Loc.T("This device's driver has no performance settings."))));
    }

    List<double> Rates()
    {
        var rates = Nums("supportedPollingRates");
        if (rates.Count == 0) rates = new() { 125, 250, 500, 1000 };
        return rates.Distinct().OrderBy(x => x).ToList();
    }

    // ───────── Buttons ─────────

    bool HasButtons => Has("buttonMappings") && _methods.Contains("setButtonMapping") && _status.GetProperty("buttonMappings").ValueKind == JsonValueKind.Object
        && Has("buttonOptions") && _status.GetProperty("buttonOptions").GetArrayLength() > 0;

    FrameworkElement ButtonsPanel()
    {
        var options = _status.GetProperty("buttonOptions").EnumerateArray().Select(o => o.GetString()).Where(o => o != null).ToList();
        var fixedOnes = Has("fixedButtons") ? _status.GetProperty("fixedButtons").EnumerateArray().Select(o => o.GetString()).ToHashSet() : new HashSet<string>();
        var sp = new StackPanel();
        sp.Children.Add(Note(Loc.T("What each button does. Saved on the mouse, so it works in every game and on any PC.")));
        foreach (var b in _status.GetProperty("buttonMappings").EnumerateObject())
        {
            string button = b.Name, action = b.Value.ToString();
            var pick = new ComboBox { Width = 240, IsEnabled = !fixedOnes.Contains(button), HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var o in options.Contains(action) ? options : options.Prepend(action)) pick.Items.Add(o);
            pick.SelectedItem = action;
            pick.SelectionChanged += (_, _) => { if (pick.SelectedItem is string a && a != action) Set("setButtonMapping", button, a); };
            sp.Children.Add(Field(Loc.T(button), pick, null));
        }
        return sp;
    }

    // ───────── Lighting ─────────

    bool HasLighting => Has("lighting") && _methods.Contains("setLighting") && _status.GetProperty("lighting").ValueKind == JsonValueKind.Object
        && _status.GetProperty("lighting").TryGetProperty("modes", out var m) && m.ValueKind == JsonValueKind.Array && m.GetArrayLength() > 0;

    FrameworkElement LightingPanel()
    {
        var l = _status.GetProperty("lighting");
        List<string> List(string name) => l.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.ToString()).ToList() : new();
        string Text(string name) => l.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
        var modes = List("modes");
        bool writeOnly = l.TryGetProperty("writeOnly", out var wo) && wo.ValueKind == JsonValueKind.True;
        var sp = new StackPanel();
        if (writeOnly) sp.Children.Add(Note(Loc.T("This mouse can't report its lighting back, so this shows what Pulse last set.")));
        var mode = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var md in modes) mode.Items.Add(md);
        mode.SelectedItem = Text("mode") ?? modes[0];
        sp.Children.Add(Field(Loc.T("Effect"), mode, null));
        var color = new TextBox { Style = (Style)FindResource("SearchBox"), Width = 110, Text = Text("color") ?? "#ffffff", HorizontalAlignment = HorizontalAlignment.Left };
        var color2 = new TextBox { Style = (Style)FindResource("SearchBox"), Width = 110, Text = Text("color2") ?? "#000000", HorizontalAlignment = HorizontalAlignment.Left };
        var speed = new ComboBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var s in List("speeds")) speed.Items.Add(s);
        speed.SelectedItem = Text("speed");
        var bright = new ComboBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var b in List("brightnessLevels")) bright.Items.Add(b);
        bright.SelectedItem = Text("brightness");
        var fColor = Field(Loc.T("Colour"), color, "#rrggbb");
        var fColor2 = Field(Loc.T("Second colour"), color2, "#rrggbb");
        var fSpeed = Field(Loc.T("Speed"), speed, null);
        var fBright = Field(Loc.T("Brightness"), bright, null);
        sp.Children.Add(fColor); sp.Children.Add(fColor2); sp.Children.Add(fSpeed); sp.Children.Add(fBright);
        void Visible()
        {
            var md = mode.SelectedItem as string;
            fColor.Visibility = List("colorModes").Contains(md) ? Visibility.Visible : Visibility.Collapsed;
            fColor2.Visibility = List("dualColorModes").Contains(md) ? Visibility.Visible : Visibility.Collapsed;
            fSpeed.Visibility = List("reactiveModes").Contains(md) && speed.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            fBright.Visibility = bright.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        mode.SelectionChanged += (_, _) => Visible();
        Visible();
        var apply = new Button { Content = Loc.T("Apply"), Style = (Style)FindResource("PrimaryButton"), Background = Green, BorderBrush = Green, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        apply.Click += (_, _) =>
        {
            // The driver takes the whole lighting object back with the changes in it.
            var next = JsonSerializer.Deserialize<Dictionary<string, object>>(l.GetRawText())!;
            next["mode"] = mode.SelectedItem as string;
            if (System.Text.RegularExpressions.Regex.IsMatch(color.Text.Trim(), "^#[0-9a-fA-F]{6}$")) next["color"] = color.Text.Trim().ToLowerInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(color2.Text.Trim(), "^#[0-9a-fA-F]{6}$")) next["color2"] = color2.Text.Trim().ToLowerInvariant();
            if (speed.SelectedItem is string spd && double.TryParse(spd, NumberStyles.Float, CultureInfo.InvariantCulture, out var sv)) next["speed"] = sv;
            if (bright.SelectedItem is string br && double.TryParse(br, NumberStyles.Float, CultureInfo.InvariantCulture, out var bv)) next["brightness"] = bv;
            Set("setLighting", next);
        };
        sp.Children.Add(apply);
        return sp;
    }

    // ───────── Profiles ─────────

    static MouseSettings Profiles => Core.TweakEngine.State.Mouse ??= new MouseSettings();
    bool CanProfile => _methods.Contains("setDpi") || _methods.Contains("setDpiStageValue") || (_methods.Contains("setPollingRate") && !Hint("pollingReadOnly"));

    /// <summary>DPI and polling rate per game, put on the mouse when the game starts; the "no game" profile when it closes.</summary>
    FrameworkElement ProfilesPanel()
    {
        var s = Profiles;
        var rates = Rates();
        bool canDpi = _methods.Contains("setDpi") || _methods.Contains("setDpiStageValue");
        bool canHz = _methods.Contains("setPollingRate") && !Hint("pollingReadOnly");
        var sp = new StackPanel();
        sp.Children.Add(Note(Loc.T("Pulse switches your mouse when a game starts, and back when it closes. Leave a box empty to keep that setting as it is.")));
        void Save() { Core.TweakEngine.Save(); GameSessionService.Refresh(); }

        FrameworkElement Line(string title, MouseProfile p, Action remove)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var dpi = new TextBox { Style = (Style)FindResource("SearchBox"), Width = 90, Text = p.Dpi > 0 ? p.Dpi.ToString(CultureInfo.CurrentCulture) : "", IsEnabled = canDpi, ToolTip = Loc.T("DPI") };
            dpi.LostKeyboardFocus += (_, _) => { p.Dpi = TryNum(dpi.Text, out var v) && v >= 50 && v <= 50000 ? (int)v : 0; dpi.Text = p.Dpi > 0 ? p.Dpi.ToString(CultureInfo.CurrentCulture) : ""; Save(); };
            row.Children.Add(dpi);
            row.Children.Add(new TextBlock { Text = Loc.T("DPI"), FontSize = 12, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 16, 0) });
            var hz = new ComboBox { Width = 120, IsEnabled = canHz };
            hz.Items.Add(new ComboBoxItem { Content = Loc.T("Keep"), Tag = 0 });
            foreach (var r in rates) hz.Items.Add(new ComboBoxItem { Content = r >= 1000 ? Loc.F("{0}K Hz", r / 1000) : Loc.F("{0} Hz", r), Tag = (int)r });
            hz.SelectedItem = hz.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == p.PollingHz) ?? hz.Items[0];
            hz.SelectionChanged += (_, _) => { p.PollingHz = (int)((ComboBoxItem)hz.SelectedItem).Tag; Save(); };
            row.Children.Add(hz);
            if (remove != null)
            {
                var x = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Remove"), Margin = new Thickness(10, 0, 0, 0) };
                x.Click += (_, _) => { remove(); Save(); ShowDevice(null); };
                row.Children.Add(x);
            }
            return Field(title, row, null);
        }

        s.Desktop ??= new MouseProfile();
        sp.Children.Add(Line(Loc.T("No game running"), s.Desktop, null));
        foreach (var p in s.Games.ToList()) sp.Children.Add(Line(p.Game, p, () => s.Games.Remove(p)));

        var add = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var pick = new ComboBox { Width = 230, IsEditable = true };
        foreach (var name in Core.Games.GameCatalog.All.Select(g => g.Name).Distinct().OrderBy(n => n))
            if (!s.Games.Any(p => string.Equals(p.Game, name, StringComparison.OrdinalIgnoreCase))) pick.Items.Add(name);
        if (GameSessionService.CurrentGame is { } running) pick.Text = running;
        var addBtn = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Add game"), Margin = new Thickness(10, 0, 0, 0) };
        addBtn.Click += (_, _) =>
        {
            var name = pick.Text?.Trim();
            if (string.IsNullOrEmpty(name) || s.Games.Any(p => string.Equals(p.Game, name, StringComparison.OrdinalIgnoreCase))) return;
            s.Games.Add(new MouseProfile { Game = name, Dpi = double.IsNaN(Num("dpi")) ? 0 : (int)Num("dpi"), PollingHz = double.IsNaN(Num("pollingRateHz")) ? 0 : (int)Num("pollingRateHz") });
            Save();
            ShowDevice(null);
        };
        add.Children.Add(pick);
        add.Children.Add(addBtn);
        sp.Children.Add(add);
        return sp;
    }

    // ───────── Advanced ─────────

    void AdvancedTab(StackPanel to)
    {
        var sp = new StackPanel();
        if (!double.IsNaN(Num("debounceMs")) && _methods.Contains("setDebounceTime"))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var box = new TextBox { Style = (Style)FindResource("SearchBox"), Width = 70, Text = Num("debounceMs").ToString("0.#", CultureInfo.CurrentCulture) };
            box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && TryNum(box.Text, out var v)) Set("setDebounceTime", v); };
            row.Children.Add(box);
            row.Children.Add(new TextBlock { Text = Loc.T("ms"), FontSize = 12.5, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            sp.Children.Add(Field(Loc.T("Click debounce"), row, Loc.T("Lower registers clicks sooner; too low can cause accidental double clicks on worn switches.")));
        }
        if (!double.IsNaN(Num("sleepTimeout")) && _methods.Contains("setSleepTimeout") && !Hint("hideSleepCard"))
        {
            var row = new WrapPanel();
            foreach (var sec in new[] { 30, 60, 120, 300, 600, 900 })
                row.Children.Add(Choice(sec < 60 ? Loc.F("{0} s", sec) : Loc.F("{0} min", sec / 60), (int)Num("sleepTimeout") == sec, true, () => Set("setSleepTimeout", sec)));
            sp.Children.Add(Field(Loc.T("Auto sleep"), row, Loc.T("How long the wireless mouse waits, unused, before it sleeps to save battery.")));
        }
        if (!double.IsNaN(Num("batteryPercent")))
        {
            var s = Profiles;
            sp.Children.Add(SwitchRow(Loc.F("Warn me when the battery drops below {0}%", s.BatteryAlertPercent), null, s.BatteryAlert, v => { s.BatteryAlert = v; Core.TweakEngine.Save(); }));
        }
        if (sp.Children.Count > 0) to.Children.Add(Panel(Loc.T("Advanced"), sp));
        to.Children.Add(Panel(Loc.T("Diagnostics"), Diagnostics()));
    }

    /// <summary>What Pulse and the driver said to the device, to copy and send when something doesn't work.</summary>
    FrameworkElement Diagnostics()
    {
        var sp = new StackPanel();
        sp.Children.Add(Note(Loc.T("If something doesn't work, copy this and send it: it shows what Pulse asked the device and what came back.")));
        var lines = MouseHost.RecentLog();
        var text = $"{_current?.Name} {_current?.VendorId:X4}:{_current?.ProductId:X4} score {_current?.Score}\n{string.Join("\n", lines)}";
        sp.Children.Add(new Border
        {
            Background = Inset, CornerRadius = new CornerRadius(8), Padding = new Thickness(10), MaxHeight = 220, Margin = new Thickness(0, 8, 0, 0),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = lines.Count == 0 ? Loc.T("Nothing yet.") : string.Join("\n", lines.TakeLast(60)), FontFamily = new FontFamily("Consolas"), FontSize = 11, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap } },
        });
        var copy = new Button { Content = Loc.T("Copy details"), Style = (Style)FindResource("GhostButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        copy.Click += (_, _) => { try { Clipboard.SetText(text); copy.Content = Loc.T("Copied"); } catch { } };
        sp.Children.Add(copy);
        return sp;
    }

    // ───────── pieces ─────────

    Brush Res(string key) => (Brush)FindResource(key);

    Border Card(UIElement child, double width) => new()
    {
        Width = width, Margin = new Thickness(0, 0, 14, 14), Padding = new Thickness(14), CornerRadius = new CornerRadius(12),
        Background = CardBg, BorderBrush = CardLine, BorderThickness = new Thickness(1), Child = child,
    };

    /// <summary>A rounded section with a small title, like OpenMouse's cards.</summary>
    Border Panel(string title, UIElement content)
    {
        var sp = new StackPanel();
        if (title != null) sp.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.Bold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 0, 0, 10) });
        sp.Children.Add(content);
        return new Border { Child = sp, Padding = new Thickness(18, 16, 18, 18), Margin = new Thickness(0, 0, 0, 12), CornerRadius = new CornerRadius(12), Background = CardBg, BorderBrush = CardLine, BorderThickness = new Thickness(1) };
    }

    FrameworkElement Field(string label, UIElement control, string help)
    {
        var g = new Grid { Margin = new Thickness(0, 8, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 6, 12, 0), TextWrapping = TextWrapping.Wrap });
        var right = new StackPanel();
        right.Children.Add(control);
        if (help != null) right.Children.Add(new TextBlock { Text = help, FontSize = 11.5, Foreground = Res("TextFaintBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) });
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    FrameworkElement SwitchRow(string label, string help, bool on, Action<bool> changed)
    {
        var cb = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = on, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) };
        cb.Click += (_, _) => changed(cb.IsChecked == true);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Res("TextBrush") });
        if (help != null) text.Children.Add(new TextBlock { Text = help, FontSize = 11.5, Foreground = Res("TextFaintBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Margin = new Thickness(0, 2, 0, 0) });
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 10) };
        row.Children.Add(cb);
        row.Children.Add(text);
        return row;
    }

    Button Choice(string text, bool on, bool enabled, Action click)
    {
        var b = new Button
        {
            Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(12, 5, 12, 5), IsEnabled = enabled || on,
            Style = (Style)FindResource("GhostButton"),
        };
        if (on) { b.Background = GreenDim; b.BorderBrush = Green; b.Foreground = Green; }
        if (!on && enabled) b.Click += (_, _) => click();
        return b;
    }

    static Border Pill(string text, SolidColorBrush fg, Brush bg) => new()
    {
        CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 0, 6, 4), Background = bg,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = fg },
    };

    TextBlock Note(string text) => new() { Text = text, FontSize = 12, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };

    /// <summary>"WLMOUSE BEAST MAX 8K RECEIVER" → "WLmouse Beast Max 8K Receiver".</summary>
    static string Pretty(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Loc.T("Device");
        if (name.Any(char.IsLower)) return name;
        var t = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
        return System.Text.RegularExpressions.Regex.Replace(t.Replace("Wlmouse", "WLmouse"), @"\b(\d+)k\b", "$1K");
    }

    /// <summary>
    /// The device's product picture (from OpenMouse's image server, kept on disk once downloaded), or, like OpenMouse,
    /// a black silhouette with a question mark when there's no picture for it.
    /// </summary>
    static FrameworkElement Art(string url, string kind, double size)
    {
        var host = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (DeviceArt.Cached(url) is { } file && Show(host, file, size)) return host;
        host.Children.Add(Unknown(kind, size));
        if (!string.IsNullOrEmpty(url)) Load(host, url, size);
        return host;

        static async void Load(Grid host, string url, double size)
        {
            if (await DeviceArt.Get(url) is { } got) Show(host, got, size);
        }
    }

    static bool Show(Grid host, string file, double size)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = (int)(size * 2);
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
            bmp.Freeze();
            host.Children.Clear();
            host.Children.Add(new Image { Source = bmp, Stretch = Stretch.Uniform });
            RenderOptions.SetBitmapScalingMode(host, BitmapScalingMode.HighQuality);
            return true;
        }
        catch { return false; }   // a format Windows can't read: keep the silhouette
    }

    /// <summary>OpenMouse's placeholder: a flat black mouse (or keyboard) with a white question mark.</summary>
    static FrameworkElement Unknown(string kind, double size)
    {
        bool keyboard = kind == "Keyboard";
        var shape = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(keyboard
                ? "M3,0 H97 A3,3 0 0 1 100,3 V30 A3,3 0 0 1 97,33 H3 A3,3 0 0 1 0,30 V3 A3,3 0 0 1 3,0 Z"
                : "M50,0 C28,0 18,16 18,40 C18,46 15,48 15,54 C15,58 18,60 18,64 C18,86 32,100 50,100 C68,100 82,86 82,64 C82,60 85,58 85,54 C85,48 82,46 82,40 C82,16 72,0 50,0 Z"),
            Fill = Brushes.Black, Stretch = Stretch.Uniform,
            Width = keyboard ? size : size * 0.62, Height = keyboard ? size * 0.33 : size,
        };
        var mark = new TextBlock
        {
            Text = "?", FontFamily = new FontFamily("Arial"), FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            FontSize = keyboard ? size * 0.22 : size * 0.3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, keyboard ? 0 : size * 0.12, 0, 0),
        };
        return new Grid { Children = { shape, mark }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>The picture on a soft floor shadow, with a green glow behind a connected device, as OpenMouse does.</summary>
    static FrameworkElement Stage(FrameworkElement picture, bool connected)
    {
        var g = new Grid { Margin = new Thickness(0, 10, 0, 12), ClipToBounds = false };
        if (connected)
            g.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 240, Height = 240, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Fill = new RadialGradientBrush(Color.FromArgb(0x30, 0x3D, 0xDC, 0x84), Color.FromArgb(0x00, 0x3D, 0xDC, 0x84)),
            });
        g.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 150, Height = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Fill = new RadialGradientBrush(Color.FromArgb(0x90, 0, 0, 0), Color.FromArgb(0, 0, 0, 0)),
        });
        g.Children.Add(picture);
        return g;
    }

    static bool TryNum(string text, out double v) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out v) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
