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

    public static Size BaseSize(string kind) => kind == "kbm" ? new Size(Pad + KbW + 14 + MouseW + Pad, Pad + KbH + Pad) : new Size(330, 196);

    protected override Size MeasureOverride(Size available) => BaseSize(_kind);

    // ───────── palette ─────────

    /// <summary>One look for the overlay: the panel behind it, keys at rest, and keys being pressed.</summary>
    public sealed record OverlayTheme(string Id, string Name, Brush Plate, Pen PlateEdge, Brush KeyFill, Pen KeyEdge, Brush KeyText, Brush Lit, Pen LitEdge, Brush LitText);

    static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    static Brush B(uint argb) => Frozen(new SolidColorBrush(C(argb)));
    static Pen P(uint argb, double w = 1) => FrozenPen(new SolidColorBrush(C(argb)), w);

    /// <summary>The overlay looks people can pick (Clips settings).</summary>
    public static readonly OverlayTheme[] Themes =
    {
        // A soft dark plate behind everything, so it reads over any game without hiding much of it.
        new("dark", "Dark", B(0x8C08090C), P(0x26FFFFFF), B(0x592A2D36), P(0x30FFFFFF), B(0xD9FFFFFF), B(0xFFF2F4F8), P(0xFFFFFFFF), B(0xFF0B0C10)),
        // No panel: see-through keys only.
        new("glass", "Glass", Brushes.Transparent, null, B(0x40FFFFFF), P(0x55FFFFFF), B(0xF2FFFFFF), B(0xE6FFFFFF), P(0xFFFFFFFF), B(0xFF0B0C10)),
        // Pressed keys light up in Pulse's cyan-to-violet.
        new("neon", "Neon", B(0x99070812), P(0x405B8CFF), B(0x4D1B2140), P(0x665B8CFF), B(0xCCBFD3FF),
            Frozen(new LinearGradientBrush(C(0xFF22D3EE), C(0xFF8B5CF6), 45)), P(0xFF9EE7FF, 1.4), B(0xFFFFFFFF)),
        // Light keys, for bright games.
        new("light", "Light", B(0xB3F4F5F8), P(0x33000000), B(0xE6FFFFFF), P(0x26000000), B(0xFF20232B), B(0xFF5B8CFF), P(0xFF3B6CEF), B(0xFFFFFFFF)),
        // Outlines only: the lightest look, pressed keys fill in.
        new("outline", "Outline", Brushes.Transparent, null, Brushes.Transparent, P(0xB3FFFFFF, 1.4), B(0xE6FFFFFF), B(0xFFFFFFFF), P(0xFFFFFFFF, 1.4), B(0xFF0B0C10)),
    };

    public static OverlayTheme ThemeFor(string id) => Themes.FirstOrDefault(t => t.Id == id) ?? Themes[0];

    OverlayTheme _theme = Themes[0];
    /// <summary>The look's id ("dark", "glass", "neon", "light", "outline").</summary>
    public string Theme { get => _theme.Id; set { _theme = ThemeFor(value); InvalidateVisual(); } }

    Brush Plate => _theme.Plate; Pen PlateEdge => _theme.PlateEdge;
    Brush KeyFill => _theme.KeyFill; Pen KeyEdge => _theme.KeyEdge; Brush KeyText => _theme.KeyText;
    Brush Lit => _theme.Lit; Pen LitEdge => _theme.LitEdge; Brush LitText => _theme.LitText;
    static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    static Brush Frozen(Brush b) { b.Freeze(); return b; }
    static Pen FrozenPen(Brush b, double w) { var p = new Pen(b, w); p.Freeze(); return p; }

    protected override void OnRender(DrawingContext dc)
    {
        if (_kind == "kbm") DrawKeyboardAndMouse(dc); else DrawController(dc, _kind == "ps");
    }

    // ───────── keyboard + mouse ─────────

    const double U = 30, G = 4, Pad = 10, MouseW = 64, MouseH = 104, Split = 42;
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
                // Keycaps: a darker lip under keys at rest; a pressed key sinks onto it.
                if (!on && _theme.Plate != Brushes.Transparent) dc.DrawRoundedRectangle(Lip, null, new Rect(rect.X, rect.Y + 2, rect.Width, rect.Height), 7, 7);
                var cap = on ? new Rect(rect.X, rect.Y + 1, rect.Width, rect.Height - 1) : _theme.Plate != Brushes.Transparent ? new Rect(rect.X, rect.Y, rect.Width, rect.Height - 2) : rect;
                dc.DrawRoundedRectangle(on ? Lit : KeyFill, on ? LitEdge : KeyEdge, cap, 7, 7);
                if (label.Length > 0)
                {
                    var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, label.Length > 1 ? 9.5 : 12.5, on ? LitText : KeyText, dip);
                    dc.DrawText(ft, new Point(cap.X + (cap.Width - ft.Width) / 2, cap.Y + (cap.Height - ft.Height) / 2));
                }
            }
        }
        DrawMouse(dc, Pad + KbW + 14, Pad + (KbH - MouseH) / 2, (MouseButton)(_frame?.Mouse ?? 0));
    }

    static readonly Brush Lip = B(0x73000000);
    static readonly Pen Seam = P(0x8C000000, 1.6);
    static Geometry Shape(string d) { var g = Geometry.Parse(d); g.Freeze(); return g; }
    // A classic mouse seen from above, 64 × 104: two buttons split down the middle, a curved seam where they meet the palm.
    static readonly Geometry MouseBody = Shape("M32 0 C50 0 64 12 64 36 L64 66 C64 90 50 104 32 104 C14 104 0 90 0 66 L0 36 C0 12 14 0 32 0 Z");
    static readonly Geometry LeftButton = Shape("M-2 -2 H31 V40 C20 42 8 44 -2 46 Z");
    static readonly Geometry RightButton = Shape("M33 -2 H66 V46 C56 44 44 42 33 40 Z");
    static readonly Geometry ButtonSeam = Shape("M0 46 C12 43 22 40 32 40 C42 40 52 43 64 46");

    void DrawMouse(DrawingContext dc, double x, double y, MouseButton m)
    {
        bool On(MouseButton b) => m.HasFlag(b);
        var seam = _theme.Plate == Brushes.Transparent ? KeyEdge : Seam;
        dc.PushTransform(new TranslateTransform(x, y));
        dc.DrawGeometry(KeyFill, KeyEdge, MouseBody);
        dc.PushClip(MouseBody);
        if (On(MouseButton.Left)) dc.DrawGeometry(Lit, null, LeftButton);
        if (On(MouseButton.Right)) dc.DrawGeometry(Lit, null, RightButton);
        dc.Pop();
        dc.DrawLine(seam, new Point(MouseW / 2, 1), new Point(MouseW / 2, Split - 2));
        dc.DrawGeometry(null, seam, ButtonSeam);
        // Wheel, with grip lines when it's not being used.
        bool wheel = On(MouseButton.Middle) || On(MouseButton.WheelUp) || On(MouseButton.WheelDown);
        dc.DrawRoundedRectangle(wheel ? Lit : WheelWell, wheel ? LitEdge : KeyEdge, new Rect(27.5, 10, 9, 20), 4.5, 4.5);
        if (!wheel) for (int i = 0; i < 4; i++) dc.DrawLine(Grip, new Point(30, 14 + i * 4), new Point(34, 14 + i * 4));
        // Side buttons on the left edge.
        dc.DrawRoundedRectangle(On(MouseButton.Forward) ? Lit : KeyFill, On(MouseButton.Forward) ? LitEdge : KeyEdge, new Rect(-2.5, Split + 8, 5, 15), 2.5, 2.5);
        dc.DrawRoundedRectangle(On(MouseButton.Back) ? Lit : KeyFill, On(MouseButton.Back) ? LitEdge : KeyEdge, new Rect(-2.5, Split + 26, 5, 15), 2.5, 2.5);
        dc.Pop();
    }

    static readonly Brush WheelWell = B(0xD90B0D12);
    static readonly Pen Grip = P(0x40FFFFFF);

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

    void Trigger(DrawingContext dc, Rect r, int percent)
    {
        dc.DrawRoundedRectangle(KeyFill, KeyEdge, r, 5, 5);
        if (percent <= 3) return;
        dc.DrawRoundedRectangle(Lit, null, new Rect(r.X, r.Y, Math.Max(10, r.Width * percent / 100.0), r.Height), 5, 5);
    }

    void Pill(DrawingContext dc, Rect r, bool on, string label, double dip)
    {
        dc.DrawRoundedRectangle(on ? Lit : KeyFill, on ? LitEdge : KeyEdge, r, r.Height / 2, r.Height / 2);
        if (label.Length == 0) return;
        var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 8.5, on ? LitText : KeyText, dip);
        dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
    }

    void Stick(DrawingContext dc, Point c, int x, int y, bool clicked)
    {
        dc.DrawEllipse(Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0x0E, 0x10, 0x16))), KeyEdge, c, 26, 26);
        var knob = new Point(c.X + x / 100.0 * 13, c.Y - y / 100.0 * 13);
        bool moved = Math.Abs(x) > 15 || Math.Abs(y) > 15;
        dc.DrawEllipse(clicked || moved ? Lit : KeyFill, clicked ? LitEdge : KeyEdge, knob, 15, 15);
    }

    void DPad(DrawingContext dc, Point c, PadButton p)
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
    public static BitmapSource Render(InputFrame frame, string kind, double scale, string theme = "dark")
    {
        var v = new InputOverlayView { Kind = kind, Frame = frame, Theme = theme };
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
