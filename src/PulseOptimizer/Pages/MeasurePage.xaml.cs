using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pulse.Core;
using Pulse.Core.Measurement;
using Pulse.Ui;

namespace Pulse.Pages;

public record RunRow(string Label, string Side, string Avg, string Low, string Latency);
public record ResultRow(string Metric, string Numbers, string Explanation);

public partial class MeasurePage : UserControl, IPage
{
    AbTest _test;
    CancellationTokenSource _cts;
    bool _busy;

    readonly System.Windows.Threading.DispatcherTimer _scan = new() { Interval = TimeSpan.FromSeconds(3) };
    List<string> _installFolders;
    int _runs = 5, _secs = 60;

    public MeasurePage()
    {
        InitializeComponent();
        TweakPick.ItemsSource = TweakCatalog.All.Where(t => t.Proof is ProofMetric.FrameTime or ProofMetric.Latency).ToList();
        _scan.Tick += (_, _) => RefreshGames();
        Loaded += (_, _) => { if (_test == null) _scan.Start(); };
        Unloaded += (_, _) => _scan.Stop();
        UpdatePlan();
    }

    public void OnShown(object parameter)
    {
        PresentMonMissing.Visibility = PresentMonCapture.Available ? Visibility.Collapsed : Visibility.Visible;
        if (parameter is Tweak t && TweakPick.Items.Contains(t)) TweakPick.SelectedItem = t;
        if (parameter is AbTest pending) { _test = pending; ShowRunning(); }
        if (_test == null) RefreshGames();
    }

    // ───────── Setup ─────────

    bool _refreshing;

    async void RefreshGames()
    {
        if (_refreshing || _test != null) return;
        _refreshing = true;
        try
        {
            var games = await Task.Run(() =>
            {
                _installFolders ??= PcDiagnostics.GameInstallFolders();
                return GameDetector.Find(_installFolders);
            });
            var keep = (GamePick.SelectedItem as GameEntry)?.Exe;
            var current = (GamePick.ItemsSource as List<GameEntry>)?.Select(g => g.Exe) ?? Enumerable.Empty<string>();
            if (!games.Select(g => g.Exe).SequenceEqual(current))
            {
                GamePick.ItemsSource = games;
                GamePick.SelectedItem = games.FirstOrDefault(g => g.Exe == keep) ?? games.FirstOrDefault();
            }
            bool any = games.Count > 0;
            GamePick.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            WaitingBox.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            UpdateStart();
        }
        finally { _refreshing = false; }
    }

