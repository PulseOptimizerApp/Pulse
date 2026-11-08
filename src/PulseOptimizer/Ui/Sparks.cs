using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Pulse.Ui;

/// <summary>
/// Little crackles of electricity that run around the logo's edge now and then. Each burst lasts about half a
/// second every few seconds; between bursts (and while Pulse is in the background) nothing runs.
/// </summary>
public sealed class Sparks : FrameworkElement
{
    readonly DispatcherTimer _frame = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(45) };
    readonly DispatcherTimer _next = new() { Interval = TimeSpan.FromSeconds(2.5) };
    readonly Random _rnd = new();
    readonly List<Point[]> _bolts = new();
    int _framesLeft;
    double _alpha;
    static readonly Pen Core = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xF2, 0xEF, 0xE8)), 1.2) { LineJoin = PenLineJoin.Round });
    static readonly Pen Halo = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xF2, 0xEF, 0xE8)), 3.2) { LineJoin = PenLineJoin.Round });
    static Pen Frozen(Pen p) { p.Freeze(); return p; }

    /// <summary>Space between the logo and the edge of this element (the sparks run in it).</summary>
    public double Inset { get; set; } = 7;
    public double Radius { get; set; } = 8;

    public Sparks()
    {
        IsHitTestVisible = false;
        _frame.Tick += (_, _) => Frame();
        _next.Tick += (_, _) => Burst();
        Loaded += (_, _) => { _next.Start(); Burst(); };
        Unloaded += (_, _) => { _next.Stop(); _frame.Stop(); };
    }

    void Burst()
    {
        _next.Interval = TimeSpan.FromSeconds(2.2 + _rnd.NextDouble() * 2.5);
        var w = Window.GetWindow(this);
        if (w == null || !w.IsActive || w.WindowState == WindowState.Minimized || !IsVisible) return;
        _framesLeft = 8 + _rnd.Next(6);
        _frame.Start();
    }

    void Frame()
    {
        _bolts.Clear();
        if (_framesLeft-- <= 0) { _frame.Stop(); InvalidateVisual(); return; }
        _alpha = 0.55 + _rnd.NextDouble() * 0.45;
        int n = 1 + _rnd.Next(2);
        for (int i = 0; i < n; i++) _bolts.Add(Bolt());
        InvalidateVisual();
    }

    /// <summary>A jagged line that follows part of the rounded square around the logo.</summary>
    Point[] Bolt()
    {
        double w = ActualWidth - Inset * 2 + 4, h = ActualHeight - Inset * 2 + 4, x0 = Inset - 2, y0 = Inset - 2;
        double per = 2 * (w + h);
        double start = _rnd.NextDouble() * per, len = per * (0.1 + _rnd.NextDouble() * 0.18);
        var pts = new List<Point>();
        for (double d = 0; d <= len; d += 3)
        {
            var (p, nx, ny) = OnEdge((start + d) % per, x0, y0, w, h);
            double j = (_rnd.NextDouble() * 2 - 1) * 2.6 + 1.2;
            pts.Add(new Point(p.X + nx * j, p.Y + ny * j));
        }
        return pts.ToArray();
    }

    static (Point P, double Nx, double Ny) OnEdge(double d, double x, double y, double w, double h)
    {
        if (d < w) return (new Point(x + d, y), 0, -1);
        d -= w;
        if (d < h) return (new Point(x + w, y + d), 1, 0);
        d -= h;
        if (d < w) return (new Point(x + w - d, y + h), 0, 1);
        d -= w;
        return (new Point(x, y + h - d), -1, 0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_bolts.Count == 0) return;
        dc.PushOpacity(_alpha);
        foreach (var b in _bolts)
        {
            if (b.Length < 2) continue;
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(b[0], false, false); c.PolyLineTo(b.Skip(1).ToList(), true, true); }
            g.Freeze();
            dc.DrawGeometry(null, Halo, g);
            dc.DrawGeometry(null, Core, g);
        }
        dc.Pop();
    }
}
