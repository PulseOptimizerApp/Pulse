using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Pulse.Core;

/// <summary>
/// BIOS checks. Read-only like everything in the checkup: Pulse never changes BIOS settings, it says what to change
/// and where. Sources: Intel's 13th/14th Gen instability guidance (microcode 0x12B and Intel Default Settings),
/// AMD's guidance for Ryzen 7000/9000 (update the BIOS, EXPO, PBO and Curve Optimizer), and the Windows 11 /
/// anti-cheat requirements for Secure Boot and TPM 2.0.
/// </summary>
public static partial class PcDiagnostics
{
    static readonly Regex IntelRaptorDesktop = new(@"1[34]th Gen Intel.*Core.*i[579]-1[34]\d{3}(K|KF|KS|F)?(?![\w-]*[HUPX])\b", RegexOptions.IgnoreCase);
    static readonly Regex Ryzen7000Plus = new(@"Ryzen\s+[3579]\s+(7|9)\d{3}", RegexOptions.IgnoreCase);
    static readonly Regex RyzenAny = new(@"Ryzen\s+[3579]\s+[1-9]\d{3}", RegexOptions.IgnoreCase);

    /// <summary>The CPU microcode revision Windows loaded (0 if unknown).</summary>
    internal static uint MicrocodeRevision()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (k?.GetValue("Update Revision") is byte[] b && b.Length >= 8) return BitConverter.ToUInt32(b, 4);
        }
        catch { }
        return 0;
    }

    internal static DateTime? BiosDate()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (k?.GetValue("BIOSReleaseDate") is string s &&
                DateTime.TryParseExact(s.Trim(), new[] { "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d;
        }
        catch { }
        return null;
    }

    static string BiosVersion()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return (k?.GetValue("BIOSVersion") as string ?? "").Trim();
        }
        catch { return ""; }
    }

    static IEnumerable<Finding> Bios()
    {
        var hw = HardwareFacts.Get();
        var cpu = (SystemInfo.Cpu ?? "").Trim();
        var date = BiosDate();
        string dateText = date is DateTime d0 ? d0.ToString("MMMM yyyy", CultureInfo.InvariantCulture) : "an unknown date";
        string updateStep = hw.Laptop || hw.Board == BoardMaker.Prebuilt
            ? "Get BIOS updates from your PC maker's support app or website (MyASUS, Lenovo Vantage, HP Support Assistant, Dell SupportAssist, MSI Center). Keep it plugged in while it updates."
            : $"Download the newest BIOS for your exact motherboard ({hw.BoardText}) from the maker's support page, put it on a USB stick, and use the BIOS's built-in flash tool (ASUS EZ Flash, MSI M-Flash, Gigabyte Q-Flash, ASRock Instant Flash). Never turn the PC off during the update.";

        // Intel 13th / 14th Gen desktop: Intel's fix for the voltage-related degradation (Vmin shift).
        if (IntelRaptorDesktop.IsMatch(cpu) && !hw.Laptop)
        {
            uint mc = MicrocodeRevision();
            if (mc != 0 && mc < 0x12B)
                yield return new Finding
                {
                    Id = "bios-intel-microcode", Rank = 0, Severity = Severity.Problem,
                    Title = "Update your BIOS: your Intel CPU needs Intel's protection fix",
                    Detail = $"Your {cpu} is running microcode 0x{mc:X}. Intel found that 13th and 14th Gen desktop CPUs can permanently degrade from too-high voltage, and fixed it in microcode 0x12B and later, which comes with a BIOS update.",
                    Fix = "Update the BIOS, then pick the \"Intel Default Settings\" profile. This protects the CPU; Intel measured no real performance change.",
                    Steps = new()
                    {
                        updateStep,
                        $"After updating, restart and tap {hw.BiosKey} to open the BIOS.",
                        "Find the CPU power profile (\"Intel Default Settings\", \"Intel Baseline Profile\" or \"Performance Preference\") and choose Intel Default Settings (\"Performance\", or \"Extreme\" for i9 K-series if offered).",
                        "Leave XMP on only if your PC is stable. If games crash, turn XMP off and test again.",
                        "Press F10 to save and exit.",
                    },
                    Confirm = "Run this checkup again: it shows the new microcode version.",
                };
            else if (mc != 0)
                yield return new Finding
                {
                    Id = "bios-intel-microcode", Rank = 50, Severity = Severity.Ok,
                    Title = "Intel's CPU protection fix is installed",
                    Detail = $"Microcode 0x{mc:X} includes Intel's fix for 13th/14th Gen degradation. Keep the \"Intel Default Settings\" profile in the BIOS and avoid Multi-Core Enhancement or raised power limits on these CPUs.",
                };
        }

        // BIOS age.
        if (date is DateTime d && d < DateTime.Today.AddMonths(-18))
        {
            bool amdNew = Ryzen7000Plus.IsMatch(cpu) && !hw.Laptop;
            yield return new Finding
            {
                Id = "bios-old", Rank = 7, Severity = amdNew && d < new DateTime(2024, 6, 1) ? Severity.Problem : Severity.Check,
                Title = "Your BIOS is over a year and a half old",
                Detail = $"Your BIOS ({BiosVersion()}) is from {dateText}. BIOS updates fix stability, memory compatibility and boosting, and add options like Resizable BAR." +
                         (amdNew ? " On Ryzen 7000 and 9000 CPUs, newer BIOS versions also cap the SoC voltage when EXPO is on, which protects the CPU." : ""),
                Fix = "Update the BIOS from your motherboard or PC maker. It takes about 5 minutes. Settings may reset, so turn XMP/EXPO and Resizable BAR back on afterwards.",
                Steps = new()
                {
                    "Write down settings you changed (XMP/EXPO, fan curves, boot order): updates usually reset them.",
                    updateStep,
                    $"After it restarts, tap {hw.BiosKey} and turn {hw.MemoryProfile} and Resizable BAR back on.",
                },
                Confirm = "Run this checkup again: the BIOS date changes.",
            };
        }

        // Secure Boot: required by Valorant, Battlefield 6, Call of Duty and others on Windows 11.
        bool? secureBoot = null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (k?.GetValue("UEFISecureBootEnabled") is int v) secureBoot = v == 1;
        }
        catch { }
        if (secureBoot == false)
            yield return new Finding
            {
                Id = "bios-secureboot", Rank = 8, Severity = Severity.Check,
                Title = "Secure Boot is off",
                Detail = "Valorant, Battlefield 6, Call of Duty and other games with kernel anti-cheat need Secure Boot (and TPM 2.0) on Windows 11, and won't start without it. It doesn't change performance.",
                Fix = "Turn Secure Boot on in the BIOS. Only do this if Windows is installed in UEFI mode (Start → System Information → BIOS Mode = UEFI).",
                Steps = new()
                {
                    "Check first: Start → type System Information → BIOS Mode must say UEFI. If it says Legacy, don't change anything: Windows wouldn't start.",
                    $"Restart and tap {hw.BiosKey} to open the BIOS.",
                    "Find Secure Boot (usually under Boot or Security). Set OS Type to \"Windows UEFI mode\" and Secure Boot to Enabled. If it asks, install the default keys.",
                    "Make sure TPM is on too: Intel PTT or AMD fTPM, usually under Security or Advanced → Trusted Computing.",
                    "Press F10 to save and exit.",
                },
                Confirm = "System Information shows \"Secure Boot State: On\".",
            };

        // Optional extra performance on Ryzen desktops: PBO with a mild negative Curve Optimizer.
        if (RyzenAny.IsMatch(cpu) && !hw.Laptop)
            yield return new Finding
            {
                Id = "bios-pbo", Rank = 60, Severity = Severity.Ok,
                Title = "Optional: more CPU performance with PBO and Curve Optimizer",
                Detail = "Your Ryzen runs at AMD's defaults, which is safe. With a good cooler, AMD's Precision Boost Overdrive plus a mild negative Curve Optimizer lowers voltage, so the CPU runs cooler and boosts higher for longer. Every CPU is different, so test it.",
                Steps = new()
                {
                    "Update the BIOS first, and turn on EXPO for your memory (DDR5-6000 is the sweet spot on Ryzen 7000/9000).",
                    $"Restart and tap {hw.BiosKey}. Find Precision Boost Overdrive (ASUS: Ai Tweaker; MSI: OC → Advanced CPU Configuration → AMD Overclocking; Gigabyte: Tweaker → Advanced CPU Settings).",
                    "Set PBO to Enabled (or Advanced), and leave power limits on Auto / Motherboard.",
                    "Curve Optimizer: All Cores, Sign Negative, Magnitude 15. Don't start higher.",
                    "Save, then play your games and run a stress test for 30 minutes. If anything crashes or freezes, go back and use 10, or turn Curve Optimizer off.",
                    "Never raise CPU or SoC voltage by hand. On Ryzen 7000/9000 the SoC voltage should stay at 1.3 V or lower.",
                },
                Confirm = "Measure a game before and after in Pulse: higher 1% lows and lower CPU temperatures mean it's working.",
            };
    }
}
