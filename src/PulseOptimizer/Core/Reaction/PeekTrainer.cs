namespace Pulse.Core.Reaction;

/// <summary>
/// One peek: an enemy (or a teammate) swings out from behind a wall edge, stays out for a moment, then hides again.
/// Sizes are in pixels and times in milliseconds; <see cref="PeekScene"/> turns them into positions.
/// </summary>
public record PeekTrial(
    // Wait before the peek (random, so it can't be timed).
    double WaitMs,
    // Comes out from the left wall (moving right) or the right wall (moving left).
    bool FromLeft,
    // false = a teammate: don't shoot.
    bool Enemy,
    // Head height as a fraction of the arena's height.
    double HeadY,
    // Head diameter in pixels.
    double HeadSize,
    // How fast the swing is, in pixels per millisecond.
    double Speed,
    // How far past the wall edge the figure swings (a jiggle is small, a wide swing is big).
    double SwingPx,
    // How long from the first pixel showing until the figure starts hiding again.
    double ExposeMs);

/// <summary>How a peek ended.</summary>
public enum PeekResult { Head, Body, TooEarly, ShotTeammate, Missed, Held }

/// <summary>
/// Where everything is in the arena: two walls with an open lane between them, like holding a doorway.
/// Kept free of any UI so the hit-testing can be tested.
/// </summary>
public sealed class PeekScene
{
    public double W { get; }
    public double H { get; }
    public PeekScene(double w, double h) { W = w; H = h; }

    public double LeftEdge => W * 0.3;
    public double RightEdge => W * 0.7;
    public double Floor => H * 0.9;

    public static double BodyWidth(double headSize) => headSize * 2.4;

    public readonly record struct Pose(double HeadX, double HeadY, double R, double BodyLeft, double BodyTop, double BodyW, double BodyH, double Edge, bool Gone);

    /// <summary>When the swing out is finished.</summary>
    public static double OutMs(PeekTrial t) => t.SwingPx / t.Speed;
    /// <summary>When the figure is fully back behind the wall: the peek is over.</summary>
    public static double EndMs(PeekTrial t) => Math.Max(t.ExposeMs, OutMs(t)) + OutMs(t);

    /// <summary>Where the figure is <paramref name="ms"/> after the peek starts.</summary>
    public Pose At(PeekTrial t, double ms)
    {
        double r = t.HeadSize / 2, bw = BodyWidth(t.HeadSize);
        double headY = t.HeadY * H;
        double bodyTop = headY + r * 0.85;
        double bodyH = Math.Max(Floor - bodyTop, r);
        double edge = t.FromLeft ? LeftEdge : RightEdge;
        int dir = t.FromLeft ? 1 : -1;
        double outMs = OutMs(t), hold = Math.Max(t.ExposeMs, outMs);
        double off;
        bool gone = false;
        if (ms <= 0) off = 0;
        else if (ms < outMs) off = ms * t.Speed;
        else if (ms < hold) off = t.SwingPx;
        else
        {
            off = t.SwingPx - (ms - hold) * t.Speed;
            if (off <= 0) { off = 0; gone = true; }
        }
        // The figure's leading side is "off" pixels past the wall edge.
        double cx = edge + dir * (off - bw / 2);
        return new Pose(cx, headY, r, cx - bw / 2, bodyTop, bw, bodyH, edge, gone);
    }

    /// <summary>Where the head stops when the swing is complete: where a well-placed crosshair should already be.</summary>
    public (double X, double Y) HoldHead(PeekTrial t)
    {
        var p = At(t, OutMs(t));
        return (p.HeadX, p.HeadY);
    }

    /// <summary>What a click at (x, y) hits at <paramref name="ms"/> into the peek: the head, the body, or nothing.</summary>
    public PeekResult? HitTest(PeekTrial t, double ms, double x, double y)
    {
        var p = At(t, ms);
        if (p.Gone || ms <= 0) return null;
        bool behindWall = t.FromLeft ? x < p.Edge : x > p.Edge;
        if (behindWall) return null;
        const double give = 2; // a couple of pixels of forgiveness, like a hitbox slightly bigger than the model
        if ((x - p.HeadX) * (x - p.HeadX) + (y - p.HeadY) * (y - p.HeadY) <= (p.R + give) * (p.R + give)) return PeekResult.Head;
        if (x >= p.BodyLeft - give && x <= p.BodyLeft + p.BodyW + give && y >= p.BodyTop && y <= p.BodyTop + p.BodyH) return PeekResult.Body;
        return null;
    }
}

