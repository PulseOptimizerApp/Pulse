using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Pulse.Core;
using Pulse.Core.Audio;
using Pulse.Ui;

namespace Pulse.Pages;


/// <summary>A horizontal level meter (−60…0 dBFS) with a falling peak line and an optional marker (the gate level).</summary>
sealed class LevelMeter : FrameworkElement
{
    double _level, _peak;
    DateTime _peakAt;
    public Brush Good, Warn, Bad, Track, Marker;
    public double MarkerDb = double.NaN;

    static double Pos(double db) => Math.Clamp((db + 60) / 60, 0, 1);

    public void Set(float peak)
    {
        double db = 20 * Math.Log10(Math.Max(peak, 1e-6));
        double p = Pos(db);
        _level = p > _level ? p : _level + (p - _level) * 0.25;
        if (p >= _peak) { _peak = p; _peakAt = DateTime.UtcNow; }
        else if ((DateTime.UtcNow - _peakAt).TotalMilliseconds > 900) _peak = Math.Max(p, _peak - 0.02);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;
        var r = new Rect(0, 0, w, h);
        dc.PushClip(new RectangleGeometry(r, h / 2, h / 2));
        dc.DrawRectangle(Track, null, r);
        // Green up to −18 dB, yellow to −6, red above.
        double lw = w * _level, g1 = w * Pos(-18), g2 = w * Pos(-6);
        if (lw > 0) dc.DrawRectangle(Good, null, new Rect(0, 0, Math.Min(lw, g1), h));
        if (lw > g1) dc.DrawRectangle(Warn, null, new Rect(g1, 0, Math.Min(lw, g2) - g1, h));
        if (lw > g2) dc.DrawRectangle(Bad, null, new Rect(g2, 0, lw - g2, h));
        dc.Pop();
        if (_peak > 0.01) dc.DrawRectangle(_peak > Pos(-6) ? Bad : Good, null, new Rect(Math.Max(0, w * _peak - 2), 0, 2, h));
        if (!double.IsNaN(MarkerDb) && Marker != null)
        {
            double x = w * Pos(MarkerDb);
            dc.DrawRectangle(Marker, null, new Rect(x - 1, -3, 2, h + 6));
        }
    }
}

/// <summary>A 10-band EQ as a graph: drag each band's point; the curve is the whole response. Used by Headphones.</summary>
sealed class EqGraph : FrameworkElement
{
    readonly Func<double[]> _bands;
    readonly Func<double, double> _curve;
    double[] Eq { get { var e = _bands(); return e.Length == 10 ? e : new double[10]; } }
    readonly Brush _accent, _grid, _label, _text, _fill;
    readonly Pen _gridPen, _zeroPen, _curvePen;
    int _drag = -1;
    public event Action Moved;
    const double Pad = 30, Range = 12;

    public EqGraph(Func<double[]> bands, Func<double, double> curve, Brush accent, Brush grid, Brush label, Brush text, Brush fill)
    {
        _bands = bands; _curve = curve; _accent = accent; _grid = grid; _label = label; _text = text; _fill = fill;
        _gridPen = new Pen(grid, 1); _zeroPen = new Pen(label, 1) { DashStyle = DashStyles.Dash };
        _curvePen = new Pen(accent, 2.5) { LineJoin = PenLineJoin.Round };
        Cursor = Cursors.Hand;
    }

    Rect Plot => new(Pad + 6, 10, Math.Max(10, ActualWidth - Pad - 16), Math.Max(10, ActualHeight - 38));
    double X(double f, Rect p) => p.Left + (Math.Log10(f) - Math.Log10(20)) / (Math.Log10(20000) - Math.Log10(20)) * p.Width;
    double Y(double db, Rect p) => p.Top + (1 - (Math.Clamp(db, -Range, Range) + Range) / (2 * Range)) * p.Height;
    Point Handle(int b, Rect p) => new(X(EqBands.Freqs[b], p), Y(Eq[b], p));

    protected override void OnRender(DrawingContext dc)
    {
        var p = Plot;
        dc.DrawRoundedRectangle(_fill, null, new Rect(0, 0, ActualWidth, ActualHeight), 10, 10);
        var tf = new Typeface("Segoe UI");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Txt(string t) => new(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 10.5, _label, dpi);
        foreach (var db in new[] { -12, -6, 0, 6, 12 })
        {
            double y = Y(db, p);
            dc.DrawLine(db == 0 ? _zeroPen : _gridPen, new Point(p.Left, y), new Point(p.Right, y));
            var t = Txt((db > 0 ? "+" : "") + db);
            dc.DrawText(t, new Point(p.Left - t.Width - 8, y - t.Height / 2));
        }
        for (int b = 0; b < 10; b++)
        {
            double x = X(EqBands.Freqs[b], p);
            dc.DrawLine(_gridPen, new Point(x, p.Top), new Point(x, p.Bottom));
            var t = Txt(EqBands.Labels[b]);
            dc.DrawText(t, new Point(x - t.Width / 2, p.Bottom + 8));
        }

        // The response curve, filled toward 0 dB.
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        var pts = new List<Point>();
        for (int i = 0; i <= 160; i++)
        {
            double f = 20 * Math.Pow(1000, i / 160.0);
            pts.Add(new Point(X(f, p), Y(_curve(f), p)));
        }
        using (var c = line.Open()) { c.BeginFigure(pts[0], false, false); c.PolyLineTo(pts.Skip(1).ToList(), true, true); }
        using (var c = area.Open())
        {
            double y0 = Y(0, p);
            c.BeginFigure(new Point(pts[0].X, y0), true, true);
            c.PolyLineTo(pts, true, true);
            c.LineTo(new Point(pts[^1].X, y0), true, true);
        }
        var soft = _accent.Clone(); soft.Opacity = 0.14;
        dc.DrawGeometry(soft, null, area);
        dc.DrawGeometry(null, _curvePen, line);

        for (int b = 0; b < 10; b++)
        {
            var h = Handle(b, p);
            dc.DrawEllipse(b == _drag ? _accent : _fill, new Pen(_accent, 2), h, 6.5, 6.5);
            if (b == _drag)
            {
                var t = new FormattedText((Eq[b] > 0 ? "+" : "") + Eq[b].ToString("0.0", CultureInfo.CurrentCulture) + " dB", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 11.5, _text, dpi);
                dc.DrawText(t, new Point(Math.Clamp(h.X - t.Width / 2, 0, ActualWidth - t.Width), Math.Max(0, h.Y - 26)));
            }
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = Plot;
        var m = e.GetPosition(this);
        int best = -1; double bd = 22;
        for (int b = 0; b < 10; b++) { double d = Math.Abs(Handle(b, p).X - m.X); if (d < bd) { bd = d; best = b; } }
        if (best < 0) return;
        if (e.ClickCount == 2) { Eq[best] = 0; Moved?.Invoke(); InvalidateVisual(); return; }
        _drag = best;
        CaptureMouse();
        Move(m);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag >= 0) Move(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag < 0) return;
        _drag = -1;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    void Move(Point m)
    {
        var p = Plot;
        double db = (1 - (m.Y - p.Top) / p.Height) * 2 * Range - Range;
        Eq[_drag] = Math.Round(Math.Clamp(db, -Range, Range) * 2) / 2;
        Moved?.Invoke();
        InvalidateVisual();
    }
}
