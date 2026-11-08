using Pulse.Ui;

namespace Pulse.Core.Games.Valorant;

/// <summary>How to get better at one thing: what it is, why it matters, what to do, and a drill.</summary>
public sealed record CoachTip(string Why, string[] Steps, string Drill, DeathTag? Watch);

/// <summary>One number the coach tracks, how it's counted and which way is better.</summary>
public sealed record Metric(
    string Id, string Name, string Unit, bool HigherBetter, double Weight, int MinSample,
    Func<PlayerTotals, double> Get, Func<PlayerTotals, int> Sample, CoachTip Tip)
{
    public string Format(double v) => double.IsNaN(v) ? "–" : Unit switch
    {
        "%" => $"{v:0}%",
        "/100" => v.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture),
        _ => v.ToString("0", System.Globalization.CultureInfo.CurrentCulture),
    };
}

/// <summary>A number compared with the players in your matches. Gap is how much better (+) or worse (−), as a share.</summary>
public sealed record Finding(Metric Metric, double Mine, double Base, double Gap, string Against);

public sealed record AbilityUse(string Agent, int Rounds, double[] Mine, double[] Others, int OtherRounds);

public sealed class CoachReport
{
    public int Matches, Rounds;
    public PlayerTotals Mine = new(), Lobby = new();
    public List<Finding> Weaknesses = new(), Strengths = new(), All = new();
    public Dictionary<DeathTag, int> Causes = new();
    public int DeathsCounted;
    public List<AbilityUse> Abilities = new();
    public bool HasAbilityRounds;
}

/// <summary>A week's focus: one number to improve, where it started, and the result when it ended.</summary>
public sealed class CoachFocus
{
    public string Metric { get; set; } = "";
    public DateTime Start { get; set; }
    public double Baseline { get; set; }
    public int BaselineMatches { get; set; }
    public DateTime? Ended { get; set; }
    /// <summary>-1 until the focus ends (or when it had no matches).</summary>
    public double After { get; set; } = -1;
    public int AfterMatches { get; set; }
}

public sealed class CoachState
{
    public CoachFocus Focus { get; set; }
    public List<CoachFocus> Past { get; set; } = new();
    /// <summary>When Pulse last downloaded recent matches for the coach.</summary>
    public DateTime LastBackfill { get; set; }
}

/// <summary>
/// The coach: finds the habits that cost you rounds by comparing your numbers with the other players in your own
/// matches (same rank, same servers), shows how to fix them, and checks whether you actually did.
/// </summary>
public static class Coach
{
    public const int MinRounds = 60;

