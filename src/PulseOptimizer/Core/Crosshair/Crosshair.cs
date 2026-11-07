using System.Globalization;
using System.Text;

namespace Pulse.Core.Crosshair;

/// <summary>One set of crosshair lines (VALORANT's inner or outer lines). Sizes are screen pixels.</summary>
public sealed class CrosshairLines
{
    public bool Show { get; set; } = true;
    public double Length { get; set; } = 6;
    /// <summary>Vertical length when it differs from <see cref="Length"/> (VALORANT's "separate vertical length"); null = same.</summary>
    public double? VerticalLength { get; set; }
    public double Thickness { get; set; } = 2;
    /// <summary>Gap from the center to where the line starts.</summary>
    public double Offset { get; set; } = 3;
    public double Opacity { get; set; } = 0.8;

    public double VLength => VerticalLength ?? Length;
    public CrosshairLines Copy() => (CrosshairLines)MemberwiseClone();
}

/// <summary>
/// A crosshair, modelled on VALORANT's settings (so VALORANT codes import exactly), plus a ring and a T-shape option.
/// Every size is in screen pixels at <see cref="Size"/> 1.
/// </summary>
public sealed class CrosshairStyle
{
    /// <summary>"#RRGGBB".</summary>
    public string Color { get; set; } = "#FFFFFF";
    public bool Outlines { get; set; } = true;
    public double OutlineThickness { get; set; } = 1;
    public double OutlineOpacity { get; set; } = 0.5;
    public bool Dot { get; set; }
    public double DotSize { get; set; } = 2;
    public double DotOpacity { get; set; } = 1;
    public CrosshairLines Inner { get; set; } = new();
    public CrosshairLines Outer { get; set; } = new() { Length = 2, Offset = 10, Opacity = 0.35 };
    /// <summary>Pulse extra: a ring around the center.</summary>
    public bool Circle { get; set; }
    public double CircleRadius { get; set; } = 8;
    public double CircleThickness { get; set; } = 1.5;
    public double CircleOpacity { get; set; } = 1;
    /// <summary>Pulse extra: no top line (a "T" crosshair).</summary>
    public bool NoTop { get; set; }
    /// <summary>Scales everything (1 = the pixel sizes as written, like in the game).</summary>
    public double Size { get; set; } = 1;

    public CrosshairStyle Copy()
    {
        var c = (CrosshairStyle)MemberwiseClone();
        c.Inner = Inner.Copy();
        c.Outer = Outer.Copy();
        return c;
    }

    /// <summary>How far the crosshair reaches from the center, in pixels (for sizing the overlay window).</summary>
    public double Reach()
    {
        double r = Dot ? DotSize / 2 : 0;
        foreach (var l in new[] { Inner, Outer })
            if (l.Show) r = Math.Max(r, l.Offset + Math.Max(l.Length, l.VLength) + l.Thickness);
        if (Circle) r = Math.Max(r, CircleRadius + CircleThickness);
        return (r + (Outlines ? OutlineThickness : 0) + 2) * Math.Max(0.1, Size);
    }
}

/// <summary>Reads and writes VALORANT crosshair codes ("0;P;c;5;h;0;0l;4;0o;2;0a;1;0f;0;1b;0").</summary>
public static class ValorantCode
{
    /// <summary>VALORANT's color choices by number (8 = custom, given as "u;RRGGBBAA").</summary>
    public static readonly string[] Colors = { "#FFFFFF", "#00FF00", "#7FFF00", "#DFFF00", "#FFFF00", "#00FFFF", "#FF00FF", "#FF0000" };

    /// <summary>The crosshair in a code (its primary crosshair), or null if this isn't a VALORANT crosshair code.</summary>
    public static CrosshairStyle Parse(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var t = code.Trim().Trim('"', '\'').Split(';');
        if (t.Length < 2 || t[0].Trim() != "0") return null;

        var s = new CrosshairStyle();
        string section = "";
        bool any = false;
        int colorIndex = 0;
        string custom = null;
        for (int i = 1; i < t.Length;)
        {
            var k = t[i].Trim();
            if (k is "P" or "A" or "S") { section = k; i++; continue; }
            if (k.Length == 0) { i++; continue; }
            if (i + 1 >= t.Length) break;
            var v = t[i + 1].Trim();
            i += 2;
            if (section != "P") continue; // only the primary crosshair
            any = true;
            double n = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
            bool on = v != "0";
            switch (k)
            {
                case "c": colorIndex = (int)n; break;
                case "u": custom = v; break;
                case "h": s.Outlines = on; break;
                case "t": s.OutlineThickness = n; break;
                case "o": s.OutlineOpacity = n; break;
                case "d": s.Dot = on; break;
                case "z": s.DotSize = n; break;
                case "a": s.DotOpacity = n; break;
                default:
                    if (k.Length == 2 && (k[0] == '0' || k[0] == '1'))
                    {
                        var l = k[0] == '0' ? s.Inner : s.Outer;
                        switch (k[1])
                        {
                            case 'b': l.Show = on; break;
                            case 't': l.Thickness = n; break;
                            case 'l': l.Length = n; break;
                            case 'v': l.VerticalLength = n; break;
                            case 'o': l.Offset = n; break;
                            case 'a': l.Opacity = n; break;
                        }
                    }
                    break;
            }
        }
        if (!any && !code.Contains(";P;", StringComparison.Ordinal)) return null;
        // "0v" only counts with "0g;1" (separate vertical length on).
        foreach (var (prefix, l) in new[] { ('0', s.Inner), ('1', s.Outer) })
            if (!code.Contains(";" + prefix + "g;1", StringComparison.Ordinal)) l.VerticalLength = null;
            else l.VerticalLength ??= l.Length;
        s.Color = colorIndex == 8 && custom is { Length: >= 6 } ? "#" + custom[..6].ToUpperInvariant()
            : colorIndex >= 0 && colorIndex < Colors.Length ? Colors[colorIndex] : Colors[0];
        return s;
    }

