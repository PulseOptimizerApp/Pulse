namespace Pulse.Core.Measurement;

/// <summary>Summary of one capture run.</summary>
public record RunSummary(
    double AvgFps,
    double Low1Fps,
    double Low01Fps,
    double FrameTimeStdDevMs,
    double? LatencyMedianMs,
    double? Latency99Ms,
    int Frames,
    double Seconds);

public enum Verdict { Helped, Hurt, NoMeasurableChange, NotEnoughData }

public record Comparison(
    string Metric,
    double BeforeMedian,
    double AfterMedian,
    double DiffMedian,
    double CiLow,
    double CiHigh,
    double Threshold,
    Verdict Verdict,
    string Explanation);

public static class Stats
{
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        if (sorted.Count == 1) return sorted[0];
        var rank = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    public static double Median(IEnumerable<double> values)
    {
        var s = values.OrderBy(v => v).ToList();
        return Percentile(s, 0.5);
    }

    public static double StdDev(IReadOnlyCollection<double> v)
    {
        if (v.Count < 2) return 0;
        var mean = v.Average();
        return Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1));
    }

    /// <summary>
    /// Builds the per-run numbers from raw frame times.
    /// "1% low" = FPS at the 99th-percentile frame time (the slowest 1% of frames).
    /// </summary>
    public static RunSummary Summarize(IReadOnlyList<double> frameTimesMs, IReadOnlyList<double> latenciesMs)
    {
        var ft = frameTimesMs.Where(x => x > 0 && x < 1000).OrderBy(x => x).ToList();
        if (ft.Count == 0) return new RunSummary(0, 0, 0, 0, null, null, 0, 0);
        var total = ft.Sum();
        var lat = latenciesMs?.Where(x => x > 0 && x < 1000).OrderBy(x => x).ToList() ?? new List<double>();
        return new RunSummary(
            AvgFps: ft.Count / (total / 1000.0),
            Low1Fps: 1000.0 / Percentile(ft, 0.99),
            Low01Fps: 1000.0 / Percentile(ft, 0.999),
            FrameTimeStdDevMs: StdDev(ft),
            LatencyMedianMs: lat.Count > 0 ? Percentile(lat, 0.5) : null,
            Latency99Ms: lat.Count > 0 ? Percentile(lat, 0.99) : null,
            Frames: ft.Count,
            Seconds: total / 1000.0);
    }

    /// <summary>
    /// Compares runs before and after a change, per the research doc's protocol:
    /// medians, a 95% bootstrap confidence interval for the difference, and a threshold of
    /// max(2% of the baseline, the normal run-to-run spread).
    /// </summary>
    public static Comparison Compare(string metric, IReadOnlyList<double> before, IReadOnlyList<double> after,
                                     bool higherIsBetter, string unit, int seed = 12345, int iterations = 5000)
    {
        if (before.Count < 3 || after.Count < 3)
            return new Comparison(metric, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
                Verdict.NotEnoughData, "Need at least 3 runs before and 3 after.");

        double mb = Median(before), ma = Median(after);
        double sign = higherIsBetter ? 1 : -1;
        double diff = (ma - mb) * sign; // positive = improvement

        var rng = new Random(seed);
        var diffs = new double[iterations];
        var bb = new double[before.Count];
        var aa = new double[after.Count];
        for (int i = 0; i < iterations; i++)
        {
            for (int j = 0; j < bb.Length; j++) bb[j] = before[rng.Next(before.Count)];
            for (int j = 0; j < aa.Length; j++) aa[j] = after[rng.Next(after.Count)];
            diffs[i] = (Median(aa) - Median(bb)) * sign;
        }
        Array.Sort(diffs);
        double lo = Percentile(diffs, 0.025), hi = Percentile(diffs, 0.975);

        double spread = Math.Max(StdDev(before.ToList()), StdDev(after.ToList()));
        double threshold = Math.Max(Math.Abs(mb) * 0.02, spread);

        Verdict v;
        string text;
        if (lo > 0 && diff >= threshold)
        {
            v = Verdict.Helped;
            text = $"Improved by {Fmt(diff)} {unit} ({Pct(diff, mb)}). That's bigger than this PC's normal run-to-run variation of {Fmt(spread)} {unit}.";
        }
        else if (hi < 0 && -diff >= threshold)
        {
            v = Verdict.Hurt;
            text = $"Got worse by {Fmt(-diff)} {unit} ({Pct(-diff, mb)}). Undoing it is recommended.";
        }
        else
        {
            v = Verdict.NoMeasurableChange;
            text = $"The difference ({(diff >= 0 ? "+" : "−")}{Fmt(Math.Abs(diff))} {unit}) is within normal run-to-run variation, so there's no measurable effect on this PC.";
        }
        return new Comparison(metric, mb, ma, diff, lo, hi, threshold, v, text);
    }

    static string Fmt(double v) => v.ToString(Math.Abs(v) >= 10 ? "0.0" : "0.00");
    static string Pct(double d, double baseline) => baseline == 0 ? "—" : $"{d / Math.Abs(baseline) * 100:0.0}%";
}
