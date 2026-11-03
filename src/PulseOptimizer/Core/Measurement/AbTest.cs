using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pulse.Core.Measurement;

public enum Side { Before, After }

public class PlannedRun
{
    public int Index { get; set; }
    public Side Side { get; set; }
    public bool WarmUp { get; set; }
    public RunSummary Result { get; set; }
}

public class MeasurementRecord
{
    public string TweakId { get; set; }
    public string Game { get; set; }
    public DateTime Date { get; set; }
    public string Verdict { get; set; }
    public string Summary { get; set; }
    public List<Comparison> Comparisons { get; set; } = new();
    public List<RunSummary> Before { get; set; } = new();
    public List<RunSummary> After { get; set; } = new();
}

/// <summary>
/// The before/after protocol from the research doc:
/// one warm-up run (discarded), then N runs per side alternating A,B,A,B so heat and background
/// drift affect both sides equally. Tweaks that need a restart can't alternate, so they run
/// all "before" runs, ask for a restart, then all "after" runs — and the report says so.
/// The test is saved to disk after every run so it survives that restart.
/// </summary>
public class AbTest
{
    public string TweakId { get; set; }
    public string ProcessName { get; set; }
    public int Seconds { get; set; } = 75;
    public int RunsPerSide { get; set; } = 5;
    public bool Alternating { get; set; }
    public bool WasOnAtStart { get; set; }
    public List<PlannedRun> Plan { get; set; } = new();
    public int NextIndex { get; set; }

    // NaN appears in "not enough data" comparisons, which JSON doesn't allow by default.
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    static string FilePath => Path.Combine(TweakEngine.DataDir, "abtest.json");
    static string StorePath => Path.Combine(TweakEngine.DataDir, "measurements.json");

    public static AbTest Create(Tweak t, string processName, int seconds = 75, int runsPerSide = 5)
    {
        var test = new AbTest
        {
            TweakId = t.Id,
            ProcessName = processName,
            Seconds = seconds,
            RunsPerSide = runsPerSide,
            Alternating = !t.HasRestartNote,
            WasOnAtStart = TweakEngine.AppliedByPulse(t),
        };
        int i = 0;
        test.Plan.Add(new PlannedRun { Index = i++, Side = Side.Before, WarmUp = true });
        if (test.Alternating)
        {
            for (int k = 0; k < runsPerSide; k++)
            {
                test.Plan.Add(new PlannedRun { Index = i++, Side = Side.Before });
                test.Plan.Add(new PlannedRun { Index = i++, Side = Side.After });
            }
        }
        else
        {
            for (int k = 0; k < runsPerSide; k++) test.Plan.Add(new PlannedRun { Index = i++, Side = Side.Before });
            for (int k = 0; k < runsPerSide; k++) test.Plan.Add(new PlannedRun { Index = i++, Side = Side.After });
        }
        test.Save();
        return test;
    }

    [JsonIgnore]
    public Tweak Tweak => TweakCatalog.All.First(t => t.Id == TweakId);
    [JsonIgnore]
    public PlannedRun Next => NextIndex < Plan.Count ? Plan[NextIndex] : null;
    [JsonIgnore]
    public bool Done => Next == null;
    [JsonIgnore]
    public int MeasuredRunsDone => Plan.Take(NextIndex).Count(p => !p.WarmUp);
    [JsonIgnore]
    public int MeasuredRunsTotal => Plan.Count(p => !p.WarmUp);

    /// <summary>True when the next run is the first "after" run of a restart-needed tweak.</summary>
    [JsonIgnore]
    public bool NeedsRestartBeforeNext =>
        !Alternating && Next is { Side: Side.After } && NextIndex > 0 && Plan[NextIndex - 1].Side == Side.Before
        && !RestartedSinceApply;

    public bool RestartedSinceApply { get; set; }
    /// <summary>When the app asked for the restart; a boot after this time means it happened.</summary>
    public DateTime? RestartRequestedAt { get; set; }

    public void MarkRestartRequested()
    {
        RestartRequestedAt = DateTime.Now;
        Save();
    }

    /// <summary>Puts the tweak in the state the next run needs. For restart tweaks, call before asking for the restart.</summary>
    public void PrepareNext()
    {
        var n = Next;
        if (n == null) return;
        var t = Tweak;
        bool on = TweakEngine.AppliedByPulse(t);
        if (n.Side == Side.After && !on) { TweakEngine.Apply(t); t.IsOn = true; }
        if (n.Side == Side.Before && on) { TweakEngine.Revert(t); t.IsOn = false; }
        Save();
    }