    public static readonly Metric[] Metrics =
    {
        new("untraded", "Deaths your team didn't trade", "%", false, 1.2, 20, t => t.UntradedPct, t => t.DeathsWithMates,
            new CoachTip(
                "When you die, a teammate close by can kill your killer a moment later, so the round stays even. A death nobody trades is a free kill for the other team.",
                new[]
                {
                    "Play within a few steps of a teammate, most of all when you're the second one into a fight.",
                    "Swing right after your entry: when they peek, you peek the same angle a second later.",
                    "Hold angles your teammate can also see, so whoever kills you is in their sights.",
                    "Say where you died and who killed you, straight away.",
                },
                "Next 5 matches: before each fight, check that a teammate is close enough to swing right after you.",
                DeathTag.NotTraded)),
        new("alone", "Dying with no teammate within 15 m", "%", false, 1.0, 20, t => t.AlonePct, t => t.DeathsPlaced,
            new CoachTip(
                "Dying far from everyone means nobody can trade you and the other team gets a free pick. One lurker can work; dying alone every round doesn't.",
                new[]
                {
                    "Rotate and push with someone instead of alone.",
                    "If you lurk, play for information and fall back when you're spotted, instead of taking the fight.",
                    "Look at the minimap after every fight and every time you move to a new area.",
                },
                "Next 5 matches: glance at the minimap every time you reach a new area, and move with whoever is closest.",
                DeathTag.Alone)),
        new("firstdeath", "First deaths per 100 rounds", "/100", false, 1.1, MinRounds, t => t.FirstDeathsPer100, t => t.Rounds,
            new CoachTip(
                "The team that loses the first fight wins far fewer rounds. Dying first, with no trade, puts your team in a 4 against 5.",
                new[]
                {
                    "Don't peek without help: use a flash, smoke or a teammate's ability first.",
                    "Jiggle-peek or shoulder-peek to see where they are before you commit.",
                    "Early in the round, hold an off-angle instead of the spot everyone pre-aims.",
                    "On a duelist, dying first is part of the job, but only with a teammate right behind you to trade.",
                },
                "Deathmatch: practice jiggle peeks: show your shoulder, hide, then swing only once you know where they are.",
                DeathTag.FirstDeath)),
        new("opening", "Opening duels won", "%", true, 0.9, 15, t => t.OpeningWinPct, t => t.FirstKills + t.FirstDeaths,
            new CoachTip(
                "The first fight of the round decides a lot of rounds. Winning it starts the round 5 against 4.",
                new[]
                {
                    "Take the first fight with utility: flash for yourself, or ask for one.",
                    "Pre-aim the spot you expect them, with your crosshair already at head height.",
                    "If you lose the first fight a lot from the same spot, play it differently (see the heatmap).",
                },
                "Range: 5 minutes of peeking a corner and shooting the first bot that appears, crosshair at head height.",
                DeathTag.FirstDeath)),
        new("outnumbered", "Deaths with 2+ enemies within 20 m", "%", false, 0.9, 20, t => t.OutnumberedPct, t => t.DeathsPlaced,
            new CoachTip(
                "Fighting two enemies at once means you have to win two fights before they win one.",
                new[]
                {
                    "Take fights where only one enemy can see you: use walls and smokes to cut the other angles.",
                    "After a kill, move instead of peeking the same spot again.",
                    "When you hear several enemies, wait for your team or fall back.",
                },
                "Range or Deathmatch: clear angles one at a time, keeping everything else behind cover.",
                DeathTag.Outnumbered)),
        new("kast", "KAST (rounds with a kill, assist, survival or trade)", "%", true, 1.0, MinRounds, t => t.KastPct, t => t.Rounds,
            new CoachTip(
                "KAST is the share of rounds where you did something for the team: a kill, an assist, you survived, or you were traded. Rounds without any of those are rounds you played 4 against 5.",
                new[]
                {
                    "Save in rounds you can't win: you keep your gun and the round still counts.",
                    "Use your utility for teammates: an assist counts as much as a kill here.",
                    "Stay where you can be traded (see trading above).",
                },
                "Next 5 matches: in every lost round, ask yourself whether you could have saved or helped instead.",
                null)),
        new("adr", "Damage per round", "", true, 0.8, MinRounds, t => t.Adr, t => t.Rounds,
            new CoachTip(
                "Damage wins fights for your teammates too: an enemy you've hit dies to the next bullet.",
                new[]
                {
                    "Shoot as soon as you see an enemy instead of waiting for a perfect shot.",
                    "Keep your crosshair at head height on the spots enemies come from.",
                    "Buy armor whenever you can; losing a fight because of no armor is lost damage.",
                },
                "Range, hard bots: 2 rounds a day with your main rifle.",
                null)),
        new("hs", "Headshot rate", "%", true, 0.7, 150, t => t.HeadshotPct, t => t.Head + t.Body + t.Legs,
            new CoachTip(
                "In VALORANT a rifle headshot kills in one bullet; body shots need three or four.",
                new[]
                {
                    "Keep your crosshair at head height while you move: most of it is placement, not flicking.",
                    "Tap or burst at range instead of spraying.",
                    "Stop moving when you shoot (counter-strafe): moving shots go wide.",
                },
                "Range, hard bots, Vandal or Phantom: tap heads only for 5 minutes before you queue.",
                null)),
        new("tradekills", "Trades per 100 rounds", "/100", true, 0.7, MinRounds, t => t.TradeKillsPer100, t => t.Rounds,
            new CoachTip(
                "A trade is killing the enemy who just killed your teammate. It's how a lost fight turns back into an even round.",
                new[]
                {
                    "When a teammate peeks, be ready to swing the same angle right after.",
                    "Stay close enough to see what killed them.",
                    "If your teammate dies, swing right away while the enemy is still reloading or moving.",
                },
                "Next 5 matches: when you're second into a fight, stay one step behind your teammate and swing as soon as they shoot.",
                DeathTag.NotTraded)),
        new("noability", "Deaths before using any ability", "%", false, 0.8, 20, t => t.NoAbilityPct, t => t.DeathsAbilityKnown,
            new CoachTip(
                "Abilities you still have when you die are wasted. Most fights are won by the player who used utility first.",
                new[]
                {
                    "Use utility before the fight it's for: flash before you peek, smoke before you cross.",
                    "In the buy phase, decide where each ability goes this round.",
                },
                "Next 5 matches: use at least one ability before your first fight of every round.",
                DeathTag.NoAbility)),
    };

