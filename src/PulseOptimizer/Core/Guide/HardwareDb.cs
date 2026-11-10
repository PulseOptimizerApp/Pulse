using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pulse.Core.Guide;

/// <param name="Price">Rough US price in dollars (0: not sold on its own, like a laptop chip).</param>
/// <param name="Used">No longer sold new: the price is a typical used one.</param>
public sealed record HwCpu(string Name, string Brand, int Year, int Cores, int Threads, double Ghz, double Score, string Kind, int Price = 0, bool Used = false);
public sealed record HwGpu(string Name, string Brand, int Year, int Vram, double Score, int Watts, string Kind, int Price = 0, bool Used = false);
public sealed record HwGame(string Name, string Settings, double Cpu, double G720, double G1080, double G1440, double V1080, double V1440, double Cap);

/// <summary>Memory as the builder sees it.</summary>
public sealed record HwRam(int Gb, string Type, int Mhz, bool Dual)
{
    public string Label => $"{Gb} GB {Type}-{Mhz}" + (Dual ? "" : " (single channel)");
}

/// <summary>One game at one resolution: the expected frame rate and what holds it back.</summary>
public sealed record HwFps(double Fps, string Limit, bool VramShort, bool Capped);

/// <summary>
/// Pulse's built-in table of CPUs and graphics cards with a gaming score for each (CPU: Ryzen 7 7800X3D = 100;
/// graphics: RTX 4070 = 100, from public review averages such as Tom's Hardware's hierarchies), plus reference frame
/// rates for popular games on a known PC. An estimate for any other PC scales those: the CPU sets the frame rate a
/// game can reach (memory speed and channels nudge it), the graphics card sets what each resolution allows (short of
/// video memory costs extra), and the lower one wins. Estimates, not benchmarks: drivers, settings, coolers and
/// patches move real numbers by 10–20%.
/// </summary>
public static class HardwareDb
{
    static readonly Lazy<(List<HwCpu> Cpus, List<HwGpu> Gpus, List<HwGame> Games, string Basis)> Data = new(Load);

    public static List<HwCpu> Cpus => Data.Value.Cpus;
    public static List<HwGpu> Gpus => Data.Value.Gpus;
    public static List<HwGame> Games => Data.Value.Games;
    public static string Basis => Data.Value.Basis;

    static (List<HwCpu>, List<HwGpu>, List<HwGame>, string) Load()
    {
        var cpus = new List<HwCpu>(); var gpus = new List<HwGpu>(); var games = new List<HwGame>(); string basis = "";
        try
        {
            using var s = typeof(HardwareDb).Assembly.GetManifestResourceStream("hardware.json");
            if (s == null) return (cpus, gpus, games, basis);
            using var doc = JsonDocument.Parse(s);
            var r = doc.RootElement;
            basis = r.GetProperty("basis").GetString();
            foreach (var c in r.GetProperty("cpus").EnumerateArray())
                cpus.Add(new HwCpu(c.GetProperty("n").GetString(), c.GetProperty("b").GetString(), c.GetProperty("y").GetInt32(), c.GetProperty("c").GetInt32(),
                    c.GetProperty("t").GetInt32(), c.GetProperty("ghz").GetDouble(), c.GetProperty("s").GetDouble(), c.GetProperty("k").GetString(), Price(c), Used(c)));
            foreach (var g in r.GetProperty("gpus").EnumerateArray())
                gpus.Add(new HwGpu(g.GetProperty("n").GetString(), g.GetProperty("b").GetString(), g.GetProperty("y").GetInt32(), g.GetProperty("vram").GetInt32(),
                    g.GetProperty("s").GetDouble(), g.GetProperty("w").GetInt32(), g.GetProperty("k").GetString(), Price(g), Used(g)));
            foreach (var g in r.GetProperty("games").EnumerateArray())
                games.Add(new HwGame(g.GetProperty("n").GetString(), g.GetProperty("set").GetString(), g.GetProperty("cpu").GetDouble(), g.GetProperty("g720").GetDouble(),
                    g.GetProperty("g1080").GetDouble(), g.GetProperty("g1440").GetDouble(), g.GetProperty("v1080").GetDouble(), g.GetProperty("v1440").GetDouble(), g.GetProperty("cap").GetDouble()));
        }
        catch { }
        return (cpus, gpus, games, basis);
    }

