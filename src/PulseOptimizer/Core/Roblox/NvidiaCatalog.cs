using System.Globalization;

namespace Pulse.Core.Roblox;

/// <summary>One NVIDIA driver setting on the Roblox page, shown as a row of choices (like a line in NVIDIA Profile Inspector).</summary>
public class NvOption : RobloxOption
{
    /// <summary>The driver setting this row shows in "Now:" (the first one it writes).</summary>
    public uint Id { get; init; }
    /// <summary>Driver settings this row writes, per choice value. "" writes nothing (the profile keeps what it had).</summary>
    public Func<string, List<NvSetting>> Writes { get; init; } = _ => new();

    /// <summary>Shown straight away; the rest sit under "More settings".</summary>
    public bool Main { get; set; }

    string _current = "";
    /// <summary>What Roblox's profile has now, in words.</summary>
    public string Current { get => _current; set => Set(ref _current, value ?? ""); }
}

/// <summary>
/// The NVIDIA settings Pulse offers for Roblox. Setting IDs and values are NVIDIA's, from NvApiDriverSettings.h in
/// NVIDIA's public NVAPI headers (github.com/NVIDIA/nvapi) and the names NVIDIA Profile Inspector shows for them.
/// Only Roblox's profile is changed, and every change is backed up for Undo.
/// </summary>
public static class NvidiaCatalog
{
    // NvApiDriverSettings.h
    public const uint PRERENDERLIMIT = 0x007BA09E;          // Max pre-rendered frames (Low Latency Mode On = 1)
    public const uint ULL_ENABLED = 0x10835000;             // Ultra Low Latency - Enabled
    public const uint ULL_CPL_STATE = 0x0005F543;           // Ultra Low Latency - CPL State: 0 Off, 1 On, 2 Ultra
    public const uint PREFERRED_PSTATE = 0x1057EB71;        // Power management mode
    public const uint VSYNCMODE = 0x00A879CF;               // Vertical sync
    public const uint FRL_FPS = 0x10835002;                 // Max Frame Rate (driver limiter), 0 = off
    public const uint QUALITY_ENHANCEMENTS = 0x00CE2691;    // Texture filtering - Quality
    public const uint THREAD_CONTROL = 0x20C1221E;          // Threaded optimization
    public const uint VRR_APP_OVERRIDE = 0x10A879CF;        // G-SYNC application mode
    public const uint REFRESH_RATE_OVERRIDE = 0x0064B541;   // Preferred refresh rate

    public const uint VSYNC_APP = 0x60925292, VSYNC_OFF = 0x08416747, VSYNC_ON = 0x47814940, VSYNC_FAST = 0x18888888;

    static NvSetting S(uint id, string name, uint value, string text) => new(id, name, value, text);

    static NvOption Simple(string key, uint id, string title, string detail, params (string label, uint value)[] choices)
    {
        var o = new NvOption
        {
            Key = "nv-" + key, Id = id, Title = title, Detail = detail,
            Writes = v =>
            {
                var hit = choices.FirstOrDefault(c => c.value.ToString(CultureInfo.InvariantCulture) == v);
                return hit.label == null ? new() : new() { S(id, title, hit.value, hit.label) };
            },
        };
        o.Choices.Add(new RobloxChoice { Owner = o, Label = "Don't change", Value = "" });
        foreach (var (l, v) in choices) o.Choices.Add(new RobloxChoice { Owner = o, Label = l, Value = v.ToString(CultureInfo.InvariantCulture) });
        return o;
    }