    /// <summary>A VALORANT code for this crosshair (the ring and T-shape have no VALORANT setting and are left out).</summary>
    public static string Write(CrosshairStyle s)
    {
        var d = new CrosshairStyle();
        var sb = new StringBuilder("0;P");
        void Add(string k, string v) => sb.Append(';').Append(k).Append(';').Append(v);
        static string N(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

        int idx = Array.FindIndex(Colors, c => c.Equals(s.Color, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) { Add("c", "8"); Add("u", s.Color.TrimStart('#').ToUpperInvariant() + "FF"); }
        else if (idx != 0) Add("c", idx.ToString(CultureInfo.InvariantCulture));
        if (!s.Outlines) Add("h", "0");
        if (s.OutlineThickness != d.OutlineThickness) Add("t", N(s.OutlineThickness));
        if (s.OutlineOpacity != d.OutlineOpacity) Add("o", N(s.OutlineOpacity));
        if (s.Dot) Add("d", "1");
        if (s.DotSize != d.DotSize) Add("z", N(s.DotSize));
        if (s.DotOpacity != d.DotOpacity) Add("a", N(s.DotOpacity));
        Add("f", "0"); // a still crosshair: no fading while shooting

        void Lines(char p, CrosshairLines l, CrosshairLines def)
        {
            if (!l.Show) { Add(p + "b", "0"); return; }
            if (l.Thickness != def.Thickness) Add(p + "t", N(l.Thickness));
            if (l.Length != def.Length) Add(p + "l", N(l.Length));
            if (l.VerticalLength is double v) { Add(p + "v", N(v)); Add(p + "g", "1"); }
            if (l.Offset != def.Offset) Add(p + "o", N(l.Offset));
            if (l.Opacity != def.Opacity) Add(p + "a", N(l.Opacity));
            Add(p + "m", "0"); Add(p + "f", "0"); // no movement or firing error
        }
        Lines('0', s.Inner, d.Inner);
        Lines('1', s.Outer, d.Outer);
        return sb.ToString();
    }
}

public sealed class SavedCrosshair
{
    public string Name { get; set; }
    public CrosshairStyle Style { get; set; }
}

/// <summary>The Crosshair page's settings.</summary>
public sealed class CrosshairSettings
{
    public bool Enabled { get; set; }
    /// <summary>Only show it while a game is running (and in front or alt-tabbed from).</summary>
    public bool OnlyInGames { get; set; } = true;
    /// <summary>Ctrl+Shift+X shows or hides it.</summary>
    public bool Hotkey { get; set; } = true;
    /// <summary>Leave it out of clips, screenshots and streams (Windows 10 2004 or newer).</summary>
    public bool HideFromCapture { get; set; }
    /// <summary>"primary" or "game" (the monitor the game is on).</summary>
    public string Monitor { get; set; } = "game";
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public CrosshairStyle Style { get; set; } = Presets.First().Style.Copy();
    public List<SavedCrosshair> Saved { get; set; } = new();

    /// <summary>Starting points. Codes are VALORANT codes, so they look the same in the game.</summary>
    public static IReadOnlyList<(string Name, CrosshairStyle Style)> Presets { get; } = new[]
    {
        ("Classic cross", ValorantCode.Parse("0;P;c;5;h;0;f;0;0l;4;0o;2;0a;1;0f;0;1b;0")),
        ("Small cross", ValorantCode.Parse("0;P;c;1;o;1;f;0;0t;1;0l;2;0o;2;0a;1;0f;0;1b;0")),
        ("Dot", ValorantCode.Parse("0;P;c;5;o;1;d;1;z;3;f;0;0b;0;1b;0")),
        ("Plus, no gap", ValorantCode.Parse("0;P;h;0;f;0;0l;4;0o;0;0a;1;0f;0;1b;0")),
        ("Red outlined", ValorantCode.Parse("0;P;c;7;t;1;o;1;f;0;0t;2;0l;5;0o;2;0a;1;0f;0;1b;0")),
        ("Dot and ring", With(ValorantCode.Parse("0;P;c;1;o;1;d;1;z;2;f;0;0b;0;1b;0"), s => { s.Circle = true; s.CircleRadius = 9; s.CircleThickness = 1.5; })),
        ("T-shape", With(ValorantCode.Parse("0;P;c;5;h;0;f;0;0l;5;0o;2;0a;1;0f;0;1b;0"), s => s.NoTop = true)),
    };

    static CrosshairStyle With(CrosshairStyle s, Action<CrosshairStyle> a) { a(s); return s; }
}
