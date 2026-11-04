using System.Diagnostics;

namespace Pulse.Core.Measurement;

/// <summary>A stutter: one frame that took much longer than the frames around it.</summary>
public record Hitch(double AtSeconds, double Ms);

/// <summary>What the stutter check found, in plain words.</summary>
public record StutterReport(int Frames, double MedianMs, List<Hitch> Hitches, string Verdict, string Detail);

/// <summary>
/// Records the game for a while with PresentMon, finds the hitches and says what most likely causes them: a
/// background app using the processor at the same moments, something that runs on a timer, loading at the start,
/// or the game itself. Reads only process CPU times; never touches the game.
/// </summary>
public static class StutterCheck
{
    /// <summary>Frames over 2.5× the typical frame and at least 25 ms (one visible skip at any frame rate).</summary>
    public static List<Hitch> FindHitches(IReadOnlyList<double> frameMs)
    {
        var list = new List<Hitch>();
        if (frameMs.Count < 30) return list;
        double median = Stats.Median(frameMs);
        double t = 0;
        foreach (var ms in frameMs)
        {
            t += ms / 1000;
            if (ms >= Math.Max(25, median * 2.5)) list.Add(new Hitch(Math.Round(t, 2), ms));
        }
        return list;
    }

    /// <summary>The plain-words verdict. <paramref name="busy"/> is each background app's CPU use (% of one core) per second of the capture.</summary>
    public static (string Verdict, string Detail) Explain(List<Hitch> hitches, double seconds, Dictionary<string, double[]> busy)
    {
        if (hitches.Count == 0) return ("Smooth: no stutters found.", "Every frame arrived close to on time while Pulse was watching.");
        double perMinute = hitches.Count / Math.Max(seconds, 1) * 60;
        string how = Ui.Loc.F("{0} stutters in {1} s (worst {2} ms).", hitches.Count, Math.Round(seconds), Math.Round(hitches.Max(h => h.Ms)));

        // 1. Loading: most hitches in the first quarter.
        if (hitches.Count >= 3 && hitches.Count(h => h.AtSeconds < seconds / 4) >= hitches.Count * 0.75)
            return ("Stutters while the game loads, then smooth.", how + " " + Ui.Loc.T("This is usually the game preparing shaders or loading the map. It fades after a few minutes or matches; keeping shader cache on in the graphics driver helps."));

        // 2. A background app busy at the same moments.
        var seconds_ = hitches.Select(h => (int)h.AtSeconds).Distinct().ToList();
        var culprit = busy
            .Select(b => (Name: b.Key, During: seconds_.Where(s => s < b.Value.Length).Select(s => b.Value[s]).DefaultIfEmpty(0).Average(), Usual: b.Value.DefaultIfEmpty(0).Average()))
            .Where(x => x.During >= 25 && x.During >= x.Usual * 2)
            .OrderByDescending(x => x.During)
            .FirstOrDefault();
        if (culprit.Name != null)
            return (Ui.Loc.F("{0} is busy when the game stutters.", culprit.Name), how + " " + Ui.Loc.F("It used about {0}% of a processor core at the moments the game skipped. Close it while playing, or check its settings for background scans or updates.", Math.Round(culprit.During)));

        // 3. On a timer: evenly spaced hitches.
        if (hitches.Count >= 4)
        {
            var gaps = hitches.Zip(hitches.Skip(1), (a, b) => b.AtSeconds - a.AtSeconds).Where(g => g > 0.5).ToList();
            if (gaps.Count >= 3)
            {
                double avg = gaps.Average();
                if (gaps.All(g => Math.Abs(g - avg) <= avg * 0.15))
                    return (Ui.Loc.F("Something runs every {0} seconds.", Math.Round(avg)), how + " " + Ui.Loc.T("Evenly spaced stutters usually come from software that checks something on a timer: RGB or fan control apps, overlays, or hardware monitors. Close them one at a time to find it."));
            }
        }

        if (perMinute < 3) return ("A few stutters, nothing serious.", how + " " + Ui.Loc.T("Occasional single skips are normal in most games."));
        return ("The game itself stutters.", how + " " + Ui.Loc.T("No background app lined up with the stutters. Lower texture quality if your graphics card's memory is full, update the graphics driver, and make sure the game is on an SSD."));
    }

    /// <summary>Runs the check on a running game for <paramref name="seconds"/>. Blocks; call off the UI thread.</summary>
    public static StutterReport Run(string processName, int seconds, CancellationToken cancel = default)
    {
        var busy = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var sampler = Task.Run(() => SampleCpu(busy, seconds, processName, stop.Token));
        var r = PresentMonCapture.Capture(processName, seconds, cancel);
        stop.Cancel();
        try { sampler.Wait(2000); } catch { }
        if (!r.Ok) return new StutterReport(0, 0, new(), r.Error, "");
        var hitches = FindHitches(r.FrameTimesMs);
        double secs = r.FrameTimesMs.Sum() / 1000;
        var (verdict, detail) = Explain(hitches, secs, busy);
        return new StutterReport(r.FrameTimesMs.Count, Stats.Median(r.FrameTimesMs), hitches, verdict, detail);
    }

    /// <summary>Each other app's share of the processor, once a second (from Windows' per-process CPU time).</summary>
    static void SampleCpu(Dictionary<string, double[]> busy, int seconds, string game, CancellationToken stop)
    {
        var last = new Dictionary<int, (string Name, TimeSpan Cpu)>();
        var clock = Stopwatch.StartNew();
        double prev = 0;
        for (int s = 0; s <= seconds && !stop.IsCancellationRequested; s++)
        {
            double now = clock.Elapsed.TotalSeconds, span = Math.Max(now - prev, 0.2);
            prev = now;
            var seen = new Dictionary<int, (string, TimeSpan)>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == Environment.ProcessId || p.Id == 0 || p.ProcessName.Equals(game, StringComparison.OrdinalIgnoreCase) || p.ProcessName is "Idle" or "System" or "PresentMon") continue;
                    var cpu = p.TotalProcessorTime;
                    seen[p.Id] = (p.ProcessName, cpu);
                    if (s > 0 && last.TryGetValue(p.Id, out var before))
                    {
                        double pct = (cpu - before.Cpu).TotalSeconds / span * 100; // % of one core
                        if (!busy.TryGetValue(p.ProcessName, out var arr)) busy[p.ProcessName] = arr = new double[seconds + 2];
                        arr[Math.Min(s, arr.Length - 1)] += pct;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            last = seen;
            try { Task.Delay(1000, stop).Wait(); } catch { break; }
        }
    }
}