    /// <summary>The rows, for a primary monitor running at <paramref name="hz"/> (0 if unknown).</summary>
    public static List<NvOption> CreateOptions(int hz)
    {
        var latency = new NvOption
        {
            Key = "nv-latency", Id = ULL_CPL_STATE, Title = "Low Latency Mode",
            Main = true,
            Detail = "Less delay between your mouse and the screen. Roblox has no NVIDIA Reflex, so this is the next best thing. Pick On if Ultra stutters.",
            Writes = v => v switch
            {
                "off" => new() { S(ULL_CPL_STATE, "Low Latency Mode", 0, "Off"), S(ULL_ENABLED, "Ultra Low Latency", 0, "Off"), S(PRERENDERLIMIT, "Max pre-rendered frames", 0, "Roblox decides") },
                "on" => new() { S(ULL_CPL_STATE, "Low Latency Mode", 1, "On"), S(ULL_ENABLED, "Ultra Low Latency", 0, "Off"), S(PRERENDERLIMIT, "Max pre-rendered frames", 1, "1") },
                "ultra" => new() { S(ULL_CPL_STATE, "Low Latency Mode", 2, "Ultra"), S(ULL_ENABLED, "Ultra Low Latency", 1, "On"), S(PRERENDERLIMIT, "Max pre-rendered frames", 1, "1") },
                _ => new(),
            },
        };
        foreach (var (l, v) in new[] { ("Don't change", ""), ("Off", "off"), ("On", "on"), ("Ultra", "ultra") })
            latency.Choices.Add(new RobloxChoice { Owner = latency, Label = l, Value = v });

        var cap = Math.Min(Math.Max(hz - 3, 0), 237);
        var frl = new List<(string, uint)> { ("Off", 0) };
        if (hz >= 100) frl.Add((Pulse.Ui.Loc.F("{0} (G-SYNC)", cap), (uint)cap));
        foreach (var f in new uint[] { 60, 144, 240 }) if (f != cap) frl.Add((f.ToString(CultureInfo.InvariantCulture), f));

        var rows = new List<NvOption>
        {
            latency,
            Simple("power", PREFERRED_PSTATE, "Power management mode",
                "Keeps the card at full speed so it doesn't stutter while speeding back up.",
                ("Normal", 5), ("Maximum performance", 1)),
            Simple("vsync", VSYNCMODE, "Vertical sync",
                "Off gives the least delay. Turn it on only if you see tearing and don't have G-SYNC.",
                ("Roblox decides", VSYNC_APP), ("Off", VSYNC_OFF), ("On", VSYNC_ON), ("Fast Sync", VSYNC_FAST)),
            Simple("frl", FRL_FPS, "Max Frame Rate (driver)",
                hz >= 100 ? Pulse.Ui.Loc.F("NVIDIA's frame limiter. {0} keeps G-SYNC working on your {1} Hz screen. Don't use it together with the Roblox frame cap above.", cap, hz)
                          : "NVIDIA's frame limiter. Don't use it together with the Roblox frame cap above.",
                frl.ToArray()),
            Simple("refresh", REFRESH_RATE_OVERRIDE, "Preferred refresh rate",
                "Lets Alt+Enter fullscreen use your monitor's top refresh rate instead of 60 Hz.",
                ("Roblox decides", 0), ("Highest available", 1)),
            Simple("gsync", VRR_APP_OVERRIDE, "G-SYNC for Roblox",
                "Only turn this off if Roblox's menus flicker.",
                ("Allow", 0), ("Off", 1)),
            Simple("texfilter", QUALITY_ENHANCEMENTS, "Texture filtering quality",
                "Slightly blurrier textures at an angle for a few more FPS. Helps most on older cards.",
                ("High performance", 20), ("Performance", 10), ("Quality", 0), ("High quality", 0xFFFFFFF6)),
            Simple("threads", THREAD_CONTROL, "Threaded optimization",
                "Spreads the driver's work across CPU cores. Auto is right for almost every PC.",
                ("Auto", 0), ("On", 1), ("Off", 2)),
        };
        foreach (var r in rows) if (r.Key is "nv-power" or "nv-vsync" or "nv-frl") r.Main = true;
        return rows;
    }

    public static readonly Dictionary<string, Dictionary<string, string>> Presets = new()
    {
        ["lowlatency"] = new() { ["nv-latency"] = "ultra", ["nv-power"] = "1", ["nv-vsync"] = VSYNC_OFF.ToString(CultureInfo.InvariantCulture), ["nv-refresh"] = "1" },
        ["none"] = new(),
    };

    /// <summary>
    /// All driver settings to write: an imported .nip file first, then the rows, then the advanced (raw ID) settings.
    /// A later one wins for the same ID, so a row you pick overrides the file.
    /// </summary>
    public static List<NvSetting> Build(IEnumerable<NvOption> options, IReadOnlyDictionary<string, string> custom, NvImport import = null)
    {
        var list = new List<NvSetting>();
        foreach (var x in import?.Settings ?? new()) list.Add(new NvSetting(x.Id, x.Name, x.Value, $"0x{x.Value:X8}"));
        foreach (var o in options) list.AddRange(o.Writes(o.Value));
        foreach (var kv in custom ?? new Dictionary<string, string>())
            if (TryParseCustom(kv.Key, kv.Value, out var id, out var value, out _))
                list.Add(new NvSetting(id, $"0x{id:X8}", value, $"0x{value:X8}"));
        return list.GroupBy(s => s.Id).Select(g => g.Last()).ToList();
    }

    /// <summary>Checks a raw setting from the advanced editor: an 8-digit hex ID and a DWORD value (hex 0x... or decimal).</summary>
    public static bool TryParseCustom(string idText, string valueText, out uint id, out uint value, out string why)
    {
        id = 0; value = 0;
        idText = (idText ?? "").Trim(); valueText = (valueText ?? "").Trim();
        if (idText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) idText = idText[2..];
        if (idText.Length == 0 || idText.Length > 8 || !uint.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id) || id == 0)
        { why = "The setting ID is the hex number NVIDIA Profile Inspector shows, like 0x10835002."; return false; }
        bool ok = valueText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(valueText[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : uint.TryParse(valueText, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        if (!ok) { why = "The value is a whole number, or hex starting with 0x."; return false; }
        why = "";
        return true;
    }

    /// <summary>Puts what the profile has now into each row's "Now:" text.</summary>
    public static void ShowCurrent(IEnumerable<NvOption> options, IReadOnlyDictionary<uint, NvCurrent> current)
    {
        foreach (var o in options)
        {
            // Set after the row is on screen, so translate here (the page only translates text present when it loads).
            if (!current.TryGetValue(o.Id, out var c)) { o.Current = Pulse.Ui.Loc.T("Now: driver default"); continue; }
            var label = o.Choices.Skip(1).FirstOrDefault(ch => o.Writes(ch.Value).FirstOrDefault(w => w.Id == o.Id)?.Value == c.Value)?.Label;
            o.Current = Pulse.Ui.Loc.F("Now: {0}", label != null ? Pulse.Ui.Loc.T(label) : $"0x{c.Value:X8}");
        }
    }
}