    public static Metric Get(string id) => Metrics.FirstOrDefault(m => m.Id == id);

    /// <summary>Everything the coach knows from these matches (newest first is fine, order doesn't matter).</summary>
    public static CoachReport Analyze(IReadOnlyList<VMatch> matches)
    {
        var r = new CoachReport { Matches = matches.Count };
        if (matches.Count == 0) return r;
        r.Mine = Analysis.Mine(matches);
        r.Lobby = Analysis.Lobby(matches);
        r.Rounds = r.Mine.Rounds;

        // First deaths and opening duels depend on the role (duelists die first by design): compare with the same
        // role when there's enough of it in your matches.
        var myRole = matches.GroupBy(m => Analysis.RoleOf(m.MyAgent)).OrderByDescending(g => g.Count()).First().Key;
        PlayerTotals sameRole = null;
        if (myRole.Length > 0)
        {
            sameRole = Analysis.Lobby(matches, (_, p) => Analysis.RoleOf(p.Agent) == myRole);
            if (sameRole.Rounds < 200) sameRole = null;
        }

        foreach (var m in Metrics)
        {
            var useRole = sameRole != null && m.Id is "firstdeath" or "opening";
            var baseT = useRole ? sameRole : r.Lobby;
            double mine = m.Get(r.Mine), b = m.Get(baseT);
            if (m.Sample(r.Mine) < m.MinSample || double.IsNaN(mine) || double.IsNaN(b) || b <= 0) continue;
            double gap = m.HigherBetter ? (mine - b) / b : (b - mine) / b;
            r.All.Add(new Finding(m, mine, b, gap, useRole ? Loc.F("other {0} players in your matches", Loc.T(myRole)) : Loc.T("the other players in your matches")));
        }
        r.Weaknesses = r.All.Where(f => f.Gap <= -0.10).OrderBy(f => f.Gap * f.Metric.Weight).Take(3).ToList();
        r.Strengths = r.All.Where(f => f.Gap >= 0.10).OrderByDescending(f => f.Gap * f.Metric.Weight).Take(2).ToList();

        // Why you died, over all your deaths.
        foreach (var m in matches)
            foreach (var d in Analysis.Deaths(m, m.Me))
            {
                r.DeathsCounted++;
                r.Causes[d.Main] = r.Causes.TryGetValue(d.Main, out var n) ? n + 1 : 1;
            }
        r.HasAbilityRounds = matches.Any(m => m.Rounds.Any(x => x.UsedAbility != null));

        // Ability use per agent you played, against others on the same agent in your matches.
        foreach (var g in matches.GroupBy(m => m.MyAgent).Where(g => g.Key.Length > 0))
        {
            var mineT = Analysis.Mine(g);
            if (mineT.CastRounds < 30) continue;
            var others = Analysis.Lobby(matches, (_, p) => p.Agent == g.Key);
            r.Abilities.Add(new AbilityUse(g.Key, mineT.CastRounds,
                Enumerable.Range(0, 4).Select(mineT.CastsPerRound).ToArray(),
                others.CastRounds >= 40 ? Enumerable.Range(0, 4).Select(others.CastsPerRound).ToArray() : null, others.CastRounds));
        }
        r.Abilities = r.Abilities.OrderByDescending(a => a.Rounds).ToList();
        return r;
    }

