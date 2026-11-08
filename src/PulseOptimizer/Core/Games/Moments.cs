using System.Text.Json;

namespace Pulse.Core.Games;

/// <summary>One kill or death, at the moment it happened on this PC's clock.</summary>
public sealed record Moment(DateTime When, string Kind, string Game)
{
    public const string Kill = "kill", Death = "death";
}

/// <summary>
/// Kills and deaths from the games that share them (VALORANT, CS2, League, Dota 2), kept for a week so a clip can show
/// where they happened on its timeline (moments.json in Pulse's data folder).
/// </summary>
public static class MomentLog
{
    static readonly object Gate = new();
    static List<Moment> _list;
    static string FilePath => Path.Combine(TweakEngine.DataDir, "moments.json");

    static List<Moment> List
    {
        get
        {
            if (_list != null) return _list;
            try { _list = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Moment>>(File.ReadAllText(FilePath)) ?? new() : new(); }
            catch { _list = new(); }
            return _list;
        }
    }

    public static void Add(IEnumerable<Moment> moments)
    {
        lock (Gate)
        {
            var l = List;
            foreach (var m in moments)
                // VALORANT's after-match data can be read twice: skip a moment that's exactly the same as one already
                // logged. Two real kills a split second apart (a quick double) are different moments and both stay.
                if (!(m.Game == "VALORANT" && l.Any(x => x.Kind == m.Kind && x.Game == m.Game && x.When == m.When))) l.Add(m);
            var cutoff = DateTime.Now.AddDays(-7);
            l.RemoveAll(x => x.When < cutoff);
            if (l.Count > 20000) l.RemoveRange(0, l.Count - 20000);
            l.Sort((a, b) => a.When.CompareTo(b.When));
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(l)); } catch { }
        }
    }

    /// <summary>Moments in a clip that ends at <paramref name="end"/> and lasts <paramref name="seconds"/>, as seconds into the clip.</summary>
    public static List<(double T, string Kind)> ForClip(DateTime end, double seconds)
    {
        lock (Gate)
            return Place(List, end, seconds);
    }

    internal static List<(double T, string Kind)> Place(IEnumerable<Moment> moments, DateTime end, double seconds) =>
        moments.Select(m => (T: seconds - (end - m.When).TotalSeconds, m.Kind))
            .Where(x => x.T >= 0 && x.T <= seconds).OrderBy(x => x.T).ToList();
}
