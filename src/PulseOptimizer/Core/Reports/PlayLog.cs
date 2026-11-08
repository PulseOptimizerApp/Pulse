using System.Text.Json;
using Microsoft.Win32;

namespace Pulse.Core.Reports;

/// <summary>One time you played a game: how long, how smooth, and on which driver and Windows build.</summary>
public sealed class PlaySession
{
    public string Game { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    /// <summary>Frames counted while FPS tracking was on (0 when it was off or PresentMon wasn't available).</summary>
    public long Frames { get; set; }
    /// <summary>Seconds the frames cover.</summary>
    public double FrameSeconds { get; set; }
    public double AvgFps { get; set; }
    /// <summary>The average of the slowest 1% of frames, as FPS.</summary>
    public double Low1 { get; set; }
    public string Driver { get; set; } = "";
    public string Windows { get; set; } = "";

    public double Minutes => (End - Start).TotalMinutes;
    public bool HasFps => Frames > 600 && FrameSeconds > 60;
}

public sealed class ReportState
{
    /// <summary>Count play time (on by default; only which game and for how long).</summary>
    public bool TrackPlay { get; set; } = true;
    /// <summary>Also count FPS while you play, for the report (uses the same frame counter as the overlay).</summary>
    public bool TrackFps { get; set; } = true;
    public List<PlaySession> Sessions { get; set; } = new();
    /// <summary>Tell me when a new graphics driver is out.</summary>
    public bool DriverAlerts { get; set; } = true;
    public string SkippedDriver { get; set; } = "";
    public DateTime LastDriverCheck { get; set; }
    public string LatestDriver { get; set; } = "";
    public DateTime LatestDriverDate { get; set; }
    /// <summary>The Monday of the last week whose report was announced.</summary>
    public DateTime AnnouncedWeek { get; set; }
}

/// <summary>
/// Counts frames as they come in and keeps a histogram (half-millisecond steps), so a whole session's average and
/// 1% low can be worked out without keeping every frame.
/// </summary>
public sealed class FrameTally
{
    readonly object _gate = new();
    readonly int[] _bins = new int[2001];
    long _count;
    double _sumMs;

    public void Add(double ms)
    {
        if (ms <= 0 || ms >= 1000) return;
        lock (_gate)
        {
            _count++;
            _sumMs += ms;
            _bins[Math.Min(_bins.Length - 1, (int)(ms * 2))]++;
        }
    }

    public (long Frames, double Seconds, double AvgFps, double Low1) Result()
    {
        lock (_gate)
        {
            if (_count == 0) return (0, 0, 0, 0);
            long want = Math.Max(1, _count / 100), got = 0;
            double sum = 0;
            for (int i = _bins.Length - 1; i >= 0 && got < want; i--)
            {
                long take = Math.Min(_bins[i], want - got);
                sum += take * (i + 0.5) / 2.0;
                got += take;
            }
            return (_count, _sumMs / 1000, 1000.0 * _count / _sumMs, 1000.0 / (sum / got));
        }
    }
}

/// <summary>Play sessions for the weekly report (kept in Pulse's settings, 13 weeks back).</summary>
public static class PlayLog
{
    static ReportState S => TweakEngine.State.Report ??= new ReportState();

    public static void Add(PlaySession p)
    {
        if (p == null || p.Minutes < 2) return;
        lock (S)
        {
            // A game that restarted within two minutes is the same sitting.
            var last = S.Sessions.LastOrDefault(x => x.Game == p.Game);
            if (last != null && (p.Start - last.End).TotalMinutes is >= 0 and < 2 && last.Driver == p.Driver)
            {
                long frames = last.Frames + p.Frames;
                double secs = last.FrameSeconds + p.FrameSeconds;
                if (frames > 0 && secs > 0)
                {
                    last.Low1 = (last.Low1 * last.FrameSeconds + p.Low1 * p.FrameSeconds) / secs;
                    last.AvgFps = frames / secs;
                }
                last.Frames = frames; last.FrameSeconds = secs; last.End = p.End;
            }
            else S.Sessions.Add(p);
            S.Sessions.RemoveAll(x => x.End < DateTime.Now.AddDays(-91));
        }
        TweakEngine.Save();
    }

    // ───────── what driver and Windows a session ran on ─────────

    static (string Name, string Version, DateTime Date, string Vendor)? _gpu;
    static DateTime _gpuAt;

    /// <summary>
    /// The graphics card that games run on and its driver, from Windows' own device list. When a PC has a built-in
    /// and a separate card, the separate (NVIDIA or AMD) one is the one that counts.
    /// </summary>
    public static (string Name, string Version, DateTime Date, string Vendor)? Gpu()
    {
        if (_gpu != null && DateTime.UtcNow - _gpuAt < TimeSpan.FromMinutes(30)) return _gpu;
        var found = new List<(string Name, string Version, DateTime Date, string Vendor)>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls != null)
                foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
                {
                    using var k = cls.OpenSubKey(sub);
                    var name = k?.GetValue("DriverDesc") as string ?? "";
                    var ver = k?.GetValue("DriverVersion") as string ?? "";
                    if (name.Length == 0 || ver.Length == 0 || name.Contains("Basic", StringComparison.OrdinalIgnoreCase) || name.Contains("Remote", StringComparison.OrdinalIgnoreCase)) continue;
                    DateTime.TryParseExact(k.GetValue("DriverDate") as string ?? "", new[] { "M-d-yyyy", "MM-dd-yyyy" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date);
                    found.Add((name, ver, date, VendorOf(name)));
                }
        }
        catch { }
        _gpu = found.OrderBy(g => g.Vendor switch { "NVIDIA" => 0, "AMD" => 1, "Intel" => 2, _ => 3 }).Select(g => ((string, string, DateTime, string)?)g).FirstOrDefault();
        _gpuAt = DateTime.UtcNow;
        return _gpu;
    }

