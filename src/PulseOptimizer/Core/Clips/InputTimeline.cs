using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pulse.Core.Clips;

/// <summary>Controller buttons, the same bits for Xbox and PlayStation (South = A / Cross, and so on).</summary>
[Flags]
public enum PadButton
{
    None = 0,
    South = 1 << 0, East = 1 << 1, West = 1 << 2, North = 1 << 3,
    LeftBumper = 1 << 4, RightBumper = 1 << 5, LeftTrigger = 1 << 6, RightTrigger = 1 << 7,
    Back = 1 << 8, Start = 1 << 9, LeftStick = 1 << 10, RightStick = 1 << 11,
    Up = 1 << 12, Down = 1 << 13, Left = 1 << 14, Right = 1 << 15,
    Home = 1 << 16, Touchpad = 1 << 17,
}

/// <summary>Mouse buttons (bits).</summary>
[Flags]
public enum MouseButton { None = 0, Left = 1, Right = 2, Middle = 4, Back = 8, Forward = 16, WheelUp = 32, WheelDown = 64 }

/// <summary>Everything held down at one moment, for the input overlay.</summary>
public sealed class InputFrame
{
    /// <summary>Seconds from the start of the clip.</summary>
    [JsonPropertyName("t")] public double T { get; set; }
    /// <summary>Keys held (Windows virtual-key codes; only the keys the overlay shows are kept).</summary>
    [JsonPropertyName("k")] public int[] Keys { get; set; } = Array.Empty<int>();
    [JsonPropertyName("m")] public int Mouse { get; set; }
    [JsonPropertyName("p")] public int Pad { get; set; }
    /// <summary>Sticks −100..100: left x, left y, right x, right y (y up is positive).</summary>
    [JsonPropertyName("s")] public int[] Sticks { get; set; } = new int[4];
    /// <summary>Triggers 0..100: left, right.</summary>
    [JsonPropertyName("tr")] public int[] Triggers { get; set; } = new int[2];

    public InputFrame Copy() => new() { T = T, Keys = Keys.ToArray(), Mouse = Mouse, Pad = Pad, Sticks = Sticks.ToArray(), Triggers = Triggers.ToArray() };

    /// <summary>Same buttons, sticks and triggers (time ignored).</summary>
    public bool SameAs(InputFrame o) =>
        o != null && Mouse == o.Mouse && Pad == o.Pad && Keys.SequenceEqual(o.Keys) && Sticks.SequenceEqual(o.Sticks) && Triggers.SequenceEqual(o.Triggers);

    public bool PadActive => Pad != 0 || Sticks.Any(s => Math.Abs(s) > 15) || Triggers.Any(t => t > 10);
    public bool KbmActive => Keys.Length > 0 || Mouse != 0;
}

/// <summary>The inputs of one clip, saved next to it as "name.input.json".</summary>
public sealed class InputTimeline
{
    /// <summary>"xbox" or "ps": which controller was used, if any.</summary>
    [JsonPropertyName("pad")] public string PadKind { get; set; } = "xbox";
    [JsonPropertyName("frames")] public List<InputFrame> Frames { get; set; } = new();

    /// <summary>Keys the keyboard overlay shows (and the only ones recorded): the left side of the keyboard games use.</summary>
    public static readonly int[] OverlayKeys =
    {
        0x1B, 0x31, 0x32, 0x33, 0x34, 0x35,              // Esc 1-5
        0x09, 0x51, 0x57, 0x45, 0x52, 0x54,              // Tab Q W E R T
        0x14, 0x41, 0x53, 0x44, 0x46, 0x47,              // Caps A S D F G
        0x10, 0x5A, 0x58, 0x43, 0x56, 0x42,              // Shift Z X C V B
        0x11, 0x12, 0x20,                                // Ctrl Alt Space
    };

    /// <summary>Left/right Shift, Ctrl and Alt count as one key each.</summary>
    public static int Normalize(int vk) => vk switch
    {
        0xA0 or 0xA1 => 0x10,
        0xA2 or 0xA3 => 0x11,
        0xA4 or 0xA5 => 0x12,
        _ => vk,
    };

    /// <summary>What was held at <paramref name="t"/> seconds (the last change at or before it).</summary>
    public InputFrame At(double t)
    {
        if (Frames.Count == 0) return null;
        int lo = 0, hi = Frames.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Frames[mid].T <= t) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found < 0 ? null : Frames[found];
    }

    /// <summary>
    /// Everything held at any point in the <paramref name="window"/> seconds up to <paramref name="t"/>: what one
    /// picture should show, so a tap shorter than a frame still appears instead of falling between two frames.
    /// Sticks and triggers are where they are at <paramref name="t"/>.
    /// </summary>
    public InputFrame Held(double t, double window)
    {
        var now = At(t);
        if (now == null) return null;
        var start = At(t - window);
        int mouse = now.Mouse, pad = now.Pad;
        var keys = new HashSet<int>(now.Keys);
        if (start != null) { mouse |= start.Mouse; pad |= start.Pad; keys.UnionWith(start.Keys); }
        foreach (var f in Frames)
        {
            if (f.T <= t - window) continue;
            if (f.T > t) break;
            mouse |= f.Mouse; pad |= f.Pad; keys.UnionWith(f.Keys);
        }
        if (mouse == now.Mouse && pad == now.Pad && keys.Count == now.Keys.Length) return now;
        return new InputFrame { T = t, Keys = keys.OrderBy(k => k).ToArray(), Mouse = mouse, Pad = pad, Sticks = now.Sticks, Triggers = now.Triggers };
    }

    /// <summary>"kbm" or the controller kind, whichever was used more in this clip.</summary>
    public string MostUsed()
    {
        int pad = Frames.Count(f => f.PadActive), kbm = Frames.Count(f => f.KbmActive);
        return pad > kbm ? PadKind : "kbm";
    }

    /// <summary>The part between two times, re-timed so <paramref name="from"/> is 0.</summary>
    public InputTimeline Slice(double from, double to)
    {
        var r = new InputTimeline { PadKind = PadKind };
        var start = At(from);
        if (start != null) { var c = start.Copy(); c.T = 0; r.Frames.Add(c); }
        foreach (var f in Frames.Where(f => f.T > from && f.T <= to))
        {
            var c = f.Copy();
            c.T = Math.Round(f.T - from, 3);
            r.Frames.Add(c);
        }
        return r;
    }

    public bool HasInput => Frames.Any(f => f.PadActive || f.KbmActive);

    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static string FileFor(string clip) => Path.ChangeExtension(clip, ".input.json");

    public void Save(string clip)
    {
        if (!HasInput) return;
        try { File.WriteAllText(FileFor(clip), JsonSerializer.Serialize(this, Json)); } catch { }
    }

    public static InputTimeline Load(string clip)
    {
        try
        {
            var f = FileFor(clip);
            return File.Exists(f) ? JsonSerializer.Deserialize<InputTimeline>(File.ReadAllText(f), Json) : null;
        }
        catch { return null; }
    }
}
