using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pulse.Core.Clips;

namespace Pulse.Ui;

/// <summary>
/// Draws the input overlay: the left of a keyboard plus a mouse, or an Xbox or PlayStation controller, with what's
/// held lit up. The same drawing is shown over the clip player and burned into exported copies.
/// </summary>
public sealed class InputOverlayView : FrameworkElement
{
    InputFrame _frame;
    string _kind = "kbm";

    public InputFrame Frame { get => _frame; set { if (ReferenceEquals(_frame, value)) return; _frame = value; InvalidateVisual(); } }
    /// <summary>"kbm", "xbox" or "ps".</summary>
    public string Kind { get => _kind; set { _kind = value ?? "kbm"; InvalidateMeasure(); InvalidateVisual(); } }

    /// <summary>How tall the overlay is, as a share of the picture's height (on screen and in exported copies).</summary>
    public const double Share = 0.17;

    public static Size BaseSize(string kind) => kind == "kbm" ? new Size(Pad + KbW + 12 + MouseW + Pad, Pad + KbH + Pad) : new Size(330, 196);

    protected override Size MeasureOverride(Size available) => BaseSize(_kind);

    // ───────── palette ─────────

    // A soft dark plate behind everything, so it reads over any game without hiding much of it.
    static readonly Brush Plate = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0x08, 0x09, 0x0C)));
    static readonly Pen PlateEdge = FrozenPen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), 1);
    static readonly Brush KeyFill = Frozen(new SolidColorBrush(Color.FromArgb(0x59, 0x2A, 0x2D, 0x36)));
    static readonly Brush KeyText = Frozen(new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)));
    static readonly Pen KeyEdge = FrozenPen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1);
    static readonly Brush Lit = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8)));
    static readonly Brush LitText = Frozen(new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x10)));
    static readonly Pen LitEdge = FrozenPen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), 1);
    static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    static Brush Frozen(Brush b) { b.Freeze(); return b; }
    static Pen FrozenPen(Brush b, double w) { var p = new Pen(b, w); p.Freeze(); return p; }

    protected override void OnRender(DrawingContext dc)
    {
        if (_kind == "kbm") DrawKeyboardAndMouse(dc); else DrawController(dc, _kind == "ps");
    }

    // ───────── keyboard + mouse ─────────

    const double U = 30, G = 4, Pad = 10, MouseW = 62;
    // The keys games use, in their real positions. Each: (label, virtual key, width in key units); vk -1 = gap.
    static readonly (string L, int Vk, double W)[][] Rows =
    {
        new[] { ("Esc", 0x1B, 1.3), ("1", 0x31, 1), ("2", 0x32, 1), ("3", 0x33, 1), ("4", 0x34, 1), ("5", 0x35, 1) },
        new[] { ("Tab", 0x09, 1.3), ("Q", 0x51, 1), ("W", 0x57, 1), ("E", 0x45, 1), ("R", 0x52, 1), ("T", 0x54, 1) },
        new[] { ("Shift", 0x10, 1.6), ("A", 0x41, 1), ("S", 0x53, 1), ("D", 0x44, 1), ("F", 0x46, 1), ("G", 0x47, 1) },
        new[] { ("Ctrl", 0x11, 1.3), ("Z", 0x5A, 1), ("X", 0x58, 1), ("C", 0x43, 1), ("V", 0x56, 1), ("B", 0x42, 1) },
        new[] { ("Alt", 0x12, 1.3), ("", 0x20, 4.7) },
    };
    static readonly double KbW = Rows.Max(r => r.Sum(k => k.W * U) + (r.Length - 1) * G);
    static readonly double KbH = Rows.Length * U + (Rows.Length - 1) * G;

    void DrawKeyboardAndMouse(DrawingContext dc)
    {
        var size = BaseSize("kbm");
        dc.DrawRoundedRectangle(Plate, PlateEdge, new Rect(0, 0, size.Width, size.Height), 12, 12);
        var held = new HashSet<int>(_frame?.Keys ?? Array.Empty<int>());
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int r = 0; r < Rows.Length; r++)
        {
            double x = Pad, y = Pad + r * (U + G);
            foreach (var (label, vk, w) in Rows[r])
            {
                var rect = new Rect(x, y, w * U, U);
                x += w * U + G;
                if (vk < 0) continue;
                bool on = held.Contains(vk);
                dc.DrawRoundedRectangle(on ? Lit : KeyFill, on ? LitEdge : KeyEdge, rect, 6, 6);
                if (label.Length > 0)
                {
                    var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, label.Length > 1 ? 9.5 : 12.5, on ? LitText : KeyText, dip);
                    dc.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
                }
            }
        }
        DrawMouse(dc, Pad + KbW + 12, Pad, (MouseButton)(_frame?.Mouse ?? 0));
    }

    static void DrawMouse(DrawingContext dc, double x, double y, MouseButton m)
    {
        double w = MouseW, h = KbH;
        var body = new Rect(x, y, w, h);
        var shell = new RectangleGeometry(body, w / 2, w / 2 + 4);
        dc.DrawGeometry(KeyFill, KeyEdge, shell);
        double split = y + h * 0.42;
        // Left and right buttons: the top part, split down the middle.
        dc.PushClip(shell);
        if (m.HasFlag(MouseButton.Left)) dc.DrawRectangle(Lit, null, new Rect(x, y, w / 2, split - y));
        if (m.HasFlag(MouseButton.Right)) dc.DrawRectangle(Lit, null, new Rect(x + w / 2, y, w / 2, split - y));
        dc.Pop();
        dc.DrawLine(KeyEdge, new Point(x + w / 2, y), new Point(x + w / 2, split));
        dc.DrawLine(KeyEdge, new Point(x + 3, split), new Point(x + w - 3, split));
        // Wheel.
        bool wheel = m.HasFlag(MouseButton.Middle) || m.HasFlag(MouseButton.WheelUp) || m.HasFlag(MouseButton.WheelDown);
        dc.DrawRoundedRectangle(wheel ? Lit : Plate, wheel ? LitEdge : KeyEdge, new Rect(x + w / 2 - 5, y + 14, 10, 22), 5, 5);
        // Side buttons on the left edge.
        double sy = split + 10;
        dc.DrawRoundedRectangle(m.HasFlag(MouseButton.Forward) ? Lit : KeyFill, m.HasFlag(MouseButton.Forward) ? LitEdge : KeyEdge, new Rect(x - 3, sy, 8, 18), 3, 3);
        dc.DrawRoundedRectangle(m.HasFlag(MouseButton.Back) ? Lit : KeyFill, m.HasFlag(MouseButton.Back) ? LitEdge : KeyEdge, new Rect(x - 3, sy + 22, 8, 18), 3, 3);
    }

    // ───────── controller ─────────

    void DrawController(DrawingContext dc, bool ps)
    {
        var p = (PadButton)(_frame?.Pad ?? 0);
        var st = _frame?.Sticks ?? new int[4];
        var tr = _frame?.Triggers ?? new int[2];
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        bool On(PadButton b) => (p & b) != 0;

        // Body.
        var body = new RectangleGeometry(new Rect(0, 30, 330, 166), 60, 60);
        dc.DrawGeometry(KeyFill, KeyEdge, body);

        // Triggers (fill shows how far they're pressed) and bumpers.
        Trigger(dc, new Rect(30, 0, 78, 12), tr[0]);
        Trigger(dc, new Rect(222, 0, 78, 12), tr[1]);
        Pill(dc, new Rect(30, 16, 78, 12), On(PadButton.LeftBumper), ps ? "L1" : "LB", dip);
        Pill(dc, new Rect(222, 16, 78, 12), On(PadButton.RightBumper), ps ? "R1" : "RB", dip);

        // Xbox: left stick top, d-pad below. PlayStation: d-pad top, sticks both low.
        var dpad = ps ? new Point(70, 92) : new Point(118, 150);
        var lstick = ps ? new Point(118, 150) : new Point(70, 92);
        var face = new Point(260, 92);
        var rstick = new Point(212, 150);

        Stick(dc, lstick, st[0], st[1], On(PadButton.LeftStick));
        Stick(dc, rstick, st[2], st[3], On(PadButton.RightStick));
        DPad(dc, dpad, p);

        // Face buttons: Xbox letters in their colors, PlayStation shapes.
        (PadButton B, double Dx, double Dy, string Xbox, Color C)[] faces =
        {
            (PadButton.South, 0, 22, "A", Color.FromRgb(0x6C, 0xC0, 0x4A)),
            (PadButton.East, 22, 0, "B", Color.FromRgb(0xE2, 0x4B, 0x4B)),
            (PadButton.West, -22, 0, "X", Color.FromRgb(0x3B, 0x82, 0xF6)),
            (PadButton.North, 0, -22, "Y", Color.FromRgb(0xF5, 0xC2, 0x3C)),
        };
        foreach (var (b, dx, dy, letter, c) in faces)
        {
            var center = new Point(face.X + dx, face.Y + dy);
            bool on = On(b);
            dc.DrawEllipse(on ? Lit : Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0x0E, 0x10, 0x16))), on ? LitEdge : KeyEdge, center, 12, 12);
            var ink = on ? LitText : new SolidColorBrush(ps ? PsColor(b) : c);
            if (ps) PsShape(dc, b, center, new Pen(ink, 2));
            else
            {
                var ft = new FormattedText(letter, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 13, ink, dip);
                dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
            }
        }

        // Middle: view/menu (create/options), home, and the PlayStation touchpad.
        if (ps) dc.DrawRoundedRectangle(On(PadButton.Touchpad) ? Lit : Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0x0E, 0x10, 0x16))), On(PadButton.Touchpad) ? LitEdge : KeyEdge, new Rect(127, 44, 76, 40), 8, 8);
        Pill(dc, new Rect(ps ? 108 : 132, ps ? 50 : 84, 16, 9), On(PadButton.Back), "", dip);
        Pill(dc, new Rect(ps ? 206 : 182, ps ? 50 : 84, 16, 9), On(PadButton.Start), "", dip);
        dc.DrawEllipse(On(PadButton.Home) ? Lit : KeyFill, On(PadButton.Home) ? LitEdge : KeyEdge, new Point(165, ps ? 100 : 62), 9, 9);
    }

    static Color PsColor(PadButton b) => b switch
    {
        PadButton.South => Color.FromRgb(0x7D, 0xA6, 0xF0),
        PadButton.East => Color.FromRgb(0xF0, 0x6A, 0x7A),
        PadButton.West => Color.FromRgb(0xE3, 0x8F, 0xD3),
        _ => Color.FromRgb(0x5E, 0xD1, 0xB2),
    };

    static void PsShape(DrawingContext dc, PadButton b, Point c, Pen pen)
    {
        const double r = 5.5;
        switch (b)
        {
            case PadButton.South:
                dc.DrawLine(pen, new Point(c.X - r, c.Y - r), new Point(c.X + r, c.Y + r));
                dc.DrawLine(pen, new Point(c.X - r, c.Y + r), new Point(c.X + r, c.Y - r));
                break;
            case PadButton.East: dc.DrawEllipse(null, pen, c, r, r); break;
            case PadButton.West: dc.DrawRectangle(null, pen, new Rect(c.X - r, c.Y - r, 2 * r, 2 * r)); break;
            default:
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(c.X, c.Y - r - 1), false, true);
                    ctx.LineTo(new Point(c.X + r + 1, c.Y + r - 1), true, false);
                    ctx.LineTo(new Point(c.X - r - 1, c.Y + r - 1), true, false);
                }
                dc.DrawGeometry(null, pen, g);
                break;
        }
    }

    static void Trigger(DrawingContext dc, Rect r, int percent)
    {
        dc.DrawRoundedRectangle(KeyFill, KeyEdge, r, 5, 5);
        if (percent <= 3) return;
        dc.DrawRoundedRectangle(Lit, null, new Rect(r.X, r.Y, Math.Max(10, r.Width * percent / 100.0), r.Height), 5, 5);
    }

    static void Pill(DrawingContext dc, Rect r, bool on, string label, double dip)
    {
        dc.DrawRoundedRectangle(on ? Lit : KeyFill, on ? LitEdge : KeyEdge, r, r.Height / 2, r.Height / 2);
        if (label.Length == 0) return;
        var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 8.5, on ? LitText : KeyText, dip);
        dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
    }

    static void Stick(DrawingContext dc, Point c, int x, int y, bool clicked)
    {
        dc.DrawEllipse(Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0x0E, 0x10, 0x16))), KeyEdge, c, 26, 26);
        var knob = new Point(c.X + x / 100.0 * 13, c.Y - y / 100.0 * 13);
        bool moved = Math.Abs(x) > 15 || Math.Abs(y) > 15;
        dc.DrawEllipse(clicked || moved ? Lit : KeyFill, clicked ? LitEdge : KeyEdge, knob, 15, 15);
    }

    static void DPad(DrawingContext dc, Point c, PadButton p)
    {
        const double s = 13;
        void Arm(PadButton b, Rect r)
        {
            bool on = (p & b) != 0;
            dc.DrawRoundedRectangle(on ? Lit : KeyFill, on ? LitEdge : KeyEdge, r, 3, 3);
        }
        Arm(PadButton.Up, new Rect(c.X - s / 2, c.Y - s * 1.5, s, s));
        Arm(PadButton.Down, new Rect(c.X - s / 2, c.Y + s * 0.5, s, s));
        Arm(PadButton.Left, new Rect(c.X - s * 1.5, c.Y - s / 2, s, s));
        Arm(PadButton.Right, new Rect(c.X + s * 0.5, c.Y - s / 2, s, s));
    }

    // ───────── export ─────────

    /// <summary>The overlay for one frame as a picture, for burning into a clip. Call on the UI thread.</summary>
    public static BitmapSource Render(InputFrame frame, string kind, double scale)
    {
        var v = new InputOverlayView { Kind = kind, Frame = frame };
        var size = BaseSize(kind);
        v.Measure(size);
        v.Arrange(new Rect(size));
        int w = (int)Math.Ceiling(size.Width * scale), h = (int)Math.Ceiling(size.Height * scale);
        // Rendering at a higher DPI scales the drawing up sharply (no blurry bitmap stretch).
        var bmp = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(v);
        bmp.Freeze();
        return bmp;
    }
}
