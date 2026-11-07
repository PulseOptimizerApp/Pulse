using System.Text.Json;

namespace Pulse.Core.Games;

/// <summary>One finished match: what Pulse shows on its recap card, like Medal's.</summary>
public class MatchRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Game { get; set; } = "";
    public string Map { get; set; } = "";
    /// <summary>Agent, champion, hero or side.</summary>
    public string Character { get; set; } = "";
    public string Mode { get; set; } = "";
    /// <summary>"Win", "Loss", "Draw" or "".</summary>
    public string Result { get; set; } = "";
    /// <summary>"13 : 3" (your team first).</summary>
    public string Score { get; set; } = "";
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    /// <summary>Headshot share 0–100, or -1 when the game doesn't say.</summary>
    public int HeadshotPercent { get; set; } = -1;
    /// <summary>VALORANT competitive tier number (3 = Iron 1 … 27 = Radiant), 0 when unranked; picks the rank badge.</summary>
    public int RankTier { get; set; }
    public string Rank { get; set; } = "";
    /// <summary>Extra numbers for this game ("CS 212", "ADR 154", "MVPs 4").</summary>
    public List<string> Extras { get; set; } = new();
    /// <summary>Big plays: "3K", "4K", "ACE", "Triple kill", "Penta kill"...</summary>
    public List<string> Highlights { get; set; } = new();
    public DateTime Start { get; set; }
    public DateTime End { get; set; }

    public string Kda => $"{Kills} / {Deaths} / {Assists}";
}

/// <summary>Matches Pulse saw, newest first (matches.json in Pulse's data folder).</summary>
public static class MatchStore
{
    static readonly object Gate = new();
    static List<MatchRecord> _list;
    static string FilePath => Path.Combine(TweakEngine.DataDir, "matches.json");

    public static List<MatchRecord> All
    {
        get
        {
            lock (Gate)
            {
                if (_list == null)
                {
                    try { _list = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<MatchRecord>>(File.ReadAllText(FilePath)) : null; } catch { }
                    _list ??= new();
                }
                return _list.ToList();
            }
        }
    }

    public static event Action<MatchRecord> Added;

    public static void Add(MatchRecord m)
    {
        lock (Gate)
        {
            _ = All;
            if (_list.Any(x => x.Id == m.Id)) return;
            _list.Insert(0, m);
            if (_list.Count > 300) _list.RemoveRange(300, _list.Count - 300);
            try
            {
                Directory.CreateDirectory(TweakEngine.DataDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_list));
            }
            catch { }
        }
        Added?.Invoke(m);
    }

    /// <summary>The match a clip saved at this time belongs to (with a little slack after the end for the last play).</summary>
    public static MatchRecord ForTime(DateTime t) =>
        All.FirstOrDefault(m => t >= m.Start.AddMinutes(-1) && t <= m.End.AddMinutes(2));
}
