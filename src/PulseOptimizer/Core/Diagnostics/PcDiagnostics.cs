using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Pulse.Ui;

namespace Pulse.Core;

public enum Severity { Ok, Check, Problem, Unknown }

public record StepLine(int Number, string Text);

public class Finding : Observable
{
    public string Id { get; init; }
    public string Title { get; init; }
    public Severity Severity { get; init; }
    /// <summary>What was found, with the numbers.</summary>
    public string Detail { get; init; }
    /// <summary>What to do about it, in plain English.</summary>
    public string Fix { get; init; } = "";
    /// <summary>"settings:..." opens a Settings page, "page:..." opens a Pulse page, "url:..." opens a site.</summary>
    public string ActionKey { get; init; } = "";
    public string ActionText { get; init; } = "";
    /// <summary>Lower runs first in the list (rough expected impact).</summary>
    public int Rank { get; init; }

    /// <summary>Numbered, PC-specific steps to fix it (filled in by FixGuides).</summary>
    public List<string> Steps { get; set; } = new();
    /// <summary>How to check the fix worked.</summary>
    public string Confirm { get; set; } = "";
    /// <summary>Values the fix guide needs (device name, refresh rate...).</summary>
    public Dictionary<string, string> Facts { get; } = new();

    public bool HasAction => !string.IsNullOrEmpty(ActionKey);
    public bool HasSteps => Steps.Count > 0;
    public bool HasConfirm => !string.IsNullOrEmpty(Confirm);
    public List<StepLine> NumberedSteps => Steps.Select((s, i) => new StepLine(i + 1, s)).ToList();
    public string SeverityText => Severity switch
    {
        Severity.Ok => "Looks good",
        Severity.Check => "Worth checking",
        Severity.Problem => "Problem found",
        _ => "Couldn't check",
    };
}

/// <summary>
/// "Diagnose before tweaking": the checks from the research doc's Diagnostics section.
/// Uses WMI class names (not localized perf-counter names) so checks work on any Windows language.
/// </summary>
public static partial class PcDiagnostics
{
    // ───────── USB drop-outs (gates the USB selective suspend tweak) ─────────

    public static bool UsbDropoutsSeen =>
        TweakEngine.State.UsbDropouts.Any(d => d > DateTime.Now.AddDays(-30));

    public static void RecordUsbDropout(string what)
    {
        TweakEngine.State.UsbDropouts.Add(DateTime.Now);
        TweakEngine.AddLog("diagnose", what);
    }

    // ───────── Run everything ─────────

    public static List<Finding> RunAll(IProgress<string> progress = null, CancellationToken cancel = default)
    {
        var checks = new (string label, Func<IEnumerable<Finding>> run)[]
        {
            ("Checking your monitors", Monitors),
            ("Checking memory speed", MemorySpeed),
            ("Checking for CPU throttling (20-second load test)", Throttling),
            ("Checking driver interrupt load", DpcLoad),
            ("Checking graphics driver age", GpuDriverAge),
            ("Checking free space", FreeSpace),
            ("Checking startup programs", StartupCount),
            ("Checking Microsoft Defender", Defender),
            ("Checking power plan", PowerPlan),
            ("Checking which graphics chip drives your monitors", MonitorOnMotherboard),
            ("Checking memory channels", MemoryChannels),
            ("Checking which drives your games are on", GamesOnHardDrive),
            ("Checking your network connection", NetworkConnection),
            ("Checking laptop power and graphics", LaptopPowerAndGpu),
            ("Checking Resizable BAR support", ResizableBar),
            ("Checking windowed game optimizations", WindowedGameOptimizations),
            ("Checking Memory Integrity", MemoryIntegrity),
        };
        var all = new List<Finding>();
        foreach (var (label, run) in checks)
        {
            if (cancel.IsCancellationRequested) break;
            progress?.Report(label);
            try { all.AddRange(run()); }
            catch (Exception ex)
            {
                all.Add(new Finding { Id = label, Title = label.Replace("Checking ", ""), Severity = Severity.Unknown, Detail = ex.Message, Rank = 99 });
            }
        }
        try { all.Add(UpgradeAdvice(all, SystemInfo.TotalRam / 1073741824.0, LastMonitors.Select(m => m.MaxHzAtCurrent).DefaultIfEmpty(0).Max())); } catch { }
        var facts = HardwareFacts.Get();
        foreach (var f in all)
        {
            try { FixGuides.Attach(f, facts); } catch { /* a guide must never break the checkup */ }
        }
        return all.OrderBy(f => f.Severity switch { Severity.Problem => 0, Severity.Check => 1, Severity.Unknown => 2, _ => 3 })
                  .ThenBy(f => f.Rank).ToList();
    }