    /// <summary>One number for one match (for the focus graph). NaN when the match doesn't say.</summary>
    public static double ValueIn(VMatch m, string metric) => Get(metric)?.Get(Analysis.Totals(m, m.Me)) ?? double.NaN;

    /// <summary>The number over several matches, counted together (not an average of averages).</summary>
    public static double ValueOver(IEnumerable<VMatch> ms, string metric) => Get(metric)?.Get(Analysis.Mine(ms)) ?? double.NaN;

    /// <summary>Starts a week's focus on one number, with your last 10 matches as the "before".</summary>
    public static CoachFocus StartFocus(CoachState state, string metric, IReadOnlyList<VMatch> matches)
    {
        if (state.Focus != null) EndFocus(state, matches);
        var before = matches.OrderByDescending(m => m.StartUtc).Take(10).ToList();
        var b = ValueOver(before, metric);
        state.Focus = new CoachFocus { Metric = metric, Start = DateTime.UtcNow, Baseline = double.IsNaN(b) ? -1 : b, BaselineMatches = before.Count };
        return state.Focus;
    }

    public static void EndFocus(CoachState state, IReadOnlyList<VMatch> matches)
    {
        var f = state.Focus;
        if (f == null) return;
        var after = matches.Where(m => m.StartUtc >= f.Start).ToList();
        var a = ValueOver(after, f.Metric);
        f.After = double.IsNaN(a) ? -1 : a;
        f.AfterMatches = after.Count;
        f.Ended = DateTime.UtcNow;
        state.Past.Insert(0, f);
        if (state.Past.Count > 20) state.Past.RemoveRange(20, state.Past.Count - 20);
        state.Focus = null;
    }

    public enum Verdict { TooEarly, Better, Same, Worse }

    /// <summary>
    /// Did it get better? Needs 5 matches since the start. A change under 8% either way counts as "about the same":
    /// a handful of matches swing that much on their own.
    /// </summary>
    public static Verdict Judge(CoachFocus f, double after, int matchesAfter)
    {
        var m = Get(f.Metric);
        if (m == null || matchesAfter < 5 || double.IsNaN(after) || after < 0 || double.IsNaN(f.Baseline) || f.Baseline <= 0) return Verdict.TooEarly;
        double change = (after - f.Baseline) / f.Baseline * (m.HigherBetter ? 1 : -1);
        return change >= 0.08 ? Verdict.Better : change <= -0.08 ? Verdict.Worse : Verdict.Same;
    }

    /// <summary>What to call each cause of death on screen.</summary>
    public static string CauseName(DeathTag t) => t switch
    {
        DeathTag.FirstDeath => "First death of the round",
        DeathTag.NotTraded => "Not traded",
        DeathTag.Alone => "No teammate within 15 m",
        DeathTag.Outnumbered => "2+ enemies close by",
        DeathTag.NoAbility => "No ability used yet",
        DeathTag.LastAlive => "Last one alive",
        DeathTag.Outgunned => "Out-bought (eco against a full buy)",
        DeathTag.Traded => "Traded by a teammate",
        _ => t.ToString(),
    };

    /// <summary>True for causes that point at something you could do differently (the others are the situation).</summary>
    public static bool IsMistake(DeathTag t) => t is DeathTag.FirstDeath or DeathTag.NotTraded or DeathTag.Alone or DeathTag.Outnumbered or DeathTag.NoAbility;
}
