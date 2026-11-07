using System.Reflection;
using System.Text.Json;

namespace Pulse.Core.Guide;

public sealed record Tip(string Title, string Body, string Impact, string PulsePage, string PulseAction, string SourceName, string SourceUrl);
public sealed record SkipItem(string Title, string Body);
/// <summary>A graphics card's launch price, best US price now, and lowest price ever seen.</summary>
public sealed record GpuPrice(string Name, int Msrp, int Now, int Lowest, string Tier);
public sealed record RamPrice(string Kit, string Now, string Before, bool Ddr5);
/// <summary>A 32 GB DDR5 kit: cheapest tracked and average listing on a date.</summary>
public sealed record RamPoint(DateTime Date, int Cheapest, int Average);
public sealed record Pick(string Label, string Name, string Why, string SourceName, string SourceUrl);
public sealed record GearGroup(string Title, string Glyph, Pick[] Picks);
public sealed record Builder(string Name, string GoodFor, string Example, string Watch, string SourceName, string SourceUrl);
public sealed record NewsItem(string Tag, bool Hot, string Title, string Body, string SourceName, string SourceUrl);

/// <summary>
/// Everything the PC guide shows. Published as guide.json on Pulse's website and refreshed from the linked sources;
/// a copy ships inside Pulse for when it's offline. <see cref="Tr"/> holds the Spanish and Portuguese text.
/// </summary>
public sealed class GuideContent
{
    public int Schema { get; set; }
    public DateTime Updated { get; set; }
    public Tip[] Tips { get; set; } = Array.Empty<Tip>();
    public SkipItem[] Skip { get; set; } = Array.Empty<SkipItem>();
    public string GpuSource { get; set; }
    public string GpuSourceUrl { get; set; }
    public GpuPrice[] Gpus { get; set; } = Array.Empty<GpuPrice>();
    public string RamSource { get; set; }
    public string RamSourceUrl { get; set; }
    public RamPrice[] Ram { get; set; } = Array.Empty<RamPrice>();
    public string RamHistorySource { get; set; }
    public RamPoint[] RamHistory { get; set; } = Array.Empty<RamPoint>();
    public GearGroup[] Gear { get; set; } = Array.Empty<GearGroup>();
    public Builder[] Builders { get; set; } = Array.Empty<Builder>();
    public string[] PrebuiltChecks { get; set; } = Array.Empty<string>();
    public NewsItem[] News { get; set; } = Array.Empty<NewsItem>();
    public string[] UpgradeTips { get; set; } = Array.Empty<string>();
    public Dictionary<string, Dictionary<string, string>> Tr { get; set; } = new();

    public const int CurrentSchema = 1;

    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Reads guide.json; null if it isn't a usable guide (wrong schema, or missing the main parts).</summary>
    public static GuideContent Parse(string json)
    {
        try
        {
            var g = JsonSerializer.Deserialize<GuideContent>(json, Options);
            if (g == null || g.Schema != CurrentSchema || g.Updated == default) return null;
            if (g.Tips is not { Length: > 0 } || g.Gpus is not { Length: > 0 } || g.Ram is not { Length: > 0 } || g.News is not { Length: > 0 }) return null;
            g.Skip ??= Array.Empty<SkipItem>(); g.RamHistory ??= Array.Empty<RamPoint>(); g.Gear ??= Array.Empty<GearGroup>();
            g.Builders ??= Array.Empty<Builder>(); g.PrebuiltChecks ??= Array.Empty<string>(); g.UpgradeTips ??= Array.Empty<string>(); g.Tr ??= new();
            return g;
        }
        catch { return null; }
    }

    /// <summary>The text in the language Pulse is showing (from the guide's own translations, then Pulse's).</summary>
    public string T(string english)
    {
        if (string.IsNullOrEmpty(english)) return english;
        if (Tr.TryGetValue(Pulse.Ui.Loc.Current, out var map) && map.TryGetValue(english, out var t) && !string.IsNullOrEmpty(t)) return t;
        return Pulse.Ui.Loc.T(english);
    }
}

public static class GuideData
{
    /// <summary>The copy that ships inside Pulse (used offline until a newer one has been downloaded).</summary>
    public static GuideContent BuiltIn { get; } = Load();

    static GuideContent Load()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("guide.json");
        using var r = new StreamReader(s);
        return GuideContent.Parse(r.ReadToEnd()) ?? throw new InvalidOperationException("The built-in guide.json is broken.");
    }
}
