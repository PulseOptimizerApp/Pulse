namespace Pulse.Core.Reaction;

/// <summary>The kinds of drill. Each trains a different part of game reactions.</summary>
public enum ReactionMode
{
    /// <summary>Wait for the screen to change, then click. Pure reaction time.</summary>
    Simple,
    /// <summary>Left or right: press the side that lights up. Reacting and choosing at once, like most games.</summary>
    Choice,
    /// <summary>Click on green, hold still on red. Trains not shooting at the wrong thing.</summary>
    GoNoGo,
    /// <summary>A target appears somewhere: move and click it. Reaction plus aim.</summary>
    Flick,
    /// <summary>An enemy swings out from behind a wall: hit the head before they hide. Reaction, decision and aim at once.</summary>
    Peek,
    /// <summary>Enemies and teammates peek: shoot only enemies. Trains deciding fast without shooting the wrong thing.</summary>
    FriendOrFoe,
    /// <summary>Hold an angle at head height; scored on how far your crosshair had to move. The habit that wins most duels.</summary>
    Crosshair,
    /// <summary>A fixed test that never changes difficulty, taken once a week, so progress is measured honestly.</summary>
    Benchmark,
}

/// <summary>One finished session, kept for the progress graph.</summary>
public class ReactionSession
{
    public DateTime When { get; set; }
    public ReactionMode Mode { get; set; }
    /// <summary>Median reaction in milliseconds (the middle result: one lucky or slow try doesn't move it).</summary>
    public double MedianMs { get; set; }
    public double BestMs { get; set; }
    /// <summary>Spread between the faster and slower quarter of tries (lower = more consistent).</summary>
    public double SpreadMs { get; set; }
    public int Trials { get; set; }
    /// <summary>Clicked before the signal, or on a "don't click" trial.</summary>
    public int Mistakes { get; set; }
    /// <summary>The difficulty level reached (Go/No-go and Flick adapt to you).</summary>
    public int Level { get; set; }

    // Peek drills and the Benchmark.
    /// <summary>Difficulty reached (1 to 20), kept so the next session starts near it.</summary>
    public double Difficulty { get; set; }
    /// <summary>Share of enemies hit (0 to 1).</summary>
    public double Accuracy { get; set; }
    /// <summary>Share of hits that were headshots (0 to 1).</summary>
    public double HeadshotShare { get; set; }
    /// <summary>Median distance (px) from the crosshair to where the head stopped, at the moment the peek started.</summary>
    public double OffsetPx { get; set; }
    /// <summary>Shots that hit nothing.</summary>
    public int MissedShots { get; set; }
    /// <summary>Benchmark score: median time to the hit, made worse by each mistake. Lower is better.</summary>
    public double Score { get; set; }
}

public class ReactionSettings
{
    public List<ReactionSession> History { get; set; } = new();
    /// <summary>The difficulty each adaptive drill reached last time, by drill name.</summary>
    public Dictionary<string, double> Levels { get; set; } = new();
    /// <summary>Days the daily training plan was finished.</summary>
    public List<DateTime> PlanDays { get; set; } = new();
    /// <summary>Move the drills' crosshair like VALORANT does at this sensitivity and DPI (raw mouse counts).</summary>
    public bool GameFeel { get; set; }
    public double GameSensitivity { get; set; } = 0.4;
    public int MouseDpi { get; set; } = 800;
}

/// <summary>What the screen should show for the next try.</summary>
public record ReactionTrial(
    // Wait this long before showing the signal (random, so it can't be timed).
    double WaitMs,
    // Choice: -1 left, 1 right. Otherwise 0.
    int Side,
    // Go/No-go: false = a "don't click" signal.
    bool Go,
    // Flick: target centre as a fraction of the area (0..1), and its diameter in pixels.
    double X, double Y, double Size,
    // Go/No-go: how long the signal stays up (ms). A missed "go" past this counts as a miss.
    double WindowMs);

/// <summary>How a try ended.</summary>
public enum TrialResult { Hit, TooEarly, WrongSide, ClickedNoGo, Missed, CorrectlyHeld }

