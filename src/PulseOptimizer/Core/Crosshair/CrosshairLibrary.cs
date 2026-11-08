namespace Pulse.Core.Crosshair;

/// <summary>
/// The built-in crosshair library: common crosshair styles, each tagged with what it's good for. These are generic
/// styles written for Pulse, not copies of any named player's crosshair. Every entry is a VALORANT code, so it looks the
/// same in the game. Your own ratings and tags live on your saved crosshairs.
/// </summary>
public static class CrosshairLibrary
{
    public sealed record Entry(string Name, string Code, string[] Uses, string Why)
    {
        public CrosshairStyle Style => ValorantCode.Parse(Code);
    }

    /// <summary>Use cases, in the order they're shown.</summary>
    public static readonly string[] UseCases = { "Precise aim", "Easy to see", "Spray control", "Movement", "Snipers", "New players", "Bright maps", "Busy screens" };

    public static IReadOnlyList<Entry> All { get; } = new[]
    {
        new Entry("Classic cross", "0;P;c;5;h;0;f;0;0l;4;0o;2;0a;1;0f;0;1b;0", new[] { "New players", "Precise aim" }, "Short cyan lines with a small gap: a safe starting point."),
        new Entry("Small cross", "0;P;c;1;o;1;f;0;0t;1;0l;2;0o;2;0a;1;0f;0;1b;0", new[] { "Precise aim" }, "Thin and tiny, so it covers very little of the target's head."),
        new Entry("Dot", "0;P;c;5;o;1;d;1;z;3;f;0;0b;0;1b;0", new[] { "Precise aim", "Busy screens" }, "Nothing but a dot: the least clutter there is."),
        new Entry("Plus, no gap", "0;P;h;0;f;0;0l;4;0o;0;0a;1;0f;0;1b;0", new[] { "Easy to see", "New players" }, "A solid plus that's hard to lose track of."),
        new Entry("Red outlined", "0;P;c;7;t;1;o;1;f;0;0t;2;0l;5;0o;2;0a;1;0f;0;1b;0", new[] { "Bright maps", "Easy to see" }, "Red with a black outline stands out on pale walls."),
        new Entry("Yellow cross", "0;P;c;4;h;1;t;1;o;1;f;0;0t;2;0l;4;0o;2;0a;1;0f;0;1b;0", new[] { "Bright maps", "Easy to see" }, "Yellow with an outline reads well on both dark and light surfaces."),
        new Entry("Green small", "0;P;c;1;h;0;f;0;0t;1;0l;3;0o;1;0a;1;0f;0;1b;0", new[] { "Precise aim", "Bright maps" }, "Bright green, thin and close to the centre."),
        new Entry("Magenta dot", "0;P;c;6;h;1;t;1;o;1;d;1;z;2;a;1;f;0;0b;0;1b;0", new[] { "Busy screens", "Easy to see" }, "Magenta rarely appears on maps or agents, so the dot never blends in."),
        new Entry("Dot with lines", "0;P;c;5;h;0;d;1;z;2;a;1;f;0;0t;1;0l;3;0o;3;0a;1;0f;0;1b;0", new[] { "Precise aim", "New players" }, "A dot to aim with and short lines to find it fast."),
        new Entry("Wide gap", "0;P;c;5;h;0;f;0;0t;2;0l;4;0o;5;0a;1;0f;0;1b;0", new[] { "Snipers", "Movement", "Busy screens" }, "Leaves the middle empty, so you see exactly what you're aiming at."),
        new Entry("Long lines", "0;P;c;1;h;0;f;0;0t;1;0l;7;0o;2;0a;1;0f;0;1b;0", new[] { "Easy to see", "Spray control" }, "Long thin lines that line up quickly with heads at range."),
        new Entry("Two-layer", "0;P;c;5;h;0;f;0;0t;2;0l;3;0o;2;0a;1;0f;0;1t;1;1l;2;1o;8;1a;0.5;1b;1", new[] { "Spray control" }, "Inner lines to aim with and faint outer lines as a guide for recoil."),
        new Entry("Black outlined white", "0;P;c;0;h;1;t;1;o;1;f;0;0t;2;0l;4;0o;2;0a;1;0f;0;1b;0", new[] { "Bright maps", "Easy to see" }, "White with a heavy outline: visible on almost anything."),
        new Entry("Tiny dot", "0;P;c;5;h;1;t;1;o;0.5;d;1;z;1;a;1;f;0;0b;0;1b;0", new[] { "Precise aim", "Snipers" }, "The smallest dot VALORANT allows."),
    };

    /// <summary>Entries matching a search (name, use or description) and, if given, one use case.</summary>
    public static List<Entry> Find(string text, string use)
    {
        text = (text ?? "").Trim();
        return All.Where(e => (string.IsNullOrEmpty(use) || e.Uses.Contains(use))
            && (text.Length == 0 || e.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || e.Why.Contains(text, StringComparison.OrdinalIgnoreCase)
                || e.Uses.Any(u => u.Contains(text, StringComparison.OrdinalIgnoreCase)))).ToList();
    }

    /// <summary>How two crosshairs differ, setting by setting (empty when they're the same).</summary>
    public static List<(string Setting, string A, string B)> Differences(CrosshairStyle a, CrosshairStyle b)
    {
        var d = new List<(string, string, string)>();
        void Add(string name, object x, object y) { var sx = Fmt(x); var sy = Fmt(y); if (sx != sy) d.Add((name, sx, sy)); }
        Add("Color", a.Color, b.Color);
        Add("Outlines", a.Outlines, b.Outlines);
        Add("Outline thickness", a.OutlineThickness, b.OutlineThickness);
        Add("Center dot", a.Dot, b.Dot);
        Add("Dot size", a.DotSize, b.DotSize);
        Add("Inner lines", a.Inner.Show, b.Inner.Show);
        Add("Inner length", a.Inner.Length, b.Inner.Length);
        Add("Inner thickness", a.Inner.Thickness, b.Inner.Thickness);
        Add("Inner gap", a.Inner.Offset, b.Inner.Offset);
        Add("Outer lines", a.Outer.Show, b.Outer.Show);
        Add("Outer length", a.Outer.Length, b.Outer.Length);
        Add("Outer gap", a.Outer.Offset, b.Outer.Offset);
        return d;
    }

    static string Fmt(object o) => o switch
    {
        bool v => v ? "On" : "Off",
        double v => v.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture),
        null => "–",
        _ => o.ToString(),
    };
}
