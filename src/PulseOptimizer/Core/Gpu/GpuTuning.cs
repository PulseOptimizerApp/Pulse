using System.Diagnostics.Eventing.Reader;

namespace Pulse.Core.Gpu;

/// <summary>What the user set on the GPU tuning page.</summary>
public sealed class GpuTuneSettings
{
    /// <summary>The risks were read and accepted (tuning is locked until then).</summary>
    public bool RisksAccepted { get; set; }
    /// <summary>Power limit as a percent of the card's default (100 = stock).</summary>
    public int PowerPercent { get; set; } = 100;
    /// <summary>Core clock offset in MHz (positive shifts the voltage/frequency curve up).</summary>
    public int CoreOffset { get; set; }
    /// <summary>Memory clock offset in MHz, on the same scale as MSI Afterburner (NVML's own scale is double).</summary>
    public int MemOffset { get; set; }
    /// <summary>Highest core clock allowed in MHz, 0 = no cap. With a positive core offset this is an undervolt.</summary>
    public int MaxClock { get; set; }
    /// <summary>Put the tune back on when Pulse starts.</summary>
    public bool ApplyAtStart { get; set; }
    /// <summary>The card the tune was made for, so it's never applied to a different one.</summary>
    public string Card { get; set; }

    public bool IsStock => PowerPercent == 100 && CoreOffset == 0 && MemOffset == 0 && MaxClock == 0;
    public GpuTuneSettings Copy() => (GpuTuneSettings)MemberwiseClone();
}

/// <summary>One second of readings from the card.</summary>
public sealed record GpuReading(double? CoreMhz, double? MemMhz, double? TempC, double? PowerW, double? LimitW, double? Usage, double? Fan,
    double? VramUsed, double? VramTotal, string HeldBackBy);

/// <summary>
/// Pulse's built-in GPU tuning for NVIDIA cards: live readings, power limit, core and memory clock offsets, and a core
/// clock cap (which, with a positive offset, is an undervolt). Values stay inside the ranges the driver reports, and
/// the driver puts everything back to stock on restart.
/// <para>
/// Safety net: if the graphics driver resets while a tune is on (the screen flashes black, a game crashes), Pulse puts
/// the card back to stock and turns "apply at startup" off. If Windows didn't shut down normally while a tune was on,
/// the tune isn't applied when Pulse starts again.
/// </para>
/// </summary>
public static class GpuTuning
{
    static GpuTuneSettings S => TweakEngine.State.GpuTune ??= new();
    static string Marker => Path.Combine(TweakEngine.DataDir, "gpu-tune.active");

    /// <summary>The tune that's on the card right now (null = stock).</summary>
    public static GpuTuneSettings Active { get; private set; }

    /// <summary>A message for the user from the safety net (shown once on the GPU page or as a toast).</summary>
    public static string Notice { get; set; }

    /// <summary>Raised when the safety net put the card back to stock.</summary>
    public static event Action<string> SafetyReset;

    static System.Threading.Timer _watch;

    // ───────── readings ─────────

    public static GpuReading Read()
    {
        if (Nvml.Device is not IntPtr d) return null;
        double? Get(Func<(int, uint)> f, double scale = 1) { try { var (r, v) = f(); return r == Nvml.Success ? v * scale : null; } catch { return null; } }
        var core = Get(() => (Nvml.Clock(d, Nvml.ClockGraphics, out var v), v));
        var mem = Get(() => (Nvml.Clock(d, Nvml.ClockMemory, out var v), v));
        var temp = Get(() => (Nvml.Temperature(d, 0, out var v), v));
        var power = Get(() => (Nvml.PowerUsage(d, out var v), v), 0.001);
        var limit = Get(() => (Nvml.EnforcedLimit(d, out var v), v), 0.001);
        var fan = Get(() => (Nvml.FanSpeed(d, out var v), v));
        double? usage = null, used = null, total = null;
        try { if (Nvml.Utilization(d, out var u) == Nvml.Success) usage = u.Gpu; } catch { }
        try { if (Nvml.Memory(d, out var m) == Nvml.Success) { used = m.Used; total = m.Total; } } catch { }
        string held = null;
        try { if (Nvml.ThrottleReasons(d, out var r) == Nvml.Success) held = HeldBack(r, usage); } catch { }
        return new GpuReading(core, mem, temp, power, limit, usage, fan, used, total, held);
    }