/// <summary>
/// Runs a session: picks random waits, adapts difficulty, and scores. Kept free of any UI so it can be tested.
/// Design follows what reaction research supports: random waits (no rhythm to anticipate), short sessions,
/// the median instead of the average, and honest feedback about normal day-to-day variation.
/// </summary>
public sealed class ReactionTrainer
{
    // Short on purpose: 8 tries give a steady median in about 20 seconds, and people actually finish them.
    public const int WarmUp = 0;
    public const int TrialsPerSession = 8;
    /// <summary>Faster than this after the signal is physically impossible: it was a guess.</summary>
    public const double AnticipationMs = 100;

    readonly Random _rng;
    readonly List<double> _times = new();
    int _done;

    public ReactionMode Mode { get; }
    public int Level { get; private set; } = 1;
    public int Mistakes { get; private set; }
    public int Done => _done;
    public bool Finished => _done >= WarmUp + TrialsPerSession;
    public bool InWarmUp => _done < WarmUp;
    public IReadOnlyList<double> Times => _times;

    public ReactionTrainer(ReactionMode mode, int seed = 0, int startLevel = 1)
    {
        if (PeekTrainer.IsPeekDrill(mode)) throw new ArgumentException("Use PeekTrainer for this drill", nameof(mode));
        Mode = mode;
        if (mode is ReactionMode.GoNoGo or ReactionMode.Flick) Level = Math.Clamp(startLevel, 1, 10);
        _rng = seed == 0 ? new Random() : new Random(seed);
    }

    /// <summary>The next try. Waits are 1.2–3.5 s with a long tail, so the signal can't be predicted.</summary>
    public ReactionTrial Next()
    {
        double wait = 1200 + Math.Min(2300, -Math.Log(1 - _rng.NextDouble()) * 800);
        int side = Mode == ReactionMode.Choice ? (_rng.Next(2) == 0 ? -1 : 1) : 0;
        // No-go share grows with the level: 20% at level 1 up to 40%.
        bool go = Mode != ReactionMode.GoNoGo || _rng.NextDouble() >= Math.Min(0.4, 0.15 + Level * 0.05);
        double size = Math.Max(26, 90 - (Level - 1) * 8);
        double margin = 0.08;
        double x = margin + _rng.NextDouble() * (1 - 2 * margin), y = margin + _rng.NextDouble() * (1 - 2 * margin);
        double window = Math.Max(350, 900 - (Level - 1) * 70);
        return new ReactionTrial(wait, side, go, x, y, size, window);
    }

    /// <summary>
    /// Records a try. <paramref name="ms"/> is the time from the signal appearing to the click (negative = before it).
    /// Returns how it went; only hits after the warm-up count towards the score.
    /// </summary>
    public TrialResult Record(ReactionTrial t, double ms, int pressedSide = 0, bool pressed = true)
    {
        TrialResult r;
        if (pressed && ms < AnticipationMs) r = TrialResult.TooEarly;
        else if (Mode == ReactionMode.GoNoGo && !t.Go) r = pressed ? TrialResult.ClickedNoGo : TrialResult.CorrectlyHeld;
        else if (!pressed) r = TrialResult.Missed;
        else if (Mode == ReactionMode.Choice && pressedSide != t.Side) r = TrialResult.WrongSide;
        else r = TrialResult.Hit;

        bool counts = !InWarmUp;
        _done++;
        if (r == TrialResult.Hit && counts) _times.Add(ms);
        if (r is TrialResult.TooEarly or TrialResult.WrongSide or TrialResult.ClickedNoGo or TrialResult.Missed && counts) Mistakes++;
        Adapt(r);
        return r;
    }

    // Up a level after three good tries in a row, down after a mistake (a "3-up 1-down" staircase): settles where you
    // get about 80% right, the edge of what you can do.
    int _streak;
    void Adapt(TrialResult r)
    {
        if (Mode is not (ReactionMode.GoNoGo or ReactionMode.Flick)) return;
        if (r is TrialResult.Hit or TrialResult.CorrectlyHeld)
        {
            if (++_streak >= 3) { Level = Math.Min(10, Level + 1); _streak = 0; }
        }
        else { Level = Math.Max(1, Level - 1); _streak = 0; }
    }

