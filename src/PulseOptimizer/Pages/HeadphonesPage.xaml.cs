using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Audio;
using Pulse.Core.Mic;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>
/// Game audio for headphones (like Sonar's game channel): where the sound comes from and goes to, presets, a 10-band
/// EQ, evening out loud and quiet sounds, virtual surround and volume.
/// </summary>
public partial class HeadphonesPage : UserControl, IPage, IReleasable
{
    static GameAudioSettings S => GameAudio.S;
    readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly DispatcherTimer _meter = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(3) };
    LevelMeter _level;
    readonly Action _changed;

    public HeadphonesPage()
    {
        InitializeComponent();
        _save.Tick += (_, _) => { _save.Stop(); TweakEngine.Save(); if (S.Engine == "apo" && GameAudio.ApplyApo() is { } p) MainWindow.Instance.Toast(Loc.T("Couldn't update Equalizer APO: ") + p, ToastKind.Warning); };
        _meter.Tick += (_, _) => { if (_level != null && IsVisible) _level.Set(GameAudio.Running ? GameAudio.Peak : 0); };
        _watch.Tick += (_, _) => { if (S.Enabled && GameAudio.Problem == "NoCable" && GameAudio.Sources().Count > 0) GameAudio.Refresh(); };
        _changed = () => Dispatcher.BeginInvoke(() => { UpdateState(); Build(); });
        Loaded += (_, _) => { GameAudio.Changed += _changed; _meter.Start(); _watch.Start(); UpdateState(); };
        Unloaded += (_, _) => Release();
        UpdateState();
        Build();
    }

    public void OnShown(object parameter) { UpdateState(); Build(); }

    public void Release()
    {
        GameAudio.Changed -= _changed;
        _meter.Stop(); _watch.Stop();
        GameAudio.Bypass = false;
        if (_save.IsEnabled) { _save.Stop(); TweakEngine.Save(); }
    }

    Brush Res(string k) => (Brush)FindResource(k);
    Border Card(UIElement child) => new() { Style = (Style)FindResource("Card"), Padding = new Thickness(20, 18, 20, 18), Margin = new Thickness(0, 0, 0, 16), Child = child };
    TextBlock Title(string t, double bottom = 10) => new() { Text = Loc.T(t), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), Margin = new Thickness(0, 0, 0, bottom) };
    TextBlock Dim(string t, double size = 12.5, string brush = "TextDimBrush") => new() { Text = t, FontSize = size, Foreground = Res(brush), TextWrapping = TextWrapping.Wrap };
    Button Btn(string text, Action click, bool primary = false)
    {
        var b = new Button { Style = (Style)FindResource(primary ? "PrimaryButton" : "GhostButton"), Content = Loc.T(text), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => click();
        return b;
    }
    void Saved() { _save.Stop(); _save.Start(); }
    static void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

    void UpdateState()
    {
        EnabledSwitch.IsChecked = S.Enabled;
        (StateText.Text, StateDot.Fill) = !S.Enabled ? (Loc.T("Off"), Res("TextFaintBrush"))
            : S.Engine == "apo" ? (EqApo.Installed ? (Loc.T("On"), Res("GoodBrush")) : (Loc.T("Needs Equalizer APO"), Res("WarnBrush")))
            : GameAudio.Problem == "NoCable" ? (Loc.T("Needs a virtual cable"), Res("WarnBrush"))
            : !GameAudio.Running ? (Loc.T("Can't start"), Res("BadBrush"))
            : (Loc.T("On"), Res("GoodBrush"));
    }

    void Enabled_Click(object sender, RoutedEventArgs e)
    {
        S.Enabled = EnabledSwitch.IsChecked == true;
        TweakEngine.Save();
        GameAudio.Refresh();
        UpdateState(); Build();
    }

    void Build()
    {
        var offset = Scroll.VerticalOffset;
        Root.Children.Clear();
        _level = null;
        Root.Children.Add(Flow());
        bool apo = S.Engine == "apo";
        if (apo) { if (!EqApo.Installed || EqApo.OnDevice(EqApo.Resolve(S.OutputId, capture: false).Id) == false) Root.Children.Add(ApoSetup()); }
        else if (GameAudio.Problem == "NoCable" || (!S.Enabled && GameAudio.Sources().Count == 0)) Root.Children.Add(Setup());
        Root.Children.Add(Presets());
        Root.Children.Add(Eq());
        Root.Children.Add(Sound());
        Dispatcher.BeginInvoke(() => Scroll.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }

    UIElement Flow()
    {
        var sp = new StackPanel();
        var modes = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var (key, label) in new[] { ("apo", "No cable (recommended)"), ("cable", "Virtual cable") })
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = Loc.T(label), GroupName = "hp-engine", IsChecked = (S.Engine ?? "apo") == key, Margin = new Thickness(0, 0, 6, 0) };
            rb.Checked += (_, _) => { if (S.Engine == key) return; S.Engine = key; TweakEngine.Save(); GameAudio.Refresh(); Build(); };
            modes.Children.Add(rb);
        }
        sp.Children.Add(modes);
        if (S.Engine == "apo")
        {
            sp.Children.Add(new TextBlock { Text = Loc.T("Your headset"), FontSize = 12, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 0, 0, 6) });
            var pick = new ComboBox { Height = 34, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 320 };
            pick.Items.Add(new ComboBoxItem { Content = Loc.T("Windows default"), Tag = "" });
            foreach (var d in GameAudio.Headphones()) pick.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
            pick.SelectedItem = pick.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == S.OutputId) ?? pick.Items[0];
            pick.SelectionChanged += (_, _) => { S.OutputId = (string)((ComboBoxItem)pick.SelectedItem).Tag; TweakEngine.Save(); GameAudio.Refresh(); Build(); };
            sp.Children.Add(pick);
            sp.Children.Add(Dim(Loc.T("Games play straight to your headset and Windows applies the EQ: no cable, no extra delay. Even out loud and quiet needs the virtual cable mode."), 12, "TextFaintBrush").Also(t => t.Margin = new Thickness(0, 10, 0, 0)));
            return Card(sp);
        }
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var src = new StackPanel();
        src.Children.Add(new TextBlock { Text = Loc.T("Game sound comes in through"), FontSize = 12, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 0, 0, 6) });
        var srcPick = new ComboBox { Height = 34 };
        srcPick.Items.Add(new ComboBoxItem { Content = Loc.T("Find the cable automatically"), Tag = "" });
        foreach (var d in GameAudio.Sources()) srcPick.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
        srcPick.SelectedItem = srcPick.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == S.SourceId) ?? srcPick.Items[0];
        srcPick.SelectionChanged += (_, _) => { S.SourceId = (string)((ComboBoxItem)srcPick.SelectedItem).Tag; TweakEngine.Save(); if (S.Enabled) GameAudio.Refresh(); };
        src.Children.Add(srcPick);
        g.Children.Add(src);
        var dst = new StackPanel();
        dst.Children.Add(new TextBlock { Text = Loc.T("and plays in"), FontSize = 12, Foreground = Res("TextDimBrush"), Margin = new Thickness(0, 0, 0, 6) });
        var dstPick = new ComboBox { Height = 34 };
        dstPick.Items.Add(new ComboBoxItem { Content = Loc.T("Windows default (not a cable)"), Tag = "" });
        foreach (var d in GameAudio.Headphones()) dstPick.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
        dstPick.SelectedItem = dstPick.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == S.OutputId) ?? dstPick.Items[0];
        dstPick.SelectionChanged += (_, _) => { S.OutputId = (string)((ComboBoxItem)dstPick.SelectedItem).Tag; TweakEngine.Save(); if (S.Enabled) GameAudio.Refresh(); };
        dst.Children.Add(dstPick);
        Grid.SetColumn(dst, 2);
        g.Children.Add(dst);
        sp.Children.Add(g);

        if (S.Enabled && GameAudio.Running)
        {
            var row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(Dim(Loc.T("Level"), 12).Also(t => t.VerticalAlignment = VerticalAlignment.Center));
            _level = new LevelMeter { Height = 12, Good = Res("GoodBrush"), Warn = Res("WarnBrush"), Bad = Res("BadBrush"), Track = Res("Surface2Brush") };
            Grid.SetColumn(_level, 1);
            row.Children.Add(_level);
            sp.Children.Add(row);
            var tip = Dim(Loc.F("In Windows sound settings (or the game's own audio settings), set the game's output to \"{0}\". Keep Discord on your headphones.", GameAudio.PlaybackNameFor(GameAudio.SourceName)), 12);
            tip.Margin = new Thickness(0, 12, 0, 8);
            sp.Children.Add(tip);
            var acts = new WrapPanel();
            acts.Children.Add(Btn("Choose an app's output", () => Open("ms-settings:apps-volume")));
            var hold = Btn("Hold to hear without Pulse", () => { });
            hold.PreviewMouseLeftButtonDown += (_, _) => GameAudio.Bypass = true;
            hold.PreviewMouseLeftButtonUp += (_, _) => GameAudio.Bypass = false;
            hold.MouseLeave += (_, _) => GameAudio.Bypass = false;
            acts.Children.Add(hold);
            sp.Children.Add(acts);
        }
        else if (S.Enabled && GameAudio.Problem is { } p && p != "NoCable")
            sp.Children.Add(new Border { Background = Res("BadSoftBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 14, 0, 0), Child = Dim(Loc.T(p), 12.5, "TextBrush") });
        return Card(sp);
    }

    UIElement ApoSetup()
    {
        var sp = new StackPanel();
        sp.Children.Add(Title("One-time setup: Equalizer APO"));
        sp.Children.Add(Dim(Loc.T("Equalizer APO is a free, open-source add-on that lets Windows change your headset's sound. Pulse sets it up for you after this."), 12.5));
        var steps = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        string[] lines = { "Download Equalizer APO and run the installer.", "When its Configurator opens, tick your headset under Playback devices.", "Restart your PC. Pulse does the rest." };
        for (int i = 0; i < lines.Length; i++) steps.Children.Add(Dim($"{i + 1}.  " + Loc.T(lines[i]), 13, "TextBrush").Also(t => t.Margin = new Thickness(0, 0, 0, 6)));
        sp.Children.Add(steps);
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (!EqApo.Installed) row.Children.Add(Btn("Get Equalizer APO (free)", () => Open(EqApo.Website), primary: true));
        else row.Children.Add(Btn("Open Equalizer APO's Configurator", EqApo.OpenConfigurator, primary: true));
        row.Children.Add(Btn("Check again", () => { GameAudio.Refresh(); Build(); }));
        sp.Children.Add(row);
        var b = Card(sp);
        b.BorderBrush = Res("WarnBrush");
        return b;
    }

    UIElement Setup()
    {
        var sp = new StackPanel();
        sp.Children.Add(Title("One-time setup: a second virtual cable"));
        sp.Children.Add(Dim(Loc.T("Games play into a virtual cable, Pulse tunes the sound and sends it to your headphones. Use a different cable from the Mic studio's: VB-Audio Hi-Fi Cable is free."), 12.5));
        bool micHasIt = MicStudio.S.Enabled && Pulse.Core.Audio.Cables.Devices().Any(d => d.Input && Pulse.Core.Audio.Cables.IsVirtual(d.Name));
        if (micHasIt)
            sp.Children.Add(Dim(Loc.T("Your VB-CABLE is already carrying your voice for the Mic studio, so game sound can't share it (your friends would hear your game). Add Hi-Fi Cable for the game, or turn the Mic studio off to use VB-CABLE here."), 12.5, "WarnBrush").Also(t => t.Margin = new Thickness(0, 8, 0, 0)));
        var steps = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (i, line) in new[] { "Download VB-Audio Hi-Fi Cable and run its installer as administrator.", "Come back here: Pulse finds it by itself.", "Set your game's sound output to \"Hi-Fi Cable Input\" (Windows sound settings → Volume mixer, or the game's audio settings)." }.Select((l, i) => (i, l)))
            steps.Children.Add(Dim($"{i + 1}.  " + Loc.T(line), 13, "TextBrush").Also(t => t.Margin = new Thickness(0, 0, 0, 6)));
        sp.Children.Add(steps);
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(Btn("Get Hi-Fi Cable (free)", () => Open("https://vb-audio.com/Cable/"), primary: true));
        row.Children.Add(Btn("Check again", GameAudio.Refresh));
        sp.Children.Add(row);
        var b = Card(sp);
        b.BorderBrush = Res("WarnBrush");
        return b;
    }

    UIElement Presets()
    {
        var sp = new StackPanel();
        sp.Children.Add(Title("Presets"));
        var wrap = new WrapPanel();
        foreach (var p in GameAudioSettings.Presets)
        {
            bool on = S.Preset == p.Key;
            var inner = new StackPanel();
            inner.Children.Add(new TextBlock { Text = Loc.T(p.Name), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            inner.Children.Add(new TextBlock { Text = Loc.T(p.Note), FontSize = 11.5, Foreground = Res("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            var tile = new Border
            {
                Width = 168, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Child = inner,
                Background = on ? Res("InfoSoftBrush") : Res("Surface2Brush"), BorderBrush = on ? Res("AccentBrush") : Res("BorderBrush"), BorderThickness = new Thickness(on ? 1.5 : 1),
            };
            var key = p.Key;
            tile.MouseLeftButtonUp += (_, _) => { S.ApplyPreset(key); Saved(); Build(); };
            wrap.Children.Add(tile);
        }
        sp.Children.Add(wrap);
        sp.Children.Add(Dim(Loc.T("Shooter presets turn down rumble and lift 2–5 kHz, where footsteps and reloads are. They're a starting point: adjust the EQ to your headphones."), 12, "TextFaintBrush"));
        return Card(sp);
    }

    UIElement Eq()
    {
        var sp = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var reset = Btn("Reset", () => { S.Eq = new double[10]; S.Preset = "custom"; Saved(); Build(); });
        DockPanel.SetDock(reset, Dock.Right);
        head.Children.Add(reset);
        head.Children.Add(Title("10-band equalizer", 0));
        sp.Children.Add(head);
        sp.Children.Add(Dim(Loc.T("Drag a point up or down. Double-click a point to set it back to 0."), 12.5));
        var graph = new EqGraph(() => S.Eq ??= new double[10], f => Curve(S.Eq, f), Res("AccentBrush"), Res("BorderBrush"), Res("TextDimBrush"), Res("TextBrush"), Res("Surface2Brush")) { Height = 240, Margin = new Thickness(0, 14, 0, 0) };
        graph.Moved += () => { S.Preset = "custom"; Saved(); };
        sp.Children.Add(graph);
        return Card(sp);
    }

    static double Curve(double[] eq, double f)
    {
        double sum = 0;
        for (int b = 0; b < Math.Min(10, eq?.Length ?? 0); b++)
        {
            if (Math.Abs(eq[b]) < 0.05) continue;
            var q = b switch { 0 => Biquad.LowShelf(48000, 40, eq[b]), 9 => Biquad.HighShelf(48000, 12000, eq[b]), _ => Biquad.Peak(48000, MicSettings.EqFreqs[b], 1.4, eq[b]) };
            sum += q.ResponseDb(48000, f);
        }
        return sum;
    }

    UIElement Sound()
    {
        var sp = new StackPanel();
        sp.Children.Add(Title("Sound", 4));
        sp.Children.Add(Slider("Even out loud and quiet", "Quiet sounds like footsteps come up, explosions come down", 0, 1, 0.05, () => S.EvenOut, v => S.EvenOut = v, v => Math.Round(v * 100) + "%"));
        sp.Children.Add(Slider("Volume", null, -12, 12, 0.5, () => S.VolumeDb, v => S.VolumeDb = v, v => (v > 0 ? "+" : "") + v.ToString("0.#", CultureInfo.CurrentCulture) + " dB"));
        var g = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new StackPanel();
        t.Children.Add(new TextBlock { Text = Loc.T("Virtual surround"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
        t.Children.Add(Dim(Loc.T(GameAudio.Running && GameAudio.SourceChannels >= 6
            ? "The game sends 7.1: each speaker is placed around your head. Great for story games; many competitive players keep it off because it changes where sounds seem to be."
            : "Places the game's two channels like speakers in front of you, which is easier on the ears over long sessions. For full surround, set the cable to 7.1 in Windows sound settings and turn on 7.1 in the game."), 11.5).Also(x => x.Margin = new Thickness(0, 2, 0, 0)));
        g.Children.Add(t);
        var cb = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = S.Spatial, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        cb.Click += (_, _) => { S.Spatial = cb.IsChecked == true; Saved(); };
        Grid.SetColumn(cb, 1);
        g.Children.Add(cb);
        sp.Children.Add(g);
        sp.Children.Add(Dim(Loc.T("Turn off Windows Sonic, Dolby Atmos or DTS for these headphones so the sound isn't processed twice."), 11.5, "TextFaintBrush").Also(x => x.Margin = new Thickness(0, 12, 0, 0)));
        return Card(sp);
    }

    FrameworkElement Slider(string label, string note, double min, double max, double step, Func<double> get, Action<double> set, Func<double, string> fmt)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
        var top = new Grid();
        var lab = new StackPanel { Orientation = Orientation.Horizontal };
        lab.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 13, Foreground = Res("TextBrush") });
        if (note != null) lab.Children.Add(new TextBlock { Text = "  " + Loc.T(note), FontSize = 11.5, Foreground = Res("TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        top.Children.Add(lab);
        var val = new TextBlock { Text = fmt(get()), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"), HorizontalAlignment = HorizontalAlignment.Right, Background = Res("SurfaceBrush"), Padding = new Thickness(8, 0, 0, 0) };
        top.Children.Add(val);
        sp.Children.Add(top);
        var sl = new System.Windows.Controls.Slider { Style = (Style)FindResource("Seek"), Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, Value = Math.Clamp(get(), min, max), Margin = new Thickness(0, 6, 0, 0) };
        sl.ValueChanged += (_, e) => { set(e.NewValue); val.Text = fmt(e.NewValue); Saved(); };
        sp.Children.Add(sl);
        return sp;
    }
}
