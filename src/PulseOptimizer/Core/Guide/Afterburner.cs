using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Pulse.Core.Guide;

/// <summary>
/// What the MSI Afterburner guide needs to know about this PC: which graphics card it has, whether Afterburner and
/// RivaTuner Statistics Server are installed (and new enough), and a sensible undervolt starting point for the card.
/// Only looks at files; it never changes anything.
/// </summary>
public static class Afterburner
{
    public const string Download = "https://www.msi.com/Landing/afterburner/graphics-cards";
    /// <summary>The first version that knows RTX 50 and Radeon RX 9000 cards.</summary>
    public static readonly Version Needed = new(4, 6, 6);

    static string Find(params string[] relative)
    {
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles") })
            foreach (var rel in relative)
                if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, rel))) return Path.Combine(root, rel);
        return null;
    }

    public static string Exe => Find(@"MSI Afterburner\MSIAfterburner.exe");
    public static string RtssExe => Find(@"RivaTuner Statistics Server\RTSS.exe");

    public static Version Version
    {
        get
        {
            try
            {
                if (Exe is not { } exe) return null;
                var v = FileVersionInfo.GetVersionInfo(exe);
                return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
            }
            catch { return null; }
        }
    }

    public static bool Running
    {
        get
        {
            try
            {
                var p = Process.GetProcessesByName("MSIAfterburner");
                foreach (var x in p) x.Dispose();
                return p.Length > 0;
            }
            catch { return false; }
        }
    }

    public static void Open()
    {
        try { if (Exe is { } exe) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); } catch { }
    }

    /// <summary>The graphics card games run on: the discrete one when there is one.</summary>
    public static string GpuName =>
        SystemInfo.Gpus.Select(g => g.Name).FirstOrDefault(PcDiagnostics.IsDiscrete) ?? SystemInfo.Gpus.Select(g => g.Name).FirstOrDefault();

    /// <summary>"nvidia", "amd", "intel" or null.</summary>
    public static string Vendor(string gpu)
    {
        if (string.IsNullOrEmpty(gpu)) return null;
        if (Regex.IsMatch(gpu, "NVIDIA|GeForce|RTX|GTX", RegexOptions.IgnoreCase)) return "nvidia";
        if (Regex.IsMatch(gpu, "AMD|Radeon", RegexOptions.IgnoreCase)) return "amd";
        if (Regex.IsMatch(gpu, @"Intel.*Arc|\bArc\b", RegexOptions.IgnoreCase)) return "intel";
        return null;
    }

    /// <summary>A common undervolt starting point for this card, in English (the page translates it). Null if unknown.</summary>
    public static string StartingPoint(string gpu)
    {
        if (string.IsNullOrEmpty(gpu)) return null;
        if (Regex.IsMatch(gpu, @"RTX\s*50\d\d", RegexOptions.IgnoreCase)) return "900 to 950 mV at about your stock boost clock (around 2,800 to 2,950 MHz)";
        if (Regex.IsMatch(gpu, @"RTX\s*40\d\d", RegexOptions.IgnoreCase)) return "950 mV at about 2,700 to 2,750 MHz";
        if (Regex.IsMatch(gpu, @"RTX\s*30\d\d", RegexOptions.IgnoreCase)) return "850 to 875 mV at about 1,850 to 1,900 MHz";
        if (Regex.IsMatch(gpu, @"RTX\s*20\d\d|GTX\s*16\d\d", RegexOptions.IgnoreCase)) return "900 mV at your stock boost clock";
        if (Regex.IsMatch(gpu, @"RX\s*9\d\d\d", RegexOptions.IgnoreCase)) return "Voltage Offset −50 mV in AMD Software, then 10 to 20 mV more per test";
        if (Regex.IsMatch(gpu, @"RX\s*[67]\d\d\d", RegexOptions.IgnoreCase)) return "Max Voltage about 50 mV lower in AMD Software";
        if (Vendor(gpu) == "intel") return "Use Intel Graphics Software for tuning; Afterburner for the overlay and frame cap";
        return null;
    }
}
