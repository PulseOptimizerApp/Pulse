using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Input;
using Pulse.Core.Input.Lua;
using Pulse.Ui;

namespace Pulse.Pages;

public partial class MacrosPage : UserControl, IPage, IReleasable
{
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly DispatcherTimer _saveSoon = new() { Interval = TimeSpan.FromMilliseconds(700) };
    MacroDef _sel;
    bool _loading;

    // Key capture: the button waiting for a key, and what to do with it.
    Button _capBtn;
    Action<int> _capSet;
    bool _capMouse;
    Func<string> _capLabel;

    // Clicker test pad
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly List<double> _pad = new();
    double _best;
    List<double[]> _rec;
    double _recStart, _lastDown = -1, _downAt;
    TextBlock _padCps, _padBest, _padSteady, _padRhythm;
    Canvas _timeline;

    // Sequence recording
    MacroRecorder _recorder;

    // Script editor
    TextBox _scriptBox, _logBox;
    TextBlock _scriptStatus;
    MacroLog _log;
    readonly DispatcherTimer _scriptCheck = new() { Interval = TimeSpan.FromMilliseconds(600) };

    public MacrosPage()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => Refresh();
        _saveSoon.Tick += (_, _) => { _saveSoon.Stop(); MacroService.Save(); };
        _scriptCheck.Tick += (_, _) => { _scriptCheck.Stop(); ApplyScript(); };
        MacroService.Changed += OnMacroChanged;
        PreviewKeyDown += Capture_KeyDown;
        PreviewMouseDown += Capture_MouseDown;
        Unloaded += (_, _) => { _tick.Stop(); FlushSave(); };
    }

    void OnMacroChanged() => Dispatcher.InvokeAsync(() => { ShowState(); MarkRunning(); });

    public void Release()
    {
        MacroService.Changed -= OnMacroChanged;
        _tick.Stop();
        DetachLog();
        _recorder?.Dispose();
        _recorder = null;
        if (_rearm) { _rearm = false; MacroService.Arm(quiet: true); }
        FlushSave();
    }

    void FlushSave()
    {
        if (_scriptCheck.IsEnabled) { _scriptCheck.Stop(); ApplyScript(); }
        if (_saveSoon.IsEnabled) { _saveSoon.Stop(); MacroService.Save(); }
    }

    void SaveSoon() { _saveSoon.Stop(); _saveSoon.Start(); }

    static MacroOptions O => MacroService.Options;

    public void OnShown(object parameter)
    {
        _loading = true;
        NotifySwitch.IsChecked = O.Notifications;
        AutoSwitch.IsChecked = O.OnAtStart;
        TraySwitch.IsChecked = O.KeepRunningInTray;
        StartupSwitch.IsChecked = O.StartWithWindows;
        AntiCheatSwitch.IsChecked = O.AllowWithAntiCheat;
        ShooterMoveSwitch.IsChecked = O.AllowMoveInShooters;
        PanicHost.Content = KeyButton(() => O.PanicKey, vk => { O.PanicKey = vk; MacroService.Save(); ShowState(); }, mouse: false);
        _loading = false;
        FillList(_sel?.Id);
        ShowState();
        _tick.Start();
    }

    // ───────── on / off ─────────

    void ShowState()
    {
        bool armed = MacroService.Armed;
        int running = MacroService.Engine?.RunningCount ?? 0;
        StateDot.Fill = (Brush)FindResource(running > 0 ? "GoodBrush" : armed ? "AccentBrush" : "TextFaintBrush");
        StateTitle.Text = Loc.T(running > 0 ? "Macro running" : armed ? "Macros on" : "Macros off");
        MacroDef[] list;
        lock (MacroService.Gate) list = MacroService.List.ToArray();
        int on = list.Count(m => m.Enabled);
        string stop = Loc.F("{0} stops what's playing", Loc.T(Keys.Display(O.PanicKey)));
        StateText.Text = armed
            ? Loc.F("{0} of {1} macros ready.", on, list.Length) + " " + stop + "."
            : Loc.T("Turn macros on, then use their keys in any game or window.") + " " + stop + ".";
        ArmBtn.Content = Loc.T(armed ? "Turn off" : "Turn on");
        bool paused = armed && MacroService.BlockedBy != null;
        BlockText.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        if (paused)
        {
            StateTitle.Text = Loc.T("Macros paused");
            StateDot.Fill = (Brush)FindResource("WarnBrush");
            BlockText.Text = Loc.F("{0} is running, so macros are paused. They carry on by themselves when it closes. If your game allows macros, switch on \"Allow in games with anti-cheat\" below.", MacroService.BlockedBy);
        }
        var shooter = MacroService.Shooter;
        ShooterText.Visibility = armed && shooter != null ? Visibility.Visible : Visibility.Collapsed;
        if (shooter != null)
        {
            ShooterText.Foreground = (Brush)FindResource(O.AllowMoveInShooters ? "WarnBrush" : "InfoBrush");
            ShooterText.Text = O.AllowMoveInShooters
                ? Loc.F("{0} is open and mouse-moving macros are allowed. We strongly advise against it: it's unfair to other players and can get your account banned.", shooter)
                : Loc.F("{0} is open: mouse-moving macros are paused. Keys and clicks still work.", shooter);
        }
    }

    void Arm_Click(object sender, RoutedEventArgs e)
    {
        FlushSave();
        if (MacroService.Armed) { MacroService.Disarm(); ShowState(); return; }
        var why = MacroService.Arm();
        if (why != null) { BlockText.Text = why; BlockText.Visibility = Visibility.Visible; }
        ShowState();
    }

    void Option_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        O.Notifications = NotifySwitch.IsChecked == true;
        O.OnAtStart = AutoSwitch.IsChecked == true;
        O.KeepRunningInTray = TraySwitch.IsChecked == true;
        if (!O.KeepRunningInTray && O.StartWithWindows) { O.StartWithWindows = false; StartupSwitch.IsChecked = false; ClipsService.SetStartWithWindows(false); }
        MacroService.Save();
    }

    void Startup_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = StartupSwitch.IsChecked == true;
        O.StartWithWindows = on;
        if (!ClipsService.SetStartWithWindows(on))
        {
            O.StartWithWindows = false;
            StartupSwitch.IsChecked = false;
            MainWindow.Instance.Toast("Windows didn't allow adding Pulse to startup.", ToastKind.Warning);
            return;
        }
        if (on)
        {
            // Starting with Windows is for having macros ready: they come on, and stay on in the tray.
            O.OnAtStart = true;
            O.KeepRunningInTray = true;
            AutoSwitch.IsChecked = true;
            TraySwitch.IsChecked = true;
        }
        MacroService.Save();
        TweakEngine.AddLog("macro", on ? "Pulse starts with Windows for macros" : "Pulse no longer starts with Windows for macros");
    }

    void AntiCheat_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (AntiCheatSwitch.IsChecked == true)
        {
            var ok = MessageBox.Show(Window.GetWindow(this),
                Loc.T("Only do this for games whose rules allow macros (like League of Legends or Fortnite).\n\nGames with Riot Vanguard, Easy Anti-Cheat, BattlEye or FACEIT can ban accounts for macro input if their rules don't allow it, and Pulse can't tell which games do. You take that risk.\n\nMouse-moving macros still pause in competitive shooters unless you allow that separately.\n\nAllow macros while an anti-cheat is running?"),
                Loc.T("Allow in games with anti-cheat"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (ok != MessageBoxResult.Yes) { AntiCheatSwitch.IsChecked = false; return; }
        }
        O.AllowWithAntiCheat = AntiCheatSwitch.IsChecked == true;
        MacroService.OptionsChanged();
        MacroService.Save();
        TweakEngine.AddLog("macro", O.AllowWithAntiCheat ? "Macros allowed while an anti-cheat runs (user's choice)" : "Macros off while an anti-cheat runs");
        ShowState();
    }

    void ShooterMove_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (ShooterMoveSwitch.IsChecked == true)
        {
            var ok = MessageBox.Show(Window.GetWindow(this),
                Loc.T("We strongly advise against this, and we don't recommend it.\n\nIn competitive shooters, a macro that moves the mouse is recoil control or aim help. That's unfair to the people you play against, it's against those games' rules, and anti-cheats ban accounts for it, often permanently. Pulse can't protect your account if you turn this on.\n\nKey macros (like lean spam) already work without this.\n\nAllow mouse-moving macros in competitive shooters anyway?"),
                Loc.T("Allow mouse movement in competitive shooters (not recommended)"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (ok != MessageBoxResult.Yes) { ShooterMoveSwitch.IsChecked = false; return; }
        }
        O.AllowMoveInShooters = ShooterMoveSwitch.IsChecked == true;
        MacroService.OptionsChanged();
        MacroService.Save();
        TweakEngine.AddLog("macro", O.AllowMoveInShooters ? "Mouse-moving macros allowed in competitive shooters (user's choice, advised against)" : "Mouse-moving macros paused in competitive shooters");
        ShowState();
    }

    // ───────── the list ─────────

    void FillList(string selectId)
    {
        _loading = true;
        MacroPick.Items.Clear();
        MacroDef[] list;
        lock (MacroService.Gate) list = MacroService.List.ToArray();
        foreach (var m in list) MacroPick.Items.Add(new ListBoxItem { Content = ListRow(m), Tag = m });
        _loading = false;
        var pick = MacroPick.Items.OfType<ListBoxItem>().FirstOrDefault(i => ((MacroDef)i.Tag).Id == selectId) ?? MacroPick.Items.OfType<ListBoxItem>().FirstOrDefault();
        if (pick != null) MacroPick.SelectedItem = pick;
        else { _sel = null; BuildEditor(); }
    }

    UIElement ListRow(MacroDef m)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = m.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = m.Enabled ? 1 : 0.5 });
        sp.Children.Add(new TextBlock
        {
            Text = Loc.T(KindName(m.Kind)) + " · " + MacroPolicy.TriggerText(m, Loc.T),
            FontSize = 11.5, Foreground = (Brush)FindResource("TextDimBrush"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        g.Children.Add(sp);
        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Tag = m.Id };
        Grid.SetColumn(dot, 1);
        g.Children.Add(dot);
        PaintDot(dot, m);
        return g;
    }

    void PaintDot(Ellipse dot, MacroDef m)
    {
        bool running = MacroService.Engine?.IsRunning(m.Id) == true;
        dot.Fill = (Brush)FindResource(running ? "GoodBrush" : m.Enabled && MacroService.Armed ? "AccentBrush" : "BorderBrush");
    }

    void MarkRunning()
    {
        foreach (var item in MacroPick.Items.OfType<ListBoxItem>())
            if (item.Content is Grid g && g.Children.OfType<Ellipse>().FirstOrDefault() is { } dot) PaintDot(dot, (MacroDef)item.Tag);
    }

    void RefreshListRow()
    {
        if (_sel == null) return;
        var item = MacroPick.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag == _sel);
        if (item != null) item.Content = ListRow(_sel);
    }

    static string KindName(string kind) => kind switch { "clicker" => "Clicker", "script" => "Logitech script", _ => "Key sequence" };

    void Pick_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if ((MacroPick.SelectedItem as ListBoxItem)?.Tag is not MacroDef m || m == _sel) return;
        FlushSave();
        _recorder?.Dispose();
        _recorder = null;
        if (_rearm) { _rearm = false; MacroService.Arm(quiet: true); ShowState(); }
        _sel = m;
        BuildEditor();
    }

    void Add(MacroDef m)
    {
        lock (MacroService.Gate) MacroService.List.Add(m);
        MacroService.Save();
        FillList(m.Id);
        ShowState();
    }

    void NewClicker_Click(object sender, RoutedEventArgs e) =>
        Add(new MacroDef { Name = Loc.T("Clicker"), Kind = "clicker", Mode = "hold", TriggerKey = Keys.VK_XBUTTON1, Clicker = new MacroSettings() });

    void NewSequence_Click(object sender, RoutedEventArgs e) =>
        Add(new MacroDef { Name = Loc.T("Key sequence"), Kind = "sequence", Mode = "once" });

    void NewScript_Click(object sender, RoutedEventArgs e) =>
        Add(new MacroDef { Name = Loc.T("Logitech script"), Kind = "script", Script = ExampleClicker });

    void ImportLua_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("Pick a Logitech script"), Filter = Loc.T("Lua scripts") + " (*.lua;*.txt)|*.lua;*.txt|" + Loc.T("All files") + " (*.*)|*.*", CheckFileExists = true };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        string text;
        try { text = File.ReadAllText(dlg.FileName); }
        catch (Exception ex) { MainWindow.Instance.Toast(Loc.F("Couldn't open that file: {0}", ex.Message), ToastKind.Warning); return; }
        if (text.Length > 500_000) { MainWindow.Instance.Toast("That file is too big to be a script.", ToastKind.Warning); return; }
        if (_sel?.Kind == "script" && sender is Button { Tag: "here" })
        {
            _sel.Script = text;
            MacroService.Save();
            BuildEditor();
            return;
        }
        Add(new MacroDef { Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), Kind = "script", Script = text });
    }

    // ───────── editor ─────────

    void BuildEditor()
    {
        DetachLog();
        Editor.Children.Clear();
        _padCps = null;
        _timeline = null;
        _scriptBox = null;
        if (_sel == null)
        {
            Editor.Children.Add(new TextBlock { Style = (Style)FindResource("Body"), Text = Loc.T("Make a macro on the left to get started."), Foreground = (Brush)FindResource("TextDimBrush") });
            return;
        }
        var m = _sel;

        // Name, on/off, duplicate, delete
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBox { Style = (Style)FindResource("SearchBox"), Text = m.Name, Tag = Loc.T("Name"), FontSize = 15, FontWeight = FontWeights.SemiBold, MaxLength = 60 };
        name.TextChanged += (_, _) => { m.Name = string.IsNullOrWhiteSpace(name.Text) ? Loc.T("Macro") : name.Text.Trim(); RefreshListRow(); SaveSoon(); };
        head.Children.Add(name);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(new TextBlock { Text = Loc.T("On"), Foreground = (Brush)FindResource("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        tools.Children.Add(Switch(m.Enabled, v => { m.Enabled = v; RefreshListRow(); ShowState(); }));
        tools.Children.Add(SmallButton("Duplicate", (_, _) => Duplicate(m), 12));
        tools.Children.Add(SmallButton("Delete", (_, _) => Delete(m), 6));
        Grid.SetColumn(tools, 1);
        head.Children.Add(tools);
        Editor.Children.Add(head);
        if (!MacroService.Armed)
        {
            // The most common "it doesn't work": the macro is set up but macros as a whole are off.
            var off = new Border { CornerRadius = new CornerRadius(10), Background = (Brush)FindResource("WarnSoftBrush"), Padding = new Thickness(14, 8, 8, 8), Margin = new Thickness(0, 12, 0, 0) };
            var og = new Grid();
            og.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            og.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            og.Children.Add(new TextBlock { Text = Loc.T("Macros are off, so this won't run yet."), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
            var on = new Button { Style = (Style)FindResource("PrimaryButton"), Content = Loc.T("Turn on"), Padding = new Thickness(16, 6, 16, 6) };
            on.Click += (s2, e2) => { Arm_Click(s2, e2); BuildEditor(); };
            Grid.SetColumn(on, 1);
            og.Children.Add(on);
            off.Child = og;
            Editor.Children.Add(off);
        }
        if (m.Kind == "script") Editor.Children.Add(Note(KindBlurb(m.Kind), top: 8));

        if (m.Kind == "script")
        {
            Editor.Children.Add(StepHeader(1, "Script", "Paste or import a Logitech G Hub script."));
            BuildScript(m);
            return;
        }

        // 1. What it does (like Synapse / G Hub: the actions come first)
        if (m.Kind == "clicker")
        {
            Editor.Children.Add(StepHeader(1, "How it clicks", null));
            BuildClicker(m);
        }
        else
        {
            Editor.Children.Add(StepHeader(1, "What it does", null));
            BuildSequence(m);
        }

        // 2. The key that starts it
        Editor.Children.Add(StepHeader(2, "Start it with", "A key or mouse button. Side buttons work best."));
        var trig = new WrapPanel();
        var kb = KeyButton(() => m.TriggerKey, vk => { m.TriggerKey = vk; RefreshListRow(); BuildEditor(); }, mouse: true);
        kb.MinWidth = 180;
        kb.FontSize = 14;
        trig.Children.Add(kb);
        trig.Children.Add(Dim("+", 6));
        trig.Children.Add(ToggleChip("Ctrl", m.Ctrl, v => { m.Ctrl = v; RefreshListRow(); }));
        trig.Children.Add(ToggleChip("Shift", m.Shift, v => { m.Shift = v; RefreshListRow(); }));
        trig.Children.Add(ToggleChip("Alt", m.Alt, v => { m.Alt = v; RefreshListRow(); }));
        Editor.Children.Add(trig);
        if (m.TriggerKey == 0)
            Editor.Children.Add(Note("Click the box above, then press the key or mouse button you want.", top: 4, brush: "WarnBrush"));
        else if (m.TriggerKey == Keys.VK_LBUTTON)
            Editor.Children.Add(Note("Left click as the trigger means the macro starts every time you click. That's usually not what you want: pick a side button instead.", top: 4, brush: "WarnBrush"));

        // 3. How it plays (G Hub's macro types)
        Editor.Children.Add(StepHeader(3, "How it plays", null));
        if (m.Kind == "clicker" && m.Mode == "once") m.Mode = "hold";
        var cards = m.Kind == "clicker"
            ? new[]
            {
                ("hold", "", "Repeat while holding", "Clicks while you hold the key."),
                ("toggle", "", "Toggle", "Press once to start clicking, again to stop."),
            }
            : new[]
            {
                ("once", "", "No repeat", "Plays once each time you press the key."),
                ("hold", "", "Repeat while holding", "Loops while you hold the key, stops when you let go."),
                ("toggle", "", "Toggle", "Press to start looping, press again to stop."),
            };
        Editor.Children.Add(PlayCards(cards, m.Mode, v => { m.Mode = v; RefreshListRow(); BuildEditor(); }));
        if (m.Kind == "sequence")
        {
            var rep = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            if (m.Mode == "once")
            {
                rep.Children.Add(Dim(Loc.T("Play it"), 0));
                rep.Children.Add(NumBox(m.Repeat, v => { m.Repeat = (int)Math.Clamp(v, 1, 10000); }, 52));
                rep.Children.Add(Dim(Loc.T("time(s) each press"), 6));
            }
            if (m.Mode != "once" || m.Repeat > 1)
            {
                rep.Children.Add(Dim(Loc.T(m.Mode == "once" ? "· wait" : "Wait"), m.Mode == "once" ? 10 : 0));
                rep.Children.Add(NumBox(m.RepeatGap, v => m.RepeatGap = Math.Clamp(v, 0, 3_600_000), 64));
                rep.Children.Add(Dim(Loc.T("ms between loops"), 6));
            }
            Editor.Children.Add(rep);
        }

        // More options, folded away like the others do
        Editor.Children.Add(MoreOptions(m));

        if (m.Kind == "clicker") BuildTestPad(m);
    }

    UIElement StepHeader(int n, string title, string sub)
    {
        var g = new Grid { Margin = new Thickness(0, 24, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var badge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = (Brush)FindResource("AccentGradient"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0) };
        badge.Child = new TextBlock { Text = n.ToString(CultureInfo.CurrentCulture), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(badge);
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Style = (Style)FindResource("CardTitle"), Text = Loc.T(title), FontSize = 15 });
        if (sub != null) sp.Children.Add(new TextBlock { Text = Loc.T(sub), FontSize = 12, Foreground = (Brush)FindResource("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(sp, 1);
        g.Children.Add(sp);
        return g;
    }

    UIElement PlayCards((string Value, string Glyph, string Title, string Desc)[] items, string current, Action<string> set)
    {
        var panel = new WrapPanel();
        foreach (var (value, glyph, title, desc) in items)
        {
            bool on = value == current;
            var card = new Border
            {
                Width = 200, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand,
                Background = (Brush)FindResource(on ? "Surface2Brush" : "SurfaceBrush"), BorderThickness = new Thickness(on ? 2 : 1),
                BorderBrush = (Brush)FindResource(on ? "AccentGradient" : "BorderBrush"),
            };
            var sp = new StackPanel();
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(new TextBlock { Style = (Style)FindResource("IconText"), Text = glyph, FontSize = 15, Foreground = (Brush)FindResource(on ? "AccentBrush" : "TextDimBrush"), Margin = new Thickness(0, 0, 8, 0) });
            top.Children.Add(new TextBlock { Text = Loc.T(title), FontWeight = FontWeights.SemiBold, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(top);
            sp.Children.Add(new TextBlock { Text = Loc.T(desc), FontSize = 12, Foreground = (Brush)FindResource("TextDimBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            card.Child = sp;
            card.MouseLeftButtonUp += (_, _) => { if (!on) { set(value); MacroService.Save(); } };
            panel.Children.Add(card);
        }
        return panel;
    }

    static bool _moreOpen;

    UIElement MoreOptions(MacroDef m)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        var toggle = new Button { Style = (Style)FindResource("LinkButton"), Content = Loc.T(_moreOpen ? "▾  Fewer options" : "▸  More options (where it works, timing)"), HorizontalAlignment = HorizontalAlignment.Left };
        toggle.Click += (_, _) => { _moreOpen = !_moreOpen; BuildEditor(); };
        sp.Children.Add(toggle);
        if (!_moreOpen) return sp;
        var box = new Border { CornerRadius = new CornerRadius(12), BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 8, 0, 0) };
        var inner = new StackPanel();
        inner.Children.Add(Row("Works in", WhereEditor(m)));
        if (m.Kind == "sequence")
        {
            inner.Children.Add(Row("Timing", Chips(new[] { ("Exact", "0"), ("Light variation", "0.05"), ("Human", "0.1"), ("Loose", "0.2") },
                m.Humanize.ToString(CultureInfo.InvariantCulture), v => m.Humanize = double.Parse(v, CultureInfo.InvariantCulture))));
            inner.Children.Add(Indent(Note("Varies every delay a little each time it plays, so it doesn't look robotic.", top: 0)));
        }
        box.Child = inner;
        sp.Children.Add(box);
        return sp;
    }

    static string KindBlurb(string kind) => Loc.T(kind switch
    {
        "clicker" => "Clicks for you on a precise rhythm: steady, butterfly, jitter, or your own recorded clicking.",
        "script" => "Runs a Logitech G Hub Lua script on any mouse: paste one in or import a .lua file. Mouse buttons 1–5 are your left, right, middle and two side buttons.",
        _ => "Plays keys, clicks, waits, typed text and mouse movement in order, from any key or mouse button.",
    });

    UIElement WhereEditor(MacroDef m)
    {
        var sp = new StackPanel();
        sp.Children.Add(Chips(new[] { ("Anywhere", "anywhere"), ("Only my game", "game"), ("Only one program", "app") }, m.Where, v => { m.Where = v; BuildEditor(); }));
        if (m.Where == "app")
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var box = new TextBox { Style = (Style)FindResource("SearchBox"), Text = m.App, Tag = "RobloxPlayerBeta", Width = 220 };
            box.TextChanged += (_, _) => { m.App = MacroService.CleanApp(box.Text); SaveSoon(); };
            row.Children.Add(box);
            var pick = SmallButton("Pick the window", null, 8);
            pick.Click += async (_, _) =>
            {
                pick.IsEnabled = false;
                for (int i = 3; i > 0; i--) { pick.Content = Loc.F("Click the program… {0}", i); await Task.Delay(1000); }
                var name = MacroService.ForegroundApp();
                pick.Content = Loc.T("Pick the window");
                pick.IsEnabled = true;
                if (string.IsNullOrEmpty(name) || name.Equals("PulseOptimizer", StringComparison.OrdinalIgnoreCase)) { MainWindow.Instance.Toast("Click the program's window within 3 seconds, then try again.", ToastKind.Info); return; }
                box.Text = name;
            };
            row.Children.Add(pick);
            sp.Children.Add(row);
            sp.Children.Add(Note("The program's name, like RobloxPlayerBeta. Use \"Pick the window\" and click the program within 3 seconds.", top: 4));
        }
        return sp;
    }

    void Duplicate(MacroDef m)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(m);
        var copy = System.Text.Json.JsonSerializer.Deserialize<MacroDef>(json);
        copy.Id = Guid.NewGuid().ToString("N")[..10];
        copy.Name = Loc.F("{0} (copy)", m.Name);
        Add(copy);
    }

    void Delete(MacroDef m)
    {
        var ok = MessageBox.Show(Window.GetWindow(this), Loc.F("Delete \"{0}\"?", m.Name), Loc.T("Delete macro"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (ok != MessageBoxResult.Yes) return;
        lock (MacroService.Gate) MacroService.List.Remove(m);
        _sel = null;
        MacroService.Save();
        FillList(null);
        ShowState();
    }

    // ───────── clicker ─────────

    void BuildClicker(MacroDef m)
    {
        var s = m.Clicker ??= new MacroSettings();
        var modeNote = Note(ClickerBlurb(s), top: 0);
        Editor.Children.Add(Row("Pattern", Chips(new[] { ("Steady", "steady"), ("Butterfly", "butterfly"), ("Jitter", "jitter"), ("My rhythm", "rhythm") }, s.Mode, v =>
        {
            s.Mode = v;
            if (v == "butterfly" && s.Cps < 12) s.Cps = 16;
            BuildEditor();
        })));
        Editor.Children.Add(Indent(modeNote));
        if (s.Mode != "rhythm" || s.ScaleRhythm)
        {
            var speed = new WrapPanel();
            speed.Children.Add(Chips(new[] { 8, 10, 12, 14, 16, 18, 20, 25 }.Select(c => (c.ToString(CultureInfo.CurrentCulture), c.ToString(CultureInfo.InvariantCulture))).ToArray(),
                Math.Round(s.Cps).ToString(CultureInfo.InvariantCulture), v => { s.Cps = double.Parse(v, CultureInfo.InvariantCulture); BuildEditor(); }));
            speed.Children.Add(NumBox(s.Cps, v => s.Cps = Math.Clamp(v, 1, 50), 56));
            speed.Children.Add(Dim(Loc.T("per second"), 8));
            Editor.Children.Add(Row("Speed", speed));
        }
        if (s.Mode != "rhythm")
            Editor.Children.Add(Row("Timing", Chips(new[] { ("Exact", "0"), ("Natural", "0.08"), ("Loose", "0.15") }, s.Variation.ToString(CultureInfo.InvariantCulture),
                v => s.Variation = double.Parse(v, CultureInfo.InvariantCulture))));
        else
            Editor.Children.Add(SwitchRow("Play my rhythm at the speed above (instead of its own)", s.ScaleRhythm, v => { s.ScaleRhythm = v; BuildEditor(); }));
        Editor.Children.Add(Row("Button", Chips(new[] { ("Left click", "left"), ("Right click", "right") }, s.Button, v => s.Button = v)));
    }

    void BuildTestPad(MacroDef m)
    {
        var s = m.Clicker ??= new MacroSettings();
        Editor.Children.Add(Section("Test pad"));
        var top = new Grid();
        top.Children.Add(Note("Click here yourself, or point at it and use the macro, to see the speed and rhythm. Record your own clicking to replay it exactly.", top: 0));
        var rec = SmallButton("Record my rhythm (5 s)", null, 0);
        rec.HorizontalAlignment = HorizontalAlignment.Right;
        rec.VerticalAlignment = VerticalAlignment.Top;
        rec.Click += (_, _) => RecordRhythm(m, rec);
        top.Children.Add(rec);
        Editor.Children.Add(top);
        var pad = new Border { Height = 170, Margin = new Thickness(0, 12, 0, 0), CornerRadius = new CornerRadius(14), Background = (Brush)FindResource("Surface2Brush"), BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
        var inner = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
        _padCps = new TextBlock { FontSize = 44, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Text = "0" };
        inner.Children.Add(_padCps);
        inner.Children.Add(new TextBlock { FontSize = 13, Foreground = (Brush)FindResource("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Center, Text = Loc.T("clicks per second") });
        pad.Child = inner;
        pad.MouseDown += Pad_MouseDown;
        pad.MouseUp += Pad_MouseUp;
        Editor.Children.Add(pad);
        Editor.Children.Add(new TextBlock { Text = Loc.T("Last 3 seconds"), FontSize = 11, Foreground = (Brush)FindResource("TextFaintBrush"), Margin = new Thickness(0, 12, 0, 4) });
        _timeline = new Canvas { Height = 40, ClipToBounds = true, Background = Brushes.Transparent };
        Editor.Children.Add(_timeline);
        var stats = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        stats.Children.Add(Stat("Best", out _padBest));
        stats.Children.Add(Stat("Steadiness", out _padSteady));
        stats.Children.Add(Stat("Rhythm saved", out _padRhythm));
        Editor.Children.Add(stats);
        _padRhythm.Text = s.Rhythm.Count >= 4 ? Loc.F("{0} CPS", ClickPattern.RhythmCps(s.Rhythm).ToString("0.0", CultureInfo.CurrentCulture)) : "–";
    }

    static string ClickerBlurb(MacroSettings s) => Loc.T(s.Mode switch
    {
        "butterfly" => "Two-finger clicking: a quick pair of clicks, then a gap, over and over. 14 to 20 per second is typical.",
        "jitter" => "Fast, short presses with uneven gaps, like tensed-arm jitter clicking.",
        "rhythm" => s.Rhythm.Count >= 4 ? "Replays the clicking you recorded on the test pad, gap for gap." : "Record your clicking on the test pad first (5 seconds), then this replays it gap for gap.",
        _ => "Evenly spaced clicks at the speed you pick.",
    });

    UIElement Stat(string label, out TextBlock value)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 32, 0) };
        sp.Children.Add(new TextBlock { Text = Loc.T(label), FontSize = 12, Foreground = (Brush)FindResource("TextDimBrush") });
        value = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, Text = "–" };
        sp.Children.Add(value);
        return sp;
    }

    double Now => _clock.Elapsed.TotalMilliseconds;

    void Pad_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_capSet != null) return;
        double t = Now;
        _pad.Add(t);
        if (_pad.Count > 400) _pad.RemoveRange(0, _pad.Count - 400);
        _downAt = t;
        if (_rec != null && _lastDown >= 0) _rec.Add(new[] { t - _lastDown, 15.0 });
        _lastDown = t;
    }

    void Pad_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_rec is { Count: > 0 }) _rec[^1][1] = Math.Clamp(Now - _downAt, 3, 200);
    }

    async void RecordRhythm(MacroDef m, Button btn)
    {
        if (_rec != null) return;
        btn.IsEnabled = false;
        for (int i = 3; i > 0; i--) { btn.Content = Loc.F("Get ready… {0}", i); await Task.Delay(700); }
        _rec = new List<double[]>();
        _lastDown = -1;
        _recStart = Now;
        while (Now - _recStart < 5000) { btn.Content = Loc.F("Click the pad! {0:0.0} s", (5000 - (Now - _recStart)) / 1000); await Task.Delay(100); }
        var got = _rec;
        _rec = null;
        btn.Content = Loc.T("Record my rhythm (5 s)");
        btn.IsEnabled = true;
        got = got.Where(r => r[0] > 0 && r[0] < 500).ToList();
        if (got.Count < 4) { MainWindow.Instance.Toast("That was too few clicks to make a rhythm. Try again and keep clicking for the whole 5 seconds.", ToastKind.Info); return; }
        m.Clicker.Rhythm = got;
        m.Clicker.Mode = "rhythm";
        MacroService.Save();
        if (_sel == m) BuildEditor();
        MainWindow.Instance.Toast(Loc.F("Saved your rhythm: {0} clicks at {1} CPS.", got.Count + 1, ClickPattern.RhythmCps(got).ToString("0.0", CultureInfo.CurrentCulture)));
    }

    // ───────── sequence ─────────

    static readonly (string Do, string Glyph, string Title, string Desc)[] ActionKinds =
    {
        ("tap", "", "Key press", "Press and let go of a key"),
        ("down", "", "Key down", "Hold a key until a Key up"),
        ("up", "", "Key up", "Let go of a held key"),
        ("-", null, null, null),
        ("click", "", "Mouse click", "Click a mouse button"),
        ("press", "", "Mouse button down", "Hold a button until a Mouse button up"),
        ("release", "", "Mouse button up", "Let go of a held button"),
        ("-", null, null, null),
        ("wait", "", "Delay", "Wait before the next action"),
        ("text", "", "Type text", "Types a word or message"),
        ("-", null, null, null),
        ("move", "", "Move mouse by", "Moves the pointer a set distance"),
        ("moveto", "", "Move mouse to", "Moves the pointer to a spot on screen"),
        ("scroll", "", "Scroll", "Scrolls the wheel up or down"),
    };

    static MacroStep NewStep(string kind) => kind switch
    {
        "tap" => new MacroStep { Do = "tap", Key = 0x45 },
        "down" => new MacroStep { Do = "down", Key = 0x57 },
        "up" => new MacroStep { Do = "up", Key = 0x57 },
        "click" => new MacroStep { Do = "click" },
        "press" => new MacroStep { Do = "press" },
        "release" => new MacroStep { Do = "release" },
        "wait" => new MacroStep { Do = "wait", Ms = 50 },
        "text" => new MacroStep { Do = "text", Text = "gg" },
        "move" => new MacroStep { Do = "move", X = 100 },
        "moveto" => new MacroStep { Do = "moveto", X = 32768, Y = 32768 },
        _ => new MacroStep { Do = "scroll", Y = 1 },
    };

    /// <summary>The Insert menu (like Synapse): adds an action at <paramref name="at"/> (or the end), with a delay before it.</summary>
    void ShowInsertMenu(MacroDef m, FrameworkElement anchor, int at)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var (kind, glyph, title, desc) in ActionKinds)
        {
            if (kind == "-") { menu.Items.Add(new Separator()); continue; }
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Style = (Style)FindResource("IconText"), Text = glyph, FontSize = 13, Width = 24, Foreground = (Brush)FindResource("AccentBrush") });
            var t = new StackPanel();
            t.Children.Add(new TextBlock { Text = Loc.T(title), FontWeight = FontWeights.SemiBold });
            t.Children.Add(new TextBlock { Text = Loc.T(desc), FontSize = 11.5, Foreground = (Brush)FindResource("TextDimBrush") });
            header.Children.Add(t);
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                int pos = at < 0 || at > m.Steps.Count ? m.Steps.Count : at;
                var step = NewStep(kind);
                // Like the others: a short delay goes between actions automatically (except next to another delay).
                bool prevIsWait = pos > 0 && m.Steps[pos - 1].Do == "wait";
                if (kind != "wait" && pos > 0 && !prevIsWait) { m.Steps.Insert(pos, new MacroStep { Do = "wait", Ms = O.FixedDelay > 0 ? O.FixedDelay : 50 }); pos++; }
                m.Steps.Insert(pos, step);
                MacroService.Save();
                BuildEditor();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    void BuildSequence(MacroDef m)
    {
        if (_recorder != null)
        {
            // While recording: one big Stop bar. Clicks on Pulse's window are never recorded, so it can't add junk.
            var stop = new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 14, 18, 14), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 10),
                Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x52)),
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Background = Brushes.White, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            _recCount = new TextBlock { Text = Loc.T("Stop recording"), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(_recCount);
            stop.Child = sp;
            stop.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; ToggleRecord(m); };
            Editor.Children.Add(stop);
            Editor.Children.Add(Note(Loc.F("Recording. Go to your game or app and press what you want. Click this bar or press {0} to finish. Clicks on Pulse's window aren't recorded.", Loc.T(Keys.Display(O.PanicKey))), top: 0, brush: "AccentBrush"));
        }
        else
        {
            // Toolbar: Record and Insert, like Synapse and iCUE
            var bar = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var rec = new Button { Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(16, 8, 18, 8) };
            var rsp = new StackPanel { Orientation = Orientation.Horizontal };
            rsp.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x5E)), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            rsp.Children.Add(new TextBlock { Text = Loc.T(m.Steps.Count > 0 ? "Record more" : "Record"), VerticalAlignment = VerticalAlignment.Center });
            rec.Content = rsp;
            rec.Click += (_, _) => ToggleRecord(m);
            bar.Children.Add(rec);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            var add = SmallButton("+  Insert action", null, 0);
            add.Click += (_, _) => ShowInsertMenu(m, add, -1);
            right.Children.Add(add);
            if (m.Steps.Count > 0)
                right.Children.Add(SmallButton("Clear", (_, _) =>
                {
                    if (MessageBox.Show(Window.GetWindow(this), Loc.T("Remove every action of this macro?"), Loc.T("Clear"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                    m.Steps.Clear(); MacroService.Save(); BuildEditor();
                }, 6));
            Grid.SetColumn(right, 2);
            bar.Children.Add(right);
            Editor.Children.Add(bar);

            // Recording settings: one quiet line, opens in place (pick your own delay there)
            string delays = O.RecordDelays switch { "fixed" => Loc.F("fixed {0} ms delays", O.FixedDelay), "none" => Loc.T("no delays"), _ => Loc.T("delays as you press") };
            var summary = new Button
            {
                Style = (Style)FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8), FontSize = 12,
                Content = (_recOptsOpen ? "▾  " : "▸  ") + Loc.F("Recording: {0} · {1}", delays, Loc.T(O.RecordMouse ? "keys and mouse" : "keys only")),
            };
            summary.Click += (_, _) => { _recOptsOpen = !_recOptsOpen; BuildEditor(); };
            Editor.Children.Add(summary);
            if (_recOptsOpen)
            {
                var box = new Border { CornerRadius = new CornerRadius(10), BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 0, 10) };
                var inner = new StackPanel();
                var d = new WrapPanel();
                d.Children.Add(Dim(Loc.T("Delays"), 0));
                d.Children.Add(Chips(new[] { ("As I press them", "recorded"), ("Fixed", "fixed"), ("None", "none") }, O.RecordDelays, v => { O.RecordDelays = v; MacroService.Save(); BuildEditor(); }));
                if (O.RecordDelays == "fixed")
                {
                    d.Children.Add(NumBox(O.FixedDelay, v => O.FixedDelay = Math.Clamp(v, 1, 60_000), 60));
                    d.Children.Add(Dim("ms", 4));
                }
                inner.Children.Add(d);
                inner.Children.Add(SwitchRow("Also record mouse buttons and scrolling", O.RecordMouse, v => { O.RecordMouse = v; BuildEditor(); }));
                box.Child = inner;
                Editor.Children.Add(box);
            }
        }

        if (m.Steps.Count == 0)
        {
            var empty = new Border { CornerRadius = new CornerRadius(12), BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(20), Margin = new Thickness(0, 4, 0, 0), Background = (Brush)FindResource("SurfaceBrush") };
            var es = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            es.Children.Add(new TextBlock { Style = (Style)FindResource("IconText"), Text = "", FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)FindResource("TextFaintBrush") });
            es.Children.Add(new TextBlock { Text = Loc.T("No actions yet"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });
            es.Children.Add(new TextBlock { Text = Loc.T("Click Record and press your keys, or use Insert action to build it step by step."), FontSize = 12, Foreground = (Brush)FindResource("TextDimBrush"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            empty.Child = es;
            Editor.Children.Add(empty);
        }
        else
        {
            var list = new StackPanel();
            int n = 0;
            for (int i = 0; i < m.Steps.Count; i++)
                list.Children.Add(m.Steps[i].Do == "wait" ? DelayRow(m, i) : ActionRow(m, i, ++n));
            Editor.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12), Background = (Brush)FindResource("SurfaceBrush"), BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1), Padding = new Thickness(4), Child = list,
            });
            double total = m.Steps.Sum(s => s.Do == "wait" ? s.Ms + s.RandomMs / 2 : s.Do is "tap" ? (s.Ms > 0 ? s.Ms : 25) : s.Do == "click" ? (s.Ms > 0 ? s.Ms : 20) : 0);
            Editor.Children.Add(Note(Loc.F("{0} actions · takes about {1} ms", n, Math.Round(total)), top: 4));
        }
        if (m.Steps.Any(s => s.Moves))
            Editor.Children.Add(Note("This macro moves the mouse. It pauses in competitive shooters unless you allow that in Settings (we strongly advise against it). In other games, only use it where the rules allow it, like auto-farming in games that permit it.", top: 8, brush: "WarnBrush"));
    }

    TextBlock _recCount;
    static bool _recOptsOpen;
    bool _rearm;

    void ToggleRecord(MacroDef m)
    {
        if (_recorder == null)
        {
            // Macros pause while recording, so they don't record each other, and come back on afterwards.
            _rearm = MacroService.Armed;
            if (_rearm) MacroService.Disarm();
            _recorder = new MacroRecorder(MacroService.Engine.Phys, O.PanicKey);
            _recorder.EventCount += n => Dispatcher.BeginInvoke(() => { if (_recCount != null && _recorder != null) _recCount.Text = Loc.F("Stop recording ({0})", n); });
            _recorder.StopRequested += () => Dispatcher.BeginInvoke(() => { if (_recorder != null && _sel == m) ToggleRecord(m); });
            BuildEditor();
            ShowState();
            return;
        }
        var r = _recorder;
        _recorder = null;
        _recCount = null;
        var steps = r.Finish(O.RecordDelays, O.FixedDelay, O.RecordMouse);
        if (_rearm) { _rearm = false; MacroService.Arm(quiet: true); ShowState(); }
        if (steps.Count == 0) { MainWindow.Instance.Toast("Nothing was recorded. Press some keys or buttons, then Stop.", ToastKind.Info); BuildEditor(); return; }
        if (m.Steps.Count > 0 && steps[0].Do != "wait" && m.Steps[^1].Do != "wait" && O.RecordDelays != "none")
            m.Steps.Add(new MacroStep { Do = "wait", Ms = O.FixedDelay > 0 ? O.FixedDelay : 50 });
        m.Steps.AddRange(steps);
        MacroService.Save();
        BuildEditor();
        MainWindow.Instance.Toast(Loc.F("Recorded {0} steps.", steps.Count(x => x.Do != "wait")));
    }

    static readonly string[] ButtonNames = { "Left click", "Right click", "Middle mouse", "Mouse 4", "Mouse 5" };

    /// <summary>A key or mouse button drawn as a key cap.</summary>
    Border KeyCap(UIElement content) => new()
    {
        CornerRadius = new CornerRadius(7), Background = (Brush)FindResource("Surface2Brush"), BorderBrush = (Brush)FindResource("BorderBrush"),
        BorderThickness = new Thickness(1, 1, 1, 3), Padding = new Thickness(2, 0, 2, 0), Child = content, VerticalAlignment = VerticalAlignment.Center,
    };

    UIElement Word(string text, double left = 8) => new TextBlock { Text = Loc.T(text), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 8, 0), FontSize = 13 };

    /// <summary>Hover reveals a row's buttons, so the list stays calm.</summary>
    void HoverReveal(FrameworkElement row, FrameworkElement buttons, Border bg = null)
    {
        buttons.Opacity = 0;
        row.MouseEnter += (_, _) => { buttons.Opacity = 1; if (bg != null) bg.Background = (Brush)FindResource("SurfaceHoverBrush"); };
        row.MouseLeave += (_, _) => { buttons.Opacity = 0; if (bg != null) bg.Background = Brushes.Transparent; };
    }

    UIElement ButtonPicker(MacroStep s)
    {
        var b = SmallButton(ButtonNames[Math.Clamp(s.Button, 0, 4)] + "  ▾", null, 0);
        b.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
            for (int x = 0; x < ButtonNames.Length; x++)
            {
                int idx = x;
                var item = new MenuItem { Header = (idx == s.Button ? "✓  " : "     ") + Loc.T(ButtonNames[idx]) };
                item.Click += (_, _) => { s.Button = idx; MacroService.Save(); BuildEditor(); };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        return b;
    }

    /// <summary>Hold length for key presses and clicks, with a hint when it's shorter than games reliably notice.</summary>
    void HoldFields(WrapPanel fields, MacroStep s, double dflt)
    {
        fields.Children.Add(Word("for"));
        var warn = new TextBlock { Text = "⚠ " + Loc.T("games may miss presses this short"), Foreground = (Brush)FindResource("WarnBrush"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        double cur = s.Ms > 0 ? s.Ms : dflt;
        warn.Visibility = cur < 15 ? Visibility.Visible : Visibility.Collapsed;
        fields.Children.Add(NumBox(cur, v => { s.Ms = Math.Clamp(v, 1, 60_000); warn.Visibility = s.Ms < 15 ? Visibility.Visible : Visibility.Collapsed; }, 52));
        fields.Children.Add(Dim("ms", 4));
        fields.Children.Add(warn);
    }

    UIElement ActionRow(MacroDef m, int i, int number)
    {
        var s = m.Steps[i];
        var bg = new Border { Background = Brushes.Transparent, Padding = new Thickness(12, 5, 8, 5), CornerRadius = new CornerRadius(8) };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = number.ToString(CultureInfo.CurrentCulture), Foreground = (Brush)FindResource("TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        var kind = ActionKinds.First(k => k.Do == s.Do);
        string glyph = s.Do is "down" or "press" ? "" : s.Do is "up" or "release" ? "" : kind.Glyph;
        var icon = new TextBlock { Style = (Style)FindResource("IconText"), Text = glyph, FontSize = 13, Foreground = (Brush)FindResource(s.Do is "up" or "release" ? "TextDimBrush" : "AccentBrush") };
        Grid.SetColumn(icon, 1);
        g.Children.Add(icon);

        var f = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        Button Key()
        {
            var k = KeyButton(() => s.Key, vk => s.Key = vk, mouse: false);
            k.MinWidth = 54;
            k.Margin = new Thickness(0, 2, 0, 2);
            k.Padding = new Thickness(12, 5, 12, 5);
            return k;
        }
        switch (s.Do)
        {
            case "tap": f.Children.Add(Word("Press", 0)); f.Children.Add(Key()); HoldFields(f, s, 25); break;
            case "down": f.Children.Add(Word("Hold down", 0)); f.Children.Add(Key()); break;
            case "up": f.Children.Add(Word("Let go of", 0)); f.Children.Add(Key()); break;
            case "click": f.Children.Add(Word("Click", 0)); f.Children.Add(ButtonPicker(s)); HoldFields(f, s, 20); break;
            case "press": f.Children.Add(Word("Hold down", 0)); f.Children.Add(ButtonPicker(s)); break;
            case "release": f.Children.Add(Word("Let go of", 0)); f.Children.Add(ButtonPicker(s)); break;
            case "text":
            {
                f.Children.Add(Word("Type", 0));
                var t = new TextBox { Style = (Style)FindResource("SearchBox"), Text = s.Text ?? "", Width = 240, Tag = Loc.T("Text to type") };
                t.TextChanged += (_, _) => { s.Text = t.Text; SaveSoon(); };
                f.Children.Add(t);
                break;
            }
            case "move":
                f.Children.Add(Word("Move mouse", 0));
                f.Children.Add(Dim("→", 0));
                f.Children.Add(NumBox(s.X, v => s.X = (int)Math.Clamp(v, -10000, 10000), 56, left: 2));
                f.Children.Add(Dim("↓", 8));
                f.Children.Add(NumBox(s.Y, v => s.Y = (int)Math.Clamp(v, -10000, 10000), 56, left: 2));
                f.Children.Add(Dim(Loc.T("px"), 4));
                break;
            case "moveto":
                f.Children.Add(Word("Move mouse to", 0));
                f.Children.Add(NumBox(Math.Round(s.X / 655.35, 1), v => s.X = (int)Math.Round(Math.Clamp(v, 0, 100) * 655.35), 52));
                f.Children.Add(Dim(Loc.T("% across,"), 4));
                f.Children.Add(NumBox(Math.Round(s.Y / 655.35, 1), v => s.Y = (int)Math.Round(Math.Clamp(v, 0, 100) * 655.35), 52, left: 4));
                f.Children.Add(Dim(Loc.T("% down"), 4));
                break;
            case "scroll":
                f.Children.Add(Word("Scroll", 0));
                f.Children.Add(NumBox(s.Y, v => s.Y = (int)Math.Clamp(v, -100, 100), 52));
                f.Children.Add(Dim(Loc.T("clicks (negative = down)"), 6));
                break;
        }
        Grid.SetColumn(f, 2);
        g.Children.Add(f);
        var btns = RowButtons(m, i);
        Grid.SetColumn(btns, 3);
        g.Children.Add(btns);
        bg.Child = g;
        HoverReveal(bg, btns, bg);
        return bg;
    }

    /// <summary>A delay between actions: small and indented, the way G Hub and Synapse show them.</summary>
    UIElement DelayRow(MacroDef m, int i)
    {
        var s = m.Steps[i];
        var bg = new Border { Background = Brushes.Transparent, Padding = new Thickness(12, 1, 8, 1), CornerRadius = new CornerRadius(8) };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new Border { Width = 2, Background = (Brush)FindResource("BorderBrush"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(30, 0, 0, 0) });
        var f = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        f.Children.Add(new TextBlock { Style = (Style)FindResource("IconText"), Text = "", FontSize = 11, Foreground = (Brush)FindResource("TextFaintBrush"), Margin = new Thickness(0, 0, 6, 0) });
        f.Children.Add(new TextBlock { Text = Loc.T("Wait"), FontSize = 12, Foreground = (Brush)FindResource("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var ms = NumBox(s.Ms, v => s.Ms = Math.Clamp(v, 0, 3_600_000), 56);
        ms.FontSize = 12;
        f.Children.Add(ms);
        f.Children.Add(Dim("ms", 4));
        var randomBits = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        randomBits.Children.Add(Dim(Loc.T("+ random up to"), 6));
        var r = NumBox(s.RandomMs, v => s.RandomMs = Math.Clamp(v, 0, 3_600_000), 52);
        r.FontSize = 12;
        randomBits.Children.Add(r);
        randomBits.Children.Add(Dim("ms", 4));
        f.Children.Add(randomBits);
        Grid.SetColumn(f, 1);
        g.Children.Add(f);
        var btns = RowButtons(m, i);
        Grid.SetColumn(btns, 2);
        g.Children.Add(btns);
        bg.Child = g;
        HoverReveal(bg, btns, bg);
        // The random part only shows when it's set, or while pointing at the row.
        if (s.RandomMs <= 0)
        {
            randomBits.Visibility = Visibility.Collapsed;
            bg.MouseEnter += (_, _) => randomBits.Visibility = Visibility.Visible;
            bg.MouseLeave += (_, _) => { if (s.RandomMs <= 0 && !r.IsKeyboardFocusWithin) randomBits.Visibility = Visibility.Collapsed; };
        }
        return bg;
    }

    StackPanel RowButtons(MacroDef m, int i)
    {
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button ins = null;
        ins = IconButton("", "Insert an action after this", true, () => ShowInsertMenu(m, ins, i + 1));
        btns.Children.Add(ins);
        btns.Children.Add(IconButton("", "Move up", i > 0, () => { (m.Steps[i - 1], m.Steps[i]) = (m.Steps[i], m.Steps[i - 1]); MacroService.Save(); BuildEditor(); }));
        btns.Children.Add(IconButton("", "Move down", i < m.Steps.Count - 1, () => { (m.Steps[i + 1], m.Steps[i]) = (m.Steps[i], m.Steps[i + 1]); MacroService.Save(); BuildEditor(); }));
        btns.Children.Add(IconButton("", "Copy this step", true, () =>
        {
            var c = System.Text.Json.JsonSerializer.Deserialize<MacroStep>(System.Text.Json.JsonSerializer.Serialize(m.Steps[i]));
            m.Steps.Insert(i + 1, c); MacroService.Save(); BuildEditor();
        }));
        btns.Children.Add(IconButton("", "Remove", true, () => { m.Steps.RemoveAt(i); MacroService.Save(); BuildEditor(); }));
        return btns;
    }

    // ───────── Logitech script ─────────

    void BuildScript(MacroDef m)
    {
        _scriptBox = new TextBox
        {
            Style = (Style)FindResource("SearchBox"), Text = m.Script ?? "", AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12.5, Height = 340, VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Tag = Loc.T("Paste a Logitech G Hub script here"),
        };
        _scriptBox.TextChanged += (_, _) => { _scriptCheck.Stop(); _scriptCheck.Start(); };
        Editor.Children.Add(_scriptBox);
        _scriptStatus = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        Editor.Children.Add(_scriptStatus);
        CheckScript(m.Script);

        var bar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var import = SmallButton("Import .lua file", ImportLua_Click, 0, right: 6);
        import.Tag = "here";
        bar.Children.Add(import);
        bar.Children.Add(SmallButton("Example: auto clicker", (_, _) => SetScript(m, ExampleClicker), 0, right: 6));
        bar.Children.Add(SmallButton("Example: Roblox auto-farm", (_, _) => SetScript(m, ExampleFarm), 0, right: 6));
        bar.Children.Add(SmallButton("Example: G-key combo", (_, _) => SetScript(m, ExampleCombo), 0, right: 6));
        Editor.Children.Add(bar);
        if (MacroPolicy.MovesMouse(m))
            Editor.Children.Add(Note("This script moves the mouse. Movement pauses in competitive shooters unless you allow that in Settings (we strongly advise against it). In other games, only use it where the rules allow it.", top: 8, brush: "WarnBrush"));

        // G-keys
        Editor.Children.Add(Section("G-keys"));
        Editor.Children.Add(Note("Logitech keyboards and mice have extra G-keys. Pick a key or button to stand in for each one your script uses (G_PRESSED events). Your mouse's own buttons work as they are.", top: 0));
        var gk = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        for (int i = 0; i < m.GKeys.Count; i++)
        {
            int idx = i;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock { Text = "G" + (i + 1), Width = 40, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(KeyButton(() => m.GKeys[idx], vk => m.GKeys[idx] = vk, mouse: true));
            if (i == m.GKeys.Count - 1) row.Children.Add(IconButton("", "Remove", true, () => { m.GKeys.RemoveAt(idx); MacroService.Save(); BuildEditor(); }));
            gk.Children.Add(row);
        }
        Editor.Children.Add(gk);
        if (m.GKeys.Count < 18)
            Editor.Children.Add(SmallButton(Loc.F("Add G{0}", m.GKeys.Count + 1), (_, _) => { m.GKeys.Add(m.GKeys.Count < 12 ? 0x7C + m.GKeys.Count : 0); /* F13, F14… (keys most keyboards lack, so remap software can send them) */ MacroService.Save(); BuildEditor(); }, 0));

        Editor.Children.Add(StepHeader(2, "Where it works", null));
        Editor.Children.Add(WhereEditor(m));

        // Log
        var logHead = new Grid { Margin = new Thickness(0, 18, 0, 6) };
        logHead.Children.Add(new TextBlock { Style = (Style)FindResource("CardTitle"), Text = Loc.T("Log"), FontSize = 14 });
        var clear = SmallButton("Clear", (_, _) => MacroEngine.LogOf(m.Id).Clear(), 0);
        clear.HorizontalAlignment = HorizontalAlignment.Right;
        logHead.Children.Add(clear);
        Editor.Children.Add(logHead);
        _logBox = new TextBox
        {
            Style = (Style)FindResource("SearchBox"), IsReadOnly = true, Height = 150, TextWrapping = TextWrapping.Wrap, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, Tag = Loc.T("OutputLogMessage and errors show up here while macros are on."),
        };
        Editor.Children.Add(_logBox);
        _log = MacroEngine.LogOf(m.Id);
        _log.Changed += OnLog;
        OnLog();

        Editor.Children.Add(Note("Supported: everything in G Hub's scripting guide for mice and keyboards (OnEvent, PressKey, PressMouseButton, MoveMouseRelative, Sleep, IsMouseButtonPressed, OutputLogMessage, GetRunningTime and the rest) plus Lua's math, string and table functions. Lighting and LCD functions are ignored. Scripts can't open files or reach the internet.", top: 10));
    }

    void SetScript(MacroDef m, string text)
    {
        if (!string.IsNullOrWhiteSpace(m.Script) && m.Script != ExampleClicker && m.Script != ExampleFarm && m.Script != ExampleCombo &&
            MessageBox.Show(Window.GetWindow(this), Loc.T("Replace your script with the example?"), Loc.T("Logitech script"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        m.Script = text;
        if (text == ExampleCombo && m.GKeys.Count == 0) m.GKeys.Add(0x7C); // F13 stands in for G1
        MacroService.Save();
        BuildEditor();
    }

    void ApplyScript()
    {
        if (_sel?.Kind != "script" || _scriptBox == null) return;
        if (_sel.Script == _scriptBox.Text) return;
        _sel.Script = _scriptBox.Text;
        MacroService.Save();
        CheckScript(_sel.Script);
    }

    void CheckScript(string text)
    {
        if (_scriptStatus == null) return;
        if (string.IsNullOrWhiteSpace(text)) { _scriptStatus.Text = ""; return; }
        try
        {
            var vm = new LuaInterpreter();
            vm.Run(text); // defines functions only; nothing runs until an event
            bool hasEvent = vm.Globals.Get("OnEvent") is LuaFunction;
            _scriptStatus.Foreground = (Brush)FindResource(hasEvent ? "GoodBrush" : "WarnBrush");
            _scriptStatus.Text = hasEvent ? "✓ " + Loc.T("No errors. It runs while macros are on.") : Loc.T("No OnEvent function: Logitech scripts start with  function OnEvent(event, arg)");
        }
        catch (LuaException ex)
        {
            _scriptStatus.Foreground = (Brush)FindResource("BadBrush");
            _scriptStatus.Text = Loc.F("Error: {0}", ex.Message);
        }
        catch (Exception ex)
        {
            // Top-level code that calls Pulse functions (Sleep, PressKey) before any event: fine at run time.
            _scriptStatus.Foreground = (Brush)FindResource("TextDimBrush");
            _scriptStatus.Text = Loc.F("Checked as far as it could: {0}", ex.Message);
        }
    }

    void OnLog() => Dispatcher.BeginInvoke(() =>
    {
        if (_logBox == null || _log == null) return;
        _logBox.Text = _log.Text;
        _logBox.ScrollToEnd();
    });

    void DetachLog()
    {
        if (_log != null) _log.Changed -= OnLog;
        _log = null;
        _logBox = null;
    }

    const string ExampleClicker =
@"-- Auto clicker: clicks while you hold mouse 5 (the front side button).
-- Works like it does in Logitech G Hub, on any mouse.

function OnEvent(event, arg)
    if event == ""MOUSE_BUTTON_PRESSED"" and arg == 5 then
        repeat
            PressAndReleaseMouseButton(1)   -- 1 = left click
            Sleep(math.random(55, 75))      -- about 15 clicks a second
        until not IsMouseButtonPressed(5)
    end
end
";

    const string ExampleFarm =
@"-- Roblox auto-farm: press mouse 4 (the back side button) to start, press it again to stop.
-- Holds W and clicks, then turns a little. Change the keys and times to fit your game.
-- Only use it in games that allow auto-farming.

local stoppedAt = -1000

local function wait(ms)
    -- Sleeps, but stops early (returns true) when you press mouse 4 again.
    local untilTime = GetRunningTime() + ms
    while GetRunningTime() < untilTime do
        if IsMouseButtonPressed(4) then return true end
        Sleep(10)
    end
    return false
end

function OnEvent(event, arg)
    if event ~= ""MOUSE_BUTTON_PRESSED"" or arg ~= 4 then return end
    if GetRunningTime() - stoppedAt < 500 then return end   -- that press was the one that stopped it
    repeat Sleep(10) until not IsMouseButtonPressed(4)      -- let go of the button that started it
    OutputLogMessage(""Farming started\n"")
    local stop = false
    while not stop do
        PressKey(""w"")
        for i = 1, 8 do
            PressAndReleaseMouseButton(1)
            if wait(120) then stop = true; break end
        end
        ReleaseKey(""w"")
        if not stop then
            MoveMouseRelative(150, 0)
            stop = wait(300)
        end
    end
    repeat Sleep(10) until not IsMouseButtonPressed(4)
    stoppedAt = GetRunningTime()
    OutputLogMessage(""Farming stopped\n"")
end
";

    const string ExampleCombo =
@"-- G-key combo: G1 types a quick message. Pick which key is G1 below (F13 by default).

function OnEvent(event, arg, family)
    if event == ""G_PRESSED"" and arg == 1 then
        PressAndReleaseKey(""enter"")
        Sleep(60)
        for _, k in ipairs({ ""g"", ""g"" }) do
            PressAndReleaseKey(k)
            Sleep(30)
        end
        PressAndReleaseKey(""enter"")
        OutputLogMessage(""Sent gg at %s\n"", GetDate(""%H:%M:%S""))
    end
end
";

    // ───────── live numbers ─────────

    void Refresh()
    {
        LiveCps.Text = (MacroService.Engine?.LiveCps ?? 0).ToString(CultureInfo.CurrentCulture);
        RunningCount.Text = (MacroService.Engine?.RunningCount ?? 0).ToString(CultureInfo.CurrentCulture);
        if (_padCps == null) return;
        double now = Now;
        int last = _pad.Count(t => now - t <= 1000);
        if (last > _best) _best = last;
        _padCps.Text = last.ToString(CultureInfo.CurrentCulture);
        _padBest.Text = _best > 0 ? Loc.F("{0} CPS", _best) : "–";
        var recent = _pad.Where(t => now - t <= 3000).ToList();
        if (recent.Count >= 6)
        {
            var gaps = recent.Zip(recent.Skip(1), (a, b) => b - a).ToList();
            double mean = gaps.Average(), sd = Math.Sqrt(gaps.Sum(g => (g - mean) * (g - mean)) / gaps.Count);
            _padSteady.Text = $"{Math.Max(0, 100 - sd / mean * 100):0}%";
        }
        DrawTimeline(recent, now);
    }

    void DrawTimeline(List<double> clicks, double now)
    {
        if (_timeline == null) return;
        _timeline.Children.Clear();
        double w = _timeline.ActualWidth, h = _timeline.ActualHeight;
        if (w < 10) return;
        var brush = (Brush)FindResource("AccentGradient");
        foreach (var t in clicks)
        {
            double x = w - (now - t) / 3000 * w;
            var tick = new Rectangle { Width = 3, Height = h - 8, RadiusX = 1.5, RadiusY = 1.5, Fill = brush, Opacity = 0.35 + 0.65 * (1 - (now - t) / 3000) };
            Canvas.SetLeft(tick, x - 1.5);
            Canvas.SetTop(tick, 4);
            _timeline.Children.Add(tick);
        }
    }

    // ───────── key capture ─────────

    Button KeyButton(Func<int> get, Action<int> set, bool mouse)
    {
        var b = new Button { Style = (Style)FindResource("GhostButton"), MinWidth = 120, Margin = new Thickness(0, 3, 6, 3), Padding = new Thickness(14, 7, 14, 7) };
        string Label() => get() == 0 ? Loc.T("Click to set") : Loc.T(Keys.Display(get()));
        b.Content = Label();
        b.ToolTip = Loc.T(mouse ? "Click, then press a key or mouse button" : "Click, then press a key");
        b.Click += (_, _) =>
        {
            if (_capBtn == b) return;
            EndCapture();
            _capBtn = b;
            _capMouse = mouse;
            _capLabel = Label;
            _capSet = vk => { set(vk); SaveSoon(); };
            b.Content = Loc.T(mouse ? "Press a key or button…" : "Press a key…");
            b.Focus();
        };
        return b;
    }

    void EndCapture()
    {
        if (_capBtn != null && _capLabel != null) _capBtn.Content = _capLabel();
        _capBtn = null;
        _capSet = null;
        _capLabel = null;
    }

    void Capture_KeyDown(object sender, KeyEventArgs e)
    {
        if (_capSet == null) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && _capBtn?.Parent != PanicHost) { EndCapture(); return; }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        // Plain Shift/Ctrl/Alt come through as the left or right key; that's what Windows reports for physical keys too.
        var set = _capSet;
        set(vk);
        EndCapture();
        RefreshListRow();
        ShowState();
    }

    void Capture_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_capSet == null) return;
        // A click outside the waiting button cancels, unless mouse buttons are allowed (then that button is the pick).
        if (!_capMouse) { if (!_capBtn.IsMouseOver) EndCapture(); return; }
        int vk = e.ChangedButton switch
        {
            MouseButton.Left => Keys.VK_LBUTTON, MouseButton.Right => Keys.VK_RBUTTON, MouseButton.Middle => Keys.VK_MBUTTON,
            MouseButton.XButton1 => Keys.VK_XBUTTON1, MouseButton.XButton2 => Keys.VK_XBUTTON2, _ => 0,
        };
        if (vk == 0) return;
        e.Handled = true;
        var set = _capSet;
        set(vk);
        EndCapture();
        RefreshListRow();
    }

    // ───────── small builders ─────────

    TextBlock Section(string title) => new() { Style = (Style)FindResource("CardTitle"), Text = Loc.T(title), FontSize = 14, Margin = new Thickness(0, 20, 0, 8) };

    UIElement Row(string label, UIElement content)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Style = (Style)FindResource("RowLabel"), Text = Loc.T(label), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 9, 8, 0), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(content, 1);
        g.Children.Add(content);
        return g;
    }

    UIElement Indent(UIElement e) { if (e is FrameworkElement fe) fe.Margin = new Thickness(150, 0, 0, 6); return e; }

    UIElement SwitchRow(string label, bool value, Action<bool> set)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Style = (Style)FindResource("RowLabel"), Text = Loc.T(label), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        var sw = Switch(value, set);
        Grid.SetColumn(sw, 1);
        g.Children.Add(sw);
        return g;
    }

    CheckBox Switch(bool value, Action<bool> set)
    {
        var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        sw.Click += (_, _) => { set(sw.IsChecked == true); MacroService.Save(); };
        return sw;
    }

    static int _group;

    WrapPanel Chips((string Label, string Value)[] items, string current, Action<string> set)
    {
        var panel = new WrapPanel();
        string group = "mchip" + (++_group);
        foreach (var (label, value) in items)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = Loc.T(label), GroupName = group, Tag = value, Margin = new Thickness(0, 3, 6, 3), IsChecked = value == current };
            rb.Checked += (s, _) => { set((string)((FrameworkElement)s).Tag); SaveSoon(); };
            panel.Children.Add(rb);
        }
        return panel;
    }

    /// <summary>An on/off chip (Ctrl, Shift, Alt).</summary>
    RadioButton ToggleChip(string label, bool value, Action<bool> set)
    {
        var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = label, GroupName = "mtoggle" + (++_group), IsChecked = value, Margin = new Thickness(0, 3, 6, 3) };
        rb.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            rb.IsChecked = rb.IsChecked != true;
            set(rb.IsChecked == true);
            SaveSoon();
        };
        return rb;
    }

    TextBox NumBox(double value, Action<double> set, double width, double left = 0)
    {
        var t = new TextBox { Style = (Style)FindResource("SearchBox"), Width = width, Text = value.ToString("0.##", CultureInfo.CurrentCulture), Margin = new Thickness(left, 3, 0, 3), HorizontalContentAlignment = HorizontalAlignment.Center };
        t.TextChanged += (_, _) =>
        {
            var txt = t.Text.Trim();
            if (double.TryParse(txt, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) || double.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                set(v);
                t.ClearValue(ForegroundProperty);
                SaveSoon();
            }
            else t.Foreground = (Brush)FindResource("BadBrush");
        };
        return t;
    }

    TextBlock Dim(string text, double left) => new() { Text = text, Foreground = (Brush)FindResource("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 6, 0), FontSize = 12.5 };

    TextBlock Note(string text, double top, string brush = "TextDimBrush") => new()
    {
        Style = (Style)FindResource("Body"), Text = Loc.T(text), FontSize = 12, Foreground = (Brush)FindResource(brush), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left,
    };

    Button SmallButton(string text, RoutedEventHandler click, double left, double right = 0)
    {
        var b = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T(text), Margin = new Thickness(left, 3, right, 3), Padding = new Thickness(12, 6, 12, 6), FontSize = 12 };
        if (click != null) b.Click += click;
        return b;
    }

    Button IconButton(string glyph, string tip, bool enabled, Action act)
    {
        var b = new Button
        {
            Style = (Style)FindResource("GhostButton"), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(4, 0, 0, 0), IsEnabled = enabled, ToolTip = Loc.T(tip),
            Content = new TextBlock { Style = (Style)FindResource("IconText"), Text = glyph, FontSize = 12 }, Opacity = enabled ? 1 : 0.35,
        };
        b.Click += (_, _) => act();
        return b;
    }
}
