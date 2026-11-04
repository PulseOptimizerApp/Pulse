using System.Globalization;
using System.Xml.Linq;

namespace Pulse.Core.Roblox;

/// <summary>One driver setting taken from an NVIDIA Profile Inspector file. Saved in Pulse's state.</summary>
public class NvImported
{
    public uint Id { get; set; }
    public string Name { get; set; } = "";
    public uint Value { get; set; }
}

/// <summary>A .nip file Pulse applies to Roblox's profile.</summary>
public class NvImport
{
    public string FileName { get; set; } = "";
    public List<NvImported> Settings { get; set; } = new();
}

/// <summary>What Pulse makes of a .nip file: the settings it will use and the ones it leaves out, with why.</summary>
public class NipReadout
{
    public string ProfileName { get; init; } = "";
    public bool ForRoblox { get; init; }
    public List<NvImported> Use { get; } = new();
    public int SkippedHardware { get; set; }
    public int SkippedWholePc { get; set; }
    public int SkippedUnknown { get; set; }
    public int SkippedText { get; set; }
    public int Skipped => SkippedHardware + SkippedWholePc + SkippedUnknown + SkippedText;
}

/// <summary>
/// Reads NVIDIA Profile Inspector's .nip export (XML: ArrayOfProfile → Profile → Settings → ProfileSetting).
/// Pulse applies a file to Roblox's profile only, so it leaves out what makes no sense for one game or could harm
/// the PC: SLI / Optimus / 3D Vision hardware switches, settings that act on the whole PC, text values, and IDs
/// without a name (nobody can say what they do).
/// </summary>
public static class NipFile
{
    static readonly string[] Hardware =
        { "SLI", "Optimus", "Stereo", "PhysX", "CUDA", "Universal GPU", "Battery Boost", "Whisper Mode", "smooth AFR", "Preferred OpenGL GPU" };

    static readonly string[] WholePc =
        { "globally", "global feature", "Do not display this profile", "Notification", "Overlay Indicator", "Display the", "Background Application" };

    /// <summary>Names for settings Pulse knows even when the file leaves the name blank.</summary>
    static readonly Dictionary<uint, string> Known = new()
    {
        [NvidiaCatalog.ULL_CPL_STATE] = "Low Latency Mode",
        [NvidiaCatalog.ULL_ENABLED] = "Ultra Low Latency",
        [NvidiaCatalog.PRERENDERLIMIT] = "Max pre-rendered frames",
        [NvidiaCatalog.PREFERRED_PSTATE] = "Power management mode",
        [NvidiaCatalog.VSYNCMODE] = "Vertical sync",
        [NvidiaCatalog.FRL_FPS] = "Max Frame Rate (driver)",
        [NvidiaCatalog.QUALITY_ENHANCEMENTS] = "Texture filtering quality",
        [NvidiaCatalog.THREAD_CONTROL] = "Threaded optimization",
        [NvidiaCatalog.VRR_APP_OVERRIDE] = "G-SYNC for Roblox",
        [NvidiaCatalog.REFRESH_RATE_OVERRIDE] = "Preferred refresh rate",
    };

    /// <summary>Reads a .nip file. Throws <see cref="FormatException"/> with a plain message if it isn't one.</summary>
    public static NipReadout Read(string path) => Parse(File.ReadAllText(path));

    public static NipReadout Parse(string xml)
    {
        XDocument doc;
        // Profile Inspector writes encoding="utf-16" even when the bytes are plain text; parsing the string ignores it.
        try { doc = XDocument.Parse(xml); }
        catch (Exception) { throw new FormatException("This isn't an NVIDIA Profile Inspector file (.nip)."); }

        var profiles = doc.Root?.Name.LocalName == "ArrayOfProfile" ? doc.Root.Elements("Profile").ToList() : new();
        if (profiles.Count == 0) throw new FormatException("This file has no NVIDIA profile in it.");

        // A file with several profiles: use Roblox's if it has one, else the first.
        var profile = profiles.FirstOrDefault(p => p.Element("Executeables")?.Elements().Any(e =>
                          e.Value.Contains(NvidiaProfile.RobloxExe, StringComparison.OrdinalIgnoreCase)) == true) ?? profiles[0];
        var r = new NipReadout
        {
            ProfileName = profile.Element("ProfileName")?.Value?.Trim() ?? "",
            ForRoblox = profile.Element("Executeables")?.Elements().Any(e => e.Value.Contains(NvidiaProfile.RobloxExe, StringComparison.OrdinalIgnoreCase)) == true,
        };

        foreach (var s in profile.Element("Settings")?.Elements("ProfileSetting") ?? Enumerable.Empty<XElement>())
        {
            var name = s.Element("SettingNameInfo")?.Value?.Trim() ?? "";
            if (!uint.TryParse(s.Element("SettingID")?.Value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
            if (!string.Equals(s.Element("ValueType")?.Value?.Trim(), "Dword", StringComparison.OrdinalIgnoreCase)) { r.SkippedText++; continue; }
            if (!TryValue(s.Element("SettingValue")?.Value, out var value)) { r.SkippedText++; continue; }
            if (name.Length == 0 && !Known.TryGetValue(id, out name)) { r.SkippedUnknown++; continue; }
            if (Known.TryGetValue(id, out var pulseName)) name = pulseName;
            else if (Hardware.Any(w => name.Contains(w, StringComparison.Ordinal))) { r.SkippedHardware++; continue; } // "SLI" must not match "slider"
            else if (WholePc.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) { r.SkippedWholePc++; continue; }
            r.Use.RemoveAll(x => x.Id == id);
            r.Use.Add(new NvImported { Id = id, Name = name, Value = value });
        }
        return r;
    }

    static bool TryValue(string text, out uint value)
    {
        text = (text ?? "").Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