/// <summary>
/// The game-like drills: Peek (react and aim at an enemy swinging out), Shoot or hold (the same, but some are
/// teammates), Crosshair placement (hold an angle; scored on how far your crosshair had to move) and the Benchmark.
///
/// What makes these train rather than just measure:
///  - They look like the game: angles, head height, enemies and teammates, so the practice carries over.
///  - Difficulty uses a weighted up/down staircase (+0.25 after a success, −1 after a failure), which settles where
///    you succeed about 80% of the time: hard enough to push you, easy enough to keep the feedback useful.
///  - Your level is kept between sessions, so every session starts near your edge.
///  - The Benchmark never adapts. It's the same test every time, so its trend shows real improvement, not the drill
///    getting easier.
/// </summary>
public sealed class PeekTrainer
{
    public const int TrialsPerSession = 15;
    public const int BenchmarkTrials = 16;
    /// <summary>The Benchmark's fixed difficulty: about 760 ms of exposure and a 37 px head.</summary>
    public const double BenchmarkDifficulty = 6;
    public const double MinDifficulty = 1, MaxDifficulty = 20;
    public const double StepUp = 0.25, StepDown = 1.0;
    /// <summary>Faster than this after the peek starts is a guess, not a reaction.</summary>
    public const double AnticipationMs = 100;

    readonly Random _rng;
    readonly List<double> _times = new();
    readonly List<double> _offsets = new();
    int _done, _heads, _bodies, _enemies, _enemyHits, _teamShots, _missedEnemies, _early;

    public ReactionMode Mode { get; }
    public double Difficulty { get; private set; }
    public double PeakDifficulty { get; private set; }
    public int MissedShots { get; private set; }
    public int Done => _done;
    public int Total => Mode == ReactionMode.Benchmark ? BenchmarkTrials : TrialsPerSession;
    public bool Finished => _done >= Total;
    public IReadOnlyList<double> Times => _times;

    public PeekTrainer(ReactionMode mode, double startDifficulty = MinDifficulty, int seed = 0)
    {
        if (mode is not (ReactionMode.Peek or ReactionMode.FriendOrFoe or ReactionMode.Crosshair or ReactionMode.Benchmark))
            throw new ArgumentException("Not a peek drill", nameof(mode));
        Mode = mode;
        _rng = seed == 0 ? new Random() : new Random(seed);
        Difficulty = mode == ReactionMode.Benchmark ? BenchmarkDifficulty : Math.Clamp(startDifficulty, MinDifficulty, MaxDifficulty);
        PeakDifficulty = Difficulty;
    }

    public static bool IsPeekDrill(ReactionMode m) => m is ReactionMode.Peek or ReactionMode.FriendOrFoe or ReactionMode.Crosshair or ReactionMode.Benchmark;

    /// <summary>How long the enemy stays out at a difficulty: 1.15 s at the start, down to about 230 ms.</summary>
    public static double ExposeAt(double d) => Math.Max(230, 1150 * Math.Pow(0.92, d - 1));
    /// <summary>Head size at a difficulty: 46 px at the start, down to 16 px.</summary>
    public static double HeadAt(double d) => Math.Max(16, 46 * Math.Pow(0.955, d - 1));
    /// <summary>Swing speed at a difficulty, in pixels per millisecond.</summary>
    public static double SpeedAt(double d) => 0.55 + 0.045 * d;

    double TeammateShare => Mode switch
    {
        ReactionMode.FriendOrFoe => Math.Min(0.45, 0.3 + Difficulty * 0.008),
        ReactionMode.Benchmark => 0.25,
        _ => 0,
    };

