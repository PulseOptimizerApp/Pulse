using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pulse.Ui;

/// <summary>
/// One of Pulse's own line icons (24×24, round 2 px strokes), drawn in the current Foreground. Used instead of stock
/// Windows glyphs so the app has one consistent icon style.
/// </summary>
public sealed class LineIcon : Control
{
    static readonly Dictionary<string, Geometry> Paths = new()
    {
        ["home"] = Make("M4 11 L12 4 L20 11 M6 9.5 V20 H18 V9.5 M10 20 V14.5 H14 V20"),
        ["games"] = Make("M7.5 8 H16.5 A4.5 4.5 0 0 1 21 12.5 V15 A3 3 0 0 1 15.9 17.1 L14.5 15.5 H9.5 L8.1 17.1 A3 3 0 0 1 3 15 V12.5 A4.5 4.5 0 0 1 7.5 8 Z M7.5 10.5 V14 M5.75 12.25 H9.25 M15.5 11.5 h0.01 M17.5 13.5 h0.01"),
        ["clips"] = Make("M4 6 H20 V18 H4 Z M10 9.5 L15 12 L10 14.5 Z"),
        ["crosshair"] = Make("M12 5 A7 7 0 1 1 11.99 5 M12 2.5 V7.5 M12 16.5 V21.5 M2.5 12 H7.5 M16.5 12 H21.5 M12 12 h0.01"),
        ["valorant"] = Make("M12 3 L20.5 12 L12 21 L3.5 12 Z M12 8.5 V15.5 M8.5 12 H15.5"),
        ["roblox"] = Make("M6.6 3.6 L20.4 6.6 L17.4 20.4 L3.6 17.4 Z M11.1 9.7 L14.3 10.4 L13.6 13.6 L10.4 12.9 Z"),
        ["diagnose"] = Make("M10.5 4 A6.5 6.5 0 1 1 10.49 4 M15.3 15.3 L20 20 M6.5 10.5 H8.3 L9.6 8.3 L11.4 12.7 L12.6 10.5 H14.5"),
        ["tweaks"] = Make("M6 4 V20 M12 4 V20 M18 4 V20 M4 15 H8 M10 8 H14 M16 12.5 H20"),
        ["measure"] = Make("M4.4 17 A8.5 8.5 0 1 1 19.6 17 M12 14 L16 9.5 M12 14 h0.01"),
        ["cleanup"] = Make("M4.5 7 H19.5 M9 7 V4.5 H15 V7 M6.5 7 L7.5 20 H16.5 L17.5 7 M10.3 10.5 V16.5 M13.7 10.5 V16.5"),
        ["startup"] = Make("M12 3 V11 M7.2 6.2 A7.5 7.5 0 1 0 16.8 6.2"),
        ["fresh"] = Make("M19.5 12 A7.5 7.5 0 1 1 17.1 6.5 M18 3 V7 H14"),
        ["mouse"] = Make("M12 3 A6 6 0 0 1 18 9 V15 A6 6 0 0 1 6 15 V9 A6 6 0 0 1 12 3 Z M12 6.5 V10"),
        ["mic"] = Make("M9 6 A3 3 0 0 1 15 6 V11 A3 3 0 0 1 9 11 Z M5.5 11 A6.5 6.5 0 0 0 18.5 11 M12 17.5 V21 M9 21 H15"),
        ["macros"] = Make("M13 3 L5 13.5 H11.5 L10.5 21 L19 10.5 H12.5 Z"),
        ["reaction"] = Make("M12 6.5 A7 7 0 1 1 11.99 6.5 M12 13.5 L14.8 10.7 M9.5 3 H14.5 M12 3 V6.5"),
        ["guide"] = Make("M3.5 5 H9.5 A2.5 2.5 0 0 1 12 7.5 V20 A2 2 0 0 0 10 18 H3.5 Z M20.5 5 H14.5 A2.5 2.5 0 0 0 12 7.5 V20 A2 2 0 0 1 14 18 H20.5 Z"),
        ["security"] = Make("M12 3 L19 6 V11 C19 15.5 16 18.8 12 21 C8 18.8 5 15.5 5 11 V6 Z M9 12 L11 14 L15 10"),
        ["safety"] = Make("M9 14 L4 9 L9 4 M4 9 H14 A5.5 5.5 0 0 1 14 20 H10.5"),
        ["overlay"] = Make("M3 4.5 H21 V16.5 H3 Z M8 20.5 H16 M12 16.5 V20.5 M6 8 H10.5 M6 10.75 H9"),
        ["headphones"] = Make("M4 15.5 V12 A8 8 0 0 1 20 12 V15.5 M4 14 H7.5 V20.5 H5.5 A1.5 1.5 0 0 1 4 19 Z M20 14 H16.5 V20.5 H18.5 A1.5 1.5 0 0 0 20 19 Z"),
        ["check"] = Make("M5 12.5 L10 17.5 L19 7"),
        ["folder"] = Make("M3.5 6.5 A1.5 1.5 0 0 1 5 5 H9.5 L11.5 7 H19 A1.5 1.5 0 0 1 20.5 8.5 V17.5 A1.5 1.5 0 0 1 19 19 H5 A1.5 1.5 0 0 1 3.5 17.5 Z"),
        ["record"] = Make("M12 6 A6 6 0 1 1 11.99 6"),
        ["play"] = Make("M8 5.5 L18.5 12 L8 18.5 Z"),
        ["back"] = Make("M15 5 L8 12 L15 19"),
        ["warning"] = Make("M12 4 L21 19.5 H3 Z M12 10 V14 M12 17 h0.01"),
        ["star"] = Make("M12 3.5 L14.6 9 L20.5 9.6 L16 13.6 L17.3 19.5 L12 16.5 L6.7 19.5 L8 13.6 L3.5 9.6 L9.4 9 Z"),
        ["share"] = Make("M12 15 V4 M8 8 L12 4 L16 8 M5 13 V19 H19 V13"),
        ["keyboard"] = Make("M3 6.5 H21 V17.5 H3 Z M6.5 10 h0.01 M9.5 10 h0.01 M12.5 10 h0.01 M15.5 10 h0.01 M17.5 10 h0.01 M7.5 14 H16.5"),
    };

    static Geometry Make(string d) { var g = Geometry.Parse(d); g.Freeze(); return g; }

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(LineIcon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    static LineIcon()
    {
        ForegroundProperty.OverrideMetadata(typeof(LineIcon), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));
        FocusableProperty.OverrideMetadata(typeof(LineIcon), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(LineIcon), new FrameworkPropertyMetadata(false));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(Kind) || !Paths.TryGetValue(Kind, out var g)) return;
        double s = Math.Min(ActualWidth, ActualHeight) / 24;
        if (s <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - 24 * s) / 2, (ActualHeight - 24 * s) / 2));
        dc.PushTransform(new ScaleTransform(s, s));
        var pen = new Pen(Foreground, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, g);
        dc.Pop(); dc.Pop();
    }
}
