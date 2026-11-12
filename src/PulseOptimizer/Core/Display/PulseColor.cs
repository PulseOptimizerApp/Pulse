using System.Runtime.InteropServices;

namespace Pulse.Core.Display;

/// <summary>A Pulse Color look: how much more vibrant, how much the shadows lift, contrast, and a tint toward the enemy highlight.</summary>
public sealed record ColorLook(int Vibrance, double Shadows, double Contrast, double Red, double Green, double Blue);

/// <summary>
/// Pulse Color: looks made for VALORANT, built from two things the PC already has: the graphics card's vibrance and
/// Windows' colour curves (the gamma ramp, which is how monitor calibration works). Nothing in the game is touched.
/// </summary>
public static class PulseColor
{
    public sealed record Preset(string Key, string Name, string Detail);

    public static readonly List<Preset> Presets = new()
    {
        new("pop", "Enemy pop", "Stronger colour and a lift on your enemy highlight colour, so outlines jump out."),
        new("dark", "See in the dark", "Lifts the shadows so players in dark corners and doorways show, without washing out the rest."),
        new("crisp", "Crisp", "A little more colour and contrast for a sharp, clean picture."),
        new("custom", "Custom", "Your own mix."),
    };

    public static readonly string[] Highlights = { "red", "yellow", "purple" };

    /// <summary>The look for a preset (and, for "pop", the enemy highlight colour the player uses in VALORANT).</summary>
    public static ColorLook Look(string preset, string highlight, ColorLook custom) => preset switch
    {
        "pop" => highlight switch
        {
            "yellow" => new ColorLook(72, 0.10, 1.06, 1.05, 1.05, 0.97),
            "purple" => new ColorLook(72, 0.10, 1.06, 1.05, 0.97, 1.05),
            _ => new ColorLook(72, 0.10, 1.06, 1.07, 0.98, 0.98),
        },
        "dark" => new ColorLook(62, 0.32, 1.02, 1, 1, 1),
        "crisp" => new ColorLook(66, 0.05, 1.10, 1, 1, 1),
        _ => custom,
    };

    /// <summary>One channel's curve: shadows lifted, contrast around the middle, then the channel's own gain. 0–1 in and out.</summary>
    public static double Curve(double x, ColorLook l, double gain)
    {
        // Lifting the shadows: brightens dark values most, leaves highlights alone.
        double y = x + l.Shadows * x * (1 - x) * (1 - x) * 1.6;
        y = (y - 0.5) * l.Contrast + 0.5;
        y *= gain;
        return Math.Clamp(y, 0, 1);
    }

    /// <summary>The 3×256 ramp Windows takes (16-bit), for a look.</summary>
    public static ushort[] Ramp(ColorLook l)
    {
        var r = new ushort[3 * 256];
        var gains = new[] { l.Red, l.Green, l.Blue };
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < 256; i++)
                r[c * 256 + i] = (ushort)Math.Round(Curve(i / 255.0, l, gains[c]) * 65535);
        return r;
    }

    /// <summary>
    /// Applies a look to a picture (BGRA bytes) in software, for the preview: the curves, and vibrance as the drivers do
    /// it (dull colours get more, already-strong colours a little).
    /// </summary>
    public static void Apply(byte[] px, ColorLook l)
    {
        double k = (l.Vibrance - 50) / 50.0 * 1.1; // 0 normal, 1.1 at most
        var lut = new byte[3][];
        var gains = new[] { l.Blue, l.Green, l.Red };
        for (int c = 0; c < 3; c++)
        {
            lut[c] = new byte[256];
            for (int i = 0; i < 256; i++) lut[c][i] = (byte)Math.Round(Curve(i / 255.0, l, gains[c]) * 255);
        }
        for (int i = 0; i < px.Length; i += 4)
        {
            double b = px[i], g = px[i + 1], r = px[i + 2];
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double avg = (r + g + b) / 3, sat = max == 0 ? 0 : (max - min) / max;
            double amt = k * (1 - sat * 0.6);
            r = Math.Clamp(r + (r - avg) * amt, 0, 255);
            g = Math.Clamp(g + (g - avg) * amt, 0, 255);
            b = Math.Clamp(b + (b - avg) * amt, 0, 255);
            px[i] = lut[0][(int)b]; px[i + 1] = lut[1][(int)g]; px[i + 2] = lut[2][(int)r];
        }
    }

    // ───────── Windows colour curves (gamma ramp), per screen ─────────

    [DllImport("gdi32.dll")] static extern IntPtr CreateDC(string driver, string device, string output, IntPtr init);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool GetDeviceGammaRamp(IntPtr dc, [Out] ushort[] ramp);
    [DllImport("gdi32.dll")] static extern bool SetDeviceGammaRamp(IntPtr dc, ushort[] ramp);

    [StructLayout(LayoutKind.Sequential)] struct RectI { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfoEx
    {
        public int Size;
        public RectI Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    delegate bool MonitorEnum(IntPtr hMonitor, IntPtr hdc, ref RectI rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnum cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx info);

    /// <summary>The screens' device names (\\.\DISPLAY1…).</summary>
    static List<string> Screens()
    {
        var names = new List<string>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hm, IntPtr _, ref RectI _, IntPtr _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(hm, ref info) && !string.IsNullOrEmpty(info.Device)) names.Add(info.Device);
            return true;
        }, IntPtr.Zero);
        return names;
    }

    /// <summary>Every screen's curves now (to put back later): screen → ramp.</summary>
    public static Dictionary<string, ushort[]> Capture()
    {
        var d = new Dictionary<string, ushort[]>();
        foreach (var name in Screens())
        {
            var dc = CreateDC(null, name, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) continue;
            try { var r = new ushort[768]; if (GetDeviceGammaRamp(dc, r)) d[name] = r; }
            finally { DeleteDC(dc); }
        }
        return d;
    }

    /// <summary>Puts a look on every screen, on top of each screen's own curves (so calibration is kept).</summary>
    public static bool Set(ColorLook l, Dictionary<string, ushort[]> own)
    {
        bool any = false;
        var look = Ramp(l);
        foreach (var name in Screens())
        {
            var dc = CreateDC(null, name, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) continue;
            try
            {
                var r = new ushort[768];
                var baseRamp = own.TryGetValue(name, out var b) ? b : null;
                for (int c = 0; c < 3; c++)
                    for (int i = 0; i < 256; i++)
                    {
                        // The look's value for this step, read through the screen's own curve.
                        double v = look[c * 256 + i] / 65535.0;
                        r[c * 256 + i] = baseRamp == null ? look[c * 256 + i] : baseRamp[c * 256 + (int)Math.Round(v * 255)];
                    }
                any |= SetDeviceGammaRamp(dc, r);
            }
            finally { DeleteDC(dc); }
        }
        return any;
    }

    /// <summary>Puts each screen's own curves back.</summary>
    public static void Restore(Dictionary<string, ushort[]> own)
    {
        foreach (var (name, ramp) in own)
        {
            var dc = CreateDC(null, name, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) continue;
            try { SetDeviceGammaRamp(dc, ramp); }
            finally { DeleteDC(dc); }
        }
    }
}