    /// <summary>Why the card isn't running faster, in words (NVML's clock event reasons).</summary>
    internal static string HeldBack(ulong r, double? usage)
    {
        if ((r & (0x20 | 0x40)) != 0) return "Temperature";
        if ((r & 0x80) != 0) return "Power supply";
        if ((r & 0x4) != 0) return "Power limit";
        if ((r & 0x8) != 0) return "Slowed by the card";
        if ((r & 0x1) != 0 || usage < 30) return "Nothing (idle)";
        return "Nothing";
    }

    // ───────── what the card allows ─────────

    public sealed record Limits(int PowerMin, int PowerMax, int DefaultW, int CoreMin, int CoreMax, int MemMin, int MemMax, int ClockMax, bool CanCore, bool CanMem, bool CanPower, bool CanCap);

    public static Limits Range()
    {
        if (Nvml.Device is not IntPtr d) return null;
        uint def = 0, min = 0, max = 0, clockMax = 0;
        int cMin = 0, cMax = 0, mMin = 0, mMax = 0;
        bool canPower = false, canCore = false, canMem = false;
        try { canPower = Nvml.DefaultLimit(d, out def) == Nvml.Success && Nvml.LimitRange(d, out min, out max) == Nvml.Success && max > 0; } catch { }
        try { canCore = Nvml.CoreOffsetRange(d, out cMin, out cMax) == Nvml.Success && cMax > cMin; } catch { }
        try { canMem = Nvml.MemOffsetRange(d, out mMin, out mMax) == Nvml.Success && mMax > mMin; } catch { }
        try { Nvml.MaxClock(d, Nvml.ClockGraphics, out clockMax); } catch { }
        int pMin = def > 0 ? (int)Math.Ceiling(100.0 * min / def) : 100, pMax = def > 0 ? (int)Math.Floor(100.0 * max / def) : 100;
        return new Limits(pMin, pMax, (int)(def / 1000), Math.Max(cMin, -1000), Math.Min(cMax, 1000), Math.Max(mMin / 2, -2000), Math.Min(mMax / 2, 3000),
            (int)clockMax, canCore, canMem, canPower, clockMax > 0);
    }

    // ───────── applying ─────────

    /// <summary>Puts a tune on the card. Returns the problems (empty when everything applied).</summary>
    public static List<string> Apply(GpuTuneSettings t)
    {
        var problems = new List<string>();
        if (Nvml.Device is not IntPtr d) { problems.Add("No NVIDIA graphics card found."); return problems; }
        var lim = Range();
        void Do(string what, Func<int> call)
        {
            int r;
            try { r = call(); } catch (Exception e) { problems.Add(what + ": " + e.Message); return; }
            if (r != Nvml.Success) problems.Add(what + ": " + Nvml.Explain(r));
        }
        if (lim.CanPower)
        {
            int pct = Math.Clamp(t.PowerPercent, lim.PowerMin, lim.PowerMax);
            Do("Power limit", () => Nvml.SetLimit(d, (uint)Math.Round(lim.DefaultW * 1000.0 * pct / 100)));
        }
        else if (t.PowerPercent != 100) problems.Add("Power limit: " + Nvml.Explain(Nvml.NotSupported));
        if (lim.CanCore || t.CoreOffset != 0) Do("Core clock", () => Nvml.SetCoreOffset(d, Math.Clamp(t.CoreOffset, lim.CoreMin, lim.CoreMax)));
        if (lim.CanMem || t.MemOffset != 0) Do("Memory clock", () => Nvml.SetMemOffset(d, Math.Clamp(t.MemOffset, lim.MemMin, lim.MemMax) * 2));
        if (t.MaxClock > 0)
            Do("Clock cap", () =>
            {
                // A low minimum lets the card idle as usual; some drivers want a real clock there instead of 0.
                int r = Nvml.LockClocks(d, 0, (uint)t.MaxClock);
                return r == Nvml.Success ? r : Nvml.LockClocks(d, 210, (uint)t.MaxClock);
            });
        else Do("Clock cap", () => { var r = Nvml.ResetLockedClocks(d); return r == Nvml.NotSupported ? Nvml.Success : r; });

        Active = t.IsStock ? null : t.Copy();
        S.Card = Nvml.CardName;
        if (Active != null) { WriteMarker(); StartWatch(); } else { DeleteMarker(); StopWatch(); }
        TweakEngine.AddLog("gpu", t.IsStock ? "GPU back to stock" : $"GPU tune on: power {t.PowerPercent}%, core {t.CoreOffset:+0;-0;0} MHz, memory {t.MemOffset:+0;-0;0} MHz" + (t.MaxClock > 0 ? $", cap {t.MaxClock} MHz" : ""));
        return problems;
    }

