using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Pulse.Core;
using Pulse.Core.Measurement;
using Pulse.Pages;
using Pulse.Ui;

namespace Pulse;

/// <summary>Pages implement this to refresh when they're shown.</summary>
public interface IPage
{
    void OnShown(object parameter);
}

/// <summary>A screen that holds things outside itself (event subscriptions, players, watchers) and lets go of them when Pulse drops it.</summary>
public interface IReleasable
{
    void Release();
}

public enum ToastKind { Success, Info, Warning }

public partial class MainWindow : Window
{
    public static MainWindow Instance { get; private set; }

    readonly Dictionary<string, UserControl> _pages = new();
    TaskCompletionSource<bool> _dialog;
    object _pendingParameter;
    int _toastVersion;

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        LangLabel.Text = Ui.Loc.Current == "pt-BR" ? "PT" : Ui.Loc.Current.ToUpperInvariant();
        Loaded += OnLoaded;
        StartUpdateChecks();
        VersionText.Text = Updater.CurrentVersion == "0.0.0" ? "dev" : Updater.CurrentVersion;
        ClipsService.Changed += () => Dispatcher.InvokeAsync(ShowClipsPill);
        // Clipped in a game? The next time you switch to Pulse it opens on your clips.
        ClipsService.Saved += _ => Dispatcher.InvokeAsync(() => { if (!IsActive) _clippedWhileAway = true; });
        Activated += (_, _) => { LeaveLean(); OpenClipsIfClipped(); CheckForUpdateSoon(); };
        SourceInitialized += (_, _) => Native.StyleWindow(new WindowInteropHelper(this).Handle);
        StateChanged += (_, _) =>
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxBtn.Content = WindowState == WindowState.Maximized ? "" : "";
        };
        TweakEngine.Changed += () => Dispatcher.InvokeAsync(UpdateSidebar);
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Updater.JustUpdatedTo is string v) Dispatcher.BeginInvoke(() => Toast(Loc.F("Updated to Pulse {0}.", v)), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        // Intro: fade and settle in.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.Opacity = 0;
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(500)) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(500)) { EasingFunction = ease });

        // Ambient glow slowly breathes.
        Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 0.7, TimeSpan.FromSeconds(5))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });

        LiveStats.Updated += UpdateMini;
        LiveStats.Start();
        AbTest.ApplyRecordsToCatalog();
        UpdateSidebar();
        ShowPage("home");

        // Report from the weekly tune-up that ran while Pulse was closed.
        if (!string.IsNullOrEmpty(TweakEngine.State.PendingReport))
        {
            var report = TweakEngine.State.PendingReport;
            TweakEngine.State.PendingReport = null;
            TweakEngine.Save();
            Dispatcher.InvokeAsync(() => Toast(report, ToastKind.Info));
        }
        _ = Task.Run(Pulse.Core.Fresh.Freshness.RefreshTaskPath);
        _ = Task.Run(Pulse.Core.Roblox.RobloxBoost.Maintain);

        // Resume a measurement interrupted by a restart.
        var pending = AbTest.LoadPending();
        if (pending != null) Dispatcher.InvokeAsync(() => Navigate("measure", pending));
    }

    void UpdateSidebar()
    {
        int n = TweakEngine.State.Applied.Count;
        SidebarStatus.Text = n == 0
            ? "No tweaks applied yet. Backups are kept for everything Pulse changes."
            : $"{n} tweak{(n == 1 ? "" : "s")} applied, each with a saved backup.";
    }

    void UpdateMini()
    {
        MiniCpu.Text = $"{LiveStats.CpuNow * 100:0}%";
        MiniGpu.Text = LiveStats.GpuNow is double g ? $"{g * 100:0}%" : "n/a";
        MiniRam.Text = $"{LiveStats.RamNow * 100:0}%";
        Pulse.Ui.Anim.Bar(MiniCpuBar, LiveStats.CpuNow);
        Pulse.Ui.Anim.Bar(MiniGpuBar, LiveStats.GpuNow ?? 0);
        Pulse.Ui.Anim.Bar(MiniRamBar, LiveStats.RamNow);
    }

    // ───────── Navigation ─────────

    public void Navigate(string key, object parameter = null)
    {
        _pendingParameter = parameter;
        var rb = NavPanel.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == key);
        if (rb == null) return;
        if (rb.IsChecked == true) ShowPage(key);
        else rb.IsChecked = true; // fires Nav_Checked
    }

    bool _justChecked;

    /// <summary>Clicking the page you're already on resets it (e.g. Games goes back to the library from inside a game).</summary>
    void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (_justChecked) { _justChecked = false; return; }
        if (sender is RadioButton rb) ShowPage((string)rb.Tag);
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        _justChecked = true;
        Dispatcher.BeginInvoke(() => _justChecked = false, System.Windows.Threading.DispatcherPriority.Background);
        if (!IsLoaded || sender is not RadioButton rb) return;
        ShowPage((string)rb.Tag);
    }

    void ShowPage(string key)
    {
        var rb = NavPanel.Children.OfType<RadioButton>().First(r => (string)r.Tag == key);
        var y = rb.TranslatePoint(new Point(0, 0), NavPanel).Y;
        NavHighlightT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(320))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "home" => new HomePage(),
                "diagnose" => new DiagnosePage(),
                "fresh" => new FreshPage(),
                "roblox" => new RobloxPage(),
                "clips" => new ClipsPage(),
                "tweaks" => new TweaksPage(),
                "measure" => new MeasurePage(),
                "reaction" => new ReactionPage(),
                "macros" => new MacrosPage(),
                "games" => new GamesPage(),
                "cleanup" => new DeclutterPage(),
                "startup" => new StartupPage(),
                "security" => new SecurityPage(),
                "safety" => new SafetyPage(),
                _ => new HomePage(),
            };
            _pages[key] = page;
        }

        PageHost.Content = page;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        PageHostT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });

        var p = _pendingParameter;
        _pendingParameter = null;
        if (page is IPage ip) ip.OnShown(p);
    }

    // ───────── Dialogs ─────────

    public Task<bool> Confirm(string title, string text, string ok = "Continue", string cancel = "Cancel")
    {
        _dialog?.TrySetResult(false);
        DialogTitle.Text = Ui.Loc.T(title);
        DialogText.Text = Ui.Loc.T(text);
        DialogText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        DialogOk.Content = Ui.Loc.T(ok);
        DialogCancel.Content = Ui.Loc.T(cancel ?? "");
        DialogCancel.Visibility = cancel == null ? Visibility.Collapsed : Visibility.Visible;
        DialogButtons.Visibility = Visibility.Visible;
        DialogBusyRow.Visibility = Visibility.Collapsed;
        HideSteps();
        ShowOverlay();
        _dialog = new TaskCompletionSource<bool>();
        return _dialog.Task;
    }

    public Task Alert(string title, string text) => Confirm(title, text, "OK", null);

    public void ShowBusy(string title, string text, string status = "Working…")
    {
        DialogTitle.Text = Ui.Loc.T(title);
        DialogText.Text = Ui.Loc.T(text);
        DialogText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        DialogBusyText.Text = Ui.Loc.T(status);
        DialogButtons.Visibility = Visibility.Collapsed;
        DialogBusyRow.Visibility = Visibility.Visible;
        HideSteps();
        ShowOverlay();
    }

    // ───────── Step-by-step progress (e.g. Debloat's cleaning screen) ─────────

    List<Ui.ProgressStep> _steps;

    /// <summary>
    /// Shows a screen with a big spinning icon, a progress bar and one line per step, so people can see work is
    /// really happening. Update the steps (Start / Finish / Skip) as you go, then call <see cref="FinishSteps"/>.
    /// </summary>
    DateTime _stepsStarted;
    readonly System.Windows.Threading.DispatcherTimer _etaTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    Action _stepStop;

    /// <summary>Overall progress of the steps on screen (0 to 1), weighted by how long each is expected to take.</summary>
    double StepProgress()
    {
        if (_steps == null || _steps.Count == 0) return 0;
        double total = _steps.Sum(s => Math.Max(s.Weight, 0.01));
        double done = _steps.Sum(s => Math.Max(s.Weight, 0.01) * (s.IsDone || s.IsSkipped ? 1 : s.IsActive ? Math.Max(s.Fraction, 0.05) : 0));
        return Math.Clamp(done / total, 0, 1);
    }

    void UpdateEta()
    {
        if (_steps == null) { _etaTimer.Stop(); return; }
        double p = StepProgress();
        var elapsed = DateTime.UtcNow - _stepsStarted;
        DialogStepPct.Text = $"{(int)(p * 100)}%";
        if (p < 0.03 || elapsed.TotalSeconds < 2) { DialogStepEta.Text = Ui.Loc.F("Working · {0}", Clock(elapsed)) + " · " + Ui.Loc.T("estimating time left…"); return; }
        var left = TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - p) / p);
        string leftText = left.TotalSeconds < 10 ? Ui.Loc.T("a few seconds left")
            : left.TotalSeconds < 60 ? Ui.Loc.F("about {0} seconds left", (int)Math.Ceiling(left.TotalSeconds / 5) * 5)
            : Ui.Loc.F("about {0} min left", (int)Math.Ceiling(left.TotalMinutes));
        DialogStepEta.Text = Ui.Loc.F("Working · {0}", Clock(elapsed)) + " · " + leftText;
    }

    static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    /// <summary>Shows a "Stop after this item" link on the progress screen; <paramref name="stop"/> runs when it's pressed.</summary>
    public void AllowStepStop(Action stop)
    {
        _stepStop = stop;
        DialogStepStop.Content = Ui.Loc.T("Stop after this item");
        DialogStepStop.IsEnabled = true;
        DialogStepStop.Visibility = stop == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void DialogStepStop_Click(object sender, RoutedEventArgs e)
    {
        _stepStop?.Invoke();
        DialogStepStop.IsEnabled = false;
        DialogStepStop.Content = Ui.Loc.T("Stopping after this item…");
    }

    public void ShowSteps(string title, string text, List<Ui.ProgressStep> steps, string glyph = "\uE74D")
    {
        bool fresh = _steps == null;
        if (_steps != null) foreach (var st in _steps) st.PropertyChanged -= Step_Changed;
        _steps = steps;
        foreach (var st in steps) st.PropertyChanged += Step_Changed;
        DialogTitle.Text = Ui.Loc.T(title);
        DialogText.Text = Ui.Loc.T(text);
        DialogText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        DialogStepGlyph.Text = glyph;
        DialogStepGlyph.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        DialogStepSpinner.Visibility = Visibility.Visible;
        DialogStepIcon.Visibility = DialogSteps.Visibility = Visibility.Visible;
        DialogStepList.ItemsSource = steps;
        DialogButtons.Visibility = Visibility.Collapsed;
        DialogBusyRow.Visibility = Visibility.Collapsed;
        if (fresh) { _stepsStarted = DateTime.UtcNow; AllowStepStop(null); }
        DialogStepEta.Visibility = DialogStepPct.Visibility = Visibility.Visible;
        if (_etaTimer.Tag == null) { _etaTimer.Tick += (_, _) => UpdateEta(); _etaTimer.Tag = true; }
        _etaTimer.Start();
        UpdateEta();
        UpdateStepBar();
        ShowOverlay();
    }

    void Step_Changed(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Ui.ProgressStep.State) or nameof(Ui.ProgressStep.Fraction)) Dispatcher.InvokeAsync(UpdateStepBar);
    }

    void UpdateStepBar()
    {
        if (_steps == null || _steps.Count == 0) return;
        double width = Math.Max(DialogSteps.ActualWidth, 420) * StepProgress();
        DialogStepBar.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(250)) { EasingFunction = new QuadraticEase() });
    }

    /// <summary>Turns the progress screen into the result: a check mark, the outcome and an OK button.</summary>
    public Task FinishSteps(string title, string text, bool ok = true)
    {
        _etaTimer.Stop();
        DialogStepPct.Text = "100%";
        DialogStepEta.Text = Ui.Loc.F("Took {0}", Clock(DateTime.UtcNow - _stepsStarted));
        AllowStepStop(null);
        DialogTitle.Text = Ui.Loc.T(title);
        DialogText.Text = Ui.Loc.T(text);
        DialogText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        DialogStepSpinner.Visibility = Visibility.Collapsed;
        DialogStepGlyph.Text = ok ? "\uE73E" : "\uE7BA";
        DialogStepGlyph.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "GoodBrush" : "WarnBrush");
        DialogStepBar.BeginAnimation(WidthProperty, new DoubleAnimation(Math.Max(DialogSteps.ActualWidth, 420), TimeSpan.FromMilliseconds(300)));
        DialogOk.Content = Ui.Loc.T("Done");
        DialogCancel.Visibility = Visibility.Collapsed;
        DialogButtons.Visibility = Visibility.Visible;
        _dialog?.TrySetResult(false);
        _dialog = new TaskCompletionSource<bool>();
        return _dialog.Task;
    }

    void HideSteps()
    {
        _etaTimer.Stop();
        DialogStepIcon.Visibility = DialogSteps.Visibility = Visibility.Collapsed;
        if (_steps != null) foreach (var st in _steps) st.PropertyChanged -= Step_Changed;
        _steps = null;
    }

    public void SetBusyStatus(string status) => DialogBusyText.Text = Ui.Loc.T(status);

    public void SetDialogText(string text)
    {
        DialogText.Text = text;
        DialogText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    int _overlayVersion; // a fade-out only hides the overlay if nothing was shown since it started

    void ShowOverlay()
    {
        _overlayVersion++;
        bool wasShown = Overlay.Visibility == Visibility.Visible && Overlay.Opacity > 0.95;
        Overlay.Visibility = Visibility.Visible;
        // Always fade in from where it is: the previous question may still be fading out (confirm, then a progress
        // screen right away), and that fade used to finish and hide the progress screen while the work went on.
        Overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(wasShown ? 1 : 180)));
        if (wasShown) return;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
        DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }
    public void HideOverlay()
    {
        int version = ++_overlayVersion;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) =>
        {
            if (version == _overlayVersion) Overlay.Visibility = Visibility.Collapsed;
        };
        Overlay.BeginAnimation(OpacityProperty, fade);
    }

    void DialogOk_Click(object sender, RoutedEventArgs e) { HideOverlay(); _dialog?.TrySetResult(true); }
    void DialogCancel_Click(object sender, RoutedEventArgs e) { HideOverlay(); _dialog?.TrySetResult(false); }

    // ───────── Toast ─────────

    public async void Toast(string text, ToastKind kind = ToastKind.Success)
    {
        int v = ++_toastVersion;
        ToastText.Text = Ui.Loc.T(text);
        (ToastIcon.Text, ToastIcon.Foreground) = kind switch
        {
            ToastKind.Warning => ("", (Brush)FindResource("WarnBrush")),
            ToastKind.Info => ("", (Brush)FindResource("InfoBrush")),
            _ => ("", (Brush)FindResource("GoodBrush")),
        };
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        ToastT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        await Task.Delay(kind == ToastKind.Warning ? 5000 : 3200);
        if (v != _toastVersion) return;
        ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
        ToastT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, TimeSpan.FromMilliseconds(250)));
    }

    // ───────── Helpers ─────────

    /// <summary>Handles a Finding/ActionKey: "page:x", "settings:ms-settings:...", "url:https://...".</summary>
    public void RunAction(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (key.StartsWith("page:")) { Navigate(key[5..]); return; }
        if (key.StartsWith("refresh:")) { _ = DisplayFixer.Apply(key[8..]); return; }
        var target = key.StartsWith("settings:") ? key[9..] : key.StartsWith("url:") ? key[4..] : key;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { _ = Alert("Couldn't open that", ex.Message); }
    }

    // ───────── Language ─────────

    void Lang_Click(object sender, RoutedEventArgs e)
    {
        LangList.Children.Clear();
        var choice = TweakEngine.State.Settings.Language ?? "auto";
        var auto = Ui.Loc.Languages.First(l => l.Code == Ui.Loc.FromWindows()).Name;
        var options = new List<(string Code, string Name)> { ("auto", $"{Ui.Loc.T("Match Windows")} ({auto})") };
        options.AddRange(Ui.Loc.Languages);
        foreach (var (code, name) in options)
        {
            var b = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 1, 0, 1),
                Content = (code == choice ? "✓  " : "     ") + name,
                Tag = code,
            };
            b.Click += LangPick_Click;
            LangList.Children.Add(b);
        }
        LangPopup.IsOpen = true;
    }

    async void LangPick_Click(object sender, RoutedEventArgs e)
    {
        LangPopup.IsOpen = false;
        var code = (string)((FrameworkElement)sender).Tag;
        if (code == (TweakEngine.State.Settings.Language ?? "auto")) return;
        TweakEngine.State.Settings.Language = code;
        TweakEngine.Save();
        var target = code == "auto" ? Ui.Loc.FromWindows() : code;
        if (target == Ui.Loc.Current) return;
        if (await Confirm("Restart Pulse to switch language?", "Pulse shows in this language after a restart.", "Restart now", "Cancel"))
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (exe != null) Process.Start(new ProcessStartInfo(exe, "--restarted") { UseShellExecute = true });
            }
            catch { return; }
            QuitApp();
        }
    }

    // ───────── Caption ─────────

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ───────── Tray (Clips keeps running with the window closed) ─────────

    bool _quitting, _trayHintShown;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var clips = TweakEngine.State.Clips;
        if (!_quitting && clips is { Enabled: true, KeepRunningInTray: true })
        {
            e.Cancel = true;
            Hide();
            Ui.TrayIcon.Show();
            GoLean(dropScreens: true);
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                Ui.ClipToast.Show(Ui.Loc.T("Pulse is still clipping"), Ui.Loc.T("It's by the clock. Right-click it to quit."), true);
            }
            return;
        }
        base.OnClosing(e);
        Ui.ClipsService.Shutdown();
        Ui.MacroService.Shutdown();
        Ui.GameSessionService.Shutdown();
        Ui.TrayIcon.Hide();
        Application.Current.Shutdown();
    }

    /// <summary>Brings the window back from the tray (or from a second launch of Pulse).</summary>
    bool _clippedWhileAway;

    void OpenClipsIfClipped()
    {
        if (!_clippedWhileAway) return;
        _clippedWhileAway = false;
        if (TweakEngine.State.Clips is not { OpenClipsAfterClip: true }) return;
        if (Overlay.Visibility == Visibility.Visible) return; // a question or a task is on screen: don't pull the page away
        Navigate("clips", "newest");
    }

    // ───────── Lean mode: hidden or minimized next to a game, Pulse should cost (almost) nothing ─────────

    bool _lean;

    /// <summary>
    /// Stops everything only the window needs (live stats, animations) and hands memory back to Windows. With
    /// <paramref name="dropScreens"/> (hidden in the tray) the screens themselves are let go too and rebuilt when Pulse opens.
    /// </summary>
    void GoLean(bool dropScreens)
    {
        _lean = true;
        LiveStats.Updated -= UpdateMini;
        LiveStats.Stop();
        Glow.BeginAnimation(OpacityProperty, null);
        if (dropScreens && Overlay.Visibility != Visibility.Visible) // nothing running in a dialog
        {
            PageHost.Content = null;
            foreach (var p in _pages.Values) if (p is IReleasable r) try { r.Release(); } catch { }
            _pages.Clear();
        }
        // The deep memory clean-up pauses Pulse for a moment, which would make a running recording drop frames (the
        // recorder waits on Pulse): only do it when Clips isn't recording.
        // While recording: the gentle version, now and every 2 minutes while Pulse stays hidden.
        Dispatcher.BeginInvoke(ClipsService.State != ClipsState.Recording ? Native.GiveBackMemory : Native.GiveBackMemoryGently,
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        _leanTrim ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMinutes(2), System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            (_, _) => { if (_lean) Native.GiveBackMemoryGently(); }, Dispatcher);
        _leanTrim.Start();
    }

    System.Windows.Threading.DispatcherTimer _leanTrim;

    void LeaveLean()
    {
        if (!_lean) return;
        _lean = false;
        _leanTrim?.Stop();
        LiveStats.Updated += UpdateMini;
        LiveStats.Start();
        Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 0.7, TimeSpan.FromSeconds(5))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        if (PageHost.Content == null)
        {
            var key = NavPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "home";
            Dispatcher.BeginInvoke(() => { if (PageHost.Content == null) ShowPage(key); }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized) GoLean(dropScreens: false);
        else if (IsVisible) LeaveLean();
    }

    public void ShowFromTray()
    {
        LeaveLean();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Ui.TrayIcon.Hide();
    }

    /// <summary>Windows is signing out or shutting down: let the window close instead of hiding in the tray.</summary>
    public void PrepareForExit() => _quitting = true;

    /// <summary>Closes Pulse for real, including Clips.</summary>
    public void QuitApp()
    {
        _quitting = true;
        Close();
    }

    void ShowClipsPill()
    {
        bool rec = ClipsService.State == ClipsState.Recording;
        ClipsPill.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
        ClipsPillGame.Text = ClipsService.Game ?? Loc.T("Your screen");
    }

    void ClipsPill_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Navigate("clips");

    // ───────── New version ─────────

    string _newVersion;
    CancellationTokenSource _updateCancel;

    /// <summary>Checks for a newer Pulse shortly after start and twice a day; shows a card if there is one.</summary>
    DateTime _lastUpdateCheck;
    Func<Task> _checkForUpdate;

    /// <summary>Also checks when Pulse comes to the front (from the tray, or another launch), at most every 30 minutes.</summary>
    void CheckForUpdateSoon()
    {
        if (_checkForUpdate == null || DateTime.UtcNow - _lastUpdateCheck < TimeSpan.FromMinutes(30)) return;
        _ = _checkForUpdate();
    }

    void StartUpdateChecks()
    {
        _checkForUpdate = Check;
        async Task Check()
        {
            _lastUpdateCheck = DateTime.UtcNow;
            var st = TweakEngine.State.Settings;
            if (!st.CheckForUpdates || UpdateBar.Visibility == Visibility.Visible) return;
            var latest = await Updater.LatestVersion();
            if (latest == null || !Updater.IsNewer(latest, Updater.CurrentVersion) || latest == st.SkippedVersion) return;
            _newVersion = latest;
            UpdateTitle.Text = Loc.F("Pulse {0} is out", latest);
            UpdateText.Text = Loc.F("You have {0}. Your settings and clips stay as they are, and Pulse restarts by itself.", Updater.CurrentVersion);
            UpdateBar.Visibility = Visibility.Visible;
        }
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(12) };
        t.Tick += async (_, _) => await Check();
        t.Start();
        _ = Task.Delay(8000).ContinueWith(_ => Dispatcher.InvokeAsync(Check));
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        // Pressing it again does nothing while it downloads (it used to cancel, so a second press undid the first).
        if (_newVersion == null || _updateCancel != null) return;
        _updateCancel = new CancellationTokenSource();
        UpdateBtn.IsEnabled = false;
        UpdateBtn.Content = Loc.T("Downloading…");
        UpdateLaterBtn.Visibility = UpdateSkipBtn.Visibility = Visibility.Collapsed;
        UpdateCancelBtn.Visibility = Visibility.Visible;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateText.Text = Loc.T("Downloading the update…");
        var progress = new Progress<(long Got, long Total)>(p =>
        {
            if (p.Total > 0) UpdateFill.Width = ((FrameworkElement)UpdateFill.Parent).ActualWidth * p.Got / p.Total;
            UpdateBtn.Content = p.Total > 0 ? Loc.F("Downloading… {0}%", (int)(p.Got * 100 / p.Total)) : Loc.T("Downloading…");
            UpdateText.Text = p.Total > 0 ? Loc.F("{0} of {1}", Format.Bytes(p.Got), Format.Bytes(p.Total)) : Format.Bytes(p.Got);
        });
        var error = await Updater.Install(_newVersion, progress, _updateCancel.Token);
        _updateCancel = null;
        UpdateCancelBtn.Visibility = Visibility.Collapsed;
        UpdateLaterBtn.Visibility = UpdateSkipBtn.Visibility = Visibility.Visible;
        UpdateBtn.IsEnabled = true;
        if (error == null)
        {
            UpdateText.Text = Loc.T("Restarting into the new version…");
            PrepareForExit();
            QuitApp();
            Application.Current.Shutdown();
            return;
        }
        UpdateText.Text = Loc.T(error);
        UpdateBtn.Content = Loc.T("Try again");
        UpdateLaterBtn.IsEnabled = UpdateSkipBtn.IsEnabled = true;
        UpdateProgress.Visibility = Visibility.Collapsed;
    }

    void UpdateLater_Click(object sender, RoutedEventArgs e) => UpdateBar.Visibility = Visibility.Collapsed;

    void UpdateCancel_Click(object sender, RoutedEventArgs e) => _updateCancel?.Cancel();

    void UpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        TweakEngine.State.Settings.SkippedVersion = _newVersion ?? "";
        TweakEngine.Save();
        UpdateBar.Visibility = Visibility.Collapsed;
    }
}