    static int Price(JsonElement e) => e.TryGetProperty("p", out var p) ? p.GetInt32() : 0;
    static bool Used(JsonElement e) => e.TryGetProperty("u", out var u) && u.GetBoolean();

    // ───────── prices ─────────

    /// <summary>"$449", or "$120 used" for parts no longer sold new; null for parts not sold on their own.</summary>
    public static string PriceText(int price, bool used) => price <= 0 ? null : "$" + price.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + (used ? " " + Pulse.Ui.Loc.T("used") : "");
    public static string PriceText(HwCpu c) => c == null ? null : PriceText(c.Price, c.Used);
    public static string PriceText(HwGpu g) => g == null ? null : PriceText(g.Price, g.Used);
    public static string PriceText(HwRam r) => r == null ? null : PriceText(RamPrice(r), false);

    /// <summary>
    /// A memory kit's rough US price (late 2026, when memory costs several times what it did a year before).
    /// One stick costs about the same as two of half the size.
    /// </summary>
    public static int RamPrice(HwRam r)
    {
        if (r == null || r.Gb <= 0) return 0;
        bool ddr5 = r.Type == "DDR5";
        double perGb = ddr5 ? 13.5 : 6.3;
        // Faster kits cost a little more; slow ones a little less.
        double speed = ddr5 ? 1 + (r.Mhz - 6000) / 1200.0 * 0.08 : 1 + (r.Mhz - 3200) / 800.0 * 0.06;
        double floor = ddr5 ? 90 : 35;
        return (int)(Math.Round(Math.Max(floor, r.Gb * perGb * Math.Clamp(speed, 0.85, 1.25)) / 5) * 5);
    }

    /// <summary>
    /// The rest of a desktop (motherboard, 1 TB SSD, power supply, case and cooler), sized to the graphics card:
    /// a bigger card needs a bigger power supply and a better case.
    /// </summary>
    public static int RestOfPc(HwCpu c, HwGpu g)
    {
        double s = g?.Score ?? 50;
        int rest = s >= 160 ? 750 : s >= 110 ? 550 : s >= 70 ? 420 : 320;
        // DDR5 boards (AM5, Intel 12th gen and newer) cost more than DDR4 ones.
        if (c != null && NewPlatform(c)) rest += 60;
        return rest;
    }

    /// <summary>AM5 Ryzens (7000 and newer) and Core Ultra take DDR5 boards.</summary>
    public static bool NewPlatform(HwCpu c) => c.Name.StartsWith("Core Ultra") || Regex.IsMatch(c.Name, @"^Ryzen \d [789]\d{3}");

    /// <summary>A ready-made build: a starting point for each budget.</summary>
    public sealed record Build(string Name, string Why, HwCpu Cpu, HwGpu Gpu, HwRam Ram)
    {
        public int Parts => (Cpu?.Price ?? 0) + (Gpu?.Price ?? 0) + RamPrice(Ram);
        public int Total => Parts + RestOfPc(Cpu, Gpu);
    }