    void ChangePlan_Click(object sender, RoutedEventArgs e) =>
        PlanChips.Visibility = PlanChips.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        (_runs, _secs) = sender == ModeQuick ? (3, 60) : sender == ModeThorough ? (7, 75) : (5, 60);
        UpdatePlan();
    }

    void UpdatePlan()
    {
        if (PlanText == null) return;
        var minutes = (_runs * 2 + 1) * (_secs + 15) / 60.0;
        PlanText.Text = $"{_runs} runs with the tweak off and {_runs} on, {_secs} s each (about {Math.Ceiling(minutes)} min)";
    }

    void Pick_Changed(object sender, SelectionChangedEventArgs e) => UpdateStart();

    void UpdateStart() =>
        StartBtn.IsEnabled = TweakPick.SelectedItem != null && GamePick.SelectedItem != null && PresentMonCapture.Available;

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        var t = (Tweak)TweakPick.SelectedItem;
        var g = (GameEntry)GamePick.SelectedItem;
        int secs = _secs, runs = _runs;
        var total = (runs * 2 + 1) * (secs + 15) / 60.0;
        if (!await MainWindow.Instance.Confirm($"Test “{t.Title}” in {g.Title}?",
                $"{runs * 2 + 1} runs of {secs} seconds, about {total:0} minutes including switching back and forth. " +
                (t.HasRestartNote ? "This tweak needs a restart, so Pulse records all 'off' runs first, then asks you to restart, then records the 'on' runs. Pulse picks the test back up after the restart."
                                  : "Pulse turns the tweak off and on between runs automatically."),
                "Start"))
            return;
        if (!t.GateStatus.Met && !await MainWindow.Instance.Confirm("This PC may not benefit", t.GateStatus.Why + "\n\nTest it anyway?", "Test anyway"))
            return;
        await TweakRunner.EnsureRestorePoint();
        _test = AbTest.Create(t, g.Exe, secs, runs);
        ShowRunning();
    }

    // ───────── Running ─────────

    void ShowRunning()
    {
        _scan.Stop();
        SetupCard.Visibility = Visibility.Collapsed;
        ResultCard.Visibility = Visibility.Collapsed;
        RunCard.Visibility = Visibility.Visible;
        Anim.Play(RunCard);
        Render();
    }

    void Render()
    {
        var t = _test.Tweak;
        var game = Path.GetFileNameWithoutExtension(_test.ProcessName);
        RunTitle.Text = $"Testing “{t.Title}” in {game}";
        RunSubtitle.Text = _test.Alternating
            ? $"{_test.RunsPerSide} runs with the tweak off and {_test.RunsPerSide} with it on, alternating. {_test.Seconds} s each."
            : $"{_test.RunsPerSide} runs off, then a restart, then {_test.RunsPerSide} runs on. {_test.Seconds} s each.";
        Anim.Bar(RunProgressBar, (double)_test.NextIndex / _test.Plan.Count);

        RunsList.ItemsSource = _test.Plan.Where(p => p.Result != null).Select(p => new RunRow(
            p.WarmUp ? "Warm-up" : $"Run {_test.Plan.Where(x => !x.WarmUp).ToList().IndexOf(p) + 1}",
            p.Side == Side.Before ? "Tweak off" : "Tweak on",
            $"{p.Result.AvgFps:0.0} FPS avg",
            $"{p.Result.Low1Fps:0.0} FPS 1% low",
            p.Result.LatencyMedianMs is double l ? $"{l:0.0} ms latency" : "")).ToList();

        RecordBtn.Visibility = Visibility.Visible;
        RestartBtn.Visibility = Visibility.Collapsed;
        FinishBtn.Visibility = Visibility.Collapsed;
        CountText.Text = "";
        CountRing.Value = 0;

        if (_test.Done)
        {
            StepTitle.Text = "All runs recorded";
            StepText.Text = "See whether the tweak made a measurable difference.";
            RecordBtn.Visibility = Visibility.Collapsed;
            FinishBtn.Visibility = Visibility.Visible;
            return;
        }
        if (_test.NeedsRestartBeforeNext)
        {
            StepTitle.Text = "Restart needed";
            StepText.Text = "All 'off' runs are done. Restart now: Pulse turns the tweak on first, then picks this test up when you reopen it after the restart.";
            RecordBtn.Visibility = Visibility.Collapsed;
            RestartBtn.Visibility = Visibility.Visible;
            return;
        }
        var n = _test.Next;
        int measuredIndex = _test.MeasuredRunsDone + 1;
        StepTitle.Text = n.WarmUp
            ? "Warm-up run (not counted) · tweak off"
            : $"Run {measuredIndex} of {_test.MeasuredRunsTotal} · tweak {(n.Side == Side.Before ? "OFF" : "ON")}";
        StepText.Text = $"Press Record, then switch to {game} within 10 seconds and play. Pulse records {_test.Seconds} seconds and beeps when done.";
    }

    async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _test == null) return;
        if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_test.ProcessName)).Length == 0)
        {
            await MainWindow.Instance.Alert("Game isn't running", $"Start {_test.ProcessName} again, then press Record.");
            return;
        }
        _busy = true;
        RecordBtn.IsEnabled = false;
        _cts = new CancellationTokenSource();
        try
        {
            // Put the tweak in the right state before the countdown, so the switch doesn't happen mid-game.
            await Task.Run(() => _test.PrepareNext());

            for (int i = 10; i > 0 && !_cts.IsCancellationRequested; i--)
            {
                StepText.Text = "Switch to your game now…";
                CountText.Text = i.ToString();
                CountRing.AnimateTo((10 - i + 1) * 10, 900);
                await Task.Delay(1000);
            }
            if (_cts.IsCancellationRequested) return;

            CountText.Text = "●";
            StepText.Text = "Recording… keep playing your scene.";
            CountRing.Value = 0;
            CountRing.AnimateTo(100, _test.Seconds * 1000);
            var token = _cts.Token;
            var cap = await Task.Run(() => _test.RunNext(token));
            System.Media.SystemSounds.Asterisk.Play();
            if (!cap.Ok && !token.IsCancellationRequested)
                await MainWindow.Instance.Alert("That run didn't record", cap.Error + "\n\nThe run wasn't counted; you can try it again.");
        }
        catch (Exception ex)
        {
            await MainWindow.Instance.Alert("That run didn't record", ex.Message);
        }
        finally
        {
            _busy = false;
            RecordBtn.IsEnabled = true;
            if (_test != null)
            {
                Render();
                if (_test.Done) ShowResult(_test.Finish());
            }
        }
    }

    async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (!await MainWindow.Instance.Confirm("Restart now?", "Save anything you have open first. Pulse will turn the tweak on and restart in a few seconds.", "Restart"))
            return;
        await Task.Run(() => { _test.PrepareNext(); _test.MarkRestartRequested(); });
        Process.Start(new ProcessStartInfo("shutdown", "/r /t 5 /c \"Pulse Optimizer: restarting to finish a measurement\"") { UseShellExecute = false, CreateNoWindow = true });
    }

    void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (_test == null) return;
        ShowResult(_test.Finish());
    }

    async void CancelTest_Click(object sender, RoutedEventArgs e)
    {
        if (!await MainWindow.Instance.Confirm("Cancel this test?", "Runs recorded so far are discarded and the tweak goes back to how it was before the test.", "Cancel test", "Keep testing"))
            return;
        _cts?.Cancel();
        var t = _test.Tweak;
        bool wasOn = _test.WasOnAtStart;
        await Task.Run(() =>
        {
            bool on = TweakEngine.AppliedByPulse(t);
            if (wasOn && !on) TweakEngine.Apply(t);
            if (!wasOn && on) TweakEngine.Revert(t);
        });
        t.IsOn = wasOn;
        AbTest.Abandon();
        _test = null;
        RunCard.Visibility = Visibility.Collapsed;
        SetupCard.Visibility = Visibility.Visible;
        RefreshGames();
        _scan.Start();
    }

    // ───────── Result ─────────

    Tweak _resultTweak;

    void ShowResult(MeasurementRecord rec)
    {
        _resultTweak = _test?.Tweak ?? TweakCatalog.All.First(x => x.Id == rec.TweakId);
        _test = null;
        RunCard.Visibility = Visibility.Collapsed;
        ResultCard.Visibility = Visibility.Visible;
        Anim.Play(ResultCard);

        var (label, fg, bg) = rec.Verdict switch
        {
            nameof(Verdict.Helped) => ("Helped", Palette.Good, Palette.GoodSoft),
            nameof(Verdict.Hurt) => ("Hurt", Palette.Bad, Palette.BadSoft),
            nameof(Verdict.NotEnoughData) => ("Not enough data", Palette.Info, Palette.InfoSoft),
            _ => ("No measurable change", Palette.Warn, Palette.WarnSoft),
        };
        ResultPill.Background = bg;
        ResultPillText.Foreground = fg;
        ResultPillText.Text = label;
        ResultTitle.Text = $"{_resultTweak.Title} in {rec.Game}";

        ResultList.ItemsSource = rec.Comparisons.Where(c => c.Verdict != Verdict.NotEnoughData).Select(c => new ResultRow(
            c.Metric,
            $"Off: {c.BeforeMedian:0.0}  →  On: {c.AfterMedian:0.0}   (95% range of the change: {c.CiLow:+0.0;-0.0} to {c.CiHigh:+0.0;-0.0}, positive = better)",
            c.Explanation)).ToList();

        ResultNote.Text = rec.Verdict switch
        {
            nameof(Verdict.Helped) => "Keep it: it measurably helped in this game on this PC.",
            nameof(Verdict.Hurt) => "Pulse recommends undoing it.",
            _ => "It didn't make a measurable difference here. Undo it to keep your PC closer to Windows' defaults, or keep it if you prefer.",
        };
        KeepBtn.Style = (Style)FindResource(rec.Verdict == nameof(Verdict.Helped) ? "PrimaryButton" : "GhostButton");
        UndoBtn.Style = (Style)FindResource(rec.Verdict == nameof(Verdict.Helped) ? "GhostButton" : "PrimaryButton");
    }

    void Keep_Click(object sender, RoutedEventArgs e)
    {
        ResultCard.Visibility = Visibility.Collapsed;
        SetupCard.Visibility = Visibility.Visible;
        _scan.Start();
        MainWindow.Instance.Toast($"Kept “{_resultTweak?.Title}”.");
    }

    async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_resultTweak != null && _resultTweak.IsOn) await TweakRunner.Set(_resultTweak, false);
        ResultCard.Visibility = Visibility.Collapsed;
        SetupCard.Visibility = Visibility.Visible;
        _scan.Start();
    }

    // ───────── Polling ─────────

    async void Pad_Click(object sender, RoutedEventArgs e)
    {
        var idx = ControllerPollingMeter.FirstConnected();
        if (idx == null)
        {
            await MainWindow.Instance.Alert("No controller found", "Connect an Xbox controller (or a PlayStation controller through Steam Input or DS4Windows) and try again.");
            return;
        }
        PadBtn.IsEnabled = false;
        PadResult.Text = "Move the sticks…";
        try
        {
            var r = await Task.Run(() => ControllerPollingMeter.Measure(idx.Value, TimeSpan.FromSeconds(5)));
            ShowPolling(r, PadResult, PadDetail);
        }
        finally { PadBtn.IsEnabled = true; }
    }

    async void Mouse_Click(object sender, RoutedEventArgs e)
    {
        MouseBtn.IsEnabled = false;
        MouseResult.Text = "Move the mouse…";
        try
        {
            using var meter = new MousePollingMeter();
            meter.Start();
            await Task.Delay(5000);
            ShowPolling(meter.Stop(), MouseResult, MouseDetail);
        }
        catch (Exception ex) { MouseResult.Text = "—"; MouseDetail.Text = ex.Message; }
        finally { MouseBtn.IsEnabled = true; }
    }

    static void ShowPolling(PollingResult r, TextBlock big, TextBlock detail)
    {
        if (r.RateHz <= 0) { big.Text = "—"; detail.Text = r.Note; return; }
        big.Text = $"{r.RateHz:0} Hz";
        var advice = r.Device.StartsWith("Mouse")
            ? (r.RateHz < 400 ? " That's low for gaming: check your mouse software for a 1000 Hz polling option, and use a USB port on the back of the PC." : "")
            : (r.RateHz < 200 ? " Wired USB, a port on the back of the PC, and closing extra controller software (two layers like Steam Input + DS4Windows) usually help." : "");
        detail.Text = $"Steadiness ±{r.JitterMs:0.00} ms.{advice} {r.Note}".Trim();
    }

    // ───────── Background CPU & boot ─────────

    async void Cpu_Click(object sender, RoutedEventArgs e)
    {
        CpuBtn.IsEnabled = false;
        CpuResult.Text = "60 s…";
        CpuTop.Text = "Don't touch the PC until it finishes.";
        try
        {
            var s = await Task.Run(() => BackgroundCpuMeter.Measure(TimeSpan.FromSeconds(60)));
            CpuResult.Text = $"{s.AveragePercent:0.0}%";
            var top = s.Top.Where(x => x.Percent >= 0.5).Take(3).Select(x => $"{x.Process} {x.Percent:0.0}%").ToList();
            CpuTop.Text = top.Count == 0 ? "Nothing busy in the background." : "Busiest: " + string.Join(", ", top);
        }
        finally { CpuBtn.IsEnabled = true; }
    }

    async void Boot_Click(object sender, RoutedEventArgs e)
    {
        BootBtn.IsEnabled = false;
        try
        {
            var boots = await Task.Run(() => BootTimeMeter.LastBoots());
            if (boots.Count == 0)
            {
                BootResult.Text = "—";
                BootList.Text = "Windows hasn't recorded boot times on this PC (the Diagnostics-Performance log may be off).";
                return;
            }
            BootResult.Text = $"{boots.Select(b => b.Seconds).OrderBy(x => x).ElementAt(boots.Count / 2):0.0} s";
            BootList.Text = $"Typical of your last {boots.Count} starts. Longest: {boots.Max(b => b.Seconds):0.0} s.";
        }
        finally { BootBtn.IsEnabled = true; }
    }
}