    /// <summary>Back to stock: default power limit, no offsets, no cap.</summary>
    public static List<string> Reset() => Apply(new GpuTuneSettings());

    // ───────── safety net ─────────

    /// <summary>
    /// Every 2 seconds while a tune is on: if the driver has put the clock offset back to 0 by itself, the driver
    /// reset (that's what a too-far tune does). Back to stock, and no more applying at startup.
    /// </summary>
    static void StartWatch() => _watch ??= new System.Threading.Timer(_ => Check(), null, 2000, 2000);
    static void StopWatch() { _watch?.Dispose(); _watch = null; }

    static void Check()
    {
        try
        {
            if (Active is not { } a || Nvml.Device is not IntPtr d) return;
            bool lost = a.CoreOffset != 0 && Nvml.GetCoreOffset(d, out var now) == Nvml.Success && now == 0;
            // A power-only tune: the driver puts the limit back to its default after a reset.
            if (!lost && a.CoreOffset == 0 && a.PowerPercent != 100 && Range() is { CanPower: true } lim
                && Nvml.EnforcedLimit(d, out var mw) == Nvml.Success && Math.Abs(mw / 1000.0 - lim.DefaultW) < 1) lost = true;
            if (!lost) return;
            OnUnstable("Your graphics driver reset while the GPU tune was on, so the last change went too far. Pulse put the card back to stock and won't apply that tune at startup. Back off one step and test again.");
        }
        catch { }
    }

    static void OnUnstable(string why)
    {
        Active = null;
        StopWatch();
        try { Reset(); } catch { }
        S.ApplyAtStart = false;
        TweakEngine.Save();
        Notice = why;
        TweakEngine.AddLog("gpu", "GPU tune undone after a driver reset");
        SafetyReset?.Invoke(why);
    }

    static void WriteMarker() { try { File.WriteAllText(Marker, DateTime.UtcNow.Ticks.ToString()); } catch { } }
    static void DeleteMarker() { try { if (File.Exists(Marker)) File.WriteAllText(Marker, ""); } catch { } }

    static DateTime? MarkerTime()
    {
        try { return File.Exists(Marker) && long.TryParse(File.ReadAllText(Marker).Trim(), out var t) ? new DateTime(t, DateTimeKind.Utc) : null; }
        catch { return null; }
    }

    /// <summary>
    /// Did Windows stop without shutting down normally since <paramref name="sinceUtc"/>? Windows logs that itself:
    /// Kernel-Power 41 ("rebooted without cleanly shutting down") and EventLog 6008 ("unexpected shutdown").
    /// </summary>
    static bool UncleanShutdownSince(DateTime sinceUtc)
    {
        try
        {
            var ms = Math.Max(1000, (long)(DateTime.UtcNow - sinceUtc).TotalMilliseconds);
            var q = new EventLogQuery("System", PathType.LogName, $"*[System[(EventID=41 or EventID=6008) and TimeCreated[timediff(@SystemTime) <= {ms}]]]");
            using var reader = new EventLogReader(q);
            using var e = reader.ReadEvent();
            return e != null;
        }
        catch { return false; }
    }

    /// <summary>When Pulse starts: put the saved tune back on, unless the safety net says not to.</summary>
    public static void Startup()
    {
        try
        {
            var since = MarkerTime();
            var bootUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            if (since is DateTime t && t < bootUtc && UncleanShutdownSince(t))
            {
                // A tune was on and Windows crashed or lost power: don't put it back automatically.
                if (S.ApplyAtStart)
                {
                    S.ApplyAtStart = false;
                    TweakEngine.Save();
                    Notice = "Windows didn't shut down normally while your GPU tune was on, so Pulse didn't apply it this time. If the PC crashed, that tune is probably too far.";
                    TweakEngine.AddLog("gpu", "GPU tune not applied at startup after an unexpected shutdown");
                }
                DeleteMarker();
                return;
            }
            DeleteMarker();
            if (!S.ApplyAtStart || !S.RisksAccepted || S.IsStock || !Nvml.Available) return;
            if (!string.IsNullOrEmpty(S.Card) && S.Card != Nvml.CardName)
            {
                S.ApplyAtStart = false;
                TweakEngine.Save();
                Notice = "Your graphics card changed, so Pulse didn't apply the tune made for the old one.";
                return;
            }
            var problems = Apply(S);
            if (problems.Count > 0) TweakEngine.AddLog("gpu", "GPU tune at startup: " + string.Join("; ", problems));
        }
        catch { }
    }
}