    // ───────── Upgrade advisor ─────────

    /// <summary>
    /// The one hardware upgrade most worth the money for this PC, from what the checkup found: cheapest fix for the
    /// biggest limit first. Honest advice: no links, no brands, and "no clear upgrade" when there isn't one.
    /// </summary>
    internal static Finding UpgradeAdvice(IReadOnlyList<Finding> found, double ramGb, int maxHz)
    {
        bool Has(string id) => found.Any(f => f.Id.StartsWith(id) && f.Severity is Severity.Problem or Severity.Check);
        (string Title, string Detail) pick =
            Has("ram-channels") ? ("A second, matching RAM stick",
                "Your memory runs on one channel. A second stick of the same size and speed doubles memory bandwidth, which often gives a big FPS boost in CPU-heavy games, for the price of one stick.")
            : ramGb > 0 && ramGb < 15 ? (Ui.Loc.F("More memory: 16 GB or more (you have {0} GB)", Math.Round(ramGb)),
                "Modern games and Discord or a browser in the background need more than you have. Running out causes stutters that no setting fixes. Buy a matching pair so it stays dual-channel.")
            : Has("games-hdd") ? ("An SSD for your games",
                "Some of your games are on a hard drive. An SSD loads maps much faster and removes the stutters that happen while a game streams in new areas.")
            : maxHz > 0 && maxHz <= 75 ? ("A high refresh rate monitor (144 Hz or more)",
                "Your screen shows at most 60 to 75 frames a second, so any FPS above that is wasted. If your games already run above 60 FPS, a 144 Hz monitor is the biggest smoothness upgrade you can buy.")
            : ("No clear upgrade needed",
                "Nothing in the checkup points at one part holding your PC back. If a game runs slow, Measure shows whether the processor or the graphics card is the limit, and that's the part to upgrade.");
        return new Finding
        {
            Id = "upgrade", Rank = 60, Severity = Severity.Ok,
            Title = Ui.Loc.T("Best upgrade for your money: ") + Ui.Loc.T(pick.Title),
            Detail = pick.Detail,
        };
    }

    // ───────── Monitor refresh rate ─────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);

    const int ENUM_CURRENT_SETTINGS = -1;
    const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;

