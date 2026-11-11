using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pulse.Core;
using Pulse.Core.Crosshair;
using Pulse.Ui;

namespace Pulse.Pages;

/// <summary>For playing VALORANT on console too: one switch for the monitor and sound, and your crosshair to carry over.</summary>
public partial class ValorantPage
{
    List<ConsoleSwitch.Screen> _screens;
    List<ConsoleSwitch.SoundDevice> _sounds;
    string _defaultSound;
    bool _screensLoading;

    static ConsoleSwitchSettings ConsoleSet => TweakEngine.State.ConsoleSwitch ??= new ConsoleSwitchSettings();

    void BuildConsole()
    {
        Root.Children.Add(SwitchCard());
        Root.Children.Add(ConsoleCrosshairCard());
        Root.Children.Add(MatchSettingsCard());
    }

    async Task LoadScreens()
    {
        _screensLoading = true;
        var (screens, sound) = await Task.Run(() => (ConsoleSwitch.Screens(), ConsoleSwitch.Sound()));
        _screens = screens; _sounds = sound.Devices; _defaultSound = sound.Default;
        _screensLoading = false;
        var s = ConsoleSet;
        // First time: the monitor and the input it's on now are the PC's.
        if (s.PcInput == 0 && _screens.Count > 0)
        {
            var first = _screens.FirstOrDefault(x => x.Name == s.Monitor) ?? _screens[0];
            s.Monitor = first.Name; s.PcInput = first.Input;
            if (string.IsNullOrEmpty(s.PcSound)) s.PcSound = _defaultSound;
            TweakEngine.Save();
        }
        if (_tab == "console") Build();
    }

