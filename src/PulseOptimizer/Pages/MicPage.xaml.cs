using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Mic;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// The Mic studio page: where your voice goes (mic → Pulse → virtual cable) with live meters, then tabs for the voice
/// chain (presets, AI noise removal, gate, tone, polish), a 10-band EQ you drag on a graph, voice effects, a soundboard
/// with hotkeys, and a record-and-compare sound check.
/// </summary>
public partial class MicPage : UserControl, IPage, IReleasable
{
    static MicSettings S => MicStudio.S;
    static string _tab = "voice";
    readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly DispatcherTimer _meters = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer _cableWatch = new() { Interval = TimeSpan.FromSeconds(3) };
    LevelMeter _inMeter, _outMeter;
    TextBlock _gateText;
    Ellipse _gateDot;
    EqGraph _eqGraph;
    readonly Action _changed;

    public MicPage()
    {
        InitializeComponent();
        _save.Tick += (_, _) => { _save.Stop(); TweakEngine.Save(); };
        _meters.Tick += (_, _) => TickMeters();
        _cableWatch.Tick += (_, _) =>
        {
            // VB-CABLE just installed? Pick it up without a restart.
            if (S.Enabled && MicStudio.Running && MicStudio.OutputName == null && MicStudio.FindCable() != null) MicStudio.Refresh();
        };
        _changed = () => Dispatcher.BeginInvoke(() => { UpdateState(); Build(); });
        Loaded += (_, _) => { MicStudio.Changed += _changed; _meters.Start(); _cableWatch.Start(); UpdateState(); Build(); };
        Unloaded += (_, _) => Release();
        EnabledSwitch.IsChecked = S.Enabled;
        UpdateState();
        Build();
    }

    public void OnShown(object parameter) { UpdateState(); Build(); }

    public void Release()
    {
        MicStudio.Changed -= _changed;
        _meters.Stop();
        _cableWatch.Stop();
        MicStudio.Bypass = false;
        if (_save.IsEnabled) { _save.Stop(); TweakEngine.Save(); }
    }

    // ───────── shared bits ─────────

    Brush Res(string k) => (Brush)FindResource(k);
    FontFamily Icons => (FontFamily)FindResource("IconFont");

    Border Card(UIElement child, double bottom = 16) => new()
    {
        Style = (Style)FindResource("Card"), Padding = new Thickness(20, 18, 20, 18), Margin = new Thickness(0, 0, 0, bottom), Child = child,
    };

