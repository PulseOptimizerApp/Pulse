using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Pulse.Core.Guide;

namespace Pulse.Ui;

/// <summary>Shared drawing bits for the guide's charts (theme colors, text).</summary>
public abstract class GuideChart : FrameworkElement
{
    public static readonly Color Green = Color.FromRgb(0x34, 0xD3, 0x99), Amber = Color.FromRgb(0xFB, 0xBF, 0x24),
        Red = Color.FromRgb(0xF8, 0x71, 0x71), Cyan = Color.FromRgb(0x22, 0xD3, 0xEE), Violet = Color.FromRgb(0x8B, 0x5C, 0xF6);

    /// <summary>Green when close to launch price, amber when noticeably above, red when far above.</summary>
    public static Color ForMarkup(double pct) => pct < 0.15 ? Green : pct < 0.5 ? Amber : Red;

    protected Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
    protected Brush TextB => Res("TextBrush", Brushes.White);
    protected Brush DimB => Res("TextDimBrush", Brushes.Gray);
    protected Brush FaintB => Res("TextFaintBrush", Brushes.DimGray);
    protected Brush LineB => Res("BorderBrush", Brushes.DimGray);
    protected Brush HoverB => Res("SurfaceHoverBrush", new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)));

    protected FormattedText Txt(string s, double size, Brush b, bool bold = false)
    {
        var ff = TextElementFont();
        return new FormattedText(s ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(ff, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    FontFamily TextElementFont() => System.Windows.Documents.TextElement.GetFontFamily(this);

    protected static string Money(double v) => "$" + v.ToString("#,0", CultureInfo.InvariantCulture);

    protected static Pen P(Brush b, double w, bool dashed = false)
    {
        var p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (dashed) p.DashStyle = new DashStyle(new[] { 2.0, 3.0 }, 0);
        p.Freeze();
        return p;
    }

    protected static SolidColorBrush B(Color c, byte a = 255) { var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); b.Freeze(); return b; }

    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); Hover(-1); }

    int _hover = -1;
    protected int HoverIndex => _hover;
    protected void Hover(int i) { if (i == _hover) return; _hover = i; InvalidateVisual(); }
}

/// <summary>
/// Graphics cards, one row each: a hollow dot at the launch price, a bar to today's best price (colored by how far
/// above launch it is), and on hover a diamond at the lowest price ever seen.
/// </summary>
public sealed class GpuPriceChart : GuideChart
{
    const double Row = 46, Head = 30, NameW = 200, RightPad = 112;
    readonly GpuPrice[] _gpus;
    readonly Func<string, string> _t;

    public GpuPriceChart(IEnumerable<GpuPrice> gpus, Func<string, string> t)
    {
        _gpus = gpus.OrderBy(g => g.Now).ToArray();
        _t = t;
        Height = Head + _gpus.Length * Row + 4;
        Cursor = Cursors.Arrow;
    }