    public static string VendorOf(string name) =>
        name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ? "NVIDIA" :
        name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "AMD" :
        name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "";

    /// <summary>
    /// The driver version as its maker writes it. NVIDIA's Windows version "32.0.15.8157" is driver 581.57 (the
    /// last five digits); AMD and Intel are shown as Windows lists them.
    /// </summary>
    public static string FriendlyVersion(string windowsVersion, string vendor)
    {
        if (vendor != "NVIDIA") return windowsVersion ?? "";
        var parts = (windowsVersion ?? "").Split('.');
        if (parts.Length < 4) return windowsVersion ?? "";
        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5 || !digits.All(char.IsDigit)) return windowsVersion;
        digits = digits[^5..];
        return $"{digits[..3]}.{digits[3..]}";
    }

    /// <summary>"NVIDIA 581.57", or "" when there's no card to report.</summary>
    public static string DriverLabel()
    {
        var g = Gpu();
        return g is { } x ? $"{(x.Vendor.Length > 0 ? x.Vendor : x.Name)} {FriendlyVersion(x.Version, x.Vendor)}".Trim() : "";
    }

    /// <summary>"Windows 11 24H2 (26100.4351)" style: the build and its update number.</summary>
    public static string WindowsLabel()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = k?.GetValue("CurrentBuild") as string ?? "";
            var ubr = k?.GetValue("UBR") is int u ? u : 0;
            var disp = k?.GetValue("DisplayVersion") as string ?? "";
            return $"{(int.TryParse(build, out var b) && b >= 22000 ? "Windows 11" : "Windows 10")} {disp} ({build}.{ubr})".Replace("  ", " ");
        }
        catch { return ""; }
    }

    // ───────── the weekly report ─────────

    public sealed record GameWeek(string Game, double Hours, int Sessions, double AvgFps, double Low1, double PrevAvgFps, double PrevLow1);

    public sealed record Change(string Game, string What, string From, string To, double FpsBefore, double FpsAfter, double LowBefore, double LowAfter, int SessionsAfter)
    {
        public double FpsChange => FpsBefore > 0 ? (FpsAfter - FpsBefore) / FpsBefore : 0;
        public double LowChange => LowBefore > 0 ? (LowAfter - LowBefore) / LowBefore : 0;
        /// <summary>Worse by 8% or more on average, or 12% or more on 1% lows.</summary>
        public bool Worse => FpsChange <= -0.08 || LowChange <= -0.12;
        public bool Better => FpsChange >= 0.08 || LowChange >= 0.12;
    }

    public static DateTime WeekStart(DateTime d)
    {
        int diff = ((int)d.DayOfWeek + 6) % 7; // Monday
        return d.Date.AddDays(-diff);
    }

    static (double Avg, double Low) Fps(IEnumerable<PlaySession> s)
    {
        var l = s.Where(x => x.HasFps).ToList();
        double secs = l.Sum(x => x.FrameSeconds);
        if (secs <= 0) return (0, 0);
        return (l.Sum(x => x.Frames) / secs, l.Sum(x => x.Low1 * x.FrameSeconds) / secs);
    }

    /// <summary>Each game you played in the week starting <paramref name="monday"/>, most played first, against the week before.</summary>
    public static List<GameWeek> Week(IEnumerable<PlaySession> all, DateTime monday)
    {
        var list = all.ToList();
        var week = list.Where(s => s.Start >= monday && s.Start < monday.AddDays(7)).ToList();
        var prev = list.Where(s => s.Start >= monday.AddDays(-7) && s.Start < monday).ToList();
        return week.GroupBy(s => s.Game).Select(g =>
        {
            var (a, l) = Fps(g);
            var (pa, pl) = Fps(prev.Where(p => p.Game == g.Key));
            return new GameWeek(g.Key, g.Sum(s => s.Minutes) / 60, g.Count(), a, l, pa, pl);
        }).OrderByDescending(g => g.Hours).ToList();
    }

    /// <summary>
    /// For each game: the newest driver or Windows change, and how FPS compares in the sessions on either side of
    /// it (at least two with FPS on each side, 10 minutes or longer).
    /// </summary>
    public static List<Change> Changes(IEnumerable<PlaySession> all)
    {
        var result = new List<Change>();
        foreach (var g in all.Where(s => s.HasFps && s.Minutes >= 10).GroupBy(s => s.Game))
        {
            var s = g.OrderBy(x => x.Start).ToList();
            for (int i = s.Count - 1; i >= 1; i--)
            {
                bool drv = s[i].Driver != s[i - 1].Driver, win = s[i].Windows != s[i - 1].Windows;
                if (!drv && !win) continue;
                var after = s.Skip(i).TakeWhile(x => x.Driver == s[i].Driver && x.Windows == s[i].Windows).ToList();
                var before = s.Take(i).Reverse().TakeWhile(x => x.Driver == s[i - 1].Driver && x.Windows == s[i - 1].Windows).Take(5).ToList();
                if (after.Count >= 2 && before.Count >= 2)
                {
                    var (ba, bl) = Fps(before);
                    var (aa, al) = Fps(after);
                    result.Add(new Change(g.Key, drv ? "driver" : "Windows", drv ? s[i - 1].Driver : s[i - 1].Windows, drv ? s[i].Driver : s[i].Windows, ba, aa, bl, al, after.Count));
                }
                break;
            }
        }
        return result;
    }
}