    TextBlock Title(string t, double bottom = 10) => new() { Text = Loc.T(t), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 0, 0, bottom) };
    TextBlock Dim(string t, double size = 12.5, string brush = "TextDimBrush") => new() { Text = t, FontSize = size, Foreground = Res(brush), TextWrapping = TextWrapping.Wrap };
    TextBlock Icon(string glyph, double size, Brush fg = null) => new() { Text = glyph, FontFamily = Icons, FontSize = size, Foreground = fg ?? Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center };
    Border Divider(double top = 12, double bottom = 12) => new() { Height = 1, Background = Res("BorderBrush"), Margin = new Thickness(0, top, 0, bottom) };

    Button Btn(string text, Action click, bool primary = false, string glyph = null)
    {
        object content = Loc.T(text);
        if (glyph != null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = Loc.T(text), VerticalAlignment = VerticalAlignment.Center });
            content = sp;
        }
        var b = new Button { Style = (Style)FindResource(primary ? "PrimaryButton" : "GhostButton"), Content = content, Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    void Saved() { _save.Stop(); _save.Start(); }

    void UpdateState()
    {
        string text; Brush dot;
        if (!S.Enabled) { text = Loc.T("Off"); dot = Res("TextFaintBrush"); }
        else if (!MicStudio.Running) { text = Loc.T("Can't start"); dot = Res("BadBrush"); }
        else if (MicStudio.Muted) { text = Loc.T("Muted"); dot = Res("BadBrush"); }
        else if (MicStudio.OutputName == null) { text = Loc.T("Needs a virtual cable"); dot = Res("WarnBrush"); }
        else { text = Loc.T("On"); dot = Res("GoodBrush"); }
        StateText.Text = text;
        StateDot.Fill = dot;
        EnabledSwitch.IsChecked = S.Enabled;
    }

    void Enabled_Click(object sender, RoutedEventArgs e)
    {
        S.Enabled = EnabledSwitch.IsChecked == true;
        TweakEngine.Save();
        MicStudio.Refresh();
        UpdateState();
        Build();
    }

    void Build()
    {
        var offset = Scroll.VerticalOffset;
        Root.Children.Clear();
        _inMeter = _outMeter = null;
        _eqGraph = null;
        Root.Children.Add(FlowCard());
        if (S.Enabled && MicStudio.Running && MicStudio.OutputName == null) Root.Children.Add(SetupCard());
        else if (S.Enabled && MicStudio.Running) Root.Children.Add(AppsCard());

        var tabs = new[] { ("voice", "Voice", ""), ("eq", "Equalizer", ""), ("effects", "Voice effects", ""), ("sounds", "Soundboard", ""), ("check", "Sound check", "") };
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        var barGrid = new Grid { Margin = new Thickness(0, 10, 0, 18) };
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
            g.MouseLeftButtonUp += (_, _) => { _tab = key; Build(); };
            bar.Children.Add(g);
        }
        Root.Children.Add(barGrid);
        Root.Children.Add(_tab switch
        {
            "eq" => EqTab(),
            "effects" => EffectsTab(),
            "sounds" => SoundsTab(),
            "check" => CheckTab(),
            _ => VoiceTab(),
        });
        Root.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Body"), FontSize = 12, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 18, 0, 0), MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left, TextWrapping = TextWrapping.Wrap,
            Text = Loc.T("Noise removal uses RNNoise (open source, by Xiph). Everything runs on your PC: your voice never leaves it except through the apps you talk in."),
        });
        Dispatcher.BeginInvoke(() => Scroll.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }

    // ───────── mic → Pulse → cable ─────────

    UIElement FlowCard()
    {
        var root = new StackPanel();
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        g.ColumnDefinitions.Add(new ColumnDefinition());

        // 1. Your mic
        var mics = MicStudio.Mics();
        var micPick = new ComboBox { Height = 34, Margin = new Thickness(0, 8, 0, 0) };
        micPick.Items.Add(new ComboBoxItem { Content = Loc.T("Windows default mic"), Tag = "" });
        foreach (var m in mics) micPick.Items.Add(new ComboBoxItem { Content = m.Name, Tag = m.Id });
        micPick.SelectedItem = micPick.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == S.CaptureId) ?? micPick.Items[0];
        micPick.SelectionChanged += (_, _) => { S.CaptureId = (string)((ComboBoxItem)micPick.SelectedItem).Tag; TweakEngine.Save(); if (S.Enabled) MicStudio.Refresh(); };
        g.Children.Add(Node("", "Your mic", MicStudio.InputName ?? Loc.T("Not started"), micPick, 0));

        g.Children.Add(Arrow(1));

        // 2. Pulse
        var chain = new List<string>();
        if (S.NoiseRemoval && MicStudio.HasNoiseRemoval) chain.Add(Loc.T("Noise removal"));
        if (S.Gate) chain.Add(Loc.T("Gate"));
        chain.Add(Loc.T("EQ"));
        if (S.Leveling > 0.01 || S.AutoVolume) chain.Add(Loc.T("Leveling"));
        if (S.Effect != "none") chain.Add(Loc.T(MicSettings.Effects.FirstOrDefault(e => e.Key == S.Effect).Name ?? ""));
        var preset = MicSettings.Presets.FirstOrDefault(p => p.Key == S.Preset);
        var pulseInfo = Dim(string.Join(" · ", chain), 12, "TextFaintBrush");
        pulseInfo.Margin = new Thickness(0, 8, 0, 0);
        g.Children.Add(Node("", "Pulse", preset.Name != null ? Loc.T(preset.Name) : Loc.T("Custom"), pulseInfo, 2, accent: true));

        g.Children.Add(Arrow(3));

        // 3. Where it goes
        var outs = MicStudio.Outputs();
        FrameworkElement outCtl;
        if (outs.Any(o => MicStudio.IsCable(o.Name)) || !string.IsNullOrEmpty(S.OutputId))
        {
            var outPick = new ComboBox { Height = 34, Margin = new Thickness(0, 8, 0, 0) };
            outPick.Items.Add(new ComboBoxItem { Content = Loc.T("Find the cable automatically"), Tag = "" });
            foreach (var o in outs.Where(o => MicStudio.IsCable(o.Name) || o.Id == S.OutputId)) outPick.Items.Add(new ComboBoxItem { Content = o.Name, Tag = o.Id });
            outPick.SelectedItem = outPick.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == S.OutputId) ?? outPick.Items[0];
            outPick.SelectionChanged += (_, _) => { S.OutputId = (string)((ComboBoxItem)outPick.SelectedItem).Tag; TweakEngine.Save(); if (S.Enabled) MicStudio.Refresh(); };
            outCtl = outPick;
        }
        else
        {
            var b = Btn("Get VB-CABLE (free)", () => Open("https://vb-audio.com/Cable/"), primary: true, glyph: "");
            b.Margin = new Thickness(0, 8, 0, 0);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            outCtl = b;
        }
        g.Children.Add(Node("", "Discord, games, OBS", MicStudio.OutputName != null ? Loc.F("Pick \"{0}\" as the mic", CableOutName(MicStudio.OutputName)) : Loc.T("Needs a virtual cable"), outCtl, 4));
        root.Children.Add(g);

        if (S.Enabled && !MicStudio.Running && MicStudio.Problem != null && MicStudio.Problem != "NoCable")
        {
            var p = new Border { Background = Res("BadSoftBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 16, 0, 0), Child = Dim(Loc.T(MicStudio.Problem), 12.5, "TextBrush") };
            root.Children.Add(p);
        }

        if (!S.Enabled)
        {
            root.Children.Add(Divider(16, 14));
            var row = new DockPanel();
            var on = Btn("Turn on the Mic studio", () => { EnabledSwitch.IsChecked = true; Enabled_Click(null, null); }, primary: true, glyph: "");
            DockPanel.SetDock(on, Dock.Right);
            row.Children.Add(on);
            row.Children.Add(Dim(Loc.T("Turn it on to see your levels and try the settings. You can listen to yourself before installing anything."), 12.5));
            root.Children.Add(row);
            return Card(root);
        }

        // Live levels
        root.Children.Add(Divider(16, 14));
        var meters = new Grid();
        meters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        meters.ColumnDefinitions.Add(new ColumnDefinition());
        meters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        meters.RowDefinitions.Add(new RowDefinition());
        meters.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        meters.RowDefinitions.Add(new RowDefinition());
        void Row(int r, string label, LevelMeter m)
        {
            var t = Dim(Loc.T(label), 12);
            t.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(t, r);
            meters.Children.Add(t);
            Grid.SetRow(m, r); Grid.SetColumn(m, 1);
            meters.Children.Add(m);
        }
        _inMeter = new LevelMeter { Height = 12, Good = Res("GoodBrush"), Warn = Res("WarnBrush"), Bad = Res("BadBrush"), Track = Res("Surface2Brush"), Marker = Res("TextDimBrush") };
        _outMeter = new LevelMeter { Height = 12, Good = Res("AccentBrush"), Warn = Res("WarnBrush"), Bad = Res("BadBrush"), Track = Res("Surface2Brush") };
        Row(0, "Mic", _inMeter);
        Row(2, "Others hear", _outMeter);
        var gate = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _gateDot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        _gateText = Dim("", 12);
        gate.Children.Add(_gateDot);
        gate.Children.Add(_gateText);
        Grid.SetColumn(gate, 2); Grid.SetRowSpan(gate, 3);
        meters.Children.Add(gate);
        root.Children.Add(meters);

        // Listen / compare / mute
        var acts = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        var listen = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = S.Monitor, VerticalAlignment = VerticalAlignment.Center };
        listen.Click += (_, _) => { MicStudio.SetMonitor(listen.IsChecked == true); Saved(); };
        var lp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 22, 6), VerticalAlignment = VerticalAlignment.Center };
        lp.Children.Add(listen);
        var lt = Dim(Loc.T("Listen to yourself"), 13, "TextBrush");
        lt.Margin = new Thickness(10, 0, 0, 0); lt.VerticalAlignment = VerticalAlignment.Center;
        lp.Children.Add(lt);
        acts.Children.Add(lp);

        var hold = Btn("Hold to hear the original", () => { }, glyph: "");
        hold.ToolTip = Loc.T("While you hold this, everyone hears your mic without Pulse, so you can compare.");
        hold.PreviewMouseLeftButtonDown += (_, _) => { MicStudio.Bypass = true; if (!S.Monitor) MicStudio.SetMonitor(true); };
        hold.PreviewMouseLeftButtonUp += (_, _) => MicStudio.Bypass = false;
        hold.MouseLeave += (_, _) => MicStudio.Bypass = false;
        hold.Margin = new Thickness(0, 0, 8, 6);
        acts.Children.Add(hold);

        var mute = Btn(MicStudio.Muted ? "Unmute" : "Mute", MicStudio.ToggleMute, glyph: MicStudio.Muted ? "" : "");
        mute.ToolTip = S.MuteHotkey ? (MicStudio.MuteHotkeyWorks ? "Ctrl+Shift+M" : Loc.T("Ctrl+Shift+M is used by another app.")) : null;
        mute.Margin = new Thickness(0, 0, 8, 6);
        acts.Children.Add(mute);
        root.Children.Add(acts);
        return Card(root);
    }

    static string CableOutName(string input) => input.Replace("CABLE Input", "CABLE Output").Replace("Voicemeeter Input", "Voicemeeter Out B1").Replace("VoiceMeeter Input", "VoiceMeeter Output");

    UIElement Node(string glyph, string title, string sub, FrameworkElement below, int col, bool accent = false)
    {
        var sp = new StackPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        var badge = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = accent ? Res("AccentBrush") : Res("Surface2Brush"), Child = Icon(glyph, 15, accent ? Res("OnAccentBrush") : Res("TextBrush")) };
        ((TextBlock)badge.Child).HorizontalAlignment = HorizontalAlignment.Center;
        top.Children.Add(badge);
        var tx = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        tx.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
        tx.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = Res("TextDimBrush"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 });
        top.Children.Add(tx);
        sp.Children.Add(top);
        if (below != null) sp.Children.Add(below);
        Grid.SetColumn(sp, col);
        return sp;
    }

    UIElement Arrow(int col)
    {
        var a = Icon("", 14, Res("TextFaintBrush"));
        a.HorizontalAlignment = HorizontalAlignment.Center;
        a.VerticalAlignment = VerticalAlignment.Top;
        a.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetColumn(a, col);
        return a;
    }

    UIElement SetupCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Title("One-time setup: a virtual cable"));
        sp.Children.Add(Dim(Loc.T("Windows needs a virtual cable to carry your cleaned-up voice into other apps. VB-CABLE is free and takes a minute. Voicemeeter works too."), 12.5));
        var steps = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        string[] lines =
        {
            "Download VB-CABLE, unzip it and run VBCABLE_Setup_x64 as administrator.",
            "Come back here. Pulse finds the cable by itself, no restart needed.",
            "In Discord (or your game), set the input device to \"CABLE Output\".",
        };
        for (int i = 0; i < lines.Length; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Res("Surface2Brush"), Child = new TextBlock { Text = (i + 1).ToString(CultureInfo.InvariantCulture), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            var t = Dim(Loc.T(lines[i]), 13, "TextBrush");
            t.Margin = new Thickness(10, 2, 0, 0);
            row.Children.Add(t);
            steps.Children.Add(row);
        }
        sp.Children.Add(steps);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        btns.Children.Add(Btn("Get VB-CABLE (free)", () => Open("https://vb-audio.com/Cable/"), primary: true, glyph: ""));
        btns.Children.Add(Btn("Check again", MicStudio.Refresh, glyph: ""));
        sp.Children.Add(btns);
        var b = Card(sp);
        b.BorderBrush = Res("WarnBrush");
        return b;
    }

    UIElement AppsCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Title("Use it in your apps"));
        var name = CableOutName(MicStudio.OutputName);
        var g = new UniformGrid3();
        g.Add(AppTip("Discord", Loc.F("Settings → Voice & Video → Input device: \"{0}\". Turn off Discord's own noise suppression, since Pulse already removes noise.", name)));
        g.Add(AppTip("Games", Loc.F("In the game's audio settings, pick \"{0}\" as the microphone. Or make it the default mic in Windows so every game uses it.", name)));
        g.Add(AppTip("OBS", Loc.F("Add an Audio Input Capture source with \"{0}\" and mute your normal mic there.", name)));
        sp.Children.Add(g.Panel);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(Btn("Open Windows sound settings", () => Open("ms-settings:sound"), glyph: ""));
        sp.Children.Add(row);
        return Card(sp);
    }

    UIElement AppTip(string app, string text)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        sp.Children.Add(new TextBlock { Text = app, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 0, 0, 4) });
        sp.Children.Add(Dim(text, 12));
        return sp;
    }

    sealed class UniformGrid3
    {
        public readonly System.Windows.Controls.Primitives.UniformGrid Panel = new() { Columns = 3 };
        public void Add(UIElement e) => Panel.Children.Add(e);
    }

    static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void TickMeters()
    {
        if (_inMeter == null || !IsVisible) return;
        bool run = MicStudio.Running;
        _inMeter.Set(run ? MicStudio.InPeak : 0);
        _inMeter.MarkerDb = S.Gate ? MicStudio.GateLevelDb : double.NaN;
        _outMeter.Set(run ? MicStudio.OutPeak : 0);
        if (_gateText != null)
        {
            bool talking = run && MicStudio.GateOpen && !MicStudio.Muted;
            _gateText.Text = !run ? Loc.T("Not running") : MicStudio.Muted ? Loc.T("Muted") : MicStudio.Bypass ? Loc.T("Original") : talking ? Loc.T("Sending voice") : Loc.T("Gate closed");
            _gateDot.Fill = !run || MicStudio.Muted ? Res("BadBrush") : MicStudio.Bypass ? Res("WarnBrush") : talking ? Res("GoodBrush") : Res("TextFaintBrush");
        }
    }

    // ───────── Voice ─────────

    UIElement VoiceTab()
    {
        var root = new StackPanel();

        // Presets
        var ps = new StackPanel();
        ps.Children.Add(Title("Sound"));
        var wrap = new WrapPanel();
        foreach (var p in MicSettings.Presets)
        {
            bool on = S.Preset == p.Key;
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = Loc.T(p.Name), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            sp.Children.Add(new TextBlock { Text = Loc.T(p.Note), FontSize = 11.5, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            var tile = new Border
            {
                Width = 168, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Child = sp,
                Background = on ? Res("InfoSoftBrush") : Res("Surface2Brush"), BorderBrush = on ? Res("AccentBrush") : Res("BorderBrush"), BorderThickness = new Thickness(on ? 1.5 : 1),
            };
            var key = p.Key;
            tile.MouseLeftButtonUp += (_, _) => { S.ApplyPreset(key); Saved(); Build(); };
            wrap.Children.Add(tile);
        }
        ps.Children.Add(wrap);
        root.Children.Add(Card(ps));

        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel();
        var right = new StackPanel();
        Grid.SetColumn(right, 2);
        cols.Children.Add(left);
        cols.Children.Add(right);

        // Clean up: noise removal and gate
        var clean = new StackPanel();
        clean.Children.Add(Title("Clean up", 4));
        if (!MicStudio.HasNoiseRemoval)
            clean.Children.Add(Dim(Loc.T("AI noise removal isn't included in this build of Pulse."), 12, "WarnBrush"));
        var strength = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
        foreach (var (val, label) in new[] { (0.6, "Light"), (0.85, "Balanced"), (1.0, "Max") })
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = Loc.T(label), GroupName = "mic-strength", IsChecked = Math.Abs(S.Strength - val) < 0.01, Margin = new Thickness(0, 0, 6, 0) };
            rb.Checked += (_, _) => { S.Strength = val; Custom(); };
            strength.Children.Add(rb);
        }
        clean.Children.Add(Section("AI noise removal", "Removes keyboard clicks, fans, dogs and room noise but keeps your voice.", () => S.NoiseRemoval, v => S.NoiseRemoval = v, strength));
        var manual = Slider("Gate level", -80, -20, 1, () => S.GateDb, v => S.GateDb = v, unit: " dB");
        manual.Visibility = S.GateAuto ? Visibility.Collapsed : Visibility.Visible;
        var auto = Switch("Set it automatically", "Learns how noisy your room is and opens just above that.", () => S.GateAuto, v => { S.GateAuto = v; manual.Visibility = v ? Visibility.Collapsed : Visibility.Visible; });
        clean.Children.Add(Section("Noise gate", "Silences your mic between words, so others only hear you when you talk.", () => S.Gate, v => S.Gate = v, auto, manual));
        left.Children.Add(Card(clean));

        // Tone
        var tone = new StackPanel();
        tone.Children.Add(Title("Tone", 4));
        tone.Children.Add(Slider("Bass", -6, 12, 0.5, () => S.BassDb, v => S.BassDb = v, unit: " dB", note: "Warmth and body"));
        tone.Children.Add(Slider("Clarity", -6, 12, 0.5, () => S.ClarityDb, v => S.ClarityDb = v, unit: " dB", note: "Makes words easier to understand"));
        tone.Children.Add(Slider("Air", -6, 12, 0.5, () => S.AirDb, v => S.AirDb = v, unit: " dB", note: "Crisp, open top end"));
        right.Children.Add(Card(tone));

        // Polish
        var pol = new StackPanel();
        pol.Children.Add(Title("Polish", 4));
        pol.Children.Add(Switch("Auto volume", "Keeps you at the same volume whether you lean in or sit back.", () => S.AutoVolume, v => S.AutoVolume = v));
        pol.Children.Add(Slider("Leveling", 0, 1, 0.05, () => S.Leveling, v => S.Leveling = v, pct: true, note: "Evens out shouting and quiet talking"));
        pol.Children.Add(Slider("De-esser", 0, 1, 0.05, () => S.DeEss, v => S.DeEss = v, pct: true, note: "Tames sharp \"s\" sounds"));
        pol.Children.Add(Slider("Output volume", -12, 12, 0.5, () => S.OutputDb, v => S.OutputDb = v, unit: " dB", note: "A limiter keeps you from ever clipping"));
        left.Children.Add(Card(pol));

        var keys = new StackPanel();
        keys.Children.Add(Title("Hotkeys", 4));
        keys.Children.Add(Switch("Ctrl+Shift+M mutes your mic", MicStudio.MuteHotkeyWorks || !S.MuteHotkey ? "Works in any game. You'll hear a short sound." : "Another app is already using Ctrl+Shift+M.", () => S.MuteHotkey, v => { S.MuteHotkey = v; MicStudio.HotkeysChanged(); }));
        right.Children.Add(Card(keys));

        root.Children.Add(cols);
        return root;
    }

    /// <summary>A slider moved away from the preset.</summary>
    void Custom()
    {
        if (S.Preset != "custom") { S.Preset = "custom"; }
        Saved();
    }

    // ───────── Equalizer ─────────

    UIElement EqTab()
    {
        var sp = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var reset = Btn("Reset", () => { S.Eq = new double[10]; Custom(); Build(); }, glyph: "");
        DockPanel.SetDock(reset, Dock.Right);
        head.Children.Add(reset);
        head.Children.Add(Title("10-band equalizer", 0));
        sp.Children.Add(head);
        sp.Children.Add(Dim(Loc.T("Drag a point up or down. Double-click a point to set it back to 0. The line shows your whole sound, including Bass, Clarity and Air."), 12.5));
        _eqGraph = new EqGraph(S, Res("AccentBrush"), Res("BorderBrush"), Res("TextDimBrush"), Res("TextBrush"), Res("Surface2Brush"))
        {
            Height = 260, Margin = new Thickness(0, 14, 0, 0),
        };
        _eqGraph.Moved += Custom;
        sp.Children.Add(_eqGraph);

        sp.Children.Add(Divider(16, 10));
        sp.Children.Add(Dim(Loc.T("Quick fixes"), 12.5, "TextBrush"));
        var fixes = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        void Fix(string name, double[] delta)
        {
            var b = Btn(name, () =>
            {
                S.Eq ??= new double[10];
                for (int i = 0; i < 10; i++) S.Eq[i] = Math.Clamp(S.Eq[i] + delta[i], -12, 12);
                Custom(); Build();
            });
            b.Margin = new Thickness(0, 0, 8, 8);
            fixes.Children.Add(b);
        }
        Fix("Less muddy", new double[] { 0, 0, -1, -3, -2, 0, 0, 0, 0, 0 });
        Fix("Less boomy", new double[] { -3, -3, -1, 0, 0, 0, 0, 0, 0, 0 });
        Fix("Less nasal", new double[] { 0, 0, 0, 0, 0, -3, -1, 0, 0, 0 });
        Fix("Less harsh", new double[] { 0, 0, 0, 0, 0, 0, -1, -3, -1, 0 });
        Fix("More presence", new double[] { 0, 0, 0, 0, 0, 1, 2, 3, 1, 0 });
        sp.Children.Add(fixes);
        return Card(sp);
    }

    // ───────── Voice effects ─────────

    UIElement EffectsTab()
    {
        var sp = new StackPanel();
        sp.Children.Add(Title("Voice effects"));
        sp.Children.Add(Dim(Loc.T("Fun voices for games with friends. Noise removal and the rest still run first, so the effect stays clean."), 12.5));
        var wrap = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var glyphs = new Dictionary<string, string>
        {
            ["none"] = "", ["radio"] = "", ["megaphone"] = "", ["robot"] = "", ["chipmunk"] = "",
            ["giant"] = "", ["demon"] = "", ["cave"] = "", ["stadium"] = "",
        };
        foreach (var (key, name) in MicSettings.Effects)
        {
            bool on = S.Effect == key;
            var inner = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var ic = Icon(glyphs.TryGetValue(key, out var gl) ? gl : "", 22, on ? Res("AccentBrush") : Res("TextBrush"));
            ic.HorizontalAlignment = HorizontalAlignment.Center;
            inner.Children.Add(ic);
            inner.Children.Add(new TextBlock { Text = Loc.T(name), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });
            var tile = new Border
            {
                Width = 108, Height = 92, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand, Child = inner, Padding = new Thickness(0, 18, 0, 0),
                Background = on ? Res("InfoSoftBrush") : Res("Surface2Brush"), BorderBrush = on ? Res("AccentBrush") : Res("BorderBrush"), BorderThickness = new Thickness(on ? 1.5 : 1),
            };
            tile.MouseLeftButtonUp += (_, _) => { S.Effect = key; Saved(); Build(); };
            wrap.Children.Add(tile);
        }
        sp.Children.Add(wrap);
        sp.Children.Add(Divider(10, 4));
        sp.Children.Add(Slider("Room reverb", 0, 1, 0.05, () => S.Reverb, v => S.Reverb = v, pct: true, note: "Adds a little space around your voice. Keep it low for voice chat."));
        var tip = Dim(Loc.T("Tip: turn on \"Listen to yourself\" above to hear the effect as you pick it."), 12, "TextFaintBrush");
        tip.Margin = new Thickness(0, 10, 0, 0);
        sp.Children.Add(tip);
        return Card(sp);
    }

    // ───────── Soundboard ─────────

    UIElement SoundsTab()
    {
        var root = new StackPanel();
        var sp = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(Btn("Stop all", MicStudio.StopSounds, glyph: ""));
        btns.Children.Add(Btn("Add sounds", AddSounds, primary: true, glyph: ""));
        DockPanel.SetDock(btns, Dock.Right);
        head.Children.Add(btns);
        head.Children.Add(Title("Soundboard", 0));
        sp.Children.Add(head);
        sp.Children.Add(Dim(Loc.T("Play sounds into voice chat, mixed with your voice. MP3, WAV, M4A, AAC and WMA up to a minute long."), 12.5));
        if (!MicStudio.Running) sp.Children.Add(Dim(Loc.T("Turn the Mic studio on to use the soundboard."), 12, "WarnBrush"));

        var wrap = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        for (int i = 0; i < S.Sounds.Count; i++)
        {
            var item = S.Sounds[i];
            int idx = i;
            var g = new Grid();
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = item.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 18, 0) });
            stack.Children.Add(new TextBlock { Text = idx < 9 && S.SoundHotkeys ? "Ctrl+Alt+" + (idx + 1) : " ", FontSize = 11, Foreground = Res("TextFaintBrush"), Margin = new Thickness(0, 2, 0, 6) });
            var vol = new Slider { Style = (Style)FindResource("Seek"), Minimum = 0, Maximum = 1.5, Value = item.Volume, ToolTip = Loc.T("Volume") };
            vol.ValueChanged += (_, e) => { item.Volume = e.NewValue; Saved(); };
            vol.PreviewMouseLeftButtonUp += (_, e) => e.Handled = false;
            stack.Children.Add(vol);
            g.Children.Add(stack);
            var del = new TextBlock { Text = "", FontFamily = Icons, FontSize = 10, Foreground = Res("TextFaintBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Cursor = Cursors.Hand, ToolTip = Loc.T("Remove") };
            del.MouseLeftButtonUp += (_, e) => { e.Handled = true; S.Sounds.Remove(item); TweakEngine.Save(); MicStudio.HotkeysChanged(); Build(); };
            g.Children.Add(del);
            var tile = new Border
            {
                Width = 176, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(12, 10, 10, 8), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand,
                Background = Res("Surface2Brush"), BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), Child = g, ToolTip = Loc.T("Click to play"),
            };
            tile.MouseLeftButtonUp += async (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && FindParent<Slider>(d) != null) return;
                tile.BorderBrush = Res("AccentBrush");
                var err = await MicStudio.Play(item);
                if (err != null) MainWindow.Instance.Toast(Loc.T(err), ToastKind.Warning);
                await Task.Delay(400);
                tile.BorderBrush = Res("BorderBrush");
            };
            wrap.Children.Add(tile);
        }
        if (S.Sounds.Count == 0)
        {
            var empty = new Border { Background = Res("Surface2Brush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(18), Child = Dim(Loc.T("No sounds yet. Add a few clips and press them (or their hotkey) while you talk."), 12.5) };
            wrap.Children.Add(empty);
        }
        sp.Children.Add(wrap);
        root.Children.Add(Card(sp));

        var opts = new StackPanel();
        opts.Children.Add(Title("Options", 4));
        opts.Children.Add(Slider("Soundboard volume", 0, 1, 0.05, () => S.SoundVolume, v => S.SoundVolume = v, pct: true, custom: false));
        opts.Children.Add(Switch("Hear sounds yourself", "Plays them in your headphones too.", () => S.HearSounds, v => S.HearSounds = v, custom: false));
        opts.Children.Add(Switch("Hotkeys", "Ctrl+Alt+1 to 9 play the first nine sounds, Ctrl+Alt+0 stops them. Work in any game.", () => S.SoundHotkeys, v => { S.SoundHotkeys = v; MicStudio.HotkeysChanged(); Build(); }, custom: false));
        root.Children.Add(Card(opts));
        return root;
    }

    static T FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    void AddSounds()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("Add sounds"), Multiselect = true, CheckFileExists = true, Filter = Loc.T("Sounds") + " (*.mp3;*.wav;*.m4a;*.aac;*.wma)|*.mp3;*.wav;*.m4a;*.aac;*.wma" };
        if (dlg.ShowDialog() != true) return;
        var dir = System.IO.Path.Combine(TweakEngine.DataDir, "sounds");
        Directory.CreateDirectory(dir);
        int added = 0, tooBig = 0;
        foreach (var f in dlg.FileNames)
        {
            try
            {
                var info = new FileInfo(f);
                if (info.Length > 25 * 1024 * 1024) { tooBig++; continue; }
                // A copy, so the sound keeps working if the original moves.
                var dest = System.IO.Path.Combine(dir, Guid.NewGuid().ToString("N")[..8] + "-" + info.Name);
                File.Copy(f, dest);
                S.Sounds.Add(new SoundItem { Name = System.IO.Path.GetFileNameWithoutExtension(f), File = dest });
                added++;
            }
            catch { }
        }
        TweakEngine.Save();
        MicStudio.HotkeysChanged();
        if (tooBig > 0) MainWindow.Instance.Toast(Loc.T("Some files were over 25 MB and weren't added."), ToastKind.Warning);
        Build();
    }

    // ───────── Sound check ─────────

    UIElement CheckTab()
    {
        var sp = new StackPanel();
        sp.Children.Add(Title("Sound check"));
        sp.Children.Add(Dim(Loc.F("Record {0} seconds, then play it back as your mic really sounds and as others hear it with Pulse. Type or turn on a fan while you talk to hear the noise removal.", MicStudio.CheckSeconds), 12.5));
        var row = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        if (!MicStudio.Running)
        {
            sp.Children.Add(Dim(Loc.T("Turn the Mic studio on to do a sound check."), 12, "WarnBrush"));
            return Card(sp);
        }
        if (MicStudio.Checking)
        {
            var rec = new StackPanel { Orientation = Orientation.Horizontal };
            rec.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = Res("BadBrush"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            rec.Children.Add(Dim(Loc.T("Recording… talk normally."), 13.5, "TextBrush"));
            row.Children.Add(rec);
        }
        else
        {
            row.Children.Add(Btn(MicStudio.HasCheck ? "Record again" : "Record", () => { MicStudio.StartCheck(); }, primary: !MicStudio.HasCheck, glyph: ""));
            if (MicStudio.HasCheck)
            {
                row.Children.Add(Btn("Play original", () => MicStudio.PlayCheck(false), glyph: ""));
                row.Children.Add(Btn("Play with Pulse", () => MicStudio.PlayCheck(true), primary: true, glyph: ""));
                row.Children.Add(Btn("Stop", MicStudio.StopCheck, glyph: ""));
            }
        }
        sp.Children.Add(row);
        var note = Dim(Loc.T("Playback uses your settings right now, so change something and press \"Play with Pulse\" again to compare."), 12, "TextFaintBrush");
        note.Margin = new Thickness(0, 12, 0, 0);
        sp.Children.Add(note);
        return Card(sp);
    }

    // ───────── controls ─────────

    FrameworkElement Section(string title, string note, Func<bool> get, Action<bool> set, params FrameworkElement[] rows)
    {
        var sp = new StackPanel();
        var body = new StackPanel { Margin = new Thickness(0, 2, 0, 4), Visibility = get() ? Visibility.Visible : Visibility.Collapsed };
        foreach (var r in rows) body.Children.Add(r);
        sp.Children.Add(Switch(title, note, get, v => { set(v); body.Visibility = v ? Visibility.Visible : Visibility.Collapsed; }, bold: true));
        sp.Children.Add(body);
        return sp;
    }

    FrameworkElement Switch(string title, string note, Func<bool> get, Action<bool> set, bool bold = false, bool custom = true)
    {
        var g = new Grid { Margin = new Thickness(0, 8, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 13, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Res("TextBrush"), TextWrapping = TextWrapping.Wrap });
        if (note != null) { var d = Dim(Loc.T(note), 11.5); d.Margin = new Thickness(0, 2, 0, 0); sp.Children.Add(d); }
        g.Children.Add(sp);
        var cb = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = get(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        cb.Click += (_, _) => { set(cb.IsChecked == true); if (custom) Custom(); else Saved(); };
        Grid.SetColumn(cb, 1);
        g.Children.Add(cb);
        return g;
    }

    FrameworkElement Slider(string label, double min, double max, double step, Func<double> get, Action<double> set, bool pct = false, string unit = "", string note = null, bool custom = true)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
        var top = new Grid();
        var lab = new StackPanel { Orientation = Orientation.Horizontal };
        lab.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 13, Foreground = Res("TextBrush") });
        if (note != null) lab.Children.Add(new TextBlock { Text = "  " + Loc.T(note), FontSize = 11.5, Foreground = Res("TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        top.Children.Add(lab);
        string Fmt(double v) => pct ? Math.Round(v * 100) + "%" : (v > 0 && unit == " dB" ? "+" : "") + v.ToString("0.#", CultureInfo.CurrentCulture) + unit;
        var val = new TextBlock { Text = Fmt(get()), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), HorizontalAlignment = HorizontalAlignment.Right, Background = Res("SurfaceBrush"), Padding = new Thickness(8, 0, 0, 0) };
        top.Children.Add(val);
        sp.Children.Add(top);
        var sl = new Slider { Style = (Style)FindResource("Seek"), Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, Value = Math.Clamp(get(), min, max), Margin = new Thickness(0, 6, 0, 0) };
        sl.ValueChanged += (_, e) => { set(Math.Round(e.NewValue / step) * step); val.Text = Fmt(get()); if (custom) Custom(); else Saved(); };
        sp.Children.Add(sl);
        return sp;
    }
}