    double Max => Math.Max(500, Math.Ceiling(_gpus.Max(g => Math.Max(g.Now, g.Msrp)) / 250.0) * 250);
    double X(double v, double w) => NameW + (w - NameW - RightPad) * v / Max;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var y = e.GetPosition(this).Y - Head;
        Hover(y < 0 ? -1 : Math.Min(_gpus.Length - 1, (int)(y / Row)));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        if (w < NameW + RightPad + 60 || _gpus.Length == 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight)); // hit testing everywhere

        // Scale: light vertical lines every $250, labelled every $500.
        var grid = P(LineB, 1, dashed: true);
        for (double v = 0; v <= Max; v += 250)
        {
            double x = Math.Round(X(v, w)) + 0.5;
            dc.DrawLine(grid, new Point(x, Head - 6), new Point(x, ActualHeight - 4));
            if (v % 500 == 0)
            {
                var t = Txt(Money(v), 11, FaintB);
                dc.DrawText(t, new Point(x - t.Width / 2, 4));
            }
        }

        for (int i = 0; i < _gpus.Length; i++)
        {
            var g = _gpus[i];
            double top = Head + i * Row, cy = top + Row / 2;
            if (i == HoverIndex) dc.DrawRoundedRectangle(HoverB, null, new Rect(-8, top + 2, w + 8, Row - 4), 8, 8);

            var name = Txt(g.Name, 13, TextB, bold: true);
            name.MaxTextWidth = NameW - 14; name.MaxLineCount = 1; name.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(name, new Point(0, cy - 15));
            dc.DrawText(Txt(_t(g.Tier), 11, FaintB), new Point(0, cy + 2));

            double pct = g.Msrp > 0 ? (g.Now - g.Msrp) / (double)g.Msrp : 0;
            var c = ForMarkup(pct);
            double x0 = X(g.Msrp, w), x1 = X(g.Now, w);

            // Bar from launch to now, fading in from the launch end.
            var grad = new LinearGradientBrush(Color.FromArgb(0x30, c.R, c.G, c.B), c, new Point(0, 0), new Point(1, 0));
            grad.Freeze();
            if (x1 > x0 + 1) dc.DrawRoundedRectangle(grad, null, new Rect(x0, cy - 3, x1 - x0, 6), 3, 3);
            dc.DrawEllipse(Res("SurfaceBrush", Brushes.Black), P(DimB, 2), new Point(x0, cy), 5, 5);
            dc.DrawEllipse(B(c), null, new Point(x1, cy), 6, 6);

            if (i == HoverIndex && g.Lowest > 0)
            {
                double xl = X(g.Lowest, w);
                var d = new StreamGeometry();
                using (var ctx = d.Open())
                {
                    ctx.BeginFigure(new Point(xl, cy - 6), true, true);
                    ctx.PolyLineTo(new[] { new Point(xl + 5, cy), new Point(xl, cy + 6), new Point(xl - 5, cy) }, true, true);
                }
                d.Freeze();
                dc.DrawGeometry(B(Cyan), null, d);
                var lt = Txt(_t("Lowest ever") + " " + Money(g.Lowest), 10.5, B(Cyan));
                dc.DrawText(lt, new Point(Math.Max(NameW, xl - lt.Width / 2), cy + 8));
            }

            // Price and markup to the right of the dot.
            var price = Txt(Money(g.Now), 13, TextB, bold: true);
            double px = x1 + 12;
            dc.DrawText(price, new Point(px, cy - price.Height / 2));
            var mk = Txt((pct >= 0 ? "+" : "") + Math.Round(pct * 100).ToString(CultureInfo.InvariantCulture) + "%", 11.5, B(c), bold: true);
            dc.DrawText(mk, new Point(px + price.Width + 7, cy - mk.Height / 2));
        }
    }
}

/// <summary>A line chart of a 32 GB DDR5 kit's price over time: cheapest (filled) and average listing (dashed).</summary>
public sealed class RamHistoryChart : GuideChart
{
    const double Left = 46, Right = 16, Top = 16, Bottom = 30;
    readonly RamPoint[] _pts;
    readonly Func<string, string> _t;

    public RamHistoryChart(IEnumerable<RamPoint> pts, Func<string, string> t)
    {
        _pts = pts.OrderBy(p => p.Date).ToArray();
        _t = t;
        Height = 250;
    }