    public ReactionSession Summary() => new()
    {
        When = DateTime.Now,
        Mode = Mode,
        MedianMs = Median(_times),
        BestMs = _times.Count > 0 ? _times.Min() : 0,
        SpreadMs = Quantile(_times, 0.75) - Quantile(_times, 0.25),
        Trials = _times.Count,
        Mistakes = Mistakes,
        Level = Level,
    };

    /// <summary>
    /// About how much of a measured time is the PC, not you: the screen (on average half a refresh until the next one,
    /// plus one refresh for Windows to compose it), the mouse (half its polling interval) and Windows' input handling.
    /// </summary>
    public static (double Screen, double Mouse, double Windows, double Total) SystemDelay(int refreshHz, int mouseHz = 1000)
    {
        double screen = 1000.0 / Math.Max(refreshHz, 30) * 1.5;
        double mouse = 1000.0 / Math.Max(mouseHz, 125) / 2;
        const double windows = 2;
        return (screen, mouse, windows, screen + mouse + windows);
    }

    public static double Median(IReadOnlyCollection<double> xs) => Quantile(xs, 0.5);

    public static double Quantile(IReadOnlyCollection<double> xs, double q)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToArray();
        double pos = (s.Length - 1) * q;
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }

    /// <summary>
    /// How a new result compares with your recent ones, in plain words. Reaction time naturally moves about ±5–10%
    /// from day to day (sleep, warm-up, caffeine), so only a change bigger than your own usual spread is called one.
    /// </summary>
    public static string Compare(ReactionSession now, IEnumerable<ReactionSession> history)
    {
        var past = history.Where(h => h.Mode == now.Mode && h.Trials > 0 && h != now).OrderByDescending(h => h.When).Take(10).Select(h => h.MedianMs).ToList();
        if (past.Count < 3) return "Do a few more sessions on different days to see your trend.";
        double avg = past.Average();
        double sd = Math.Sqrt(past.Sum(p => (p - avg) * (p - avg)) / (past.Count - 1));
        double normal = Math.Max(sd, avg * 0.04);
        double diff = now.MedianMs - avg;
        if (diff < -normal) return "Faster than your usual. Nice.";
        if (diff > normal) return "Slower than your usual. Tired, cold hands or not warmed up all slow you down; try again later.";
        return "Within your normal range. Day-to-day changes this small are normal.";
    }
}

/// <summary>
/// How fast people are on a simple "click when it changes" test. Large online datasets put the median near 273 ms;
/// times are skewed (a long slow tail, a hard floor near 150 ms), so they're modelled as log-normal with a log-space
/// spread of 0.25. Those online numbers include each person's own PC, mouse and screen, so they're compared with the
/// full measured time, not the "you alone" part.
/// </summary>
public static class ReactionPopulation
{
    public const double MedianMs = 273;
    public const double LogSpread = 0.25;

    /// <summary>Share of people (0 to 1) slower than <paramref name="ms"/>.</summary>
    public static double FasterThan(double ms) => ms <= 0 ? 1 : 1 - Phi((Math.Log(ms) - Math.Log(MedianMs)) / LogSpread);

    /// <summary>How common a time is (the curve's height), for drawing.</summary>
    public static double Density(double ms)
    {
        if (ms <= 0) return 0;
        double z = (Math.Log(ms) - Math.Log(MedianMs)) / LogSpread;
        return Math.Exp(-z * z / 2) / (ms * LogSpread * Math.Sqrt(2 * Math.PI));
    }

    /// <summary>A plain name for a time.</summary>
    public static string Tier(double ms) => ms switch
    {
        < 180 => "Elite",
        < 220 => "Very fast",
        < 250 => "Fast",
        < 300 => "Average",
        < 350 => "A bit slow",
        _ => "Slow today",
    };

    static double Phi(double z)
    {
        // Abramowitz & Stegun 7.1.26 (error under 1.5e-7): plenty for a percentile.
        double t = 1 / (1 + 0.3275911 * Math.Abs(z) / Math.Sqrt(2));
        double y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z / 2);
        return z >= 0 ? (1 + y) / 2 : (1 - y) / 2;
    }
}
