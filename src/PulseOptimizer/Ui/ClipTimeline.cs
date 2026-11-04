using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Pulse.Ui;

/// <summary>
/// The trimmer's timeline: a time ruler, a filmstrip of frames from the clip, the kept part between two handles
/// (the rest dimmed), and a playhead. Drag a handle to trim, drag anywhere else to scrub.
/// </summary>
public class ClipTimeline : FrameworkElement
{
    const double Pad = 14, RulerH = 22, StripTop = 30, StripH = 58, HandleW = 12;

    public static readonly DependencyProperty DurationProperty = Reg(nameof(Duration), 1.0);
    public static readonly DependencyProperty StartProperty = Reg(nameof(Start), 0.0);
    public static readonly DependencyProperty EndProperty = Reg(nameof(End), 1.0);
    public static readonly DependencyProperty PositionProperty = Reg(nameof(Position), 0.0);
    public static readonly DependencyProperty StripProperty = DependencyProperty.Register(nameof(Strip), typeof(ImageSource), typeof(ClipTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    static DependencyProperty Reg(string name, double def) => DependencyProperty.Register(name, typeof(double), typeof(ClipTimeline),
        new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, Math.Max(value, 0.1)); }
    public double Start { get => (double)GetValue(StartProperty); set => SetValue(StartProperty, value); }
    public double End { get => (double)GetValue(EndProperty); set => SetValue(EndProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public ImageSource Strip { get => (ImageSource)GetValue(StripProperty); set => SetValue(StripProperty, value); }

    /// <summary>The user scrubbed to a time (seconds).</summary>
    public event Action<double> Seek;
    /// <summary>The user moved a trim handle.</summary>
    public event Action SelectionChanged;

    /// <summary>The shortest part that can be kept, in seconds.</summary>
    public double MinLength { get; set; } = 0.5;

    enum Drag { None, Start, End, Playhead }
    Drag _drag;

    public ClipTimeline()
    {
        Height = StripTop + StripH + 4;
        Cursor = Cursors.Hand;
        ClipToBounds = false;
    }

    double Width0 => Math.Max(ActualWidth - 2 * Pad, 1);
    double X(double t) => Pad + Math.Clamp(t / Duration, 0, 1) * Width0;
    double T(double x) => Math.Clamp((x - Pad) / Width0, 0, 1) * Duration;

    protected override void OnRender(DrawingContext dc)
    {
        var dim = (Brush)TryFindResource("TextDimBrush") ?? Brushes.Gray;
        var faint = (Brush)TryFindResource("TextFaintBrush") ?? Brushes.DimGray;
        var accent = (Brush)TryFindResource("AccentGradient") ?? Brushes.Cyan;
        var font = new Typeface((FontFamily)TryFindResource("UiFont") ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // whole area takes the mouse

        // Ruler: labels every "step" seconds, small ticks in between.
        double step = new[] { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300 }.First(s => s / Duration * Width0 >= 70 || s >= 300);
        var tickPen = new Pen(faint, 1); tickPen.Freeze();
        for (double t = 0; t <= Duration + 1e-6; t += step / 5)
        {
            double x = Math.Round(X(t)) + 0.5;
            bool major = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
            dc.DrawLine(tickPen, new Point(x, major ? RulerH - 8 : RulerH - 4), new Point(x, RulerH));
            if (!major) continue;
            var label = new FormattedText(Label(t), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, font, 11, dim, dip);
            double lx = Math.Clamp(x - label.Width / 2, 0, Math.Max(0, ActualWidth - label.Width));
            dc.DrawText(label, new Point(lx, 0));
        }

        // Filmstrip.
        var strip = new Rect(Pad, StripTop, Width0, StripH);
        var round = new RectangleGeometry(strip, 6, 6);
        dc.PushClip(round);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x10)), null, strip);
        if (Strip != null) dc.DrawImage(Strip, strip);
        // Dim what's cut off.
        var shade = new SolidColorBrush(Color.FromArgb(0xB8, 0x05, 0x06, 0x0A));
        double xs = X(Start), xe = X(End);
        if (xs > strip.Left) dc.DrawRectangle(shade, null, new Rect(strip.Left, strip.Top, xs - strip.Left, StripH));
        if (xe < strip.Right) dc.DrawRectangle(shade, null, new Rect(xe, strip.Top, strip.Right - xe, StripH));
        dc.Pop();

        // Kept part: a frame in Pulse's accent gradient with a handle at each end.
        var frame = new Pen(accent, 2.5);
        dc.DrawRoundedRectangle(null, frame, new Rect(xs, StripTop + 1, Math.Max(xe - xs, 1), StripH - 2), 6, 6);
        DrawHandle(dc, xs - HandleW / 2, accent);
        DrawHandle(dc, xe - HandleW / 2, accent);

        // Playhead: white, so it stands out against the gradient frame.
        double xp = X(Position);
        var head = new Pen(Brushes.White, 2); head.Freeze();
        dc.DrawLine(head, new Point(xp, RulerH - 2), new Point(xp, StripTop + StripH + 2));
        dc.DrawRoundedRectangle(Brushes.White, null, new Rect(xp - 6, RulerH - 8, 12, 10), 3, 3);
    }

    static void DrawHandle(DrawingContext dc, double x, Brush b)
    {
        dc.DrawRoundedRectangle(b, null, new Rect(x, StripTop - 1, HandleW, StripH + 2), 5, 5);
        var grip = new Pen(Brushes.White, 2); grip.Freeze();
        dc.DrawLine(grip, new Point(x + HandleW / 2, StripTop + StripH / 2 - 9), new Point(x + HandleW / 2, StripTop + StripH / 2 + 9));
    }

    static string Label(double t) => t >= 3600
        ? TimeSpan.FromSeconds(t).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : t % 1 == 0 ? TimeSpan.FromSeconds(t).ToString(@"m\:ss", CultureInfo.InvariantCulture) : TimeSpan.FromSeconds(t).ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        bool onStrip = p.Y >= StripTop - 4;
        if (onStrip && Math.Abs(p.X - X(Start)) <= HandleW) _drag = Drag.Start;
        else if (onStrip && Math.Abs(p.X - X(End)) <= HandleW) _drag = Drag.End;
        else _drag = Drag.Playhead;
        CaptureMouse();
        Apply(p.X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag != Drag.None) { Apply(p.X); return; }
        bool onHandle = p.Y >= StripTop - 4 && (Math.Abs(p.X - X(Start)) <= HandleW || Math.Abs(p.X - X(End)) <= HandleW);
        Cursor = onHandle ? Cursors.SizeWE : Cursors.Hand;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _drag = Drag.None;
        ReleaseMouseCapture();
    }

    void Apply(double x)
    {
        double t = T(x);
        switch (_drag)
        {
            case Drag.Start:
                Start = Math.Min(t, End - MinLength);
                if (Start < 0) Start = 0;
                Position = Start;
                Seek?.Invoke(Start);
                SelectionChanged?.Invoke();
                break;
            case Drag.End:
                End = Math.Max(t, Start + MinLength);
                if (End > Duration) End = Duration;
                Position = End;
                Seek?.Invoke(End);
                SelectionChanged?.Invoke();
                break;
            case Drag.Playhead:
                Position = t;
                Seek?.Invoke(t);
                break;
        }
    }
}