    double MaxY => Math.Max(100, Math.Ceiling(_pts.Max(p => Math.Max(p.Cheapest, p.Average)) / 200.0) * 200);
    DateTime T0 => _pts[0].Date;
    double Span => Math.Max(1, (_pts[^1].Date - T0).TotalDays);
    double X(DateTime d) => Left + (ActualWidth - Left - Right) * (d - T0).TotalDays / Span;
    double Y(double v) => Top + (ActualHeight - Top - Bottom) * (1 - v / MaxY);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pts.Length == 0) return;
        double mx = e.GetPosition(this).X;
        int best = 0;
        for (int i = 1; i < _pts.Length; i++) if (Math.Abs(X(_pts[i].Date) - mx) < Math.Abs(X(_pts[best].Date) - mx)) best = i;
        Hover(best);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_pts.Length < 2 || ActualWidth < 200) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        double bottom = ActualHeight - Bottom;

        // Horizontal grid with prices.
        var grid = P(LineB, 1, dashed: true);
        for (double v = 0; v <= MaxY; v += MaxY > 800 ? 200 : 100)
        {
            double y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(grid, new Point(Left, y), new Point(ActualWidth - Right, y));
            var t = Txt(Money(v), 10.5, FaintB);
            dc.DrawText(t, new Point(Left - 8 - t.Width, y - t.Height / 2));
        }
        // Month labels every 3 months.
        var m = new DateTime(T0.Year, T0.Month, 1).AddMonths(1);
        while (m <= _pts[^1].Date)
        {
            if (m.Month % 3 == 1)
            {
                var label = m.Month == 1 ? m.ToString("MMM yyyy", CultureInfo.CurrentCulture) : m.ToString("MMM", CultureInfo.CurrentCulture);
                var t = Txt(label, 10.5, FaintB);
                dc.DrawText(t, new Point(X(m) - t.Width / 2, bottom + 8));
            }
            m = m.AddMonths(1);
        }

        Geometry Line(Func<RamPoint, int> v, bool closed)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(X(_pts[0].Date), closed ? bottom : Y(v(_pts[0]))), closed, closed);
                if (closed) c.LineTo(new Point(X(_pts[0].Date), Y(v(_pts[0]))), false, false);
                c.PolyLineTo(_pts.Skip(1).Select(p => new Point(X(p.Date), Y(v(p)))).ToList(), true, true);
                if (closed) c.LineTo(new Point(X(_pts[^1].Date), bottom), false, false);
            }
            g.Freeze();
            return g;
        }

        var fill = new LinearGradientBrush(Color.FromArgb(0x55, Cyan.R, Cyan.G, Cyan.B), Color.FromArgb(0x00, Cyan.R, Cyan.G, Cyan.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, Line(p => p.Cheapest, true));
        dc.DrawGeometry(null, P(B(Violet, 0xC0), 2, dashed: true), Line(p => p.Average, false));
        dc.DrawGeometry(null, P(B(Cyan), 2.5), Line(p => p.Cheapest, false));

        int i = HoverIndex >= 0 ? HoverIndex : _pts.Length - 1;
        var pt = _pts[i];
        double hx = X(pt.Date);
        dc.DrawLine(P(DimB, 1, dashed: true), new Point(hx, Top), new Point(hx, bottom));
        dc.DrawEllipse(Res("SurfaceBrush", Brushes.Black), P(B(Violet), 2), new Point(hx, Y(pt.Average)), 4, 4);
        dc.DrawEllipse(B(Cyan), P(Res("SurfaceBrush", Brushes.Black), 2), new Point(hx, Y(pt.Cheapest)), 5.5, 5.5);

        // Value box next to the marker.
        var d = Txt(pt.Date.ToString("MMM d, yyyy", CultureInfo.CurrentCulture), 11, DimB);
        var a = Txt(_t("Cheapest") + "  " + Money(pt.Cheapest), 12.5, B(Cyan), bold: true);
        var b = Txt(_t("Average") + "  " + Money(pt.Average), 12, B(Violet));
        double bw = Math.Max(d.Width, Math.Max(a.Width, b.Width)) + 20, bh = d.Height + a.Height + b.Height + 14;
        double bx = hx + 12 + bw > ActualWidth - Right ? hx - 12 - bw : hx + 12;
        double by = Math.Clamp(Y(pt.Cheapest) - bh - 10, Top, bottom - bh);
        dc.DrawRoundedRectangle(Res("Surface2Brush", Brushes.Black), P(LineB, 1), new Rect(bx, by, bw, bh), 8, 8);
        dc.DrawText(d, new Point(bx + 10, by + 6));
        dc.DrawText(a, new Point(bx + 10, by + 6 + d.Height + 2));
        dc.DrawText(b, new Point(bx + 10, by + 6 + d.Height + a.Height + 4));
    }
}