    /// <summary>Builds from low end to the best, plus one made for competitive shooters (most frames for the money).</summary>
    public static List<Build> Builds()
    {
        HwCpu C(string n) => Cpus.FirstOrDefault(c => c.Name == n);
        HwGpu G(string n) => Gpus.FirstOrDefault(g => g.Name == n);
        var list = new List<Build>
        {
            new("Low end", "Cheapest that plays everything at 1080p, with used graphics", C("Ryzen 5 5500"), G("Radeon RX 6600"), new HwRam(16, "DDR4", 3200, true)),
            new("Entry level", "New parts, smooth 1080p, high fps in Valorant and Fortnite", C("Ryzen 5 5600"), G("GeForce RTX 5060"), new HwRam(16, "DDR4", 3200, true)),
            new("Mid range", "1440p in most games, and room to upgrade", C("Ryzen 5 9600X"), G("Radeon RX 9060 XT 16GB"), new HwRam(32, "DDR5", 6000, true)),
            new("Competitive", "The fastest gaming CPU with a sensible card: the most frames in Valorant and CS2 for the money", C("Ryzen 7 9800X3D"), G("GeForce RTX 5060 Ti 16GB"), new HwRam(32, "DDR5", 6000, true)),
            new("High end", "High settings at 1440p, high frame rates everywhere", C("Ryzen 7 9800X3D"), G("GeForce RTX 5070 Ti"), new HwRam(32, "DDR5", 6000, true)),
            new("Top gaming", "The fastest there is, 4K included", C("Ryzen 7 9850X3D"), G("GeForce RTX 5090"), new HwRam(32, "DDR5", 6400, true)),
        };
        return list.Where(b => b.Cpu != null && b.Gpu != null).ToList();
    }

    // ───────── names ─────────

