namespace Pulse.Core.Input;

/// <summary>Everything the user picked on the Macros page. Saved in Pulse's state file.</summary>
public class MacroSettings
{
    /// <summary>"steady", "butterfly", "jitter" or "rhythm" (the user's own recorded clicking).</summary>
    public string Mode { get; set; } = "butterfly";
    /// <summary>Clicks per second asked for (the recorded rhythm keeps its own speed unless this is set to scale it).</summary>
    public double Cps { get; set; } = 16;
    /// <summary>How much each gap may vary, as a share (0 = metronome, 0.08 = natural, 0.15 = loose).</summary>
    public double Variation { get; set; } = 0.08;
    public string Button { get; set; } = "left";
    /// <summary>"hold" (clicks while the trigger is held) or "toggle" (press once to start, again to stop).</summary>
    public string Activation { get; set; } = "hold";
    /// <summary>Virtual-key code of the trigger (0x05 = mouse side button 4).</summary>
    public int TriggerKey { get; set; } = 0x05;
    /// <summary>Only click while the game Pulse detected is the window in front.</summary>
    public bool OnlyInGame { get; set; }
    /// <summary>The user's own clicking, recorded on the test pad: (gap before the click, how long it was held), in ms.</summary>
    public List<double[]> Rhythm { get; set; } = new();
    /// <summary>Play the recorded rhythm at <see cref="Cps"/> instead of its own speed.</summary>
    public bool ScaleRhythm { get; set; }
}

/// <summary>
/// The timing of each click (how long until it, and how long the button is held). Pure maths, so it can be tested:
/// the engine just waits and presses as told.
/// </summary>
public sealed class ClickPattern
{
    readonly MacroSettings _s;
    readonly Random _rng;
    int _index;
    bool _secondOfPair;

    public ClickPattern(MacroSettings s, int seed = 0) { _s = s; _rng = seed == 0 ? new Random() : new Random(seed); }

    double Normal(double sd) // Box–Muller
    {
        double u1 = 1 - _rng.NextDouble(), u2 = _rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2) * sd;
    }

    double Vary(double ms, double share) => Math.Max(ms * (1 + Math.Clamp(Normal(share), -2.5 * share, 2.5 * share)), 1);

    /// <summary>The next click: wait <c>Gap</c> ms after the previous press started, then hold for <c>Hold</c> ms.</summary>
    public (double Gap, double Hold) Next()
    {
        double cps = Math.Clamp(_s.Cps, 1, 50);
        double v = Math.Clamp(_s.Variation, 0, 0.4);
        switch (_s.Mode)
        {
            case "butterfly":
            {
                // Two fingers on one button: a quick pair, then a longer gap while both fingers come back up.
                // The pair's spacing is about a third of the pair's period, like real butterfly clicking.
                double period = 2000 / cps;
                double inPair = period * (0.30 + _rng.NextDouble() * 0.10);
                double gap = _secondOfPair ? inPair : period - inPair;
                _secondOfPair = !_secondOfPair;
                double g = Vary(gap, v);
                return (g, Math.Min(Vary(14, 0.25), g * 0.6));
            }
            case "jitter":
            {
                // Tensed-arm jitter clicking: fast, short presses, noticeably uneven.
                double g = Vary(1000 / cps, Math.Max(v, 0.18));
                return (g, Math.Min(Vary(18, 0.3), g * 0.55));
            }
            case "rhythm" when _s.Rhythm.Count >= 4:
            {
                var r = _s.Rhythm[_index++ % _s.Rhythm.Count];
                double scale = _s.ScaleRhythm ? RhythmCps(_s.Rhythm) / cps : 1;
                double g = Math.Max(r[0] * scale, 1);
                return (g, Math.Clamp(r[1] * Math.Min(scale, 1), 1, g * 0.8));
            }
            default:
            {
                double g = Vary(1000 / cps, v);
                return (g, Math.Min(Vary(22, 0.25), g * 0.5));
            }
        }
    }

    /// <summary>The speed of a recorded rhythm, in clicks per second.</summary>
    public static double RhythmCps(IReadOnlyList<double[]> rhythm)
    {
        double total = rhythm.Sum(r => r[0]);
        return total > 0 ? rhythm.Count * 1000 / total : 0;
    }
}
