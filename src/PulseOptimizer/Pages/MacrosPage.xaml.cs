using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Input;
using Pulse.Ui;

namespace Pulse.Pages;

public partial class MacrosPage : UserControl, IPage, IReleasable
{
    static MacroSettings S => MacroService.S;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly List<double> _pad = new();          // when each click on the pad started (ms)
    double _best;
    bool _loading, _capturing;
    // Recording: (gap since the previous press, how long this one was held)
    List<double[]> _rec;
    double _recStart, _lastDown = -1, _downAt;

    public MacrosPage()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => Refresh();
        MacroService.Changed += OnMacroChanged;
        Unloaded += (_, _) => _tick.Stop();
    }

    void OnMacroChanged() => Dispatcher.InvokeAsync(ShowState);

    public void Release() { MacroService.Changed -= OnMacroChanged; _tick.Stop(); }

    public void OnShown(object parameter)
    {
        _loading = true;
        Fill(ModeChips, "mode", new[] { ("Steady", "steady"), ("Butterfly", "butterfly"), ("Jitter", "jitter"), ("My rhythm", "rhythm") });
        Fill(CpsChips, "cps", new[] { 8, 10, 12, 14, 16, 18, 20, 25 }.Select(c => ($"{c}", c.ToString(CultureInfo.InvariantCulture))).ToArray());
        Fill(VariationChips, "var", new[] { ("Exact", "0"), ("Natural", "0.08"), ("Loose", "0.15") });
        Fill(ButtonChips, "btn", new[] { ("Left click", "left"), ("Right click", "right") });
        Fill(ActivationChips, "act", new[] { ("Holding", "hold"), ("Pressing once", "toggle") });
        Mark(ModeChips, S.Mode);
        Mark(CpsChips, Math.Round(S.Cps).ToString(CultureInfo.InvariantCulture));
        Mark(VariationChips, S.Variation.ToString(CultureInfo.InvariantCulture));
        Mark(ButtonChips, S.Button);
        Mark(ActivationChips, S.Activation);
        ScaleSwitch.IsChecked = S.ScaleRhythm;
        OnlyGameSwitch.IsChecked = S.OnlyInGame;
        _loading = false;
        ShowSettings();
        ShowState();
        _tick.Start();
    }

    void Fill(WrapPanel panel, string group, (string Label, string Value)[] items)
    {
        panel.Children.Clear();
        foreach (var (label, value) in items)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = Loc.T(label), GroupName = "macro-" + group, Tag = value, Margin = new Thickness(0, 3, 6, 3) };
            rb.Checked += (s, _) => { if (!_loading) Chip(group, (string)((FrameworkElement)s).Tag); };
            panel.Children.Add(rb);
        }
    }

    static void Mark(WrapPanel panel, string value)
    {
        foreach (var rb in panel.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Tag == value;
    }

    void Chip(string group, string v)
    {
        double d = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
        switch (group)
        {
            case "mode":
                S.Mode = v;
                if (v == "butterfly" && S.Cps < 12) { S.Cps = 16; Mark(CpsChips, "16"); }
                break;
            case "cps": S.Cps = d; break;
            case "var": S.Variation = d; break;
            case "btn": S.Button = v; break;
            case "act": S.Activation = v; break;
        }
        TweakEngine.Save();
        ShowSettings();
        ShowState();
    }

    void Setting_Click(object sender, RoutedEventArgs e)
    {
        S.ScaleRhythm = ScaleSwitch.IsChecked == true;
        S.OnlyInGame = OnlyGameSwitch.IsChecked == true;
        TweakEngine.Save();
        ShowState();
    }

    void ShowSettings()
    {
        ModeText.Text = Loc.T(S.Mode switch
        {
            "butterfly" => "Two-finger clicking: a quick pair of clicks, then a gap, over and over. 14 to 20 per second is typical.",
            "jitter" => "Fast, short presses with uneven gaps, like tensed-arm jitter clicking.",
            "rhythm" => S.Rhythm.Count >= 4 ? "Replays the clicking you recorded on the test pad, gap for gap." : "Record your clicking on the test pad first (5 seconds), then this replays it gap for gap.",
            _ => "Evenly spaced clicks at the speed you pick.",
        });
        VariationRow.Visibility = S.Mode == "rhythm" ? Visibility.Collapsed : Visibility.Visible;
        ScaleRow.Visibility = S.Mode == "rhythm" ? Visibility.Visible : Visibility.Collapsed;
        TriggerBtn.Content = _capturing ? Loc.T("Press a key or a mouse side button…") : MacroService.KeyName(S.TriggerKey);
        PadRhythm.Text = S.Rhythm.Count >= 4 ? Loc.F("{0} CPS", ClickPattern.RhythmCps(S.Rhythm).ToString("0.0", CultureInfo.CurrentCulture)) : "–";
    }

    void ShowState()
    {
        bool armed = MacroService.Armed, active = MacroService.Active;
        StateDot.Fill = (Brush)FindResource(active ? "GoodBrush" : armed ? "AccentBrush" : "TextFaintBrush");
        StateTitle.Text = Loc.T(active ? "Clicking" : armed ? "Macro on" : "Macro off");
        string how = Loc.F("{0} {1} to click", Loc.T(S.Activation == "toggle" ? "Press" : "Hold"), MacroService.KeyName(S.TriggerKey));
        string pattern = S.Mode == "rhythm" && S.Rhythm.Count >= 4 && !S.ScaleRhythm
            ? Loc.T("your rhythm")
            : Loc.F("{0} CPS {1}", Math.Round(S.Cps), Loc.T(S.Mode switch { "butterfly" => "butterfly", "jitter" => "jitter", "rhythm" => "rhythm", _ => "steady" }));
        StateText.Text = armed
            ? how + " · " + pattern + " · " + Loc.T("Esc stops it") + (S.OnlyInGame ? " · " + Loc.T("only in your game") : "")
            : Loc.T("Turn it on, then use your trigger in any game or window.") + " " + how + ", " + pattern + ".";
        ArmBtn.Content = Loc.T(armed ? "Turn off" : "Turn on");
        BlockText.Visibility = MacroService.BlockedBy != null && !armed ? Visibility.Visible : Visibility.Collapsed;
        BlockText.Text = MacroService.BlockedBy != null ? Loc.F("{0} is running, so the macro stays off: anti-cheats like it can ban accounts for macro clicks.", MacroService.BlockedBy) : "";
    }

    void Arm_Click(object sender, RoutedEventArgs e)
    {
        if (MacroService.Armed) { MacroService.Disarm(); ShowState(); return; }
        if (S.Mode == "rhythm" && S.Rhythm.Count < 4) { MainWindow.Instance.Toast("Record your rhythm on the test pad first.", ToastKind.Info); return; }
        var why = MacroService.Arm();
        if (why != null) { BlockText.Text = why; BlockText.Visibility = Visibility.Visible; }
        ShowState();
    }

    // ───────── trigger key ─────────

    void Trigger_Click(object sender, RoutedEventArgs e)
    {
        _capturing = true;
        ShowSettings();
        TriggerBtn.Focus();
    }

    void Trigger_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { _capturing = false; ShowSettings(); return; } // Esc is the stop key, never the trigger
        SetTrigger(KeyInterop.VirtualKeyFromKey(key));
    }

    void Trigger_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_capturing) return;
        // Left and right are what the macro presses; the trigger has to be something else.
        int vk = e.ChangedButton switch { MouseButton.Middle => 0x04, MouseButton.XButton1 => 0x05, MouseButton.XButton2 => 0x06, _ => 0 };
        if (vk == 0) return;
        e.Handled = true;
        SetTrigger(vk);
    }

    void SetTrigger(int vk)
    {
        _capturing = false;
        S.TriggerKey = vk;
        TweakEngine.Save();
        ShowSettings();
        ShowState();
    }

    // ───────── test pad ─────────

    double Now => _clock.Elapsed.TotalMilliseconds;

    void Pad_MouseDown(object sender, MouseButtonEventArgs e)
    {
        double t = Now;
        _pad.Add(t);
        if (_pad.Count > 400) _pad.RemoveRange(0, _pad.Count - 400);
        _downAt = t;
        if (_rec != null && _lastDown >= 0) _rec.Add(new[] { t - _lastDown, 15.0 });
        _lastDown = t;
    }

    void Pad_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // How long the last press was held, for the recorded rhythm.
        if (_rec is { Count: > 0 }) _rec[^1][1] = Math.Clamp(Now - _downAt, 3, 200);
    }

    async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_rec != null) return;
        RecordBtn.IsEnabled = false;
        for (int i = 3; i > 0; i--) { RecordBtn.Content = Loc.F("Get ready… {0}", i); await Task.Delay(700); }
        _rec = new List<double[]>();
        _lastDown = -1;
        _recStart = Now;
        while (Now - _recStart < 5000) { RecordBtn.Content = Loc.F("Click the pad! {0:0.0} s", (5000 - (Now - _recStart)) / 1000); await Task.Delay(100); }
        var got = _rec;
        _rec = null;
        RecordBtn.Content = Loc.T("Record my rhythm (5 s)");
        RecordBtn.IsEnabled = true;
        // Gaps over half a second are pauses, not rhythm.
        got = got.Where(r => r[0] > 0 && r[0] < 500).ToList();
        if (got.Count < 4) { MainWindow.Instance.Toast("That was too few clicks to make a rhythm. Try again and keep clicking for the whole 5 seconds.", ToastKind.Info); return; }
        S.Rhythm = got;
        S.Mode = "rhythm";
        TweakEngine.Save();
        _loading = true; Mark(ModeChips, "rhythm"); _loading = false;
        ShowSettings();
        ShowState();
        MainWindow.Instance.Toast(Loc.F("Saved your rhythm: {0} clicks at {1} CPS.", got.Count + 1, ClickPattern.RhythmCps(got).ToString("0.0", CultureInfo.CurrentCulture)));
    }

    void Refresh()
    {
        double now = Now;
        int last = _pad.Count(t => now - t <= 1000);
        if (last > _best) _best = last;
        PadCps.Text = last.ToString(CultureInfo.CurrentCulture);
        PadBest.Text = _best > 0 ? Loc.F("{0} CPS", _best) : "–";
        var recent = _pad.Where(t => now - t <= 3000).ToList();
        if (recent.Count >= 6)
        {
            // Steadiness: how even the gaps are (100% = metronome). Butterfly is uneven on purpose (pairs).
            var gaps = recent.Zip(recent.Skip(1), (a, b) => b - a).ToList();
            double mean = gaps.Average(), sd = Math.Sqrt(gaps.Sum(g => (g - mean) * (g - mean)) / gaps.Count);
            PadSteady.Text = $"{Math.Max(0, 100 - sd / mean * 100):0}%";
        }
        LiveCps.Text = MacroService.Active ? Math.Round(MacroService.Macro.LiveCps).ToString(CultureInfo.CurrentCulture) : "0";
        DrawTimeline(recent, now);
    }

    /// <summary>A tick per click over the last 3 seconds: butterfly shows up as pairs.</summary>
    void DrawTimeline(List<double> clicks, double now)
    {
        Timeline.Children.Clear();
        double w = Timeline.ActualWidth, h = Timeline.ActualHeight;
        if (w < 10) return;
        var brush = (Brush)FindResource("AccentGradient");
        foreach (var t in clicks)
        {
            double x = w - (now - t) / 3000 * w;
            var tick = new Rectangle { Width = 3, Height = h - 8, RadiusX = 1.5, RadiusY = 1.5, Fill = brush, Opacity = 0.35 + 0.65 * (1 - (now - t) / 3000) };
            Canvas.SetLeft(tick, x - 1.5);
            Canvas.SetTop(tick, 4);
            Timeline.Children.Add(tick);
        }
    }
}