    /// <summary>
    /// Windows' names and the table's to one form: "13th Gen Intel(R) Core(TM) i7-13700KF" → "i7-13700k",
    /// "AMD Ryzen 7 7840HS w/ Radeon 780M Graphics" → "ryzen 7 7840hs", "NVIDIA GeForce RTX 4060 Laptop GPU" → "rtx 4060 laptop".
    /// </summary>
    public static string Norm(string name)
    {
        var s = (name ?? "").ToLowerInvariant();
        s = s.Replace("(r)", " ").Replace("(tm)", " ").Replace("®", " ").Replace("™", " ");
        s = Regex.Replace(s, @"\s(w/|with)\s.*$", " ");                         // "w/ Radeon 780M Graphics"
        s = Regex.Replace(s, @"\bcpu\s*@.*$", " ");                             // "CPU @ 2.90GHz"
        s = Regex.Replace(s, @"\b\d+(st|nd|rd|th) gen\b", " ");
        s = Regex.Replace(s, @"\b\d+-core\b|\bprocessor\b|\bgpu\b|\bnvidia\b|\bamd\b|\bintel\b|\bgeforce\b", " ");
        s = Regex.Replace(s, @"\bcore\b(?!\s*ultra)", " ");                      // "Core i7", but keep "Core Ultra"
        s = s.Replace("core ultra", "ultra");
        s = Regex.Replace(s, @"\bradeon\b(?!\s*\d{3}m\b)(?!\s*vega)", " ");    // "Radeon RX 6600" → "rx 6600"; keep "Radeon 780M"
        s = Regex.Replace(s, @"\b(i[3579]-\d{4,5}[a-z]*?)(kf|f|ks)\b", m => m.Groups[1].Value + (m.Groups[2].Value == "f" ? "" : "k"));
        s = Regex.Replace(s, @"\b(ultra [579] \d{3}k?)f\b", "$1");              // Ultra 7 265KF
        s = Regex.Replace(s, @"\bgraphics\b", " ");
        s = Regex.Replace(s, @"[^a-z0-9\-\. ]", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    static T Best<T>(IEnumerable<T> items, Func<T, string> name, string detected) where T : class
    {
        var d = " " + Norm(detected) + " ";
        T best = null; int len = 0;
        foreach (var i in items)
        {
            var k = Norm(name(i));
            if (k.Length > len && d.Contains(" " + k + " ", StringComparison.Ordinal)) { best = i; len = k.Length; }
        }
        return best;
    }

    public static HwCpu MatchCpu(string detected) => string.IsNullOrWhiteSpace(detected) ? null : Best(Cpus, c => c.Name, detected);

    /// <summary>
    /// The table's card for Windows' name. Cards sold with two memory sizes under one name ("RTX 4060 Ti" 8 or 16 GB,
    /// "RTX 3060" 12 or 8 GB) pick the one closest to <paramref name="vramBytes"/> when it's known.
    /// </summary>
    public static HwGpu MatchGpu(string detected, long vramBytes = 0)
    {
        if (string.IsNullOrWhiteSpace(detected)) return null;
        var best = Best(Gpus, g => g.Name, detected);
        if (best == null || vramBytes <= 0) return best;
        var key = Norm(best.Name);
        var family = Gpus.Where(g => g == best || Regex.IsMatch(Norm(g.Name), "^" + Regex.Escape(key) + @" \d+gb$")).ToList();
        double gb = vramBytes / 1073741824.0;
        return family.OrderBy(g => Math.Abs(g.Vram - gb)).First();
    }

    /// <summary>Parts whose name has every word typed, best first (exact start, then newer and faster).</summary>
    public static List<object> Search(string text, int max = 8)
    {
        var words = Norm(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return new();
        bool Has(string name) { var n = Norm(name) + " " + name.ToLowerInvariant(); return words.All(w => n.Contains(w, StringComparison.Ordinal)); }
        var hits = new List<(object Part, double Rank)>();
        foreach (var c in Cpus) if (Has(c.Name)) hits.Add((c, Rank(c.Name, c.Year) + c.Score / 100000));
        foreach (var g in Gpus) if (Has(g.Name)) hits.Add((g, Rank(g.Name, g.Year) + g.Score / 100000));
        foreach (var r in RamKits) if (Has(r.Label + " ram memory")) hits.Add((r, Rank(r.Label, 2020)));
        return hits.OrderByDescending(h => h.Rank).Take(max).Select(h => h.Part).ToList();

        double Rank(string name, int year)
        {
            var n = Norm(name);
            double r = (year - 2000) / 100.0;
            var tokens = n.Split(' ');
            r += 2 * words.Count(w => tokens.Contains(w));                     // whole model numbers beat partial ones
            r += words.Count(w => tokens.Any(t => t.StartsWith(w, StringComparison.Ordinal)));   // "rtx 50" → RTX 50-series before RTX 3050
            if (n == string.Join(' ', words)) r += 10;
            else if (n.StartsWith(words[0], StringComparison.Ordinal) || n.Contains(" " + words[0], StringComparison.Ordinal)) r += 1;
            return r - n.Length / 1000.0;
        }
    }

    /// <summary>Memory to pick from (and to find in search).</summary>
    public static readonly List<HwRam> RamKits = new[] { 8, 16, 32, 64 }.SelectMany(gb =>
        new[] { ("DDR4", 2400), ("DDR4", 2666), ("DDR4", 3200), ("DDR4", 3600), ("DDR5", 4800), ("DDR5", 5600), ("DDR5", 6000), ("DDR5", 6400), ("DDR5", 7200) }
            .Select(t => new HwRam(gb, t.Item1, t.Item2, true))).ToList();

    // ───────── how good ─────────

    public static double MaxCpu => Cpus.Count == 0 ? 100 : Cpus.Max(c => c.Score);
    public static double MaxGpu => Gpus.Count == 0 ? 100 : Gpus.Max(g => g.Score);

    /// <summary>"Top gaming", "High end", "Mid range", "Entry level" or "Basic", by score.</summary>
    public static string Tier(HwCpu c) => c.Score >= 95 ? "Top gaming" : c.Score >= 78 ? "High end" : c.Score >= 58 ? "Mid range" : c.Score >= 40 ? "Entry level" : "Basic";
    public static string Tier(HwGpu g) => g.Score >= 140 ? "Top gaming" : g.Score >= 95 ? "High end" : g.Score >= 60 ? "Mid range" : g.Score >= 30 ? "Entry level" : "Basic";

    /// <summary>How memory speed, channels and size move CPU-limited frame rates (DDR5-6000, dual channel, 16 GB+ = 1).</summary>
    public static double RamFactor(HwRam r)
    {
        if (r == null) return 1;
        double speed = r.Type == "DDR5"
            ? r.Mhz switch { >= 7000 => 1.02, >= 6400 => 1.01, >= 6000 => 1.0, >= 5600 => 0.98, >= 5200 => 0.97, _ => 0.95 }
            : r.Mhz switch { >= 3600 => 0.97, >= 3200 => 0.95, >= 3000 => 0.93, >= 2666 => 0.90, >= 2400 => 0.88, _ => 0.85 };
        double channels = r.Dual ? 1 : 0.82;
        double size = r.Gb <= 8 ? 0.85 : 1;
        return speed * channels * size;
    }

    // ───────── frame rates ─────────

    /// <summary>
    /// The expected frame rate in a game at a resolution (720, 1080 or 1440). The CPU's and the graphics card's limits
    /// are blended so the slower one dominates (a hard minimum overstates real frame rates near the crossover).
    /// </summary>
    public static HwFps Estimate(HwCpu cpu, HwGpu gpu, HwRam ram, HwGame game, int res)
    {
        if (cpu == null || gpu == null || game == null) return null;
        double cpuFps = game.Cpu * cpu.Score / 100.0 * RamFactor(ram);
        double gpuRef = res switch { 720 => game.G720, 1440 => game.G1440, _ => game.G1080 };
        double gpuFps = gpuRef * gpu.Score / 100.0;
        double need = res >= 1440 ? game.V1440 : res <= 720 ? Math.Max(1, game.V1080 - 1) : game.V1080;
        // Integrated graphics share system memory: count what the system can spare.
        double vram = gpu.Vram > 0 ? gpu.Vram : Math.Max(1, (ram?.Gb ?? 16) / 4.0);
        bool shortOn = vram + 0.01 < need;
        if (shortOn) gpuFps *= 0.55 + 0.45 * Math.Clamp(vram / need, 0, 1);
        const double k = 6;
        double fps = Math.Pow(Math.Pow(cpuFps, -k) + Math.Pow(gpuFps, -k), -1 / k);
        bool capped = game.Cap > 0 && fps > game.Cap;
        if (capped) fps = game.Cap;
        string limit = capped ? "cap" : Math.Abs(cpuFps - gpuFps) / Math.Max(cpuFps, gpuFps) < 0.1 ? "balanced" : cpuFps < gpuFps ? "cpu" : "gpu";
        return new HwFps(fps, limit, shortOn, capped);
    }

    // ───────── this PC ─────────

    /// <summary>The memory in this PC (size, DDR4/DDR5, speed it runs at, sticks), via Windows' own WMI; null if unknown.</summary>
    public static HwRam DetectRam()
    {
        try
        {
            var r = Shell.PowerShell("ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_PhysicalMemory | Select-Object Capacity,ConfiguredClockSpeed,Speed,SMBIOSMemoryType)", 20_000);
            using var doc = JsonDocument.Parse(r.Output.Trim().Length == 0 ? "[]" : r.Output.Trim());
            long bytes = 0; int mhz = 0, type = 0, sticks = 0;
            foreach (var m in doc.RootElement.EnumerateArray())
            {
                sticks++;
                if (m.TryGetProperty("Capacity", out var c) && c.ValueKind == JsonValueKind.Number) bytes += c.GetInt64();
                int speed = 0;
                if (m.TryGetProperty("ConfiguredClockSpeed", out var cs) && cs.ValueKind == JsonValueKind.Number) speed = cs.GetInt32();
                if (speed == 0 && m.TryGetProperty("Speed", out var sp) && sp.ValueKind == JsonValueKind.Number) speed = sp.GetInt32();
                mhz = Math.Max(mhz, speed);
                if (m.TryGetProperty("SMBIOSMemoryType", out var t) && t.ValueKind == JsonValueKind.Number) type = Math.Max(type, t.GetInt32());
            }
            if (sticks == 0) return null;
            int gb = (int)Math.Round(bytes / 1073741824.0);
            string kind = type >= 34 ? "DDR5" : type == 26 ? "DDR4" : mhz >= 4400 ? "DDR5" : "DDR4";
            return new HwRam(gb, kind, mhz, sticks >= 2);
        }
        catch { return null; }
    }
}
