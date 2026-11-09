using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pulse.Core.Mouse;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// "Your mice": finds mice OpenMouse has a driver for, reads their settings and changes them on the mouse. Only the
/// controls a mouse's driver actually supports are shown, and every change is read back from the mouse.
/// </summary>
public partial class MousePage
{
    sealed record Found(string Key, int VendorId, int ProductId, string Name, double Score);

    List<Found> _found = new();
    Found _current;
    int _clientId;
    HashSet<string> _methods = new();
    JsonElement _status;
    bool _busy;

    async void ScanMice()
    {
        var host = MouseHost.Instance;
        if (host == null) { MiceStatus.Text = Loc.T("Mouse support isn't included in this build of Pulse."); ScanButton.IsEnabled = false; return; }
        MiceStatus.Text = Loc.T("Looking for mice…");
        try
        {
            var json = await host.Run("scan");
            // One entry per mouse: a mouse and its receiver can show up several times; keep the best match of each.
            _found = JsonSerializer.Deserialize<List<JsonElement>>(json)!
                .Select(e => new Found(e.GetProperty("key").GetString(), e.GetProperty("vendorId").GetInt32(), e.GetProperty("productId").GetInt32(),
                    e.GetProperty("productName").GetString() ?? "", e.GetProperty("score").GetDouble()))
                .GroupBy(f => (f.VendorId, f.ProductId)).Select(g => g.OrderByDescending(f => f.Score).First()).ToList();
        }
        catch (Exception ex) { MiceStatus.Text = Loc.F("Couldn't look for mice: {0}", ex.Message); return; }

        MiceList.Children.Clear();
        MiceStatus.Text = _found.Count == 0
            ? Loc.T("No supported mouse found. Plug it in (or its receiver) and press Scan again. Close your mouse's own app if it's open: it can hold on to the mouse.")
            : Loc.F("{0} found. Pick one to see its settings.", _found.Count);
        foreach (var f in _found)
        {
            var b = new Button { Style = (Style)FindResource(f == _current ? "PrimaryButton" : "GhostButton"), Margin = new Thickness(0, 0, 8, 8), Content = f.Name.Length > 0 ? f.Name : $"{f.VendorId:X4}:{f.ProductId:X4}", Tag = f };
            b.Click += (_, _) => OpenMouse((Found)b.Tag);
            MiceList.Children.Add(b);
        }
        if (_found.Count == 1 && _current == null) OpenMouse(_found[0]);
    }

    void Scan_Click(object sender, RoutedEventArgs e) => ScanMice();

    async void OpenMouse(Found f)
    {
        var host = MouseHost.Instance;
        if (host == null || _busy) return;
        _busy = true;
        MousePanel.Children.Clear();
        MousePanel.Children.Add(Faint(Loc.T("Connecting…")));
        try
        {
            if (_clientId != 0) { try { await host.Run("close", _clientId); } catch { } _clientId = 0; }
            var opened = JsonSerializer.Deserialize<JsonElement>(await host.Run("open", f.Key));
            _clientId = opened.GetProperty("id").GetInt32();
            _methods = opened.GetProperty("methods").EnumerateArray().Select(m => m.GetString()).ToHashSet();
            _current = f;
            Profiles.Device = MouseSettings.DeviceId(f.VendorId, f.ProductId);
            Core.TweakEngine.Save();
            await ReadStatus();
        }
        catch (Exception ex)
        {
            MousePanel.Children.Clear();
            MousePanel.Children.Add(Faint(Loc.F("Couldn't connect: {0}", ex.Message)));
        }
        finally { _busy = false; }
        foreach (Button b in MiceList.Children) b.Style = (Style)FindResource(b.Tag == _current ? "PrimaryButton" : "GhostButton");
    }

    async Task ReadStatus()
    {
        _status = JsonSerializer.Deserialize<JsonElement>(await MouseHost.Instance!.Run("status", _clientId));
        ShowSettings();
    }

