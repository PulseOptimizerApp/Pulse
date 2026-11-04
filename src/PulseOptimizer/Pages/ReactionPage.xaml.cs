using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Reaction;
using Pulse.Ui;

namespace Pulse.Pages;

public partial class ReactionPage : UserControl, IPage
{
    static ReactionSettings S => TweakEngine.State.Reaction ??= new ReactionSettings();

    static readonly (ReactionMode Mode, string Label, string Text)[] Modes =
    {
        (ReactionMode.Simple, "Reaction", "Wait for the screen to light up, then click as fast as you can. Clicking early counts as a mistake."),
        (ReactionMode.Choice, "Left or right", "One side lights up: click that side, or press A for left and D for right. Reacting and choosing at once, like most games."),
        (ReactionMode.GoNoGo, "Don't click on red", "Click when the circle is cyan, hold still when it's red. Trains not shooting at the wrong thing. Gets harder as you get it right."),
        (ReactionMode.Flick, "Flick", "A target appears somewhere: move to it and click it. Targets get smaller as you get faster."),
    };

    static readonly Brush Go = (Brush)new BrushConverter().ConvertFrom("#22D3EE");
    static readonly Brush NoGo = (Brush)new BrushConverter().ConvertFrom("#F43F5E");
    static readonly Brush Lit = (Brush)new BrushConverter().ConvertFrom("#4022D3EE");

    enum Phase { Idle, Waiting, Signal, Feedback }

    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly DispatcherTimer _timer = new();
    ReactionMode _mode = ReactionMode.Simple;
    ReactionTrainer _trainer;
    ReactionTrial _trial;
    Phase _phase = Phase.Idle;
    double _shownAt = -1;
    Ellipse _target;

