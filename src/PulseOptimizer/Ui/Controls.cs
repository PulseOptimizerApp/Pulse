using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Pulse.Ui;

/// <summary>A progress ring drawn as an arc. Animate <see cref="Value"/> (0–100) for smooth fills.</summary>
public class Ring : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(Ring),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(Ring),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(nameof(Brush), typeof(Brush), typeof(Ring),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(Ring),
        new FrameworkPropertyMetadata(DefaultTrack, FrameworkPropertyMetadataOptions.AffectsRender));

    // Property defaults must be frozen, or WPF refuses them ("cannot be bound to a specific thread").
    static Brush DefaultTrack
    {
        get
        {
            var b = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            b.Freeze();
            return b;
        }
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    /// <summary>Animates the ring to a new value.</summary>
    public void AnimateTo(double value, int ms = 900) =>
        BeginAnimation(ValueProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(ms))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        double r = (size - Thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(TrackBrush, Thickness), c, r, r);

        double v = Math.Clamp(Value, 0, 100);
        if (v <= 0.05) return;
        var pen = new Pen(Brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v >= 99.95) { dc.DrawEllipse(null, pen, c, r, r); return; }

        double angle = v / 100 * 360;
        Point P(double deg)
        {
            double rad = (deg - 90) * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(0), false, false);
            ctx.ArcTo(P(angle), new Size(r, r), 0, angle > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>An endlessly spinning arc for "working…" states.</summary>
public class Spinner : Ring
{
    public Spinner()
    {
        Value = 28;
        Thickness = 3;
        RenderTransformOrigin = new Point(0.5, 0.5);
        var rot = new RotateTransform();
        RenderTransform = rot;
        Loaded += (_, _) => rot.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        Unloaded += (_, _) => rot.BeginAnimation(RotateTransform.AngleProperty, null);
    }
}

/// <summary>Attached animations: Anim.Reveal="True" fades and slides an element in, staggered with its siblings.</summary>
public static class Anim
{
    public static readonly DependencyProperty RevealProperty = DependencyProperty.RegisterAttached("Reveal", typeof(bool), typeof(Anim),
        new PropertyMetadata(false, OnReveal));
    public static bool GetReveal(DependencyObject d) => (bool)d.GetValue(RevealProperty);
    public static void SetReveal(DependencyObject d, bool v) => d.SetValue(RevealProperty, v);

    static int _seq;
    static DateTime _last;

    static void OnReveal(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement fe && (bool)e.NewValue)
        {
            fe.Opacity = 0;
            fe.Loaded += (_, _) => Play(fe);
        }
    }

    public static void Play(FrameworkElement fe)
    {
        var now = DateTime.Now;
        if ((now - _last).TotalMilliseconds > 250) _seq = 0;
        _last = now;
        var delay = TimeSpan.FromMilliseconds(Math.Min(_seq++, 12) * 35);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var tt = new TranslateTransform(0, 14);
        fe.RenderTransform = tt;
        fe.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { BeginTime = delay, EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = delay, EasingFunction = ease });
    }

    /// <summary>Animates a horizontal bar's ScaleX to a 0–1 fraction.</summary>
    public static void Bar(FrameworkElement fill, double fraction)
    {
        if (fill.RenderTransform is not ScaleTransform st)
        {
            st = new ScaleTransform(0, 1);
            fill.RenderTransformOrigin = new Point(0, 0.5);
            fill.RenderTransform = st;
        }
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(Math.Clamp(fraction, 0, 1), TimeSpan.FromMilliseconds(450))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}

/// <summary>Icon.Glyph lets nav buttons and chips carry an icon character.</summary>
public static class Icon
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached("Glyph", typeof(string), typeof(Icon),
        new FrameworkPropertyMetadata(""));
    public static string GetGlyph(DependencyObject d) => (string)d.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject d, string v) => d.SetValue(GlyphProperty, v);

    /// <summary>Pulse's own line icon (24×24 path data, drawn with a 2 px round stroke). Used instead of the glyph when set.</summary>
    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached("Path", typeof(System.Windows.Media.Geometry), typeof(Icon),
        new FrameworkPropertyMetadata(null));
    public static System.Windows.Media.Geometry GetPath(DependencyObject d) => (System.Windows.Media.Geometry)d.GetValue(PathProperty);
    public static void SetPath(DependencyObject d, System.Windows.Media.Geometry v) => d.SetValue(PathProperty, v);
}

public class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Visible when the value is a non-empty string (or non-null object).</summary>
public class HasValueToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool has = value switch { null => false, string s => !string.IsNullOrWhiteSpace(s), _ => true };
        return has ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Dims an element to 50% when the bound bool is false.</summary>
public class BoolToOpacity : IValueConverter
{
    /// <summary>ConverterParameter="invert" dims when true instead (used for finished checklist steps).</summary>
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (value is true) ^ (p as string == "invert") ? 1.0 : 0.55;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class InverseBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is not true;
}

/// <summary>A small filled line chart of the last few values (0..1).</summary>
public class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(nameof(Brush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }

    IReadOnlyList<double> _values = Array.Empty<double>();
    public void SetValues(IReadOnlyList<double> values) { _values = values.ToArray(); InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || _values.Count < 2) return;
        int n = Pulse.Core.LiveStats.HistoryLength;
        double step = w / (n - 1);
        double x0 = w - step * (_values.Count - 1);
        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        Point P(int i) => new(x0 + i * step, h - 2 - Math.Clamp(_values[i], 0, 1) * (h - 4));
        using (var c = line.Open())
        {
            c.BeginFigure(P(0), false, false);
            for (int i = 1; i < _values.Count; i++) c.LineTo(P(i), true, true);
        }
        using (var c = fill.Open())
        {
            c.BeginFigure(new Point(x0, h), true, true);
            for (int i = 0; i < _values.Count; i++) c.LineTo(P(i), false, false);
            c.LineTo(new Point(w, h), false, false);
        }
        line.Freeze(); fill.Freeze();
        var fillBrush = Brush.CloneCurrentValue();
        fillBrush.Opacity = 0.14;
        dc.DrawGeometry(fillBrush, null, fill);
        dc.DrawGeometry(null, new Pen(Brush, 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}

/// <summary>Visible when the first bool is true and the second false (an icon, but no cover art).</summary>
public class IconOnlyVisibility : IMultiValueConverter
{
    public object Convert(object[] v, Type t, object p, CultureInfo c) =>
        v.Length == 2 && v[0] is true && v[1] is not true ? Visibility.Visible : Visibility.Collapsed;
    public object[] ConvertBack(object value, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}