    /// <summary>Changes one setting on the mouse, then reads everything back so what's shown is what the mouse has.</summary>
    async void Set(string method, params object[] args)
    {
        if (_busy || _clientId == 0) return;
        _busy = true;
        try
        {
            await MouseHost.Instance!.Run("call", _clientId, method, JsonSerializer.Serialize(args));
            await ReadStatus();
        }
        catch (Exception ex) { MousePanel.Children.Insert(0, Faint(Loc.F("The mouse didn't take that: {0}", ex.Message), "BadBrush")); }
        finally { _busy = false; }
    }

    // ───────── reading the status ─────────

    bool Has(string name) => _status.ValueKind == JsonValueKind.Object && _status.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    double Num(string name) => Has(name) && _status.GetProperty(name).ValueKind == JsonValueKind.Number ? _status.GetProperty(name).GetDouble() : double.NaN;
    string Str(string name) => Has(name) ? _status.GetProperty(name).ToString() : null;
    bool? Bool(string name) => Has(name) && _status.GetProperty(name).ValueKind is JsonValueKind.True or JsonValueKind.False ? _status.GetProperty(name).GetBoolean() : null;
    List<double> Nums(string name) => Has(name) && _status.GetProperty(name).ValueKind == JsonValueKind.Array
        ? _status.GetProperty(name).EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToList() : new();
    bool Hint(string name) => Has("ui") && _status.GetProperty("ui").TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // ───────── the settings ─────────