/// <summary>A horizontal level meter (−60…0 dBFS) with a falling peak line and an optional marker (the gate level).</summary>
sealed class LevelMeter : FrameworkElement
{
    double _level, _peak;
    DateTime _peakAt;
    public Brush Good, Warn, Bad, Track, Marker;
    public double MarkerDb = double.NaN;

    static double Pos(double db) => Math.Clamp((db + 60) / 60, 0, 1);

    public void Set(float peak)
    {
        double db = 20 * Math.Log10(Math.Max(peak, 1e-6));
        double p = Pos(db);
        _level = p > _level ? p : _level + (p - _level) * 0.25;
        if (p >= _peak) { _peak = p; _peakAt = DateTime.UtcNow; }
        else if ((DateTime.UtcNow - _peakAt).TotalMilliseconds > 900) _peak = Math.Max(p, _peak - 0.02);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;
        var r = new Rect(0, 0, w, h);
        dc.PushClip(new RectangleGeometry(r, h / 2, h / 2));
        dc.DrawRectangle(Track, null, r);
        // Green up to −18 dB, yellow to −6, red above.
        double lw = w * _level, g1 = w * Pos(-18), g2 = w * Pos(-6);
        if (lw > 0) dc.DrawRectangle(Good, null, new Rect(0, 0, Math.Min(lw, g1), h));
        if (lw > g1) dc.DrawRectangle(Warn, null, new Rect(g1, 0, Math.Min(lw, g2) - g1, h));
        if (lw > g2) dc.DrawRectangle(Bad, null, new Rect(g2, 0, lw - g2, h));
        dc.Pop();
        if (_peak > 0.01) dc.DrawRectangle(_peak > Pos(-6) ? Bad : Good, null, new Rect(Math.Max(0, w * _peak - 2), 0, 2, h));
        if (!double.IsNaN(MarkerDb) && Marker != null)
        {
            double x = w * Pos(MarkerDb);
            dc.DrawRectangle(Marker, null, new Rect(x - 1, -3, 2, h + 6));
        }
    }
}

