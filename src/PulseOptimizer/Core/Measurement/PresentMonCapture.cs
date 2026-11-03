using System.Diagnostics;
using System.Globalization;

namespace Pulse.Core.Measurement;

public record CaptureResult(
    List<double> FrameTimesMs,
    List<double> LatenciesMs,
    string LatencyKind,
    string CsvPath,
    string Error)
{
    public bool Ok => Error == null && FrameTimesMs.Count > 0;
}

/// <summary>
/// Wraps Intel's PresentMon console app (MIT license). Release builds carry it inside PulseOptimizer.exe and unpack it
/// to %LocalAppData%\PulseOptimizer\tools the first time it's needed; a tools\PresentMon.exe next to the app also works.
/// Columns follow PresentMon 2.x's README-ConsoleApplication.md.
/// </summary>
public static class PresentMonCapture
{
    static string Beside => Path.Combine(AppContext.BaseDirectory, "tools", "PresentMon.exe");
    static string Unpacked => Path.Combine(TweakEngine.DataDir, "tools", "PresentMon.exe");

    public static string ExePath => File.Exists(Beside) ? Beside : Unpack() ? Unpacked : Beside;

    public static bool Available => File.Exists(ExePath);

    static readonly object UnpackLock = new();

    /// <summary>Writes the embedded PresentMon out once (again only if this Pulse version carries a different one).</summary>
    static bool Unpack()
    {
        lock (UnpackLock)
        {
            try
            {
                using var res = typeof(PresentMonCapture).Assembly.GetManifestResourceStream("PresentMon.exe");
                if (res == null) return File.Exists(Unpacked);
                if (File.Exists(Unpacked) && new FileInfo(Unpacked).Length == res.Length) return true;
                Directory.CreateDirectory(Path.GetDirectoryName(Unpacked));
                var tmp = Unpacked + ".new";
                using (var f = File.Create(tmp)) res.CopyTo(f);
                File.Move(tmp, Unpacked, overwrite: true);
                return true;
            }
            catch { return File.Exists(Unpacked); }
        }
    }

    static string CaptureDir => Path.Combine(TweakEngine.DataDir, "captures");

    /// <summary>Records one run of a game for a fixed time. Blocks until done; call off the UI thread.</summary>
    public static CaptureResult Capture(string processName, int seconds, CancellationToken cancel = default)
    {
        if (!Available)
            return Fail($"PresentMon wasn't found at {ExePath}. Download Pulse again from the official release.");
        if (!processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) processName += ".exe";
        if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName)).Length == 0)
            return Fail($"{processName} isn't running. Start the game first.");

        Directory.CreateDirectory(CaptureDir);
        var csv = Path.Combine(CaptureDir, $"{Path.GetFileNameWithoutExtension(processName)}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var args = string.Join(" ",
            "--process_name", Quote(processName),
            "--output_file", Quote(csv),
            "--timed", seconds.ToString(CultureInfo.InvariantCulture),
            "--terminate_after_timed",
            "--no_console_stats",
            "--session_name", "PulseOptimizer",
            "--stop_existing_session");

        Process proc = null;
        using var reg = cancel.Register(() => { try { proc?.Kill(true); } catch { } });
        var r = Shell.Run(ExePath, args, (seconds + 30) * 1000, p => proc = p);
        if (cancel.IsCancellationRequested) return Fail("Cancelled.");
        if (!File.Exists(csv))
            return Fail("PresentMon didn't record any frames. " + (CmdAction.FirstLine(r.Error) ?? CmdAction.FirstLine(r.Output) ?? ""));
        return Parse(csv);
    }

    /// <summary>Reads a PresentMon CSV, tolerating column differences between versions.</summary>
    public static CaptureResult Parse(string csvPath)
    {
        var lines = File.ReadAllLines(csvPath);
        if (lines.Length < 2) return Fail("The capture was empty — was the game in focus and rendering?", csvPath);

        var header = lines[0].Split(',');
        int Col(params string[] names)
        {
            foreach (var n in names)
            {
                var i = Array.FindIndex(header, h => h.Trim().Equals(n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
            }
            return -1;
        }

        int ft = Col("MsBetweenPresents", "msBetweenPresents", "FrameTime");
        int busy = Col("MsCPUBusy"), wait = Col("MsCPUWait");
        // Prefer end-to-end input latency; fall back to frame-start-to-display.
        (int idx, string kind)[] latencyCandidates =
        {
            (Col("MsAllInputToPhotonLatency", "AllInputToPhotonLatency"), "Input to photon"),
            (Col("MsClickToPhotonLatency", "ClickToPhotonLatency"), "Click to photon"),
            (Col("MsPCLatency"), "PC latency"),
            (Col("DisplayLatency", "MsUntilDisplayed"), "Frame start to display"),
        };

        var frames = new List<double>();
        var lat = new Dictionary<string, List<double>>();
        foreach (var line in lines.Skip(1))
        {
            var c = line.Split(',');
            double v;
            if (ft >= 0 && TryNum(c, ft, out v)) frames.Add(v);
            else if (busy >= 0 && wait >= 0 && TryNum(c, busy, out var b) && TryNum(c, wait, out var w)) frames.Add(b + w);

            foreach (var (idx, kind) in latencyCandidates)
            {
                if (idx >= 0 && TryNum(c, idx, out v))
                {
                    if (!lat.TryGetValue(kind, out var list)) lat[kind] = list = new List<double>();
                    list.Add(v);
                }
            }
        }

        // Use the best latency kind that has a reasonable number of samples.
        var chosen = latencyCandidates.Select(l => l.kind)
            .FirstOrDefault(k => lat.TryGetValue(k, out var l) && l.Count >= 20);
        return new CaptureResult(frames, chosen != null ? lat[chosen] : new List<double>(), chosen ?? "", csvPath,
            frames.Count == 0 ? "No frame times found in the capture." : null);
    }

    static bool TryNum(string[] cols, int i, out double v)
    {
        v = 0;
        return i < cols.Length && double.TryParse(cols[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    static string Quote(string s) => "\"" + s + "\"";
    static CaptureResult Fail(string msg, string csv = null) => new(new(), new(), "", csv, msg);
}