    void ShowSettings()
    {
        MousePanel.Children.Clear();
        var head = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        head.Children.Add(new TextBlock { Text = Str("name") ?? _current?.Name ?? "", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 14, 0) });
        var facts = new List<string>();
        if (Str("connectionType") is { } conn) facts.Add(Loc.T(conn) + (Str("connectionDetail") is { Length: > 0 } d ? " · " + d : ""));
        if (!double.IsNaN(Num("batteryPercent"))) facts.Add(Loc.F("Battery {0}%", Num("batteryPercent")) + (Str("batteryState") is { } bs && bs != "Unknown" && bs != "Discharging" ? " · " + Loc.T(bs) : ""));
        if (Has("firmware") && _status.GetProperty("firmware").ValueKind == JsonValueKind.Array)
        {
            var fw = string.Join(", ", _status.GetProperty("firmware").EnumerateArray().Select(x => x.ToString()).Where(x => x.Length > 0));
            if (fw.Length > 0) facts.Add(Loc.F("Firmware {0}", fw));
        }
        head.Children.Add(new TextBlock { Text = string.Join("  ·  ", facts), FontSize = 12.5, Foreground = (Brush)FindResource("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center });
        MousePanel.Children.Add(head);

        // DPI: stages when the mouse has them, otherwise one value.
        var stages = Nums("dpiStages");
        if (stages.Count > 0 && _methods.Contains("setDpiStageValue"))
        {
            int active = double.IsNaN(Num("activeDpiStage")) ? -1 : (int)Num("activeDpiStage");
            var row = Row("DPI stages", "Click a stage to use it; type a new value and press Enter to change it.");
            for (int i = 0; i < stages.Count; i++)
            {
                int stage = i;
                var box = new TextBox { Style = (Style)FindResource("SearchBox"), Width = 76, Text = stages[i].ToString("0", CultureInfo.CurrentCulture), ToolTip = Loc.F("Stage {0}", i + 1) };
                if (i == active) box.FontWeight = FontWeights.Bold;
                box.GotKeyboardFocus += (_, _) => { if (stage != active && _methods.Contains("setActiveDpiStage")) Set("setActiveDpiStage", stage); };
                box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && TryNum(box.Text, out var v)) Set("setDpiStageValue", stage, (int)v); };
                // The stage in use has a bar under it.
                var cell = new StackPanel { Margin = new Thickness(0, 0, 6, 6) };
                cell.Children.Add(box);
                cell.Children.Add(new Border { Height = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(10, 4, 10, 0), Background = i == active ? (Brush)FindResource("TextBrush") : Brushes.Transparent });
                row.Children.Add(cell);
            }
            MousePanel.Children.Add(Wrap(row));
        }
        else if (!double.IsNaN(Num("dpi")) && _methods.Contains("setDpi"))
        {
            var row = Row("DPI", "Type a value and press Enter.");
            var box = new TextBox { Style = (Style)FindResource("SearchBox"), Width = 90, Text = Num("dpi").ToString("0", CultureInfo.CurrentCulture) };
            box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && TryNum(box.Text, out var v)) Set("setDpi", (int)v); };
            row.Children.Add(box);
            MousePanel.Children.Add(Wrap(row));
        }

        // Polling rate.
        if (!double.IsNaN(Num("pollingRateHz")))
        {
            var rates = Nums("supportedPollingRates");
            if (rates.Count == 0) rates = new() { 125, 250, 500, 1000 };
            bool writable = _methods.Contains("setPollingRate") && !Hint("pollingReadOnly");
            var row = Row("Polling rate", writable ? null : "This mouse reports its polling rate but can't change it from here.");
            foreach (var hz in rates.Distinct().OrderBy(x => x))
                row.Children.Add(Choice(hz >= 1000 ? Loc.F("{0}K Hz", hz / 1000) : Loc.F("{0} Hz", hz), hz == Num("pollingRateHz"), writable, () => Set("setPollingRate", (int)hz)));
            MousePanel.Children.Add(Wrap(row));
        }

        // Lift-off distance.
        if (Has("liftOffDistance") && _methods.Contains("setLiftOffDistance"))
        {
            var row = Row("Lift-off distance", "How high you can lift the mouse before it stops tracking. Low suits low-sens players who lift and reset often.");
            foreach (var lod in new[] { "Low", "Medium", "High" })
            {
                if (lod == "Low" && Hint("hideLodLow")) continue;
                row.Children.Add(Choice(Loc.T(lod), Str("liftOffDistance") == lod, true, () => Set("setLiftOffDistance", lod)));
            }
            MousePanel.Children.Add(Wrap(row));
        }

        // Sensor processing.
        if (!Hint("hideProcessingCard"))
        {
            var toggles = new WrapPanel();
            void Toggle(string field, string setter, string label, string hide, string tip)
            {
                if (Hint(hide) || Bool(field) is not { } on || !_methods.Contains(setter)) return;
                var cb = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = on, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                cb.Click += (_, _) => Set(setter, cb.IsChecked == true);
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 26, 8), ToolTip = Loc.T(tip) };
                sp.Children.Add(cb);
                sp.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 13, Foreground = (Brush)FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
                toggles.Children.Add(sp);
            }
            Toggle("motionSync", "setMotionSync", "Motion sync", "hideMotionSync", "Lines sensor reads up with USB polls: smoother tracking for about half a millisecond of delay.");
            Toggle("angleSnapping", "setAngleSnapping", "Angle snapping", "hideAngleSnapping", "Straightens lines you draw. Usually off for aiming: it bends small corrections.");
            Toggle("rippleControl", "setRippleControl", "Ripple control", "hideRippleControl", "Smooths jitter at high DPI, at the cost of a little delay.");
            if (toggles.Children.Count > 0)
            {
                var row = Row("Sensor", null);
                row.Children.Add(toggles);
                MousePanel.Children.Add(Wrap(row));
            }
        }

        // Debounce.
        if (!double.IsNaN(Num("debounceMs")) && _methods.Contains("setDebounceTime"))
        {
            var row = Row("Click debounce", "Lower registers clicks sooner; too low can cause accidental double clicks on worn switches.");
            var box = new TextBox { Style = (Style)FindResource("SearchBox"), Width = 70, Text = Num("debounceMs").ToString("0.#", CultureInfo.CurrentCulture) };
            box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && TryNum(box.Text, out var v)) Set("setDebounceTime", v); };
            row.Children.Add(box);
            row.Children.Add(new TextBlock { Text = Loc.T("ms"), FontSize = 12.5, Foreground = (Brush)FindResource("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            MousePanel.Children.Add(Wrap(row));
        }

        // Sleep.
        if (!double.IsNaN(Num("sleepTimeout")) && _methods.Contains("setSleepTimeout") && !Hint("hideSleepCard"))
        {
            var row = Row("Sleep after", "How long the wireless mouse waits, unused, before it sleeps to save battery.");
            foreach (var sec in new[] { 30, 60, 120, 300, 600, 900 })
                row.Children.Add(Choice(sec < 60 ? Loc.F("{0} s", sec) : Loc.F("{0} min", sec / 60), (int)Num("sleepTimeout") == sec, true, () => Set("setSleepTimeout", sec)));
            MousePanel.Children.Add(Wrap(row));
        }

        if (Has("ui") && _status.GetProperty("ui").TryGetProperty("statusNote", out var note) && note.ValueKind == JsonValueKind.String)
            MousePanel.Children.Add(Faint(note.GetString()));
        ShowProfiles();
    }

    // ───────── game profiles ─────────

    static MouseSettings Profiles => Core.TweakEngine.State.Mouse ??= new MouseSettings();

    /// <summary>
    /// DPI and polling rate per game, put on the mouse when the game starts; the "no game" profile when it closes.
    /// Blank (or "Keep") leaves that setting as it is.
    /// </summary>
    void ShowProfiles()
    {
        var s = Profiles;
        var rates = Nums("supportedPollingRates");
        if (rates.Count == 0) rates = new() { 125, 250, 500, 1000 };
        bool canDpi = _methods.Contains("setDpi") || _methods.Contains("setDpiStageValue");
        bool canHz = _methods.Contains("setPollingRate") && !Hint("pollingReadOnly");
        if (!canDpi && !canHz) return;

        var box = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
        box.Children.Add(new TextBlock { Text = Loc.T("Game profiles"), FontSize = 15, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("TextBrush") });
        box.Children.Add(Faint(Loc.T("Pulse switches your mouse when a game starts, and back when it closes. Leave a box empty to keep that setting as it is.")));

        void Save() { Core.TweakEngine.Save(); Ui.GameSessionService.Refresh(); }

        FrameworkElement Line(string title, MouseProfile p, Action remove)
        {
            var g = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var dpi = new TextBox { Style = (Style)FindResource("SearchBox"), Width = 90, Text = p.Dpi > 0 ? p.Dpi.ToString(CultureInfo.CurrentCulture) : "", IsEnabled = canDpi, ToolTip = Loc.T("DPI") };
            dpi.LostKeyboardFocus += (_, _) => { p.Dpi = TryNum(dpi.Text, out var v) && v >= 50 && v <= 50000 ? (int)v : 0; dpi.Text = p.Dpi > 0 ? p.Dpi.ToString(CultureInfo.CurrentCulture) : ""; Save(); };
            Grid.SetColumn(dpi, 1);
            g.Children.Add(dpi);
            g.Children.Add(new TextBlock { Text = Loc.T("DPI"), FontSize = 12, Foreground = (Brush)FindResource("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 16, 0) }.Also(t => Grid.SetColumn(t, 2)));
            var hz = new ComboBox { Width = 120, IsEnabled = canHz };
            hz.Items.Add(new ComboBoxItem { Content = Loc.T("Keep"), Tag = 0 });
            foreach (var r in rates.Distinct().OrderBy(x => x)) hz.Items.Add(new ComboBoxItem { Content = r >= 1000 ? Loc.F("{0}K Hz", r / 1000) : Loc.F("{0} Hz", r), Tag = (int)r });
            hz.SelectedItem = hz.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == p.PollingHz) ?? hz.Items[0];
            hz.SelectionChanged += (_, _) => { p.PollingHz = (int)((ComboBoxItem)hz.SelectedItem).Tag; Save(); };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(hz);
            if (remove != null)
            {
                var x = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Remove"), Margin = new Thickness(10, 0, 0, 0) };
                x.Click += (_, _) => { remove(); Save(); ShowSettings(); };
                right.Children.Add(x);
            }
            Grid.SetColumn(right, 3);
            g.Children.Add(right);
            return g;
        }

        s.Desktop ??= new MouseProfile();
        box.Children.Add(Line(Loc.T("No game running"), s.Desktop, null));
        foreach (var p in s.Games.ToList())
            box.Children.Add(Line(p.Game, p, () => s.Games.Remove(p)));

        // Add a game: Pulse's game list, or type one in.
        var add = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var pick = new ComboBox { Width = 220, IsEditable = true };
        foreach (var name in Core.Games.GameCatalog.All.Select(g => g.Name).Distinct().OrderBy(n => n))
            if (!s.Games.Any(p => string.Equals(p.Game, name, StringComparison.OrdinalIgnoreCase))) pick.Items.Add(name);
        if (Ui.GameSessionService.CurrentGame is { } running) pick.Text = running;
        var addBtn = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T("Add game"), Margin = new Thickness(10, 0, 0, 0) };
        addBtn.Click += (_, _) =>
        {
            var name = pick.Text?.Trim();
            if (string.IsNullOrEmpty(name) || s.Games.Any(p => string.Equals(p.Game, name, StringComparison.OrdinalIgnoreCase))) return;
            s.Games.Add(new MouseProfile { Game = name, Dpi = (int)(double.IsNaN(Num("dpi")) ? 0 : Num("dpi")), PollingHz = (int)(double.IsNaN(Num("pollingRateHz")) ? 0 : Num("pollingRateHz")) });
            Save();
            ShowSettings();
        };
        add.Children.Add(pick);
        add.Children.Add(addBtn);
        box.Children.Add(add);

        // Battery alert.
        if (!double.IsNaN(Num("batteryPercent")))
        {
            var bat = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = s.BatteryAlert, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            bat.Click += (_, _) => { s.BatteryAlert = bat.IsChecked == true; Core.TweakEngine.Save(); };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
            row.Children.Add(bat);
            row.Children.Add(new TextBlock { Text = Loc.F("Warn me when the battery drops below {0}%", s.BatteryAlertPercent), FontSize = 13, Foreground = (Brush)FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center });
            box.Children.Add(row);
        }
        MousePanel.Children.Add(box);
    }

    // ───────── small pieces ─────────

    WrapPanel Row(string label, string help)
    {
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        row.Tag = (label, help);
        return row;
    }

    FrameworkElement Wrap(WrapPanel row)
    {
        var (label, help) = ((string, string))row.Tag;
        var g = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 6, 12, 0) });
        g.Children.Add(left);
        var right = new StackPanel();
        right.Children.Add(row);
        if (help != null) right.Children.Add(new TextBlock { Text = Loc.T(help), FontSize = 11.5, Foreground = (Brush)FindResource("TextFaintBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    Button Choice(string text, bool on, bool enabled, Action click)
    {
        var b = new Button { Style = (Style)FindResource(on ? "PrimaryButton" : "GhostButton"), Content = text, Margin = new Thickness(0, 0, 6, 6), IsEnabled = enabled || on, Padding = new Thickness(12, 5, 12, 5) };
        if (!on && enabled) b.Click += (_, _) => click();
        return b;
    }

    TextBlock Faint(string text, string brush = "TextDimBrush") =>
        new() { Text = text, FontSize = 12.5, Foreground = (Brush)FindResource(brush), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    static bool TryNum(string text, out double v) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out v) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