    FrameworkElement SwitchCard()
    {
        var s = ConsoleSet;
        var sp = new StackPanel();
        sp.Children.Add(Heading("Switch between PC and console"));
        var keyName = ConsoleSwitch.KeyText(s.HotkeyMods, s.HotkeyKey);
        sp.Children.Add(Dim(Loc.F("One click moves your monitor to the console and your PC's sound where you want it. Press {0} to come back, even while the screen shows the console.", keyName), 12.5, 4));

        if (_screens == null)
        {
            if (!_screensLoading) _ = LoadScreens();
            sp.Children.Add(Dim(Loc.T("Looking for your monitor…"), 12.5, 12));
            return Card(sp);
        }
        if (_screens.Count == 0)
        {
            sp.Children.Add(Dim(Loc.T("Windows couldn't reach your monitor's controls. Turn on DDC/CI in its on-screen menu (usually under System or Other), then look again. A TV or a monitor plugged in through a dock or adapter may not support it."), 12.5, 12));
            var again = Btn("Look again");
            again.HorizontalAlignment = HorizontalAlignment.Left;
            again.Margin = new Thickness(0, 12, 0, 0);
            again.Click += (_, _) => { _screens = null; Build(); };
            sp.Children.Add(again);
            return Card(sp);
        }

        bool ready = s.PcInput != 0 && s.ConsoleInput != 0;
        // The big switch.
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var go = Btn(s.OnConsole ? "Back to PC" : "Switch to console", primary: true);
        go.Padding = new Thickness(22, 10, 22, 10);
        go.FontSize = 14.5;
        go.IsEnabled = ready;
        go.Click += async (_, _) => { go.IsEnabled = false; await ConsoleSwitchService.SwapAsync(!s.OnConsole); Build(); };
        row.Children.Add(go);
        row.Children.Add(new TextBlock { Text = s.OnConsole ? Loc.T("You're on console") : Loc.T("You're on PC"), FontSize = 13, Foreground = Res("TextDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        sp.Children.Add(row);
        if (!ready) sp.Children.Add(Faint(Loc.T("Pick the PC's and the console's inputs below first."), 11.5, 6));

        // Setup.
        sp.Children.Add(Label("Monitor").Also(l => l.Margin = new Thickness(0, 22, 0, 6)));
        var mons = new WrapPanel();
        foreach (var m in _screens)
        {
            var mm = m;
            mons.Children.Add(Chip(m.Name, "cs-mon", s.Monitor == m.Name, () => { s.Monitor = mm.Name; if (mm.Input != 0) s.PcInput = mm.Input; TweakEngine.Save(); Build(); }));
        }
        sp.Children.Add(mons);

        FrameworkElement Inputs(string group, int current, Action<int> set)
        {
            var w = new WrapPanel();
            foreach (var code in ConsoleSwitch.CommonInputs.Concat(current != 0 && !ConsoleSwitch.CommonInputs.Contains(current) ? new[] { current } : Array.Empty<int>()))
            {
                int c = code;
                w.Children.Add(Chip(ConsoleSwitch.InputName(c), group, current == c, () => { set(c); TweakEngine.Save(); ConsoleSwitchService.Refresh(); Build(); }));
            }
            return w;
        }
        sp.Children.Add(Label("PC is on").Also(l => l.Margin = new Thickness(0, 14, 0, 6)));
        sp.Children.Add(Inputs("cs-pc", s.PcInput, v => s.PcInput = v));
        sp.Children.Add(Label("Console is on").Also(l => l.Margin = new Thickness(0, 14, 0, 6)));
        sp.Children.Add(Inputs("cs-console", s.ConsoleInput, v => s.ConsoleInput = v));
        sp.Children.Add(Faint(Loc.T("Check the ports the PC's and console's cables go into on the back of the monitor."), 11.5, 2));
        var mon = _screens.FirstOrDefault(x => x.Name == s.Monitor);
        if (mon is { Input: 0 }) sp.Children.Add(Faint(Loc.T("Your monitor doesn't say which input it's on, so pick both yourself. Switching still works."), 11.5, 4));
        if (ready)
        {
            var test = Btn("Test: switch to console for 10 seconds");
            test.HorizontalAlignment = HorizontalAlignment.Left;
            test.Margin = new Thickness(0, 10, 0, 0);
            test.Click += async (_, _) =>
            {
                test.IsEnabled = false;
                var ok = await Task.Run(() => ConsoleSwitch.SetInput(s.Monitor, s.ConsoleInput));
                await Task.Delay(10000);
                await Task.Run(() => ConsoleSwitch.SetInput(s.Monitor, s.PcInput));
                test.IsEnabled = true;
                MainWindow.Instance.Toast(ok ? Loc.T("Did the screen switch? If it showed the wrong thing, pick another console input.") : Loc.T("Your monitor didn't take the switch. Turn on DDC/CI in its on-screen menu."), ok ? ToastKind.Info : ToastKind.Warning);
            };
            sp.Children.Add(test);
        }

        // Sound.
        ComboBox Sound(string sel, Action<string> set)
        {
            var b = new ComboBox { MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Left };
            b.Items.Add(new ComboBoxItem { Content = Loc.T("Don't change"), Tag = "" });
            foreach (var d in _sounds) b.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
            b.SelectedIndex = Math.Max(0, b.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == (sel ?? "")));
            b.SelectionChanged += (_, _) => { set((string)((ComboBoxItem)b.SelectedItem).Tag); TweakEngine.Save(); };
            return b;
        }
        sp.Children.Add(Label("PC sound on PC").Also(l => l.Margin = new Thickness(0, 18, 0, 6)));
        sp.Children.Add(Sound(s.PcSound, v => s.PcSound = v));
        sp.Children.Add(Label("PC sound on console").Also(l => l.Margin = new Thickness(0, 12, 0, 6)));
        sp.Children.Add(Sound(s.ConsoleSound, v => s.ConsoleSound = v));
        sp.Children.Add(Faint(Loc.T("For example: your headset on PC, and the speakers (or nothing you'll hear) while you're on console, so Discord and the PC don't talk over the game."), 11.5, 4));

        var hkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        var hk = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = s.Hotkey, Content = Loc.F("{0} switches back and forth", keyName), Foreground = Res("TextBrush"), VerticalAlignment = VerticalAlignment.Center };
        hk.Checked += (_, _) => { s.Hotkey = true; TweakEngine.Save(); ConsoleSwitchService.Refresh(); };
        hk.Unchecked += (_, _) => { s.Hotkey = false; TweakEngine.Save(); ConsoleSwitchService.Refresh(); };
        hkRow.Children.Add(hk);
        // Change the key: press the new combination.
        var change = Btn("Change key");
        change.Margin = new Thickness(14, 0, 0, 0);
        change.Click += (_, _) =>
        {
            change.Content = Loc.T("Press the new keys…");
            change.Focus();
            void Pick(object _, System.Windows.Input.KeyEventArgs e)
            {
                var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
                if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
                    or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin) return;
                e.Handled = true;
                change.PreviewKeyDown -= Pick;
                if (key == System.Windows.Input.Key.Escape) { Build(); return; }
                var m = System.Windows.Input.Keyboard.Modifiers;
                int mods = (m.HasFlag(System.Windows.Input.ModifierKeys.Alt) ? 1 : 0) | (m.HasFlag(System.Windows.Input.ModifierKeys.Control) ? 2 : 0)
                    | (m.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? 4 : 0) | (m.HasFlag(System.Windows.Input.ModifierKeys.Windows) ? 8 : 0);
                int vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
                // A key on its own would fire while typing anywhere: ask for Ctrl, Alt or Shift too, except F13–F24 and Pause.
                if (mods == 0 && !(vk is >= 0x7C and <= 0x87 or 0x13 or 0x91))
                {
                    MainWindow.Instance.Toast(Loc.T("Hold Ctrl, Alt or Shift with the key, so it doesn't go off while you type."), ToastKind.Info);
                    Build();
                    return;
                }
                s.HotkeyMods = mods; s.HotkeyKey = vk; s.Hotkey = true;
                TweakEngine.Save();
                ConsoleSwitchService.Refresh();
                Build();
                if (ready && !ConsoleSwitchService.HotkeyWorks) MainWindow.Instance.Toast(Loc.F("Another app already uses {0}. Pick other keys.", ConsoleSwitch.KeyText(mods, vk)), ToastKind.Warning);
                else MainWindow.Instance.Toast(Loc.F("{0} now switches between PC and console.", ConsoleSwitch.KeyText(mods, vk)), ToastKind.Success);
            }
            change.PreviewKeyDown += Pick;
        };
        hkRow.Children.Add(change);
        sp.Children.Add(hkRow);
        if (s.Hotkey && ready && !ConsoleSwitchService.HotkeyWorks) sp.Children.Add(Faint(Loc.F("Another app already uses {0}, so the key won't work. Pick other keys.", keyName), 11.5, 4));
        return Card(sp);
    }