    static IEnumerable<Finding> RefreshRates()
    {
        var results = new List<Finding>();
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            var cur = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref cur)) continue;

            int best = cur.dmDisplayFrequency;
            for (int m = 0; ; m++)
            {
                var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (!EnumDisplaySettings(dd.DeviceName, m, ref dm)) break;
                if (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight && dm.dmDisplayFrequency > best)
                    best = dm.dmDisplayFrequency;
            }

            var name = MonitorName(dd.DeviceName) ?? dd.DeviceName.TrimStart('\\', '.');
            var res = $"{cur.dmPelsWidth}×{cur.dmPelsHeight}";
            if (best > cur.dmDisplayFrequency + 1)
                results.Add(new Finding
                {
                    Id = "refresh-" + i, Rank = 0, Severity = Severity.Problem,
                    Title = $"{name} is running slower than it can",
                    Detail = $"Set to {cur.dmDisplayFrequency} Hz at {res}, but it supports {best} Hz.",
                    Fix = $"Switch it to {best} Hz. This is the single biggest smoothness upgrade a high-refresh monitor can get, and it's free.",
                    ActionKey = "settings:ms-settings:display-advanced", ActionText = "Open display settings",
                });
            else
                results.Add(new Finding
                {
                    Id = "refresh-" + i, Rank = 0, Severity = Severity.Ok,
                    Title = $"{name} refresh rate",
                    Detail = $"Running at its maximum: {cur.dmDisplayFrequency} Hz at {res}.",
                });
        }
        return results;
    }

    static string MonitorName(string adapterDevice)
    {
        var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        return EnumDisplayDevices(adapterDevice, 0, ref mon, 0) && !string.IsNullOrWhiteSpace(mon.DeviceString)
            ? mon.DeviceString : null;
    }

    // ───────── Memory speed (XMP / EXPO) ─────────

    static IEnumerable<Finding> MemorySpeed()
    {
        var json = Ps("ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_PhysicalMemory | Select-Object Capacity,Speed,ConfiguredClockSpeed,SMBIOSMemoryType,Manufacturer,PartNumber)");
        var mods = json.RootElement.EnumerateArray().ToList();
        if (mods.Count == 0) yield break;

        var configured = mods.Select(m => Int(m, "ConfiguredClockSpeed")).Where(v => v > 0).DefaultIfEmpty(0).Min();
        var type = Int(mods[0], "SMBIOSMemoryType");
        var ddr = type switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", _ => "RAM" };
        var total = mods.Sum(m => Long(m, "Capacity"));
        var part = Str(mods[0], "PartNumber").Trim();

        // JEDEC base speeds that motherboards use when XMP/EXPO is off.
        bool atBase = (type == 26 && configured <= 2666) || (type == 34 && configured <= 4800);
        var detail = $"{Format.Bytes(total, 0)} {ddr} in {mods.Count} module(s), running at {configured} MT/s" +
                     (part.Length > 0 ? $" (part {part})." : ".");
        if (atBase)
            yield return new Finding
            {
                Id = "ram", Rank = 1, Severity = Severity.Check,
                Title = "Memory may be running at its slow default speed",
                Detail = detail + $" That's the standard {ddr} fallback speed used when XMP/EXPO is off.",
                Fix = "Look up your RAM's rated speed (on the box, the stick's label, or by searching the part number). If it's higher, turn on XMP (Intel) or EXPO (AMD) in your BIOS. Pulse never changes BIOS settings for you.",
                ActionKey = string.IsNullOrEmpty(part) ? "" : "url:https://www.bing.com/search?q=" + Uri.EscapeDataString(part + " RAM speed"),
                ActionText = "Look up this RAM",
            };
        else
            yield return new Finding { Id = "ram", Rank = 1, Severity = Severity.Ok, Title = "Memory speed", Detail = detail };

        if (mods.Select(m => Int(m, "ConfiguredClockSpeed")).Distinct().Count() > 1)
            yield return new Finding
            {
                Id = "ram-mixed", Rank = 1, Severity = Severity.Check, Title = "Memory modules run at different speeds",
                Detail = "Mixed modules usually all slow down to the slowest one.",
                Fix = "Matching kits run faster and more reliably.",
            };
    }

    // ───────── CPU throttling ─────────

    static IEnumerable<Finding> Throttling()
    {
        const string script = @"
$r = for ($i = 0; $i -lt 15; $i++) {
  $p = Get-CimInstance Win32_PerfFormattedData_Counters_ProcessorInformation -Filter ""Name='_Total'""
  [pscustomobject]@{ Perf = [double]$p.PercentProcessorPerformance; Limit = [double]$p.PercentPerformanceLimit }
  Start-Sleep -Milliseconds 900
}
ConvertTo-Json -Compress -InputObject @($r)";
        using var stop = new CancellationTokenSource();
        var workers = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => Task.Run(() =>
        {
            double x = 1;
            while (!stop.IsCancellationRequested) x = Math.Sqrt(x + 1.0001);
            return x;
        })).ToArray();
        JsonDocument doc;
        try
        {
            Thread.Sleep(2000); // let clocks settle under load
            doc = JsonDocument.Parse(Shell.PowerShell(script, 60_000).Output);
        }
        finally { stop.Cancel(); }

        var samples = doc.RootElement.EnumerateArray().Select(e => (perf: e.GetProperty("Perf").GetDouble(), limit: e.GetProperty("Limit").GetDouble())).ToList();
        if (samples.Count == 0) { yield return Unknown("cpu-throttle", "CPU throttling", "Windows didn't report CPU performance counters."); yield break; }
        var perf = samples.Average(s => s.perf);
        var limit = samples.Average(s => s.limit);

        if (limit > 5 || perf < 90)
            yield return new Finding
            {
                Id = "cpu-throttle", Rank = 2, Severity = Severity.Problem,
                Title = "Your CPU is being held back under load",
                Detail = $"Under full load the CPU ran at {perf:0}% of its base speed, and was limited {limit:0}% of the time by heat, power or firmware limits.",
                Fix = "Check CPU temperatures with your motherboard's tool or HWiNFO. Common causes: dust, a weak cooler, dried thermal paste, or a laptop on battery or a soft surface. Plug laptops in and use a higher power mode.",
            };
        else
            yield return new Finding
            {
                Id = "cpu-throttle", Rank = 2, Severity = Severity.Ok, Title = "CPU under load",
                Detail = $"Ran at {perf:0}% of base speed under full load with no meaningful throttling ({limit:0}% limited).",
            };
    }

    // ───────── DPC / interrupt load (screening) ─────────

    static IEnumerable<Finding> DpcLoad()
    {
        const string script = @"
$r = for ($i = 0; $i -lt 10; $i++) {
  $p = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter ""Name='_Total'""
  [pscustomobject]@{ Dpc = [double]$p.PercentDPCTime; Int = [double]$p.PercentInterruptTime }
  Start-Sleep -Milliseconds 900
}
ConvertTo-Json -Compress -InputObject @($r)";
        var doc = JsonDocument.Parse(Shell.PowerShell(script, 60_000).Output);
        var s = doc.RootElement.EnumerateArray().Select(e => (dpc: e.GetProperty("Dpc").GetDouble(), irq: e.GetProperty("Int").GetDouble())).ToList();
        if (s.Count == 0) { yield return Unknown("dpc", "Driver interrupt load", "Windows didn't report DPC counters."); yield break; }
        var dpc = s.Average(x => x.dpc); var irq = s.Average(x => x.irq); var peak = s.Max(x => x.dpc + x.irq);

        // Screening only: this shows total driver load, not which driver or how long each delay was.
        if (dpc + irq > 3 || peak > 8)
            yield return new Finding
            {
                Id = "dpc", Rank = 2, Severity = Severity.Check,
                Title = "Drivers are using a lot of CPU time at idle",
                Detail = $"At idle, drivers used {dpc:0.0}% of CPU time in DPCs and {irq:0.0}% in interrupts (peak {peak:0.0}%). Healthy PCs usually stay well under 2%.",
                Fix = "A misbehaving driver (network, audio, storage or RGB software are common) can cause stutter and audio pops. Pulse's next version will name the driver; until then, LatencyMon can pinpoint it. Updating chipset, network and audio drivers fixes most cases.",
            };
        else
            yield return new Finding
            {
                Id = "dpc", Rank = 2, Severity = Severity.Ok, Title = "Driver interrupt load",
                Detail = $"Low at idle: {dpc:0.0}% DPC, {irq:0.0}% interrupt time.",
            };
    }

    // ───────── Graphics driver age ─────────

    static IEnumerable<Finding> GpuDriverAge()
    {
        var doc = Ps("ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_VideoController | ForEach-Object { [pscustomobject]@{ Name=$_.Name; Version=$_.DriverVersion; Date=if ($_.DriverDate) { $_.DriverDate.ToString('o') } else { '' } } })");
        foreach (var g in doc.RootElement.EnumerateArray())
        {
            var name = Str(g, "Name");
            if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Remote", StringComparison.OrdinalIgnoreCase)) continue;
            if (!DateTime.TryParse(Str(g, "Date"), out var date)) continue;
            var days = (DateTime.Now - date).TotalDays;
            var vendorUrl =
                name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "https://www.nvidia.com/Download/index.aspx" :
                name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "https://www.amd.com/en/support/download/drivers.html" :
                name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "https://www.intel.com/content/www/us/en/download-center/home.html" : "";
            yield return days > 180
                ? new Finding
                {
                    Id = "gpu-driver-" + name, Rank = 3, Severity = Severity.Check,
                    Title = $"{name} driver is {(int)(days / 30)} months old",
                    Detail = $"Driver {Str(g, "Version")} from {date:MMM d, yyyy}.",
                    Fix = "New drivers often include fixes and performance improvements for recent games. Get them from the official site only.",
                    ActionKey = vendorUrl.Length > 0 ? "url:" + vendorUrl : "", ActionText = "Official driver page",
                }
                : new Finding { Id = "gpu-driver-" + name, Rank = 3, Severity = Severity.Ok, Title = $"{name} driver", Detail = $"Recent: {date:MMM d, yyyy}." };
        }
    }

    // ───────── Free space ─────────

    static IEnumerable<Finding> FreeSpace()
    {
        var d = SystemInfo.SystemDrive;
        double pct = (double)d.AvailableFreeSpace / d.TotalSize * 100;
        var detail = $"{Format.Bytes(d.AvailableFreeSpace)} free of {Format.Bytes(d.TotalSize)} ({pct:0}%) on {d.Name.TrimEnd('\\')}.";
        yield return pct < 10
            ? new Finding
            {
                Id = "space", Rank = 4, Severity = Severity.Problem, Title = "Your system drive is nearly full",
                Detail = detail, Fix = "Windows and games slow down and updates fail when the drive is this full. Clean junk files or move big games to another drive.",
                ActionKey = "page:cleanup", ActionText = "Open Debloat",
            }
            : new Finding { Id = "space", Rank = 4, Severity = pct < 20 ? Severity.Check : Severity.Ok, Title = "Free space", Detail = detail,
                            ActionKey = pct < 20 ? "page:cleanup" : "", ActionText = "Open Debloat" };
    }

    // ───────── Startup programs ─────────

    static IEnumerable<Finding> StartupCount()
    {
        int count = 0;
        foreach (var (hive, path) in new[]
        {
            (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
        })
            count += Reg.ValueNames(hive, path).Length;
        foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            try { count += Directory.GetFiles(Environment.GetFolderPath(folder)).Count(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)); } catch { }
        }
        yield return new Finding
        {
            Id = "startup", Rank = 5,
            Severity = count > 12 ? Severity.Check : Severity.Ok,
            Title = "Startup programs",
            Detail = $"{count} programs are set to launch when you sign in (some may already be disabled in Task Manager).",
            Fix = count > 12 ? "Each one uses memory and CPU in the background while you play. Review which ones you actually need." : "",
            ActionKey = count > 12 ? "page:startup" : "", ActionText = "Review startup apps",
        };
    }

    // ───────── Defender ─────────

    static IEnumerable<Finding> Defender()
    {
        var doc = Ps("$s = Get-MpComputerStatus; ConvertTo-Json -Compress -InputObject ([pscustomobject]@{ On=[bool]$s.AntivirusEnabled; Rt=[bool]$s.RealTimeProtectionEnabled; Age=[long]$s.AntivirusSignatureAge })");
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object || !r.GetProperty("On").GetBoolean())
        {
            yield return new Finding { Id = "defender", Rank = 6, Severity = Severity.Check, Title = "Microsoft Defender isn't active",
                Detail = "Another antivirus may be in charge.", Fix = "That's fine if you trust your other antivirus. Pulse's scanner uses Defender, so its scans won't run.",
                ActionKey = "page:security", ActionText = "Open Security" };
            yield break;
        }
        bool rt = r.GetProperty("Rt").GetBoolean(); long age = r.GetProperty("Age").GetInt64();
        if (!rt || age > 3)
            yield return new Finding
            {
                Id = "defender", Rank = 6, Severity = Severity.Problem, Title = "Your PC's protection needs attention",
                Detail = (!rt ? "Real-time protection is off. " : "") + (age > 3 ? $"Virus definitions are {age} days old." : ""),
                Fix = "Turn real-time protection back on and update definitions.", ActionKey = "page:security", ActionText = "Open Security",
            };
        else
            yield return new Finding { Id = "defender", Rank = 6, Severity = Severity.Ok, Title = "Microsoft Defender", Detail = "Real-time protection on, definitions up to date." };
    }

    // ───────── Power plan ─────────

    static IEnumerable<Finding> PowerPlan()
    {
        var o = Shell.Cmd("powercfg /getactivescheme").Output;
        var name = System.Text.RegularExpressions.Regex.Match(o, @"\(([^)]+)\)").Groups[1].Value;
        bool saver = name.Contains("saver", StringComparison.OrdinalIgnoreCase);
        yield return new Finding
        {
            Id = "power", Rank = 2, Severity = saver ? Severity.Problem : Severity.Ok,
            Title = "Power plan",
            Detail = $"Active plan: {(name.Length > 0 ? name : "unknown")}.",
            Fix = saver ? "Power saver caps CPU speed. Switch to Balanced (or plug in, on a laptop)." : "",
            ActionKey = saver ? "settings:ms-settings:powersleep" : "", ActionText = "Open power settings",
        };
    }

    // ───────── helpers ─────────

    static JsonDocument Ps(string script)
    {
        var r = Shell.PowerShell(script, 60_000);
        var text = r.Output.Trim();
        return JsonDocument.Parse(text.Length == 0 ? "[]" : text);
    }

    static Finding Unknown(string id, string title, string why) =>
        new() { Id = id, Title = title, Severity = Severity.Unknown, Detail = why, Rank = 99 };

    static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

    static int Int(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    static long Long(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : 0;
}
