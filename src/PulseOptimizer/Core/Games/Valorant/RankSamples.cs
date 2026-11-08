using System.IO.Compression;
using System.Text.Json;

namespace Pulse.Core.Games.Valorant;

/// <summary>
/// The numbers behind "plays like Diamond 2", from matches that aren't yours (players you searched): for each player
/// in those matches, only their rank and their stats (headshot rate, damage per round…). No names, no account ids,
/// nothing that says who played. Your own saved matches are used as they are; this adds more players at more ranks,
/// so every comparison gets sharper the more Pulse sees.
/// </summary>
public static class RankSamples
{
    public sealed class Row
    {
        public int T { get; set; }
        public Dictionary<string, double> V { get; set; } = new();
    }

    sealed class Data
    {
        public List<string> Seen { get; set; } = new();
        public List<Row> Rows { get; set; } = new();
    }

    const int MaxRows = 6000, MaxSeen = 2000;
    static readonly object Gate = new();
    static Data _data;
    static string FilePath => Path.Combine(TweakEngine.DataDir, "valorant", "rank-scale.json.gz");

    /// <summary>Changes each time rows are added (so the scale knows to rebuild).</summary>
    public static int Version { get; private set; }

    static Data D
    {
        get
        {
            if (_data != null) return _data;
            try
            {
                if (File.Exists(FilePath))
                {
                    using var fs = File.OpenRead(FilePath);
                    using var gz = new GZipStream(fs, CompressionMode.Decompress);
                    _data = JsonSerializer.Deserialize<Data>(gz);
                }
            }
            catch { }
            return _data ??= new Data();
        }
    }

    public static List<Row> Rows { get { lock (Gate) return D.Rows.ToList(); } }

    /// <summary>Adds the ranked players of a match (once per match; your own saved matches are skipped).</summary>
    public static void Add(VMatch m, Func<VMatch, VPlayer, Dictionary<string, double>> values)
    {
        if (m == null || string.IsNullOrEmpty(m.Id) || !VMatch.Counts(m.Queue) || VStore.Has(m.Id)) return;
        lock (Gate)
        {
            var d = D;
            if (d.Seen.Contains(m.Id)) return;
            d.Seen.Add(m.Id);
            if (d.Seen.Count > MaxSeen) d.Seen.RemoveRange(0, d.Seen.Count - MaxSeen);
            foreach (var p in m.Players)
                if (p.Tier >= 3) d.Rows.Add(new Row { T = p.Tier, V = values(m, p) });
            if (d.Rows.Count > MaxRows) d.Rows.RemoveRange(0, d.Rows.Count - MaxRows);
            Version++;
        }
    }

    public static void Save()
    {
        lock (Gate)
        {
            if (_data == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using (var fs = File.Create(FilePath + ".tmp"))
                using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
                    JsonSerializer.Serialize(gz, _data);
                File.Move(FilePath + ".tmp", FilePath, true);
            }
            catch { }
        }
    }

    /// <summary>For tests.</summary>
    internal static void Reset() { lock (Gate) { _data = new Data(); Version++; } }
}