    FrameworkElement ConsoleCrosshairCard()
    {
        var s = ConsoleSet;
        var sp = new StackPanel();
        sp.Children.Add(Heading("Your crosshair on console"));
        sp.Children.Add(Dim(Loc.T("Console VALORANT can import crosshair codes too. On PC, go to Settings › Crosshair and press Export, then paste the code here. Pulse shows it in short pieces that are easy to type."), 12.5, 4));
        var box = new TextBox { Style = (Style)FindResource("SearchBox"), Tag = "0;P;c;5;…", Text = s.CrosshairCode, Margin = new Thickness(0, 12, 0, 0), FontSize = 13 };
        var shown = new StackPanel();
        void Show()
        {
            shown.Children.Clear();
            var code = (s.CrosshairCode ?? "").Trim();
            if (code.Length == 0) return;
            if (ValorantCode.Parse(code) == null) { shown.Children.Add(Faint(Loc.T("That isn't a VALORANT crosshair code. It starts with 0;P;"), 11.5, 8)); return; }
            var pieces = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            for (int i = 0; i < code.Length; i += 8)
                pieces.Children.Add(new Border { Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(8), Background = Res("SurfaceHoverBrush"),
                    Child = new TextBlock { Text = code.Substring(i, Math.Min(8, code.Length - i)), FontSize = 18, FontFamily = new FontFamily("Consolas"), Foreground = Res("TextBrush") } });
            shown.Children.Add(pieces);
            var copy = Btn("Copy");
            copy.HorizontalAlignment = HorizontalAlignment.Left;
            copy.Click += (_, _) => { try { Clipboard.SetText(code); MainWindow.Instance.Toast(Loc.T("Copied."), ToastKind.Success); } catch { } };
            shown.Children.Add(copy);
            shown.Children.Add(Faint(Loc.T("Typing with a controller is slow: plug a USB keyboard into the console and type it in a few seconds."), 11.5, 10));
        }
        box.TextChanged += (_, _) => { s.CrosshairCode = box.Text.Trim(); TweakEngine.Save(); Show(); };
        sp.Children.Add(box);
        sp.Children.Add(shown);
        Show();
        return Card(sp);
    }

    FrameworkElement MatchSettingsCard()
    {
        var sp = new StackPanel();
        sp.Children.Add(Heading("Keep these the same on both"));
        sp.Children.Add(Dim(Loc.T("Not every setting carries over between PC and console. Check these on both so switching feels the same:"), 12.5, 4));
        foreach (var (what, why) in new[]
        {
            ("Crosshair", "The code above."),
            ("Enemy highlight color", "So enemies pop out the same way."),
            ("Minimap: rotate, keep player centered, size and zoom", "So callouts and positions read the same."),
            ("Brightness and display mode", "So dark corners look the same."),
            ("Voice chat and push-to-talk", "So your team hears you the same way."),
        })
        {
            var r = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            r.Children.Add(new TextBlock { Text = "✓  " + Loc.T(what), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush") });
            r.Children.Add(Faint(Loc.T(why), 11.5, 2).Also(f => f.Margin = new Thickness(20, 2, 0, 0)));
            sp.Children.Add(r);
        }
        sp.Children.Add(Faint(Loc.T("Aim settings don't carry from mouse to controller: they work differently, so set those on console by feel."), 11.5, 12));
        return Card(sp);
    }
}
