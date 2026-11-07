using System.Windows;
using System.Windows.Media;
using Pulse.Core.Crosshair;

namespace Pulse.Ui;

/// <summary>
/// Draws a <see cref="CrosshairStyle"/> centered in itself, in whole screen pixels (crisp at any Windows scaling),
/// the way VALORANT draws its crosshair: four lines around a gap, optional outlines, center dot, outer lines.
/// Used by the in-game overlay and the editor's preview.
/// </summary>
public sealed class CrosshairVisual : FrameworkElement
{
    CrosshairStyle _style;
    double _zoom = 1;

    public CrosshairStyle Crosshair { get => _style; set { _style = value; InvalidateVisual(); } }
    /// <summary>Extra magnification for previews (the overlay uses 1).</summary>
    public double Zoom { get => _zoom; set { _zoom = value; InvalidateVisual(); } }

    public CrosshairVisual()
    {
        // Every edge lands on a whole screen pixel (see P below), so lines stay sharp with smoothing on and the ring stays round.
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        IsHitTestVisible = false;
    }

    /// <summary>A drawn (pixel grid) or picture crosshair, centered, in whole screen pixels.</summary>
    void RenderCustom(DrawingContext dc, CrosshairStyle s, double dpi)
    {
        double k = Math.Max(0.1, s.Size) * _zoom;
        double cx = Math.Round(ActualWidth * dpi / 2) / dpi, cy = Math.Round(ActualHeight * dpi / 2) / dpi;
        if (s.Mode == "image")
        {
            var img = LoadImage(s.ImageFile);
            if (img == null) return;
            double w = Math.Round(img.PixelWidth * k) / dpi, h = Math.Round(img.PixelHeight * k) / dpi;
            RenderOptions.SetBitmapScalingMode(this, k >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            dc.DrawImage(img, new Rect(Math.Round((cx - w / 2) * dpi) / dpi, Math.Round((cy - h / 2) * dpi) / dpi, w, h));
            return;
        }
        var cells = s.PixelCells();
        int n = s.GridSize;
        double px = Math.Max(1, Math.Round(k)) / dpi; // one drawn pixel = whole screen pixels
        double left = cx - n / 2 * px - px / 2 + (n % 2 == 0 ? px / 2 : 0), top = cy - n / 2 * px - px / 2 + (n % 2 == 0 ? px / 2 : 0);
        left = Math.Round(left * dpi) / dpi; top = Math.Round(top * dpi) / dpi;
        var brushes = new Dictionary<string, Brush>();
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                var c = cells[y * n + x];
                if (string.IsNullOrEmpty(c)) continue;
                if (!brushes.TryGetValue(c, out var b)) { b = new SolidColorBrush(ParseColor(c)); b.Freeze(); brushes[c] = b; }
                dc.DrawRectangle(b, null, new Rect(left + x * px, top + y * px, px, px));
            }
    }

    public static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return Colors.White; }
    }

    static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapImage> Images = new();

    public static System.Windows.Media.Imaging.BitmapImage LoadImage(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        lock (Images)
        {
            if (Images.TryGetValue(path, out var hit)) return hit;
            try
            {
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit(); img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; img.UriSource = new Uri(path); img.EndInit(); img.Freeze();
                return Images[path] = img;
            }
            catch { return null; }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var s = _style;
        if (s == null) return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (s.Mode == "draw" || s.Mode == "image") { RenderCustom(dc, s, dpi); return; }
        double k = Math.Max(0.1, s.Size) * _zoom;           // screen pixels per crosshair pixel
        double P(double px) => Math.Round(px * k) / dpi;    // crosshair pixels → whole screen pixels → WPF units
        double cx = Math.Round(ActualWidth * dpi / 2) / dpi, cy = Math.Round(ActualHeight * dpi / 2) / dpi;

        var color = ParseColor(s.Color);
        Brush Fill(double opacity) { var b = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B)); b.Freeze(); return b; }
        var outlineBrush = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(s.OutlineOpacity, 0, 1)), 0, 0, 0));
        outlineBrush.Freeze();
        double ot = s.Outlines ? Math.Max(1 / dpi, P(s.OutlineThickness)) : 0;

        // Rectangles relative to the center, in WPF units already snapped to screen pixels.
        var rects = new List<(Rect R, double Opacity)>();
        void Line(CrosshairLines l)
        {
            if (!l.Show || l.Length <= 0 || l.Thickness <= 0) return;
            double t = Math.Max(1 / dpi, P(l.Thickness)), o = P(l.Offset), len = P(l.Length), vlen = P(l.VLength);
            double half = Math.Floor(t * dpi / 2) / dpi; // odd thickness: the extra pixel goes down/right
            rects.Add((new Rect(o, -half, len, t), l.Opacity));              // right
            rects.Add((new Rect(-o - len, -half, len, t), l.Opacity));       // left
            if (vlen > 0)
            {
                rects.Add((new Rect(-half, o, t, vlen), l.Opacity));         // down
                if (!s.NoTop) rects.Add((new Rect(-half, -o - vlen, t, vlen), l.Opacity)); // up
            }
        }
        Line(s.Inner);
        Line(s.Outer);
        if (s.Dot && s.DotSize > 0)
        {
            double z = Math.Max(1 / dpi, P(s.DotSize)), half = Math.Floor(z * dpi / 2) / dpi;
            rects.Add((new Rect(-half, -half, z, z), s.DotOpacity));
        }

        dc.PushTransform(new TranslateTransform(cx, cy));
        if (ot > 0)
            foreach (var (r, _) in rects)
            {
                var o = r; o.Inflate(ot, ot);
                dc.DrawRectangle(outlineBrush, null, o);
            }
        if (s.Circle && s.CircleRadius > 0)
        {
            double rr = P(s.CircleRadius), th = Math.Max(1 / dpi, s.CircleThickness * k / dpi);
            if (ot > 0) { var op = new Pen(outlineBrush, th + 2 * ot); op.Freeze(); dc.DrawEllipse(null, op, new Point(0, 0), rr, rr); }
            var pen = new Pen(Fill(s.CircleOpacity), th); pen.Freeze();
            dc.DrawEllipse(null, pen, new Point(0, 0), rr, rr);
        }
        foreach (var (r, op) in rects) dc.DrawRectangle(Fill(op), null, r);
        dc.Pop();
    }
}
