using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Pulse.Ui;

/// <summary>
/// Electricity around the logo. Three kinds of event, one every second or so:
///  - crackles: short jagged arcs that dance along the logo's edge for a few frames,
///  - strikes: a forked bolt that jumps out from the edge, flashes the logo, then flickers once or twice like real lightning,
///  - charges (now and then): a bright spark races all the way around the edge, leaving a trail, and ends in a strike.
/// Bolts are built by midpoint displacement (how lightning is usually drawn), with branches, a wide soft glow and a
/// white-hot core. Nothing runs while Pulse is in the background or minimized, and between events the timer is off.
/// </summary>
public sealed class Sparks : FrameworkElement
{
    readonly DispatcherTimer _frame = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer _next = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Random _rnd = new();
    readonly List<Bolt> _bolts = new();
    double _flash;          // logo flash after a strike, fades out
    double _charge = -1;    // position (0..1) of a spark racing round the edge, or -1
    double _chargeSpeed;

    static readonly Pen Core = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), 1.1) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Pen Inner = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0xC9, 0xE2, 0xFF)), 2.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Pen Outer = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x26, 0x8F, 0xC1, 0xFF)), 7) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Pen BranchCore = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)), 0.7) { LineJoin = PenLineJoin.Round });
    static readonly Brush Glow = Frozen(new RadialGradientBrush(Color.FromArgb(0x70, 0xBF, 0xDB, 0xFF), Color.FromArgb(0x00, 0x8F, 0xC1, 0xFF)));
    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    sealed class Bolt
    {
        public List<Point> Main = new();
        public List<List<Point>> Branches = new();
        public int Life, Age;
        public bool Crackle;        // re-jittered every frame
        public double Start, Length; // crackles: where on the edge
        public int[] DarkFrames = Array.Empty<int>(); // strikes: frames where it blinks out (the flicker)
    }

    /// <summary>Space between the logo and the edge of this element (the sparks run in it).</summary>
    public double Inset { get; set; } = 7;

    public Sparks()
    {
        IsHitTestVisible = false;
        _frame.Tick += (_, _) => Frame();
        _next.Tick += (_, _) => Event();
        Loaded += (_, _) => { _next.Start(); Event(); };
        Unloaded += (_, _) => { _next.Stop(); _frame.Stop(); };
    }

    bool Showing()
    {
        var w = Window.GetWindow(this);
        return w != null && w.IsActive && w.WindowState != WindowState.Minimized && IsVisible;
    }

    void Event()
    {
        _next.Interval = TimeSpan.FromSeconds(0.6 + _rnd.NextDouble() * 1.3);
        if (!Showing() || ActualWidth < 10) return;
        double r = _rnd.NextDouble();
        if (r < 0.1 && _charge < 0) { _charge = _rnd.NextDouble(); _chargeLaps = 0; _chargeSpeed = 0.07 + _rnd.NextDouble() * 0.03; }
        else if (r < 0.45) Strike();
        else
        {
            int n = 1 + _rnd.Next(3);
            for (int i = 0; i < n; i++) AddCrackle(_rnd.NextDouble(), 0.08 + _rnd.NextDouble() * 0.16, 5 + _rnd.Next(8));
        }
        _frame.Start();
    }

    // ───────── the edge (a rounded square just outside the logo) ─────────

    double X0 => Inset - 2;
    double Y0 => Inset - 2;
    double EW => ActualWidth - Inset * 2 + 4;
    double EH => ActualHeight - Inset * 2 + 4;

    /// <summary>A point on the edge (0..1 round it) and the outward direction there.</summary>
    (Point P, Vector N) OnEdge(double f)
    {
        double w = EW, h = EH, per = 2 * (w + h), d = ((f % 1) + 1) % 1 * per;
        if (d < w) return (new Point(X0 + d, Y0), new Vector(0, -1));
        d -= w;
        if (d < h) return (new Point(X0 + w, Y0 + d), new Vector(1, 0));
        d -= h;
        if (d < w) return (new Point(X0 + w - d, Y0 + h), new Vector(0, 1));
        d -= w;
        return (new Point(X0, Y0 + h - d), new Vector(-1, 0));
    }

    // ───────── building bolts ─────────

    /// <summary>Midpoint displacement: split the line, push the middle sideways by a random amount, repeat with half the push.</summary>
    List<Point> Jagged(Point a, Point b, double rough, int depth)
    {
        var pts = new List<Point> { a, b };
        for (int level = 0; level < depth; level++)
        {
            var next = new List<Point>(pts.Count * 2) { pts[0] };
            for (int i = 1; i < pts.Count; i++)
            {
                var p = pts[i - 1]; var q = pts[i];
                var d = q - p;
                var n = d.Length > 0 ? new Vector(-d.Y / d.Length, d.X / d.Length) : new Vector();
                var mid = p + d / 2 + n * ((_rnd.NextDouble() * 2 - 1) * rough);
                next.Add(mid); next.Add(q);
            }
            pts = next;
            rough /= 2;
        }
        return pts;
    }

    void AddCrackle(double start, double length, int life)
    {
        var b = new Bolt { Crackle = true, Start = start, Length = length, Life = life };
        Rebuild(b);
        _bolts.Add(b);
    }

    /// <summary>A crackle along the edge: points stepped along it, each pushed in or out a little.</summary>
    void Rebuild(Bolt b)
    {
        b.Main.Clear(); b.Branches.Clear();
        double per = 2 * (EW + EH);
        int steps = Math.Max(4, (int)(b.Length * per / 3));
        for (int i = 0; i <= steps; i++)
        {
            var (p, n) = OnEdge(b.Start + b.Length * i / steps);
            double j = (_rnd.NextDouble() * 2 - 1) * 2.4 + 1.2;
            b.Main.Add(p + n * j);
        }
        // Now and then a tiny spark jumps off it.
        if (_rnd.NextDouble() < 0.35)
        {
            int at = _rnd.Next(b.Main.Count);
            var (_, n) = OnEdge(b.Start + b.Length * at / steps);
            var from = b.Main[at];
            var to = from + n * (3 + _rnd.NextDouble() * 4) + new Vector((_rnd.NextDouble() - 0.5) * 4, (_rnd.NextDouble() - 0.5) * 4);
            b.Branches.Add(Jagged(from, to, 2, 2));
        }
    }

    /// <summary>A forked bolt from the edge outwards (never towards the "Pulse" text on the right).</summary>
    void Strike(double? at = null)
    {
        double f;
        Point p; Vector n;
        int tries = 0;
        do
        {
            f = at ?? _rnd.NextDouble();
            (p, n) = OnEdge(f);
            at = null;
        } while (n.X > 0 && ++tries < 10);
        if (n.X > 0) return;

        var side = new Vector(-n.Y, n.X);
        double reach = 9 + _rnd.NextDouble() * 7;
        var end = p + n * reach + side * ((_rnd.NextDouble() * 2 - 1) * reach * 0.6);
        var b = new Bolt { Life = 7 + _rnd.Next(4) };
        b.Main = Jagged(p, end, reach * 0.35, 4);
        // Forks: from points along the main bolt, shorter, angled away.
        int forks = 1 + _rnd.Next(3);
        for (int i = 0; i < forks; i++)
        {
            int idx = 2 + _rnd.Next(Math.Max(1, b.Main.Count - 4));
            var from = b.Main[Math.Min(idx, b.Main.Count - 1)];
            double len = reach * (0.3 + _rnd.NextDouble() * 0.35);
            var dir = n * 0.7 + side * ((_rnd.Next(2) == 0 ? -1 : 1) * (0.6 + _rnd.NextDouble() * 0.5));
            dir.Normalize();
            b.Branches.Add(Jagged(from, from + dir * len, len * 0.3, 3));
        }
        // Lightning flickers: it blinks out for a frame and comes back once or twice.
        b.DarkFrames = _rnd.NextDouble() < 0.7 ? new[] { 2, 5 } : new[] { 3 };
        _bolts.Add(b);
        // The edge lights up where it struck.
        AddCrackle(f - 0.06, 0.12, 4);
        _flash = 1;
    }

    // ───────── running ─────────

    void Frame()
    {
        if (!Showing()) { _bolts.Clear(); _charge = -1; _flash = 0; _frame.Stop(); InvalidateVisual(); return; }

        if (_charge >= 0)
        {
            AddCrackle(_charge - 0.05, 0.05 + _chargeSpeed, 5);   // the trail it leaves
            _charge += _chargeSpeed;
            _chargeLaps += _chargeSpeed;
            if (_chargeLaps >= 1)                                 // one full lap, then it strikes
            {
                _chargeLaps = 0;
                Strike(_charge % 1);
                _charge = -1;
            }
        }

        foreach (var b in _bolts)
        {
            b.Age++;
            if (b.Crackle) Rebuild(b);
        }
        _bolts.RemoveAll(b => b.Age >= b.Life);
        _flash = Math.Max(0, _flash - 0.18);

        if (_bolts.Count == 0 && _charge < 0 && _flash <= 0) _frame.Stop();
        InvalidateVisual();
    }

    double _chargeLaps;

    protected override void OnRender(DrawingContext dc)
    {
        if (_flash > 0)
        {
            double cx = ActualWidth / 2, cy = ActualHeight / 2;
            dc.PushOpacity(_flash * 0.8);
            dc.DrawEllipse(Glow, null, new Point(cx, cy), ActualWidth * 0.62, ActualHeight * 0.62);
            dc.Pop();
        }
        if (_charge >= 0)
        {
            var (p, _) = OnEdge(_charge);
            dc.DrawEllipse(Glow, null, p, 7, 7);
            dc.DrawEllipse(Brushes.White, null, p, 1.6, 1.6);
        }

        foreach (var b in _bolts)
        {
            if (b.Main.Count < 2 || b.DarkFrames.Contains(b.Age)) continue;
            // Strikes are brightest at the start and fade; crackles flicker at random.
            double alpha = b.Crackle ? 0.55 + _rnd.NextDouble() * 0.45 : Math.Max(0.15, 1 - (double)b.Age / b.Life);
            dc.PushOpacity(alpha);
            var main = Geometry(b.Main);
            dc.DrawGeometry(null, Outer, main);
            dc.DrawGeometry(null, Inner, main);
            dc.DrawGeometry(null, Core, main);
            foreach (var br in b.Branches)
            {
                if (br.Count < 2) continue;
                var g = Geometry(br);
                dc.DrawGeometry(null, Inner, g);
                dc.DrawGeometry(null, BranchCore, g);
            }
            dc.Pop();
        }
    }

    static StreamGeometry Geometry(List<Point> pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open()) { c.BeginFigure(pts[0], false, false); c.PolyLineTo(pts.Skip(1).ToList(), true, true); }
        g.Freeze();
        return g;
    }
}