    public ReactionPage()
    {
        InitializeComponent();
        _timer.Tick += Timer_Tick;
        PreviewKeyDown += Page_KeyDown;
        Unloaded += (_, _) => { _timer.Stop(); Reset(); };
        foreach (var (mode, label, _) in Modes)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = Loc.T(label), GroupName = "reaction-mode", Tag = mode, Margin = new Thickness(0, 3, 6, 3), IsChecked = mode == _mode };
            rb.Checked += (s, _) => { _mode = (ReactionMode)((FrameworkElement)s).Tag; Reset(); };
            ModeChips.Children.Add(rb);
        }
        Reset();
    }

    public void OnShown(object parameter) => Focus();

    double Now => _clock.Elapsed.TotalMilliseconds;

    void Reset()
    {
        _timer.Stop();
        _trainer = null;
        _phase = Phase.Idle;
        ModeText.Text = Loc.T(Modes.First(m => m.Mode == _mode).Text);
        ClearField();
        BigText.Text = Loc.T("Click to start");
        SmallText.Text = Loc.F("{0} warm-up tries, then {1} that count.", ReactionTrainer.WarmUp, ReactionTrainer.TrialsPerSession);
        ProgressText.Text = "";
        Sides.Visibility = _mode == ReactionMode.Choice ? Visibility.Visible : Visibility.Collapsed;
        ShowHistory();
    }

    void ClearField()
    {
        Field.Children.Clear();
        _target = null;
        LeftSide.Background = RightSide.Background = Brushes.Transparent;
        Glow.BeginAnimation(OpacityProperty, null);
        Glow.Opacity = 0;
    }

    // ───────── running a session ─────────

    void Start()
    {
        _trainer = new ReactionTrainer(_mode);
        ResultCard.Visibility = Visibility.Collapsed;
        NextTrial();
    }

    void NextTrial()
    {
        if (_trainer.Finished) { Finish(); return; }
        ClearField();
        _trial = _trainer.Next();
        _phase = Phase.Waiting;
        _shownAt = -1;
        BigText.Text = Loc.T(_mode == ReactionMode.Flick ? "Get ready…" : "Wait…");
        BigText.Foreground = (Brush)FindResource("TextDimBrush");
        SmallText.Text = "";
        ProgressText.Text = _trainer.InWarmUp
            ? Loc.F("Warm-up {0} of {1}", _trainer.Done + 1, ReactionTrainer.WarmUp)
            : Loc.F("Try {0} of {1}", _trainer.Done - ReactionTrainer.WarmUp + 1, ReactionTrainer.TrialsPerSession);
        Arm(_trial.WaitMs);
    }

    void Arm(double ms)
    {
        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(ms);
        _timer.Start();
    }

    void Timer_Tick(object sender, EventArgs e)
    {
        _timer.Stop();
        if (_phase == Phase.Waiting) ShowSignal();
        else if (_phase == Phase.Signal) Respond(pressed: false);   // nothing pressed in time
        else if (_phase == Phase.Feedback) NextTrial();
    }

    void ShowSignal()
    {
        _phase = Phase.Signal;
        BigText.Text = "";
        switch (_mode)
        {
            case ReactionMode.Simple:
                Glow.Opacity = 1;
                BigText.Text = Loc.T("Click!");
                BigText.Foreground = Brushes.White;
                break;
            case ReactionMode.Choice:
                (_trial.Side < 0 ? LeftSide : RightSide).Background = Lit;
                break;
            case ReactionMode.GoNoGo:
                AddCircle(0.5, 0.5, 120, _trial.Go ? Go : NoGo);
                break;
            case ReactionMode.Flick:
                _target = AddCircle(_trial.X, _trial.Y, _trial.Size, Go);
                break;
        }
        // Time from the frame the signal is actually drawn, not from when it was asked for.
        CompositionTarget.Rendering += Stamp;
        double limit = _mode switch { ReactionMode.GoNoGo => _trial.WindowMs, ReactionMode.Flick => 2000, _ => 2500 };
        Arm(limit);
    }

    void Stamp(object sender, EventArgs e)
    {
        CompositionTarget.Rendering -= Stamp;
        _shownAt = Now;
    }

    Ellipse AddCircle(double fx, double fy, double size, Brush fill)
    {
        double w = Arena.ActualWidth, h = Arena.ActualHeight;
        var c = new Ellipse { Width = size, Height = size, Fill = fill, Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = ((SolidColorBrush)fill).Color, BlurRadius = 30, ShadowDepth = 0, Opacity = 0.8 } };
        Canvas.SetLeft(c, fx * w - size / 2);
        Canvas.SetTop(c, fy * h - size / 2);
        Field.Children.Add(c);
        return c;
    }

    void Arena_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (_phase == Phase.Idle) { Start(); return; }
        int side = e.GetPosition(Arena).X < Arena.ActualWidth / 2 ? -1 : 1;
        if (_mode == ReactionMode.Flick && _phase == Phase.Signal && _target != null)
        {
            var p = e.GetPosition(Field);
            double cx = Canvas.GetLeft(_target) + _target.Width / 2, cy = Canvas.GetTop(_target) + _target.Height / 2;
            if (Math.Pow(p.X - cx, 2) + Math.Pow(p.Y - cy, 2) > Math.Pow(_target.Width / 2 + 4, 2)) return; // missed the target: keep going
        }
        Press(side);
    }

    void Page_KeyDown(object sender, KeyEventArgs e)
    {
        int side = e.Key switch { Key.A or Key.Left => -1, Key.D or Key.Right => 1, Key.Space or Key.Enter => 0, _ => 2 };
        if (side == 2) return;
        e.Handled = true;
        if (_phase == Phase.Idle) { Start(); return; }
        if (_mode == ReactionMode.Flick) return; // flick needs the mouse
        if (_mode == ReactionMode.Choice && side == 0) return;
        Press(side);
    }

    void Press(int side)
    {
        if (_phase == Phase.Waiting) { Respond(pressed: true, early: true); return; }
        if (_phase == Phase.Signal) Respond(pressed: true, side: side);
    }

    void Respond(bool pressed, int side = 0, bool early = false)
    {
        _timer.Stop();
        CompositionTarget.Rendering -= Stamp;
        double ms = early ? -1 : _shownAt < 0 ? 0 : Now - _shownAt;
        var r = _trainer.Record(_trial, ms, side, pressed);
        _phase = Phase.Feedback;
        ClearField();
        BigText.Foreground = Brushes.White;
        (BigText.Text, SmallText.Text) = r switch
        {
            TrialResult.Hit => (Loc.F("{0} ms", Math.Round(ms)), _trainer.InWarmUp || _trainer.Done <= ReactionTrainer.WarmUp ? Loc.T("Warm-up") : ""),
            TrialResult.TooEarly => (Loc.T("Too early"), Loc.T("Wait for the signal: guessing doesn't train anything.")),
            TrialResult.WrongSide => (Loc.T("Wrong side"), ""),
            TrialResult.ClickedNoGo => (Loc.T("That was red"), Loc.T("Hold still on red.")),
            TrialResult.CorrectlyHeld => (Loc.T("Good hold"), ""),
            _ => (Loc.T("Missed"), ""),
        };
        if (r is TrialResult.TooEarly or TrialResult.WrongSide or TrialResult.ClickedNoGo or TrialResult.Missed) BigText.Foreground = NoGo;
        Arm(r == TrialResult.Hit ? 750 : 1100);
    }

    void Finish()
    {
        _phase = Phase.Idle;
        var s = _trainer.Summary();
        if (s.Trials > 0)
        {
            S.History.Add(s);
            if (S.History.Count > 500) S.History.RemoveRange(0, S.History.Count - 500);
            TweakEngine.Save();
        }
        ClearField();
        BigText.Text = Loc.T("Done");
        BigText.Foreground = Brushes.White;
        SmallText.Text = Loc.T("Click to go again.");
        ProgressText.Text = "";

        ResultCard.Visibility = Visibility.Visible;
        MedianText.Text = s.Trials > 0 ? Loc.F("{0} ms", Math.Round(s.MedianMs)) : "–";
        BestText.Text = s.Trials > 0 ? Loc.F("{0} ms", Math.Round(s.BestMs)) : "–";
        SpreadText.Text = s.Trials > 1 ? Loc.F("±{0} ms", Math.Round(s.SpreadMs / 2)) : "–";
        MistakeText.Text = s.Mistakes.ToString(CultureInfo.CurrentCulture);
        LevelBox.Visibility = _mode is ReactionMode.GoNoGo or ReactionMode.Flick ? Visibility.Visible : Visibility.Collapsed;
        LevelText.Text = s.Level.ToString(CultureInfo.CurrentCulture);
        CompareText.Text = s.Trials > 0 ? Loc.T(ReactionTrainer.Compare(s, S.History)) : Loc.T("No tries counted. Wait for the signal before clicking.");
        _ = CountUp(s.MedianMs);
        ShowHistory();
    }

    async Task CountUp(double target)
    {
        if (target <= 0) return;
        for (int i = 1; i <= 20; i++)
        {
            MedianText.Text = Loc.F("{0} ms", Math.Round(target * (1 - Math.Pow(1 - i / 20.0, 3))));
            await Task.Delay(16);
        }
    }

    void Again_Click(object sender, RoutedEventArgs e) => Start();

    // ───────── progress chart ─────────

    void ShowHistory()
    {
        var list = S.History.Where(h => h.Mode == _mode && h.Trials > 0).OrderBy(h => h.When).TakeLast(30).ToList();
        HistoryCard.Visibility = list.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        if (list.Count < 2) return;
        var recent = list.TakeLast(10).Select(h => h.MedianMs).ToList();
        HistoryText.Text = Loc.F("Last {0} sessions. Lower is faster. The band is your normal day-to-day range.", list.Count);
        Dispatcher.InvokeAsync(() => DrawChart(list, recent), DispatcherPriority.Loaded);
    }

    void DrawChart(List<ReactionSession> list, List<double> recent)
    {
        Chart.Children.Clear();
        double w = Math.Max(Chart.ActualWidth, 300), h = Chart.Height;
        double lo = list.Min(x => x.MedianMs), hi = list.Max(x => x.MedianMs);
        double pad = Math.Max(10, (hi - lo) * 0.2);
        lo -= pad; hi += pad;
        double Y(double v) => h - (v - lo) / (hi - lo) * h;
        double X(int i) => list.Count == 1 ? w / 2 : i * (w - 8) / (list.Count - 1) + 4;

        // Normal range: average of recent sessions ± their spread (at least 4%).
        double avg = recent.Average();
        double sd = recent.Count > 1 ? Math.Sqrt(recent.Sum(v => (v - avg) * (v - avg)) / (recent.Count - 1)) : 0;
        double band = Math.Max(sd, avg * 0.04);
        var rect = new Rectangle { Width = w, Height = Math.Max(2, Y(avg - band) - Y(avg + band)), Fill = (Brush)new BrushConverter().ConvertFrom("#1422D3EE"), RadiusX = 6, RadiusY = 6 };
        Canvas.SetTop(rect, Y(avg + band));
        Chart.Children.Add(rect);

        var line = new Polyline { Stroke = (Brush)FindResource("AccentGradient"), StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round };
        for (int i = 0; i < list.Count; i++) line.Points.Add(new Point(X(i), Y(list[i].MedianMs)));
        Chart.Children.Add(line);
        for (int i = 0; i < list.Count; i++)
        {
            var dot = new Ellipse { Width = 7, Height = 7, Fill = Brushes.White, ToolTip = $"{list[i].When:d} · {Math.Round(list[i].MedianMs)} ms" };
            Canvas.SetLeft(dot, X(i) - 3.5);
            Canvas.SetTop(dot, Y(list[i].MedianMs) - 3.5);
            Chart.Children.Add(dot);
        }
    }
}