/// <summary>The 10-band EQ as a graph: drag each band's point; the curve is the full EQ response (with bass / clarity / air).</summary>
sealed class EqGraph : FrameworkElement
{
    readonly MicSettings _s;
    readonly Brush _accent, _grid, _label, _text, _fill;
    readonly Pen _gridPen, _zeroPen, _curvePen;
    int _drag = -1;
    public event Action Moved;
    const double Pad = 30, Range = 12;

    public EqGraph(MicSettings s, Brush accent, Brush grid, Brush label, Brush text, Brush fill)
    {
        _s = s; _accent = accent; _grid = grid; _label = label; _text = text; _fill = fill;
        _gridPen = new Pen(grid, 1); _zeroPen = new Pen(label, 1) { DashStyle = DashStyles.Dash };
        _curvePen = new Pen(accent, 2.5) { LineJoin = PenLineJoin.Round };
        Cursor = Cursors.Hand;
        _s.Eq ??= new double[10];
        if (_s.Eq.Length != 10) _s.Eq = new double[10];
    }

    Rect Plot => new(Pad + 6, 10, Math.Max(10, ActualWidth - Pad - 16), Math.Max(10, ActualHeight - 38));
    double X(double f, Rect p) => p.Left + (Math.Log10(f) - Math.Log10(20)) / (Math.Log10(20000) - Math.Log10(20)) * p.Width;
    double Y(double db, Rect p) => p.Top + (1 - (Math.Clamp(db, -Range, Range) + Range) / (2 * Range)) * p.Height;
    Point Handle(int b, Rect p) => new(X(MicSettings.EqFreqs[b], p), Y(_s.Eq[b], p));