    /// <summary>The next peek.</summary>
    public PeekTrial Next()
    {
        double d = Difficulty;
        double wait = 1100 + Math.Min(2400, -Math.Log(1 - _rng.NextDouble()) * 800);
        // The Crosshair drill alternates sides in pairs, and the page tells you which angle to hold.
        bool fromLeft = Mode == ReactionMode.Crosshair ? (_done / 2) % 2 == 0 : _rng.Next(2) == 0;
        bool enemy = _rng.NextDouble() >= TeammateShare;
        double head = HeadAt(d);
        // Head height: standing heads are all at about the same height (that's what crosshair placement uses);
        // the drills vary it a little, more in Peek (crouch peeks, high ground) than in Crosshair.
        double spreadY = Mode switch { ReactionMode.Crosshair => 0.015 + d * 0.002, ReactionMode.Benchmark => 0.05, _ => 0.08 };
        double headY = 0.36 + (_rng.NextDouble() * 2 - 1) * spreadY;
        double bw = PeekScene.BodyWidth(head);
        double swing = bw * (1.0 + _rng.NextDouble() * 0.9);
        double speed = SpeedAt(d) * (0.85 + _rng.NextDouble() * 0.3);
        double expose = Math.Max(ExposeAt(d), swing / speed + 120);
        return new PeekTrial(wait, fromLeft, enemy, headY, head, speed, swing, expose);
    }

    /// <summary>A shot that hit nothing while the peek was on. It doesn't end the try, like a missed shot in game.</summary>
    public void MissedShot() => MissedShots++;

    /// <summary>
    /// Records how a peek ended. <paramref name="ms"/> is the time from the first pixel showing to the hit;
    /// <paramref name="offsetPx"/> is how far the crosshair was from where the head stopped, when the peek started.
    /// </summary>
    public PeekResult Record(PeekTrial t, PeekResult r, double ms = 0, double offsetPx = -1)
    {
        if (r is PeekResult.Head or PeekResult.Body && ms < AnticipationMs) r = PeekResult.TooEarly;
        _done++;
        if (t.Enemy) _enemies++;
        switch (r)
        {
            case PeekResult.Head: _heads++; _enemyHits++; _times.Add(ms); break;
            case PeekResult.Body: _bodies++; _enemyHits++; _times.Add(ms); break;
            case PeekResult.ShotTeammate: _teamShots++; break;
            case PeekResult.Missed: _missedEnemies++; break;
            case PeekResult.TooEarly: _early++; break;
        }
        if (offsetPx >= 0 && t.Enemy) _offsets.Add(offsetPx);
        Adapt(r);
        return r;
    }

    // Weighted up/down: +0.25 after a success, −1 after a failure. Balanced when p·0.25 = (1−p)·1, so p = 80%.
    // A body shot counts as a success in Peek and Shoot or hold, but Crosshair placement wants the head.
    void Adapt(PeekResult r)
    {
        if (Mode == ReactionMode.Benchmark) return;
        bool ok = r is PeekResult.Head or PeekResult.Held || (r == PeekResult.Body && Mode != ReactionMode.Crosshair);
        Difficulty = Math.Clamp(Difficulty + (ok ? StepUp : -StepDown), MinDifficulty, MaxDifficulty);
        PeakDifficulty = Math.Max(PeakDifficulty, Difficulty);
    }

    public int Mistakes => _teamShots + _missedEnemies + _early;
    public double Accuracy => _enemies == 0 ? 0 : (double)_enemyHits / _enemies;
    public double HeadshotShare => _enemyHits == 0 ? 0 : (double)_heads / _enemyHits;

    /// <summary>
    /// One number for the Benchmark: your median time to hit, made worse by each mistake (a missed enemy, a shot
    /// teammate or a guess). Lower is better. 16 tries with 2 mistakes adds 12.5%.
    /// </summary>
    public static double ScoreOf(double medianMs, int mistakes, int trials) =>
        medianMs <= 0 || trials == 0 ? 0 : medianMs * (1 + (double)mistakes / trials);

    public ReactionSession Summary() => new()
    {
        When = DateTime.Now,
        Mode = Mode,
        MedianMs = ReactionTrainer.Median(_times),
        BestMs = _times.Count > 0 ? _times.Min() : 0,
        SpreadMs = ReactionTrainer.Quantile(_times, 0.75) - ReactionTrainer.Quantile(_times, 0.25),
        Trials = _times.Count,
        Mistakes = Mistakes,
        MissedShots = MissedShots,
        Level = (int)Math.Round(Difficulty),
        Difficulty = Math.Round(Difficulty, 2),
        Accuracy = Math.Round(Accuracy, 3),
        HeadshotShare = Math.Round(HeadshotShare, 3),
        OffsetPx = _offsets.Count > 0 ? Math.Round(ReactionTrainer.Median(_offsets), 1) : 0,
        Score = Math.Round(ScoreOf(ReactionTrainer.Median(_times), Mistakes, _done), 1),
    };
}

