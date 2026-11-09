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
        (ReactionMode.Peek, "Peek", "An enemy swings out from behind a wall. Click their head before they hide again. Reaction, spotting and aim together, like a real duel. Gets faster and smaller as you improve."),
        (ReactionMode.FriendOrFoe, "Shoot or hold", "Enemies (red) and teammates (teal) peek. Shoot only enemies. Trains deciding fast without shooting the wrong thing, which is where most real reaction time goes."),
        (ReactionMode.Crosshair, "Crosshair placement", "Hold the angle you're told, at head height, before the peek. You're scored on how far your crosshair had to move: pros aren't faster clickers, they're already aimed."),
        (ReactionMode.Simple, "Reaction", "Wait for the screen to light up, then click as fast as you can. Clicking early counts as a mistake."),
        (ReactionMode.Choice, "Left or right", "One side lights up: click that side, or press A for left and D for right. Reacting and choosing at once, like most games."),
        (ReactionMode.GoNoGo, "Don't click on red", "Click when the circle is white, hold still when it's red. Trains not shooting at the wrong thing. Gets harder as you get it right."),
        (ReactionMode.Flick, "Flick", "A target appears somewhere: move to it and click it. Targets get smaller as you get faster."),
        (ReactionMode.Benchmark, "Benchmark", "The same 16 peeks every time, with some teammates mixed in. It never gets easier or harder, so take it once a week: its trend is your real progress."),
    };

    static string Name(ReactionMode m) => Loc.T(Modes.First(x => x.Mode == m).Label);

    static readonly Brush Enemy = Frozen("#FF4655");
    static readonly Brush Teammate = Frozen("#4FD1C5");
    static readonly Brush FigureFill = Frozen("#262A33");
    static readonly Brush WallFill = Frozen(new LinearGradientBrush(Color.FromRgb(0x30, 0x35, 0x40), Color.FromRgb(0x22, 0x26, 0x2E), 90));
    static readonly Brush WallEdge = Frozen("#4A5263");
    static readonly Brush WallLine = Frozen("#14FFFFFF");
    static readonly Brush Lane = Frozen(new LinearGradientBrush(Color.FromRgb(0x1C, 0x20, 0x29), Color.FromRgb(0x15, 0x18, 0x1E), 90));
    static readonly Brush FloorFill = Frozen("#121418");
    static readonly Brush Guide = Frozen("#40F2EFE8");
    static Brush Frozen(string hex) { var b = (Brush)new BrushConverter().ConvertFrom(hex); b.Freeze(); return b; }
    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    static readonly Brush Go = (Brush)new BrushConverter().ConvertFrom("#F2EFE8");
    static readonly Brush NoGo = (Brush)new BrushConverter().ConvertFrom("#F43F5E");
    static readonly Brush Lit = (Brush)new BrushConverter().ConvertFrom("#40F2EFE8");

    enum Phase { Idle, Waiting, Signal, Feedback }

    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly DispatcherTimer _timer = new();
    ReactionMode _mode = ReactionMode.Simple;
    ReactionTrainer _trainer;
    ReactionTrial _trial;
    Phase _phase = Phase.Idle;
    double _shownAt = -1;
    Ellipse _target;

    // Peek drills
    PeekTrainer _peek;
    PeekTrial _pt;
    PeekScene _scene;
    Canvas _figLayer;
    Ellipse _head;
    System.Windows.Shapes.Path _body;
    double _offset = -1;
    double _startLevel;

    // Daily plan: the drills left today, or null when not running the plan.
    List<ReactionMode> _plan;
    int _planIndex;
    bool _switching;

    public ReactionPage()
    {
        InitializeComponent();
        _timer.Tick += Timer_Tick;
        PreviewKeyDown += Page_KeyDown;
        _ = Task.Run(() => { var hz = ScreenHz(); Dispatcher.Invoke(() => _hz = hz); });
        Unloaded += (_, _) => { _timer.Stop(); StopFrames(); _plan = null; Reset(); };
        foreach (var (mode, label, _) in Modes)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = Loc.T(label), GroupName = "reaction-mode", Tag = mode, Margin = new Thickness(0, 3, 6, 3), IsChecked = mode == _mode };
            rb.Checked += (s, _) => { _mode = (ReactionMode)((FrameworkElement)s).Tag; if (!_switching) _plan = null; Reset(); };
            ModeChips.Children.Add(rb);
        }
        _mode = ReactionMode.Peek;
        ((RadioButton)ModeChips.Children[0]).IsChecked = true;
        Reset();
    }

    public void OnShown(object parameter) { Focus(); ShowPlan(); }

    bool IsPeek => PeekTrainer.IsPeekDrill(_mode);

    double Now => _clock.Elapsed.TotalMilliseconds;

    void Reset()
    {
        _timer.Stop();
        StopFrames();
        _trainer = null;
        _peek = null;
        _phase = Phase.Idle;
        ModeText.Text = Loc.T(Modes.First(m => m.Mode == _mode).Text);
        ClearField();
        BigText.Foreground = Brushes.White;
        BigText.Text = Loc.T("Click to start");
        SmallText.Text = IsPeek
            ? Loc.F("{0} peeks, about a minute. Use your mouse.", _mode == ReactionMode.Benchmark ? PeekTrainer.BenchmarkTrials : PeekTrainer.TrialsPerSession)
            : Loc.F("{0} tries, about 20 seconds.", ReactionTrainer.TrialsPerSession);
        if (_plan != null)
        {
            BigText.Text = Loc.F("Next: {0}", Name(_mode));
            SmallText.Text = Loc.F("Drill {0} of {1} · click when you're ready", _planIndex + 1, _plan.Count);
        }
        ProgressText.Text = "";
        Sides.Visibility = _mode == ReactionMode.Choice ? Visibility.Visible : Visibility.Collapsed;
        Arena.Cursor = IsPeek ? Cursors.Cross : Cursors.Hand;
        if (IsPeek) Dispatcher.InvokeAsync(() => { if (_phase == Phase.Idle && IsPeek) DrawScene(); }, DispatcherPriority.Loaded);
        ShowHistory();
        ShowPlan();
    }

    void SelectMode(ReactionMode m)
    {
        _switching = true;
        try
        {
            foreach (RadioButton rb in ModeChips.Children)
                if ((ReactionMode)rb.Tag == m) { if (rb.IsChecked == true) { _mode = m; Reset(); } else rb.IsChecked = true; }
        }
        finally { _switching = false; }
    }

    void ClearField()
    {
        Field.Children.Clear();
        _target = null;
        _figLayer = null; _head = null; _body = null;
        LeftSide.Background = RightSide.Background = Brushes.Transparent;
        Glow.BeginAnimation(OpacityProperty, null);
        Glow.Opacity = 0;
    }

    // ───────── running a session ─────────

    void Start()
    {
        if (IsPeek)
        {
            _startLevel = TrainingPlan.StartLevel(S, _mode);
            _peek = new PeekTrainer(_mode, _startLevel);
            _trainer = null;
        }
        else
        {
            _startLevel = S.Levels.TryGetValue(_mode.ToString(), out var lv) ? Math.Max(1, Math.Round(lv) - 1) : 1;
            _trainer = new ReactionTrainer(_mode, startLevel: (int)_startLevel);
            _peek = null;
        }
        ResultCard.Visibility = Visibility.Collapsed;
        PopCard.Visibility = Visibility.Collapsed;
        NextTrial();
    }

    void NextTrial()
    {
        if (_peek != null) { NextPeek(); return; }
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
        if (_peek != null)
        {
            if (_phase == Phase.Waiting) ShowPeek();
            else if (_phase == Phase.Feedback) NextTrial();
            return;
        }
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
        var c = new Ellipse { Width = size, Height = size, Fill = fill };
        Canvas.SetLeft(c, fx * w - size / 2);
        Canvas.SetTop(c, fy * h - size / 2);
        Field.Children.Add(c);
        return c;
    }

    void Arena_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (_phase == Phase.Idle) { Start(); return; }
        if (_peek != null) { PeekClick(e.GetPosition(Field)); return; }
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
        if (_mode == ReactionMode.Flick || _peek != null) return; // these need the mouse
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
            TrialResult.Hit => (Loc.F("{0} ms", Math.Round(ms)), ""),
            TrialResult.TooEarly => (Loc.T("Too early"), Loc.T("Wait for the signal: guessing doesn't train anything.")),
            TrialResult.WrongSide => (Loc.T("Wrong side"), ""),
            TrialResult.ClickedNoGo => (Loc.T("That was red"), Loc.T("Hold still on red.")),
            TrialResult.CorrectlyHeld => (Loc.T("Good hold"), ""),
            _ => (Loc.T("Missed"), ""),
        };
        if (r is TrialResult.TooEarly or TrialResult.WrongSide or TrialResult.ClickedNoGo or TrialResult.Missed) BigText.Foreground = NoGo;
        Arm(r == TrialResult.Hit ? 600 : 1000);
    }

    void Finish()
    {
        _phase = Phase.Idle;
        StopFrames();
        var s = _peek != null ? _peek.Summary() : _trainer.Summary();
        double endLevel = _peek != null ? _peek.Difficulty : _trainer.Level;
        bool adaptive = _mode is not (ReactionMode.Simple or ReactionMode.Choice or ReactionMode.Benchmark);
        if (adaptive) TrainingPlan.SaveLevel(S, _mode, endLevel);
        if (s.Trials > 0 || _peek != null)
        {
            S.History.Add(s);
            if (S.History.Count > 1000) S.History.RemoveRange(0, S.History.Count - 1000);
        }

        // The daily plan: on to the next drill, or done for today.
        bool planDone = false;
        if (_plan != null)
        {
            _planIndex++;
            if (_planIndex >= _plan.Count)
            {
                if (!S.PlanDays.Any(d => d.Date == DateTime.Today)) S.PlanDays.Add(DateTime.Today);
                if (S.PlanDays.Count > 400) S.PlanDays.RemoveRange(0, S.PlanDays.Count - 400);
                _plan = null;
                planDone = true;
            }
        }
        TweakEngine.Save();

        ResultCard.Visibility = Visibility.Visible;
        int hz = _hz;
        var d = ReactionTrainer.SystemDelay(hz);
        double you = Math.Max(0, s.MedianMs - d.Total);
        MedianText.Text = s.Trials > 0 ? Loc.F("{0} ms", Math.Round(you)) : "–";
        BestText.Text = s.Trials > 0 ? Loc.F("{0} ms", Math.Round(Math.Max(0, s.BestMs - d.Total))) : "–";
        YouCol.Width = new GridLength(Math.Max(you, 1), GridUnitType.Star);
        PcCol.Width = new GridLength(Math.Max(d.Total, 1), GridUnitType.Star);
        SplitText.Text = s.Trials > 0
            ? Loc.F("Measured {0} ms: about {1} ms was you, and about {2} ms was your PC, mouse and screen.", Math.Round(s.MedianMs), Math.Round(you), Math.Round(d.Total))
            : "";
        SplitDetail.Text = Loc.F("Screen {0} ms ({1} Hz) · mouse {2} ms · Windows {3} ms", Math.Round(d.Screen, 1), hz, Math.Round(d.Mouse, 1), Math.Round(d.Windows)) +
            (hz < 120 ? " · " + Loc.T("A 144 Hz screen would cut the screen part by more than half.") : "");
        SpreadText.Text = s.Trials > 1 ? Loc.F("±{0} ms", Math.Round(s.SpreadMs / 2)) : "–";
        MistakeText.Text = s.Mistakes.ToString(CultureInfo.CurrentCulture);
        LevelBox.Visibility = adaptive ? Visibility.Visible : Visibility.Collapsed;
        LevelText.Text = _peek != null
            ? Loc.F("{0} → {1}", _startLevel.ToString("0.#", CultureInfo.CurrentCulture), endLevel.ToString("0.#", CultureInfo.CurrentCulture))
            : s.Level.ToString(CultureInfo.CurrentCulture);
        AccuracyBox.Visibility = HeadBox.Visibility = _peek != null ? Visibility.Visible : Visibility.Collapsed;
        AccuracyText.Text = Loc.F("{0}%", Math.Round(s.Accuracy * 100));
        HeadText.Text = s.Trials > 0 ? Loc.F("{0}%", Math.Round(s.HeadshotShare * 100)) : "–";
        OffsetBox.Visibility = _mode == ReactionMode.Crosshair ? Visibility.Visible : Visibility.Collapsed;
        OffsetText.Text = Loc.F("{0} px", Math.Round(s.OffsetPx));

        CompareText.Text = _mode switch
        {
            ReactionMode.Benchmark => Loc.F("Benchmark score {0} (lower is better: your time to the hit, plus a penalty for each mistake).", Math.Round(s.Score)) + " " + ProgressText2(),
            _ when _peek != null => Loc.T(PeekAdvice(s)),
            _ when s.Trials == 0 => Loc.T("No tries counted. Wait for the signal before clicking."),
            _ => Loc.T(ReactionTrainer.Compare(s, S.History)),
        };
        if (s.Trials > 0) _ = CountUp(you);
        _popMeasured = s.Trials > 0 && _mode == ReactionMode.Simple ? s.MedianMs : 0;
        _popYou = you;
        ShowPopulation();

        if (_plan != null) { SelectMode(_plan[_planIndex]); return; }   // shows "Next: …"
        ClearField();
        if (IsPeek) DrawScene();
        BigText.Foreground = Brushes.White;
        BigText.Text = planDone ? Loc.T("Today's training done") : Loc.T("Done");
        SmallText.Text = planDone ? Loc.F("{0}-day streak. Same time tomorrow.", TrainingPlan.Streak(S, DateTime.Today)) : Loc.T("Click to go again.");
        ProgressText.Text = "";
        ShowHistory();
        ShowPlan();
    }

    static string ProgressText2()
    {
        var (f, a) = TrainingPlan.Progress(S);
        return Loc.F(f, a);
    }

    /// <summary>One concrete thing to work on, from what went wrong most this session.</summary>
    string PeekAdvice(ReactionSession s)
    {
        if (_mode == ReactionMode.Crosshair && s.OffsetPx > 60) return "Your crosshair was far from where heads appeared. Before each peek, park it right at the wall edge, at head height.";
        if (_mode == ReactionMode.FriendOrFoe && s.Mistakes > 2 && s.Accuracy > 0.8) return "You're fast but shot teammates. Speed comes back on its own; the decision is the skill here.";
        if (s.Trials > 3 && s.HeadshotShare < 0.5) return "Most hits were body shots. Keep your crosshair at head height so the flick is sideways only.";
        if (s.Accuracy < 0.6) return "Many enemies got away. It'll settle at a level where you hit most of them: that's where you improve fastest.";
        return "Good session. The drill raises the level whenever you get them right, so a higher level next time is your progress.";
    }

    // ───────── peek drills ─────────

    /// <summary>The scene: a lane between two walls, a floor, and (in Crosshair placement) the angle to hold.</summary>
    void DrawScene()
    {
        Field.Children.Clear();
        _target = null;
        double w = Field.ActualWidth, h = Field.ActualHeight;
        if (w < 50 || h < 50) { w = Arena.ActualWidth; h = Arena.ActualHeight; }
        if (w < 50 || h < 50) return;
        _scene = new PeekScene(w, h);
        Rect(0, 0, w, h, Lane);
        Rect(0, _scene.Floor, w, h - _scene.Floor, FloorFill);
        _figLayer = new Canvas { Width = w, Height = h };
        Field.Children.Add(_figLayer);
        Wall(0, _scene.LeftEdge, _scene.LeftEdge - 2);
        Wall(_scene.RightEdge, w - _scene.RightEdge, _scene.RightEdge);

        if (_pt != null && _mode == ReactionMode.Crosshair && _phase == Phase.Waiting)
        {
            // Show the angle to hold; at low levels also where heads appear.
            double edge = _pt.FromLeft ? _scene.LeftEdge : _scene.RightEdge;
            double y = 0.36 * h;
            var glow = new Rectangle { Width = 3, Height = _scene.Floor, Fill = Guide };
            Canvas.SetLeft(glow, edge - 1.5);
            Field.Children.Add(glow);
            if ((_peek?.Difficulty ?? 1) < 6)
            {
                var line = new Line { X1 = Math.Min(edge, edge + (_pt.FromLeft ? 1 : -1) * 120), X2 = Math.Max(edge, edge + (_pt.FromLeft ? 1 : -1) * 120), Y1 = y, Y2 = y, Stroke = Guide, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 4 } };
                Field.Children.Add(line);
            }
        }

        void Rect(double x, double y, double rw, double rh, Brush b)
        {
            var r = new Rectangle { Width = Math.Max(rw, 0), Height = Math.Max(rh, 0), Fill = b };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
            Field.Children.Add(r);
        }
        void Wall(double x, double ww, double edgeX)
        {
            Rect(x, 0, ww, h, WallFill);
            for (double y = 60; y < h; y += 60) Rect(x, y, ww, 1, WallLine);   // panel seams
            Rect(edgeX, 0, 2, h, WallEdge);
        }
    }

    void NextPeek()
    {
        if (_peek.Finished) { Finish(); return; }
        _pt = _peek.Next();
        _phase = Phase.Waiting;
        _shownAt = -1;
        _offset = -1;
        ClearField();
        DrawScene();
        BigText.Text = "";
        SmallText.Text = _mode == ReactionMode.Crosshair ? Loc.T(_pt.FromLeft ? "Hold the left angle" : "Hold the right angle") : "";
        ProgressText.Text = Loc.F("Peek {0} of {1}", _peek.Done + 1, _peek.Total) +
            (_mode == ReactionMode.Benchmark ? "" : " · " + Loc.F("level {0}", _peek.Difficulty.ToString("0.#", CultureInfo.CurrentCulture)));
        Arm(_pt.WaitMs);
    }

    void ShowPeek()
    {
        if (_scene == null || _figLayer == null) DrawScene();
        if (_scene == null) { Arm(200); return; }
        _phase = Phase.Signal;
        SmallText.Text = "";
        var stroke = _pt.Enemy ? Enemy : Teammate;
        var pose = _scene.At(_pt, 0);
        _body = new System.Windows.Shapes.Path { Data = Silhouette(pose.BodyW, pose.BodyH), Fill = FigureFill, Stroke = stroke, StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round };
        _head = new Ellipse { Width = pose.R * 2, Height = pose.R * 2, Fill = FigureFill, Stroke = stroke, StrokeThickness = 2.5 };
        _figLayer.Children.Add(_body);
        _figLayer.Children.Add(_head);
        Place(pose);
        CompositionTarget.Rendering += PeekFrame;
    }

    /// <summary>A person's outline (shoulders, arms, legs) inside the body's hitbox, so what you see is what you can hit.</summary>
    static Geometry Silhouette(double w, double h)
    {
        Point P(double x, double y) => new(x * w, y * h);
        var f = new PathFigure { StartPoint = P(0.2, 0.05), IsClosed = true };
        f.Segments.Add(new QuadraticBezierSegment(P(0.5, -0.03), P(0.8, 0.05), true));   // shoulders
        f.Segments.Add(new QuadraticBezierSegment(P(0.98, 0.08), P(1, 0.2), true));
        foreach (var (x, y) in new[] { (0.97, 0.52), (0.84, 0.53), (0.82, 0.3), (0.8, 0.56), (0.78, 1.0), (0.56, 1.0), (0.5, 0.64), (0.44, 1.0), (0.22, 1.0), (0.2, 0.56), (0.18, 0.3), (0.16, 0.53), (0.03, 0.52), (0, 0.2) })
            f.Segments.Add(new LineSegment(P(x, y), true));
        f.Segments.Add(new QuadraticBezierSegment(P(0.02, 0.08), P(0.2, 0.05), true));
        var g = new PathGeometry(new[] { f });
        g.Freeze();
        return g;
    }

    void Place(PeekScene.Pose p)
    {
        Canvas.SetLeft(_body, p.BodyLeft); Canvas.SetTop(_body, p.BodyTop);
        Canvas.SetLeft(_head, p.HeadX - p.R); Canvas.SetTop(_head, p.HeadY - p.R);
    }

    /// <summary>Every frame while the peek is on: move the figure; end the try when they're back behind the wall.</summary>
    void PeekFrame(object sender, EventArgs e)
    {
        if (_phase != Phase.Signal || _head == null) { StopFrames(); return; }
        if (_shownAt < 0)
        {
            // Time from the first frame the peek is drawn. Where's the crosshair right now? That's the placement score.
            _shownAt = Now;
            var m = Mouse.GetPosition(Field);
            var (hx, hy) = _scene.HoldHead(_pt);
            _offset = Math.Sqrt((m.X - hx) * (m.X - hx) + (m.Y - hy) * (m.Y - hy));
        }
        double ms = Now - _shownAt;
        var pose = _scene.At(_pt, ms);
        Place(pose);
        if (ms >= PeekScene.EndMs(_pt)) EndPeek(_pt.Enemy ? PeekResult.Missed : PeekResult.Held, 0);
    }

    void StopFrames()
    {
        CompositionTarget.Rendering -= PeekFrame;
        CompositionTarget.Rendering -= Stamp;
    }

    void PeekClick(Point p)
    {
        if (_phase == Phase.Waiting) { _timer.Stop(); EndPeek(PeekResult.TooEarly, 0); return; }
        if (_phase != Phase.Signal || _shownAt < 0) return;
        double ms = Now - _shownAt;
        var hit = _scene.HitTest(_pt, ms, p.X, p.Y);
        if (hit == null)
        {
            _peek.MissedShot();
            var dot = new Ellipse { Width = 5, Height = 5, Fill = Guide, IsHitTestVisible = false };
            Canvas.SetLeft(dot, p.X - 2.5); Canvas.SetTop(dot, p.Y - 2.5);
            Field.Children.Add(dot);
            return;
        }
        EndPeek(_pt.Enemy ? hit.Value : PeekResult.ShotTeammate, ms);
    }

    void EndPeek(PeekResult r, double ms)
    {
        StopFrames();
        r = _peek.Record(_pt, r, ms, _offset);
        _phase = Phase.Feedback;
        var d = ReactionTrainer.SystemDelay(_hz).Total;
        BigText.Foreground = Brushes.White;
        (BigText.Text, SmallText.Text) = r switch
        {
            PeekResult.Head => (Loc.F("{0} ms", Math.Round(Math.Max(0, ms - d))), Loc.T("Headshot")),
            PeekResult.Body => (Loc.F("{0} ms", Math.Round(Math.Max(0, ms - d))), Loc.T("Body shot. Aim at head height.")),
            PeekResult.TooEarly => (Loc.T("Too early"), Loc.T("Wait for the peek: guessing doesn't train anything.")),
            PeekResult.ShotTeammate => (Loc.T("Teammate!"), Loc.T("Only shoot red.")),
            PeekResult.Held => (Loc.T("Good hold"), ""),
            _ => (Loc.T("Too slow"), Loc.T("They got away.")),
        };
        if (_mode == ReactionMode.Crosshair && _offset >= 0 && _pt.Enemy && r != PeekResult.TooEarly)
            SmallText.Text += (SmallText.Text.Length > 0 ? " · " : "") + Loc.F("crosshair {0} px off", Math.Round(_offset));
        if (r is PeekResult.TooEarly or PeekResult.ShotTeammate or PeekResult.Missed) BigText.Foreground = NoGo;
        else if (r == PeekResult.Head) BigText.Foreground = Teammate;
        // Leave the figure where it was hit for a moment, then clear.
        if (_head != null && r is PeekResult.Head or PeekResult.Body) { _head.Opacity = 0.4; _body.Opacity = 0.4; }
        Arm(r is PeekResult.Head or PeekResult.Body or PeekResult.Held ? 650 : 1000);
    }

    // ───────── daily plan ─────────

    void ShowPlan()
    {
        var today = TrainingPlan.Today(DateTime.Today);
        var doneToday = S.History.Where(h => h.When.Date == DateTime.Today).Select(h => h.Mode).ToHashSet();
        PlanSteps.Children.Clear();
        for (int i = 0; i < today.Length; i++)
        {
            var m = today[i];
            bool done = doneToday.Contains(m);
            bool current = _plan != null && _planIndex < _plan.Count && _plan[_planIndex] == m;
            var pill = new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 10, 5), Margin = new Thickness(0, 0, 6, 6),
                Background = (Brush)FindResource(current ? "Surface2Brush" : "SurfaceBrush"),
                BorderBrush = current ? Brushes.White : (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = (done ? "✓ " : $"{i + 1}. ") + Name(m),
                    FontSize = 12,
                    Foreground = done ? Teammate : (Brush)FindResource(current ? "TextBrush" : "TextDimBrush"),
                },
            };
            PlanSteps.Children.Add(pill);
        }
        int streak = TrainingPlan.Streak(S, DateTime.Today);
        StreakText.Text = streak > 0 ? Loc.F("🔥 {0}-day streak", streak) : "";
        bool finishedToday = S.PlanDays.Any(d => d.Date == DateTime.Today);
        PlanButton.Content = _plan != null ? Loc.T("Stop the plan") : finishedToday ? Loc.T("Train again") : Loc.T("Start (about 5 min)");

        bool due = TrainingPlan.BenchmarkDue(S, DateTime.Now);
        var last = TrainingPlan.Benchmarks(S).LastOrDefault();
        string when = last == null ? Loc.T("Benchmark: not taken yet.")
            : due ? Loc.T("Benchmark: due this week.")
            : Loc.F("Benchmark: next one in {0} days.", Math.Max(1, 7 - (int)(DateTime.Now - last.When).TotalDays));
        BenchText.Text = when + " " + ProgressText2();
        BenchButton.Content = due ? Loc.T("Take the Benchmark") : Loc.T("Benchmark");
    }

    void StartPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_plan != null) { _plan = null; Reset(); return; }
        _plan = TrainingPlan.Today(DateTime.Today).ToList();
        _planIndex = 0;
        ResultCard.Visibility = Visibility.Collapsed;
        PopCard.Visibility = Visibility.Collapsed;
        SelectMode(_plan[0]);
        Arena.BringIntoView();
        Focus();
    }

    void Benchmark_Click(object sender, RoutedEventArgs e)
    {
        _plan = null;
        SelectMode(ReactionMode.Benchmark);
        Arena.BringIntoView();
        Focus();
    }

    // ───────── you vs everyone ─────────

    double _popMeasured, _popYou;
    const double PopMin = 120, PopMax = 520;

    void PopChart_SizeChanged(object sender, SizeChangedEventArgs e) { if (PopCard.Visibility == Visibility.Visible) DrawPopulation(); }

    /// <summary>Only the plain reaction test is compared: people online take that one (the other drills are harder by design).</summary>
    void ShowPopulation()
    {
        PopCard.Visibility = _popMeasured > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_popMeasured <= 0) return;
        double faster = ReactionPopulation.FasterThan(_popMeasured);
        PopPct.Text = Loc.F("Faster than {0}%", Math.Round(faster * 100));
        PopTier.Text = Loc.T(ReactionPopulation.Tier(_popMeasured)) + " · " + Loc.F("{0} ms with your setup", Math.Round(_popMeasured));
        PopText.Text = Loc.F("The average person is about {0} ms on this test, counting their own PC, mouse and screen like yours is counted here.", ReactionPopulation.MedianMs);
        DrawPopulation();
    }

    void DrawPopulation()
    {
        PopChart.Children.Clear();
        double w = PopChart.ActualWidth, h = PopChart.ActualHeight;
        if (w < 50 || h < 50) return;
        double top = 26, bottom = h - 34; // room for the marker label above and the scale below
        double peak = ReactionPopulation.Density(ReactionPopulation.MedianMs * Math.Exp(-ReactionPopulation.LogSpread * ReactionPopulation.LogSpread));
        double X(double ms) => (Math.Clamp(ms, PopMin, PopMax) - PopMin) / (PopMax - PopMin) * w;
        double Y(double ms) => bottom - ReactionPopulation.Density(ms) / peak * (bottom - top);

        // Tier bands behind the curve, so it's easy to read where a time falls.
        var bands = new (double From, double To, string Name, string Color)[]
        {
            (PopMin, 180, "Elite", "#33F2EFE8"), (180, 220, "Very fast", "#26F2EFE8"), (220, 250, "Fast", "#1AF2EFE8"),
            (250, 300, "Average", "#12F2EFE8"), (300, 350, "A bit slow", "#0CF2EFE8"), (350, PopMax, "Slow today", "#06F2EFE8"),
        };
        foreach (var b in bands)
        {
            var r = new Rectangle { Width = Math.Max(X(b.To) - X(b.From) - 1, 0), Height = bottom - top, Fill = (Brush)new BrushConverter().ConvertFromString(b.Color) };
            Canvas.SetLeft(r, X(b.From)); Canvas.SetTop(r, top);
            PopChart.Children.Add(r);
            var label = new TextBlock { Text = Loc.T(b.Name), FontSize = 10, Foreground = (Brush)FindResource("TextFaintBrush") };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (label.DesiredSize.Width < X(b.To) - X(b.From) - 4)
            {
                Canvas.SetLeft(label, (X(b.From) + X(b.To) - label.DesiredSize.Width) / 2); Canvas.SetTop(label, bottom + 16);
                PopChart.Children.Add(label);
            }
        }

        // The curve: how many people get each time.
        var fig = new PathFigure { StartPoint = new Point(0, bottom), IsClosed = true };
        var line = new PathFigure { StartPoint = new Point(0, Y(PopMin)) };
        for (double ms = PopMin; ms <= PopMax; ms += 2)
        {
            fig.Segments.Add(new LineSegment(new Point(X(ms), Y(ms)), true));
            line.Segments.Add(new LineSegment(new Point(X(ms), Y(ms)), true));
        }
        fig.Segments.Add(new LineSegment(new Point(w, bottom), true));
        var area = new LinearGradientBrush(Color.FromArgb(0x55, 0xF2, 0xEF, 0xE8), Color.FromArgb(0x08, 0xF2, 0xEF, 0xE8), 90);
        PopChart.Children.Add(new System.Windows.Shapes.Path { Data = new PathGeometry(new[] { fig }), Fill = area });
        PopChart.Children.Add(new System.Windows.Shapes.Path { Data = new PathGeometry(new[] { line }), Stroke = (Brush)FindResource("AccentGradient"), StrokeThickness = 2 });

        // Scale.
        foreach (var ms in new[] { 150, 200, 250, 300, 350, 400, 450, 500 })
        {
            var t = new TextBlock { Text = $"{ms}", FontSize = 10, Foreground = (Brush)FindResource("TextFaintBrush") };
            Canvas.SetLeft(t, X(ms) - 9); Canvas.SetTop(t, bottom + 2);
            PopChart.Children.Add(t);
        }

        // The average person, and you.
        Marker(X(ReactionPopulation.MedianMs), Loc.F("Average {0} ms", ReactionPopulation.MedianMs), "#8A90A0", dashed: true, labelTop: top + 4);
        if (_popYou > 0 && _popYou < _popMeasured - 3)
            Marker(X(_popYou), Loc.T("You, without the PC delay"), "#5EEAD4", dashed: true, labelTop: top + 22, thin: true);
        Marker(X(_popMeasured), Loc.F("You · {0} ms", Math.Round(_popMeasured)), "#FFFFFF", dashed: false, labelTop: 0, bold: true);

        void Marker(double x, string text, string color, bool dashed, double labelTop, bool bold = false, bool thin = false)
        {
            var brush = (Brush)new BrushConverter().ConvertFromString(color);
            var l = new Line { X1 = x, X2 = x, Y1 = top, Y2 = bottom, Stroke = brush, StrokeThickness = bold ? 2.5 : thin ? 1 : 1.5, Opacity = bold ? 1 : 0.8 };
            if (dashed) l.StrokeDashArray = new DoubleCollection { 3, 3 };
            PopChart.Children.Add(l);
            if (bold)
            {
                var dot = new Ellipse { Width = 10, Height = 10, Fill = brush };
                Canvas.SetLeft(dot, x - 5); Canvas.SetTop(dot, Math.Max(Y(Math.Clamp(_popMeasured, PopMin, PopMax)) - 5, top - 5));
                PopChart.Children.Add(dot);
            }
            var tb = new TextBlock { Text = text, FontSize = bold ? 12 : 10.5, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = brush };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, Math.Clamp(x - tb.DesiredSize.Width / 2, 0, Math.Max(w - tb.DesiredSize.Width, 0)));
            Canvas.SetTop(tb, labelTop);
            PopChart.Children.Add(tb);
        }
    }

    int _hz = 60; // read once in the background (reading monitors can take a second)

    static int ScreenHz()
    {
        try
        {
            var m = Displays.Enumerate().FirstOrDefault(x => x.IsPrimary) ?? Displays.Enumerate().FirstOrDefault();
            return m?.Current.Hz > 1 ? m.Current.Hz : 60;
        }
        catch { return 60; }
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

    /// <summary>What the progress chart plots for a drill: time for fixed drills, level for adaptive ones (the time
    /// there stays about the same because the drill gets harder as you get faster), and the score for the Benchmark.</summary>
    (Func<ReactionSession, double> Value, bool HigherIsBetter, string Text) Measure() => _mode switch
    {
        ReactionMode.Benchmark => (h => h.Score, false, "Benchmark score, lower is better. The band is your normal week-to-week range."),
        ReactionMode.Peek or ReactionMode.FriendOrFoe or ReactionMode.Crosshair => (h => h.Difficulty, true, "Level reached, higher is better. The drill gets harder as you improve, so this is your progress."),
        ReactionMode.GoNoGo or ReactionMode.Flick => (h => h.Level, true, "Level reached, higher is better. The drill gets harder as you improve, so this is your progress."),
        _ => (h => h.MedianMs, false, "Lower is faster. The band is your normal day-to-day range."),
    };

    void ShowHistory()
    {
        var (value, higher, text) = Measure();
        var list = S.History.Where(h => h.Mode == _mode && value(h) > 0).OrderBy(h => h.When).TakeLast(30).ToList();
        HistoryCard.Visibility = list.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        if (list.Count < 2) return;
        var vals = list.Select(value).ToList();
        HistoryText.Text = Loc.F("Last {0} sessions.", list.Count) + " " + Loc.T(text);
        Dispatcher.InvokeAsync(() => DrawChart(list, vals, higher), DispatcherPriority.Loaded);
    }

    void DrawChart(List<ReactionSession> list, List<double> vals, bool higherIsBetter)
    {
        Chart.Children.Clear();
        double w = Math.Max(Chart.ActualWidth, 300), h = Chart.Height;
        double lo = vals.Min(), hi = vals.Max();
        double pad = Math.Max(higherIsBetter ? 0.5 : 10, (hi - lo) * 0.2);
        lo -= pad; hi += pad;
        // Better is always up: for times (lower is better) the axis is flipped.
        double Y(double v) => higherIsBetter ? h - (v - lo) / (hi - lo) * h : (v - lo) / (hi - lo) * h;
        double X(int i) => list.Count == 1 ? w / 2 : i * (w - 8) / (list.Count - 1) + 4;

        // Normal range: average of the last 10 ± their spread (at least 4%).
        var recent = vals.TakeLast(10).ToList();
        double avg = recent.Average();
        double sd = recent.Count > 1 ? Math.Sqrt(recent.Sum(v => (v - avg) * (v - avg)) / (recent.Count - 1)) : 0;
        double band = Math.Max(sd, Math.Abs(avg) * 0.04);
        double y1 = Y(avg + band), y2 = Y(avg - band);
        var rect = new Rectangle { Width = w, Height = Math.Max(2, Math.Abs(y2 - y1)), Fill = (Brush)new BrushConverter().ConvertFrom("#14F2EFE8"), RadiusX = 6, RadiusY = 6 };
        Canvas.SetTop(rect, Math.Min(y1, y2));
        Chart.Children.Add(rect);

        var line = new Polyline { Stroke = (Brush)FindResource("AccentGradient"), StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round };
        for (int i = 0; i < list.Count; i++) line.Points.Add(new Point(X(i), Y(vals[i])));
        Chart.Children.Add(line);
        for (int i = 0; i < list.Count; i++)
        {
            var dot = new Ellipse { Width = 7, Height = 7, Fill = Brushes.White, ToolTip = $"{list[i].When:d} · {vals[i].ToString(higherIsBetter ? "0.#" : "0", CultureInfo.CurrentCulture)}" };
            Canvas.SetLeft(dot, X(i) - 3.5);
            Canvas.SetTop(dot, Y(vals[i]) - 3.5);
            Chart.Children.Add(dot);
        }
    }
}