    /// <summary>Records the next planned run. Blocks for the capture length; call off the UI thread.</summary>
    public CaptureResult RunNext(CancellationToken cancel = default)
    {
        var n = Next ?? throw new InvalidOperationException("The test is already finished.");
        if (NeedsRestartBeforeNext) throw new InvalidOperationException("Restart the PC before the 'after' runs.");
        PrepareNext();
        var cap = PresentMonCapture.Capture(ProcessName, Seconds, cancel);
        if (!cap.Ok) return cap;
        n.Result = Stats.Summarize(cap.FrameTimesMs, cap.LatenciesMs);
        NextIndex++;
        Save();
        return cap;
    }

    public MeasurementRecord Finish()
    {
        var t = Tweak;
        var before = Plan.Where(p => !p.WarmUp && p.Side == Side.Before && p.Result != null).Select(p => p.Result).ToList();
        var after = Plan.Where(p => !p.WarmUp && p.Side == Side.After && p.Result != null).Select(p => p.Result).ToList();

        var comps = new List<Comparison>
        {
            Stats.Compare("Average FPS", before.Select(r => r.AvgFps).ToList(), after.Select(r => r.AvgFps).ToList(), true, "FPS"),
            Stats.Compare("1% low FPS", before.Select(r => r.Low1Fps).ToList(), after.Select(r => r.Low1Fps).ToList(), true, "FPS"),
        };
        var bl = before.Where(r => r.LatencyMedianMs.HasValue).Select(r => r.LatencyMedianMs.Value).ToList();
        var al = after.Where(r => r.LatencyMedianMs.HasValue).Select(r => r.LatencyMedianMs.Value).ToList();
        if (bl.Count >= 3 && al.Count >= 3)
            comps.Add(Stats.Compare("Latency (median)", bl, al, false, "ms"));

        Verdict overall =
            comps.Any(c => c.Verdict == Verdict.Hurt) ? Verdict.Hurt :
            comps.Any(c => c.Verdict == Verdict.Helped) ? Verdict.Helped :
            comps.All(c => c.Verdict == Verdict.NotEnoughData) ? Verdict.NotEnoughData :
            Verdict.NoMeasurableChange;

        var game = Path.GetFileNameWithoutExtension(ProcessName);
        string headline = overall switch
        {
            Verdict.Helped => $"Helped in {game}",
            Verdict.Hurt => $"Hurt performance in {game}",
            Verdict.NotEnoughData => "Not enough runs to judge",
            _ => $"No measurable change in {game}",
        };
        var detail = string.Join(" ", comps.Where(c => c.Verdict != Verdict.NotEnoughData).Select(c => $"{c.Metric}: {c.Explanation}"));
        if (!Alternating) detail += " (Runs couldn't alternate because this tweak needs a restart, so drift between sessions can affect the result.)";

        var rec = new MeasurementRecord
        {
            TweakId = TweakId, Game = game, Date = DateTime.Now, Verdict = overall.ToString(),
            Summary = $"{headline}. {detail}", Comparisons = comps, Before = before, After = after
        };
        var all = LoadRecords();
        all.Insert(0, rec);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(all, Json));
        t.MeasuredResult = $"{headline} ({rec.Date:MMM d})";
        TweakEngine.AddLog("measure", $"Measured “{t.Title}”: {headline}");
        try { File.Delete(FilePath); } catch { }
        return rec;
    }

    public static void Abandon()
    {
        try { File.Delete(FilePath); } catch { }
    }

    public void Save()
    {
        Directory.CreateDirectory(TweakEngine.DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Resumes a test interrupted by a restart, if any.</summary>
    public static AbTest LoadPending()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var t = JsonSerializer.Deserialize<AbTest>(File.ReadAllText(FilePath), Json);
            var lastBoot = DateTime.Now - SystemInfo.Uptime;
            if (t != null && t.NeedsRestartBeforeNext && t.RestartRequestedAt is { } asked && lastBoot > asked)
            {
                // Windows booted after we asked for the restart → the restart happened.
                t.RestartedSinceApply = true;
                t.Save();
            }
            return t;
        }
        catch { return null; }
    }

    public static List<MeasurementRecord> LoadRecords()
    {
        try
        {
            return File.Exists(StorePath)
                ? JsonSerializer.Deserialize<List<MeasurementRecord>>(File.ReadAllText(StorePath), Json) ?? new()
                : new();
        }
        catch { return new(); }
    }

    /// <summary>Shows each tweak's most recent measured verdict on its card.</summary>
    public static void ApplyRecordsToCatalog()
    {
        foreach (var g in LoadRecords().GroupBy(r => r.TweakId))
        {
            var latest = g.OrderByDescending(r => r.Date).First();
            var t = TweakCatalog.All.FirstOrDefault(x => x.Id == g.Key);
            if (t == null) continue;
            var head = latest.Summary.Split('.')[0];
            t.MeasuredResult = $"{head} ({latest.Date:MMM d})";
        }
    }
}