/// <summary>
/// The daily plan and the weekly Benchmark. Research on skill training points the same way every time: short,
/// frequent, varied sessions beat one long one, and progress should be measured on a test you don't practise.
/// </summary>
public static class TrainingPlan
{
    static readonly ReactionMode[] Rotating = { ReactionMode.Choice, ReactionMode.GoNoGo, ReactionMode.Flick };

    /// <summary>Today's drills: the three game drills plus one of the classic ones, changing each day. About 5 minutes.</summary>
    public static ReactionMode[] Today(DateTime day) =>
        new[] { ReactionMode.Peek, ReactionMode.FriendOrFoe, ReactionMode.Crosshair, Rotating[day.DayOfYear % Rotating.Length] };

    public static IEnumerable<ReactionSession> Benchmarks(ReactionSettings s) =>
        s.History.Where(h => h.Mode == ReactionMode.Benchmark && h.Score > 0).OrderBy(h => h.When);

    /// <summary>The Benchmark is due when you haven't taken one in 7 days (or ever).</summary>
    public static bool BenchmarkDue(ReactionSettings s, DateTime now)
    {
        var last = Benchmarks(s).LastOrDefault();
        return last == null || (now - last.When).TotalDays >= 7;
    }

    /// <summary>Days in a row (ending today or yesterday) with the daily plan finished.</summary>
    public static int Streak(ReactionSettings s, DateTime today)
    {
        var days = new HashSet<DateTime>(s.PlanDays.Select(d => d.Date));
        var day = today.Date;
        if (!days.Contains(day)) day = day.AddDays(-1);
        int n = 0;
        while (days.Contains(day)) { n++; day = day.AddDays(-1); }
        return n;
    }

    /// <summary>Where a drill should start: a little below where you left it, as a warm-up.</summary>
    public static double StartLevel(ReactionSettings s, ReactionMode m) =>
        s.Levels.TryGetValue(m.ToString(), out var d) ? Math.Max(PeekTrainer.MinDifficulty, d - 1.5) : PeekTrainer.MinDifficulty;

    public static void SaveLevel(ReactionSettings s, ReactionMode m, double d) => s.Levels[m.ToString()] = Math.Round(d, 2);

    /// <summary>
    /// Your first Benchmarks vs your latest ones, in plain words. Uses the average of up to three on each side, so one
    /// good or bad day doesn't decide it, and only calls a change when it's bigger than normal day-to-day variation.
    /// Returns an English format string (for translation) and its values.
    /// </summary>
    public static (string Format, object[] Args) Progress(ReactionSettings s)
    {
        var b = Benchmarks(s).ToList();
        if (b.Count == 0) return ("Take the Benchmark to get your starting point. Then take it once a week.", Array.Empty<object>());
        if (b.Count == 1) return ("Starting point saved. Take the Benchmark again next week to see what the training did.", Array.Empty<object>());
        int k = Math.Min(3, b.Count / 2);
        var first = b.Take(k).ToList();
        var last = b.Skip(b.Count - k).ToList();
        double f = first.Average(x => x.Score), l = last.Average(x => x.Score);
        double fm = first.Average(x => x.MedianMs), lm = last.Average(x => x.MedianMs);
        double fa = first.Average(x => x.Accuracy), la = last.Average(x => x.Accuracy);
        double change = (f - l) / f;
        if (change > 0.05)
            return ("Your Benchmark score is {0}% better than when you started: {1} ms faster to the hit, accuracy {2}% → {3}%.",
                new object[] { Math.Round(change * 100), Math.Round(fm - lm), Math.Round(fa * 100), Math.Round(la * 100) });
        if (change < -0.05)
            return ("Your latest Benchmarks are below your start. Sleep, warm-up and time of day matter: take it fresh, at the same time of day.", Array.Empty<object>());
        return ("About the same as when you started. Most of the gain comes after 2–4 weeks of daily training.", Array.Empty<object>());
    }
}