    protected override void OnRender(DrawingContext dc)
    {
        var p = Plot;
        dc.DrawRoundedRectangle(_fill, null, new Rect(0, 0, ActualWidth, ActualHeight), 10, 10);
        var tf = new Typeface("Segoe UI");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Txt(string t) => new(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 10.5, _label, dpi);
        foreach (var db in new[] { -12, -6, 0, 6, 12 })
        {
            double y = Y(db, p);
            dc.DrawLine(db == 0 ? _zeroPen : _gridPen, new Point(p.Left, y), new Point(p.Right, y));
            var t = Txt((db > 0 ? "+" : "") + db);
            dc.DrawText(t, new Point(p.Left - t.Width - 8, y - t.Height / 2));
        }
        for (int b = 0; b < 10; b++)
        {
            double x = X(MicSettings.EqFreqs[b], p);
            dc.DrawLine(_gridPen, new Point(x, p.Top), new Point(x, p.Bottom));
            var t = Txt(MicSettings.EqLabels[b]);
            dc.DrawText(t, new Point(x - t.Width / 2, p.Bottom + 8));
        }

        // The response curve, filled toward 0 dB.
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        var pts = new List<Point>();
        for (int i = 0; i <= 160; i++)
        {
            double f = 20 * Math.Pow(1000, i / 160.0);
            pts.Add(new Point(X(f, p), Y(MicChain.EqCurveDb(_s, f), p)));
        }
        using (var c = line.Open()) { c.BeginFigure(pts[0], false, false); c.PolyLineTo(pts.Skip(1).ToList(), true, true); }
        using (var c = area.Open())
        {
            double y0 = Y(0, p);
            c.BeginFigure(new Point(pts[0].X, y0), true, true);
            c.PolyLineTo(pts, true, true);
            c.LineTo(new Point(pts[^1].X, y0), true, true);
        }
        var soft = _accent.Clone(); soft.Opacity = 0.14;
        dc.DrawGeometry(soft, null, area);
        dc.DrawGeometry(null, _curvePen, line);

        for (int b = 0; b < 10; b++)
        {
            var h = Handle(b, p);
            dc.DrawEllipse(b == _drag ? _accent : _fill, new Pen(_accent, 2), h, 6.5, 6.5);
            if (b == _drag)
            {
                var t = new FormattedText((_s.Eq[b] > 0 ? "+" : "") + _s.Eq[b].ToString("0.0", CultureInfo.CurrentCulture) + " dB", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 11.5, _text, dpi);
                dc.DrawText(t, new Point(Math.Clamp(h.X - t.Width / 2, 0, ActualWidth - t.Width), Math.Max(0, h.Y - 26)));
            }
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = Plot;
        var m = e.GetPosition(this);
        int best = -1; double bd = 22;
        for (int b = 0; b < 10; b++) { double d = Math.Abs(Handle(b, p).X - m.X); if (d < bd) { bd = d; best = b; } }
        if (best < 0) return;
        if (e.ClickCount == 2) { _s.Eq[best] = 0; Moved?.Invoke(); InvalidateVisual(); return; }
        _drag = best;
        CaptureMouse();
        Move(m);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag >= 0) Move(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag < 0) return;
        _drag = -1;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    void Move(Point m)
    {
        var p = Plot;
        double db = (1 - (m.Y - p.Top) / p.Height) * 2 * Range - Range;
        _s.Eq[_drag] = Math.Round(Math.Clamp(db, -Range, Range) * 2) / 2;
        Moved?.Invoke();
        InvalidateVisual();
    }
}
